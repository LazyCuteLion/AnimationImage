using SkiaSharp;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace AnimationImage.Apng
{
    /// <summary>APNG 单帧元信息（面向调用方的最小视图）。</summary>
    internal readonly struct ApngFrameInfo
    {
        /// <summary>该帧显示时长（毫秒）。</summary>
        public double DurationMs { get; }

        public ApngFrameInfo(double durationMs) => DurationMs = durationMs;
    }

    /// <summary>
    /// APNG 解码器（API 设计参考 <see cref="SKCodec"/>）：<br/>
    /// 典型用法：<br/><c>using var codec = ApngCodec.Create(stream); <br/>codec.GetPixels(index, info, address);</c>
    /// </summary>
    internal sealed class ApngCodec : IDisposable
    {
        #region 字段
        private readonly ApngHeader _header;
        private readonly List<ApngFrameEntry> _frames;
        private readonly ApngFrameAssembler _assembler;
        private readonly ApngCompositor _compositor;
        /// <summary>按子帧尺寸缓存 <see cref="SKBitmap"/>，避免每帧重复分配（多数 APNG 全帧同尺寸）。</summary>
        private readonly Dictionary<long, SKBitmap> _subBitmapPool = [];
        private readonly ApngFrameInfo[] _framesInfo;
        /// <summary>合成器当前呈现的帧（用于判断增量顺序合成）。</summary>
        private int _composedIndex = -1;
        /// <summary>生命周期状态：<c>0</c>=存活；<c>1</c>=已发起释放，仍在等待正在进行的解码退出；<c>2</c>=资源已完全释放。</summary>
        private int _state;
        /// <summary>当前处于 <see cref="TryEnter"/>/<see cref="Leave"/> 保护区间内的调用数；<br/>
        /// 用于"最后离开者负责释放"模式，避免在 SkiaSharp 原生调用栈上释放其依赖对象导致 AVE。</summary>
        private int _active;
        #endregion

        #region 元信息（对齐 SKCodec 语义）
        /// <summary>画布宽（IHDR width）。</summary>
        public int Width => _header.CanvasWidth;

        /// <summary>画布高（IHDR height）。</summary>
        public int Height => _header.CanvasHeight;

        /// <summary>动画总帧数。</summary>
        public int FrameCount => _frames.Count;

        /// <summary>播放循环次数：<c>-1</c> = 无限；<c>n ≥ 0</c> = 再循环 n 次（共 n+1 次）。</summary>
        public int RepetitionCount { get; }

        /// <summary>逐帧元信息（按出现顺序）。</summary>
        public IReadOnlyList<ApngFrameInfo> Frames => _framesInfo;
        #endregion

        private ApngCodec(ApngHeader header, List<ApngFrameEntry> frames, ApngFrameAssembler assembler)
        {
            _header = header;
            _frames = frames;
            _assembler = assembler;
            _compositor = new ApngCompositor(header.CanvasWidth, header.CanvasHeight);
            // APNG num_plays: 0=无限；n>0=总共播放 n 次
            // 项目 RepetitionCount: -1=无限；n≥0=再循环 n 次（共 n+1 次）
            RepetitionCount = header.NumPlays == 0 ? -1 : header.NumPlays - 1;

            _framesInfo = new ApngFrameInfo[frames.Count];
            for (int i = 0; i < frames.Count; i++)
                _framesInfo[i] = new ApngFrameInfo(frames[i].DurationMs);
        }

        #region 工厂
        /// <summary>尝试从 <paramref name="stream"/> 创建 <see cref="ApngCodec"/>。<br/>
        /// 返回 <c>null</c> 表示非 APNG 或缺少 acTL/fcTL；调用方需自行降级为静态 PNG 解码。</summary>
        /// <param name="stream">可 Seek 的 PNG/APNG 流；codec 存续期间需保持可用。</param>
        public static ApngCodec? Create(Stream stream)
        {
            if (!ApngChunkReader.TryScan(stream, out var header, out var frames) || frames.Count == 0)
                return null;
            var assembler = new ApngFrameAssembler(stream, header);
            return new ApngCodec(header, frames, assembler);
        }

        /// <summary>快速探测：不完整解析 chunk 表，仅在文件前若干字节里寻找 acTL 签名。</summary>
        public static bool IsApng(Stream stream) => ApngChunkReader.IsApng(stream);
        #endregion

        #region 解码
        /// <summary>
        /// 解码到 <paramref name="frameIndex"/> 帧并把主画布输出到 <paramref name="pixels"/>。<br/>
        /// 内部维护顺序合成状态：前进（含跳帧）时从 <c>_composedIndex+1</c> 增量补合成；
        /// 倒退或初始状态时 Reset 后从 0 重放，避免跳帧引发的雪崩式重放。
        /// </summary>
        /// <param name="frameIndex">目标帧索引，范围 <c>[0, FrameCount)</c>。</param>
        /// <param name="info">目标像素信息，字节大小需 ≥ 主画布字节大小。</param>
        /// <param name="pixels">目标像素地址；调用方保证在返回前有效。</param>
        public SKCodecResult GetPixels(int frameIndex, SKImageInfo info, IntPtr pixels)
        {
            if (frameIndex < 0 || frameIndex >= _frames.Count || pixels == IntPtr.Zero)
                return SKCodecResult.InvalidParameters;
            // 进入解码保护区间，与 Dispose 引用计数配对；已释放/释放中直接失败返回
            if (!TryEnter())
                return SKCodecResult.InvalidParameters;
            try
            {
                int start;
                if (_composedIndex == frameIndex)
                {
                    CopyCanvasTo(info, pixels);
                    return SKCodecResult.Success;
                }
                if (_composedIndex >= 0 && _composedIndex < frameIndex)
                {
                    // 前进（含跳帧）：从下一帧补到 frameIndex
                    start = _composedIndex + 1;
                }
                else
                {
                    // 倒退或初始状态：从 0 重放
                    _compositor.Reset();
                    _composedIndex = -1;
                    start = 0;
                }

                for (int i = start; i <= frameIndex; i++)
                {
                    if (!ComposeFrame(_frames[i]))
                        return SKCodecResult.ErrorInInput;
                    _composedIndex = i;
                }

                CopyCanvasTo(info, pixels);
                return SKCodecResult.Success;
            }
            finally
            {
                Leave();
            }
        }

        /// <summary>手动重置合成状态；下次 <see cref="GetPixels"/> 从 0 开始重放。</summary>
        public void Reset()
        {
            if (!TryEnter()) return;
            try
            {
                _compositor.Reset();
                _composedIndex = -1;
            }
            finally
            {
                Leave();
            }
        }
        #endregion

        #region 私有实现
        /// <summary>
        /// 解码单个子帧并合成到内部主画布：<br/>
        /// ① 从 assembler 拿到 mini-PNG（ArrayPool 管理）；<br/>
        /// ② pin 后以 <see cref="SKData.Create(IntPtr, long)"/> 零拷贝构造 SKData；<br/>
        /// ③ 快捷路径：dispose=None + blend=Source + 全覆盖时直接解到主画布，跳过 sub→canvas blit；<br/>
        /// ④ 常规路径：从 <see cref="_subBitmapPool"/> 取/创建目标 SKBitmap 解入，再交由 compositor 合成。
        /// </summary>
        private bool ComposeFrame(ApngFrameEntry frame)
        {
            if (IsDisposed)
                return false;
            if (!_assembler.TryBuild(frame, out var rented, out var length))
                return false;
            var handle = GCHandle.Alloc(rented, GCHandleType.Pinned);
            try
            {
                using var data = SKData.Create(handle.AddrOfPinnedObject(), length);
                using var codec = SKCodec.Create(data);
                if (codec == null || IsDisposed) return false;

                // 快捷路径：本帧完全替换整幅画布，直接解到主画布，省一次 DrawBitmap
                if (frame.DisposeOp == ApngDisposeOp.None
                    && frame.BlendOp == ApngBlendOp.Source
                    && frame.OffsetX == 0 && frame.OffsetY == 0
                    && frame.Width == _header.CanvasWidth
                    && frame.Height == _header.CanvasHeight)
                {
                    var canvasInfo = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    if (codec.GetPixels(canvasInfo, _compositor.Canvas.GetPixels()) != SKCodecResult.Success)
                        return false;
                    _compositor.MarkComposed(frame);
                    return true;
                }

                var subInfo = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var subBitmap = GetOrCreateSubBitmap(frame.Width, frame.Height, subInfo);
                if (codec.GetPixels(subInfo, subBitmap.GetPixels()) != SKCodecResult.Success)
                    return false;

                _compositor.Compose(subBitmap, frame);
                return true;
            }
            finally
            {
                handle.Free();
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>从池里取或创建指定尺寸的子帧 <see cref="SKBitmap"/>。</summary>
        private SKBitmap GetOrCreateSubBitmap(int width, int height, SKImageInfo info)
        {
            long key = ((long)width << 32) | (uint)height;
            if (!_subBitmapPool.TryGetValue(key, out var bmp))
            {
                bmp = new SKBitmap(info);
                _subBitmapPool[key] = bmp;
            }
            return bmp;
        }

        private unsafe void CopyCanvasTo(SKImageInfo info, IntPtr address)
        {
            var src = _compositor.Canvas.GetPixels();
            if (src == IntPtr.Zero) return;
            Buffer.MemoryCopy(src.ToPointer(), address.ToPointer(), info.BytesSize, info.BytesSize);
        }
        #endregion

        /// <summary>
        /// 发起释放：立即禁止后续 <see cref="GetPixels"/> 进入解码，并尝试释放原生资源。<br/>
        /// 若此时仍有解码在 SkiaSharp 原生栈上（如后台预加载线程），本方法<b>立即返回</b>，
        /// 由最后离开保护区间的线程负责调用 <see cref="DisposeCore"/>。<br/>
        /// 该策略确保永不在 native 调用运行中释放其依赖对象，从根本上消除 AVE，同时不阻塞 UI 线程。
        /// </summary>
        public void Dispose()
        {
            // 状态从 存活(0) → 释放中(1)；重复 Dispose 直接返回
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return;

            // 若无并发调用，当前线程立即完成释放；
            // 否则由 Leave() 在最后一次离开时触发 DisposeCore。
            if (Volatile.Read(ref _active) == 0
                && Interlocked.CompareExchange(ref _state, 2, 1) == 1)
            {
                DisposeCore();
            }
        }

        /// <summary>是否已发起或完成释放（外部只读的快速旁路判断）。</summary>
        private bool IsDisposed => Volatile.Read(ref _state) != 0;

        /// <summary>
        /// 尝试进入解码保护区间；返回 <c>false</c> 表示 codec 已在释放或已释放，调用方应直接返回失败。<br/>
        /// 顺序不可颠倒：<b>先自增再校验</b>，否则 Dispose 侧可能读到旧的 <c>_active=0</c> 而抢先释放资源。
        /// </summary>
        private bool TryEnter()
        {
            Interlocked.Increment(ref _active);
            if (Volatile.Read(ref _state) != 0)
            {
                Leave();
                return false;
            }
            return true;
        }

        /// <summary>
        /// 离开解码保护区间；若本次是最后离开者且已被标记为"释放中"，则在此完成实际的原生资源释放。
        /// </summary>
        private void Leave()
        {
            if (Interlocked.Decrement(ref _active) == 0
                && Interlocked.CompareExchange(ref _state, 2, 1) == 1)
            {
                DisposeCore();
            }
        }

        /// <summary>
        /// 真正的原生资源释放：合成器画布 + 子帧位图池。<br/>
        /// </summary>
        private void DisposeCore()
        {
            try { _compositor.Dispose(); } catch { /* Dispose 不得抛出，静默忽略 */ }
            foreach (var bmp in _subBitmapPool.Values)
            {
                try { bmp.Dispose(); } catch { }
            }
            _subBitmapPool.Clear();
        }
    }
}
