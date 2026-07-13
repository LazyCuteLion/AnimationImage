using AnimationImage.Apng;
using SkiaSharp;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

#if WPF
using System.Windows.Media.Imaging;
#endif

#if AVALONIA
using Avalonia.Media.Imaging;
#endif

namespace AnimationImage
{
    /// <summary>
    /// APNG 动图播放器：仅依赖 <see cref="ApngCodec"/> 完成解码与合成，
    /// 本类只负责时间轴、缓存分派与 UI 层像素输出。
    /// </summary>
    internal class ApngBitmap : AnimatableBitmap
    {
        #region 字段
        private MMFFrameCache? _frameCache;
        private ApngCodec? _codec;
        private SKImageInfo _decodeInfo;                 // 主画布 BGRA Premul
        private readonly List<double> _durations = [];
        private int _currentIndex = -1;
        private CancellationTokenSource _loadToken = new();
        private CancellationTokenSource? _renderToken;
        /// <summary>后台预解码任务句柄；Dispose 时需等待其退出后再释放 <see cref="_frameCache"/>，以防写入已释放的 MMF。</summary>
        private Task? _loadTask;
        #endregion

        public ApngBitmap(AnimatableBitmapOptions options) : base(options)
        {
            // 快速指纹：仅读取首尾 64KB + 文件大小生成缓存键，耍时 <5ms（而非全文件 MD5 的 ~2s）。
            var fingerprint = options.Preload ? _stream.FastFingerprint() : null;

            _codec = ApngCodec.Create(_stream);
            if (_codec == null)
            {
                // 非 APNG（无 acTL）：降级为静态 PNG，仅显示一帧
                try
                {
#if WPF
                    Frame = new WriteableBitmap(new BitmapImage(options.Source));
#endif
#if AVALONIA
                    _stream.Position = 0;
                    Frame = WriteableBitmap.Decode(_stream);
#endif
                }
                catch { }
                State = AnimationState.Error;
                return;
            }

            // 累计时间轴（每帧的结束时间点，毫秒）
            double duration = 0.0;
            for (int i = 0; i < _codec.FrameCount; i++)
            {
                duration += _codec.Frames[i].Duration;
                _durations.Add(duration);
            }

            Metadata = new Metadata(
                _codec.Width,
                _codec.Height,
                duration,
                _codec.FrameCount,
                duration > 0 ? (int)Math.Round(_codec.FrameCount * 1000.0 / duration) : 0,
                _codec.RepetitionCount);

            _decodeInfo = CreateDecodeInfo(_codec.Width, _codec.Height);
            Frame = CreateNewFrame(_decodeInfo.Width, _decodeInfo.Height);

            if (options.Preload)
            {
                try
                {
                    _frameCache = new MMFFrameCache(fingerprint!, _codec.FrameCount, _decodeInfo.BytesSize);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {e.Message}将使用即时解码");
                }
            }

            if (_frameCache != null)
            {
                LoadAsync();
                // 等待首帧就绪，避免首屏空白；限制最多 200 次（~2s）防止死循环
                var maxWait = 200;
                while (_frameCache.LoadedCount < 1 && --maxWait > 0)
                {
                    Thread.Sleep(10);
                }
            }

            Render(0);
        }

        private bool _disposed;
        /// <summary>
        /// 异步安全的释放策略：<br/>
        /// ① UI 线程只做三件轻量事：取消 token、摽除引用、立即返回，不阻塞渲染；<br/>
        /// ② 真正的原生资源释放（codec / frameCache / 令牌源）由后台线程执行，
        ///    并 <c>await _loadTask</c> 等预解码任务优雅退出后再释放 <see cref="_frameCache"/>，
        ///    杜绝 MMF 写入已释放实例的竞态崩溃；<br/>
        /// ③ <see cref="ApngCodec"/> 内部采用引用计数，若此时仍有原生解码在栈上，
        ///    真正释放会推迟到该解码返回后自动执行，从根本上消除 AVE。<br/>
        /// ④ 全部释放路径 <c>try/catch</c> 兜底，保证 Dispose 永不抛。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (!disposing)
            {
                base.Dispose(disposing);
                return;
            }

            // 1) 摽出所有可能被后台线程使用的引用，切断新的访问入口
            var renderToken = _renderToken;
            var loadToken = _loadToken;
            var loadTask = _loadTask;
            var codec = _codec;
            var frameCache = _frameCache;

            _renderToken = null;
            _loadTask = null;
            _codec = null;
            _frameCache = null;

