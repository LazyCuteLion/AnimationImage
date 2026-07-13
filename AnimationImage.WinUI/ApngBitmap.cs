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

namespace AnimationImage
{
    /// <summary>
    /// APNG 动图数据模型：基于 <see cref="ApngCodec"/> 逐帧解码直接写入
    /// <see cref="CompositionHost.PixelAddress"/> BackBuffer，再调用 <see cref="CompositionHost.Present"/> 上屏。
    /// </summary>
    internal partial class ApngBitmap : AnimatableBitmap
    {
        #region 字段
        private MMFFrameCache? _frameCache;
        private ApngCodec? _codec;
        private SKImageInfo _decodeInfo;
        private readonly List<double> _durations = [];
        private int _currentIndex = -1;
        private CancellationTokenSource _loadToken = new();
        private CancellationTokenSource? _renderToken;
        private Task? _loadTask;
        #endregion

        public ApngBitmap(AnimatableBitmapOptions options) : base(options)
        {
            var fingerprint = options.Preload ? _stream!.FastFingerprint() : null;

            _codec = ApngCodec.Create(_stream);
            if (_codec == null)
            {
                State = AnimationState.Error;
                return;
            }

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

            if (options.Preload)
            {
                try
                {
                    _frameCache = new MMFFrameCache(fingerprint!, _codec.FrameCount, _decodeInfo.BytesSize);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {e.Message}，将使用即时解码");
                }
            }

            if (_frameCache != null)
                LoadAsync();
        }

        public override bool IsAnimatable => base.IsAnimatable && _codec is { FrameCount: > 0 };

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
                if (_disposed || _codec == null || _host == null)
                    return;

                _host.EnsureSize(_decodeInfo.Width, _decodeInfo.Height);
                var target = _host.PixelAddress;
                if (target == IntPtr.Zero) return;

                bool success;
                if (_frameCache != null)
                {
                    _renderToken?.Cancel();

                    if (!_frameCache.Contains(index))
                    {
                        var time = _codec.Frames[index].Duration * 0.8;
                        _renderToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(time > 0 ? time : 30));
                        while (!_renderToken.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(5, _renderToken.Token);
                                if (_frameCache?.Contains(index) == true) break;
                            }
                            catch (OperationCanceledException) { break; }
                        }
                    }
                    success = _frameCache?.TryGet(index, target) ?? false;
                }
                else
                {
                    success = _codec.GetPixels(index, _decodeInfo, target) == SKCodecResult.Success;
                }

                if (success && !_disposed)
                {
                    _host.Present();
                    _currentIndex = index;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} APNG Render({index:000})错误：{e.Message}");
            }
            finally
            {
#if DEBUG
                st.Stop();
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} APNG Render({index:000})耗时：{st.ElapsedMilliseconds}");
#endif
            }
        }

        private int TimeToIndex(double milliseconds)
            => FrameTimeHelper.TimeToIndex(milliseconds, _durations, _currentIndex);

        private void LoadAsync()
        {
            if (_frameCache == null || !_frameCache.CanWrite || _codec == null)
                return;

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
                            Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} APNG Preload Decode({i})失败");
                            continue;
                        }
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
