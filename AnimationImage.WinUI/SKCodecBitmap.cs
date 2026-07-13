using SkiaSharp;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AnimationImage
{
    /// <summary>
    /// GIF / WebP / 静态图 的动图数据模型：基于 <see cref="SKCodec"/> 逐帧解码直接写入
    /// <see cref="CompositionHost.PixelAddress"/> BackBuffer，再调用 <see cref="CompositionHost.Present"/> 上屏。
    /// 支持内存映射文件（MMF）预解码缓存。
    /// </summary>
    internal partial class SKCodecBitmap : AnimatableBitmap
    {
        #region 字段
        private MMFFrameCache? _frameCache;
        private SKCodec? _codec;
        private readonly int _frameCount;
        private readonly SKImageInfo _codecInfo;
        private readonly SKCodecFrameInfo[] _frameInfo;
        private readonly List<double> _durations = [];
        private int _currentIndex = -1;
        private CancellationTokenSource _loadToken = new();
        private CancellationTokenSource? _renderToken;
        private Task? _loadTask;
        private readonly AnimatableBitmapOptions _options;
        #endregion

        public SKCodecBitmap(AnimatableBitmapOptions options) : base(options)
        {
            _options = options;
            var md5 = _options.Preload ? _stream!.FastFingerprint() : null;
            _codec = SKCodec.Create(_stream);
            if (_codec == null)
            {
                State = AnimationState.Error;
                return;
            }
            _frameCount = Math.Max(1, _codec.FrameCount);
            _frameInfo = _codec.FrameInfo is { Length: > 0 }
                ? _codec.FrameInfo
                : [new SKCodecFrameInfo() { FrameRect = _codec.Info.Rect, RequiredFrame = -1 }];

            var duration = 0.0;
            if (_frameCount > 1)
            {
                for (int i = 0; i < _frameCount; i++)
                {
                    duration += _codec.FrameInfo[i].Duration;
                    _durations.Add(duration);
                }
            }

            Metadata = new Metadata(_codec.Info.Width,
                _codec.Info.Height,
                duration,
                _frameCount,
                duration > 0 ? (int)Math.Round(_frameCount * 1000.0 / duration) : 0,
                _codec.RepetitionCount);

            _codecInfo = CreateDecodeInfo(_codec.Info.Width, _codec.Info.Height);

            if (_options.Preload)
            {
                try
                {
                    _frameCache = new MMFFrameCache(md5!, _frameCount, _codec.Info.BytesSize);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {e.Message}，将使用即时解码");
                }
            }

            if (_frameCache != null)
                LoadAsync();
        }

        public override bool IsAnimatable => base.IsAnimatable && _frameCount > 0 && _codec != null;

        internal override void SeekTime(double milliseconds)
        {
            if (!IsAnimatable) return;

            var index = TimeToIndex(milliseconds);
            try
            {
                if (index < 0 || index > _frameCount - 1 || index == _currentIndex)
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
                if (_disposed || _codec == null || _host == null)
                    return;

                _host.EnsureSize(_codecInfo.Width, _codecInfo.Height);
                var target = _host.PixelAddress;
                if (target == IntPtr.Zero) return;

                bool success;
                if (_frameCache != null)
                {
                    _renderToken?.Cancel();

                    if (!_frameCache.Contains(index))
                    {
                        var time = _frameInfo[index].Duration * 0.8;
                        _renderToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(time > 0 ? time : 30));
                        while (!_renderToken.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(5, _renderToken.Token);
                                if (_frameCache?.Contains(index) == true)
                                    break;
                            }
                            catch (OperationCanceledException) { break; }
                        }
                    }

                    success = _frameCache?.TryGet(index, target) ?? false;
                }
                else
                {
                    success = Decode(index, target);
                }

                if (success && !_disposed)
                {
                    _host.Present();
                    _currentIndex = index;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Render({index:000})错误：{e.Message}");
            }
            finally
            {
#if DEBUG
                st.Stop();
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Render({index:000})耗时：{st.ElapsedMilliseconds}");
#endif
            }
        }

        private int TimeToIndex(double milliseconds)
            => FrameTimeHelper.TimeToIndex(milliseconds, _durations, _currentIndex);

        private bool Decode(int index, nint address)
        {
            if (_disposed || _codec == null || address == IntPtr.Zero)
                return false;

            var frameInfo = _frameInfo[index];
            var requiredFrame = frameInfo.RequiredFrame;

            if (index == 0 || index < _currentIndex || requiredFrame == -1)
            {
                var result = _codec.GetPixels(_codecInfo, address, new SKCodecOptions(index, -1) { ZeroInitialized = SKZeroInitialized.No });
                return result == SKCodecResult.Success;
            }

            var priorFrame = _currentIndex;
            if (priorFrame > requiredFrame)
                priorFrame = -1;
            var decodeResult = _codec.GetPixels(_codecInfo, address, new SKCodecOptions(index, priorFrame));

            if (decodeResult != SKCodecResult.Success && index - _currentIndex > 1)
            {
                var options = new SKCodecOptions();
                for (int i = _currentIndex + 1; i <= index; i++)
                {
                    if (!_codec.GetFrameInfo(i, out var info)) continue;
                    if (info.DisposalMethod != SKCodecAnimationDisposalMethod.Keep) continue;

                    options.FrameIndex = i;
                    options.PriorFrame = i - 1;
                    decodeResult = _codec.GetPixels(_codecInfo, address, options);
                    if (decodeResult != SKCodecResult.Success)
                    {
                        options.PriorFrame = -1;
                        decodeResult = _codec.GetPixels(_codecInfo, address, options);
                    }
                    if (decodeResult != SKCodecResult.Success)
                        break;
                }
            }

            return decodeResult == SKCodecResult.Success;
        }

        private void LoadAsync()
        {
            if (_frameCache == null || !_frameCache.CanWrite)
                return;

            var codec = _codec;
            var frameCache = _frameCache;
            var codecInfo = _codecInfo;
            var token = _loadToken.Token;

            _loadTask = Task.Run(() =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(codecInfo.BytesSize);
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var address = handle.AddrOfPinnedObject();
                    for (var i = 0; i < _frameCount; i++)
                    {
                        if (token.IsCancellationRequested) break;

                        var options = new SKCodecOptions(i, i - 1) { ZeroInitialized = SKZeroInitialized.No };
                        var result = codec!.GetPixels(codecInfo, address, options);
                        if (result != SKCodecResult.Success && i > 0)
                        {
                            options.PriorFrame = _frameInfo[i].RequiredFrame;
                            if (options.PriorFrame != -1 && !frameCache.TryGet(options.PriorFrame, address))
                                options.PriorFrame = -1;
                            result = codec.GetPixels(codecInfo, address, options);
                        }
                        if (result == SKCodecResult.Success)
                        {
                            if (token.IsCancellationRequested) break;
                            try { frameCache.TryAdd(i, address); }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} 写入帧缓存失败：{ex.Message}");
                                break;
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Preload Decode({i})失败");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} 预解码任务异常：{ex.Message}");
                }
                finally
                {
                    try { File.SetLastWriteTime(frameCache.TempPath, DateTime.Now); } catch { }
                    handle.Free();
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }, token);
        }

        private bool _disposed;
        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (!disposing) { base.Dispose(disposing); return; }

            var renderToken = _renderToken;
            var loadToken = _loadToken;
            var loadTask = _loadTask;
            var codec = _codec;
            var frameCache = _frameCache;

            _renderToken = null;
            _loadTask = null;
            _codec = null;
            _frameCache = null;

            try { renderToken?.Cancel(); } catch { }
            try { loadToken.Cancel(); } catch { }
            _durations.Clear();

            _ = Task.Run(async () =>
            {
                try { if (loadTask != null) await loadTask.ConfigureAwait(false); } catch { }
                try { codec?.Dispose(); } catch { }
                try { frameCache?.Dispose(); } catch { }
                try { renderToken?.Dispose(); } catch { }
                try { loadToken.Dispose(); } catch { }
            });

            base.Dispose(disposing);
        }
    }
}