            // 2) 立即发信号取消（Cancel 仅置位，不阻塞 UI）
            try { renderToken?.Cancel(); } catch { }
            try { loadToken?.Cancel(); } catch { }
            _durations.Clear();

            // 3) 将原生资源释放丢到线程池；UI 线程立即返回。
            //    先 await 预解码任务退出，确保不会向已释放的 MMF 写入；
            //    codec 内部引用计数会自行等待正在进行的 GetPixels 结束后才释放 native 资源。
            _ = Task.Run(async () =>
            {
                try { if (loadTask != null) await loadTask.ConfigureAwait(false); } catch { }
                try { codec?.Dispose(); } catch { }
                try { frameCache?.Dispose(); } catch { }
                try { renderToken?.Dispose(); } catch { }
                try { loadToken?.Dispose(); } catch { }
            });

            base.Dispose(disposing);
        }

        public override bool IsAnimatable => base.IsAnimatable && _codec is { FrameCount: > 0 };

        /// <summary>跳转到指定时间点（毫秒）。</summary>
        internal override void SeekTime(double milliseconds)
        {
            if (!IsAnimatable) return;

            var index = TimeToIndex(milliseconds);
            try
            {
                if (index < 0 || index > _codec!.FrameCount - 1 || index == _currentIndex)
                    return;
                Render(index);
            }
            finally
            {
                _currentIndex = index;
                base.SeekTime(milliseconds);
            }
        }

        private async void Render(int index)
        {
#if DEBUG
            var st = Stopwatch.StartNew();
#endif
            try
            {
                if (_frameCache != null)
                {
                    _renderToken?.Cancel();

                    // 阶段1：无锁等待缓存就绪（超时使用本帧显示时长的 80%）
                    if (!_frameCache!.Contains(index))
                    {
                        var time = _codec!.Frames[index].Duration * 0.8;
                        _renderToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(time));
                        while (!_renderToken.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(5, _renderToken.Token);
                                if (_frameCache!.Contains(index)) break;
                            }
                            catch (OperationCanceledException) { break; }
                        }
                    }

                    // 阶段2：锁定并拷贝
                    using var b = Frame.LockScope();
                    if (_frameCache!.TryGet(index, b.Address))
                    {
                        b.Update(_decodeInfo.Rect);
                        _currentIndex = index;
                    }
                }
                else
                {
                    using var b = Frame.LockScope();
                    if (_codec!.GetPixels(index, _decodeInfo, b.Address) == SKCodecResult.Success)
                    {
                        b.Update(_decodeInfo.Rect);
                        _currentIndex = index;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} SeekTime({index:000})错误：{e.Message}");
            }
            finally
            {
#if DEBUG
                st.Stop();
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} SeekTime({index:000})耗时：{st.ElapsedMilliseconds}");
#endif
            }
        }

        private int TimeToIndex(double milliseconds)
            => FrameTimeHelper.TimeToIndex(milliseconds, _durations, _currentIndex);

        /// <summary>后台顺序解码所有帧，把合成后的完整画布写入 <see cref="MMFFrameCache"/>。</summary>
        private void LoadAsync()
        {
            if (_frameCache == null || !_frameCache.CanWrite || _codec == null)
                return;

            // 以局部变量快照引用，避免运行中被 Dispose 摽为 null
            var codec = _codec;
            var frameCache = _frameCache;
            var decodeInfo = _decodeInfo;
            var token = _loadToken.Token;

            _loadTask = Task.Run(() =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(decodeInfo.BytesSize);
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var address = handle.AddrOfPinnedObject();
                    for (int i = 0; i < codec.FrameCount; i++)
                    {
                        if (token.IsCancellationRequested) break;

                        if (codec.GetPixels(i, decodeInfo, address) != SKCodecResult.Success)
                        {
                            Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Decode({i})失败");
                            continue;
                        }
                        // Dispose 可能已发起，提前退出避免向即将释放的 MMF 写入
                        if (token.IsCancellationRequested) break;
                        try { frameCache.TryAdd(i, address); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} 写入帧缓存失败：{ex.Message}");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} 预解码任务异常：{ex.Message}");
                }
                finally
                {
                    // 更新最后写入时间；竞态下 cache 可能已被释放，此处静默忽略即可。
                    try { File.SetLastWriteTime(frameCache.TempPath, DateTime.Now); } catch { }
                    handle.Free();
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }, token);
        }
    }
}
