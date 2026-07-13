using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using System;
using System.Collections.Generic;

#if WPF
using System.Windows.Media.Imaging;
#endif

#if AVALONIA
using Avalonia.Media.Imaging;
#endif

namespace AnimationImage
{
    internal class SKCodecBitmap : AnimatableBitmap
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
        private readonly AnimatableBitmapOptions _options;
        private CancellationTokenSource _renderToken;
        #endregion

        public SKCodecBitmap(AnimatableBitmapOptions options) : base(options)
        {
            _options = options;
            var md5 = _options.Preload ? _stream.FastFingerprint() : null;
            _codec = SKCodec.Create(_stream);
            if (_codec == null)
            {
                try
                {
#if WPF
                    this.Frame = new WriteableBitmap(new BitmapImage(options.Source));
#endif
#if AVALONIA
                    this.Frame = WriteableBitmap.Decode(_stream);
#endif
                }
                catch { }
                State = AnimationState.Error;
                return;
            }
            _frameCount = Math.Max(1, _codec.FrameCount);
            _frameInfo = _codec.FrameInfo is { Length: > 0 }
                ? _codec.FrameInfo
                : [new SKCodecFrameInfo() { FrameRect = _codec.Info.Rect, RequiredFrame = -1 }];

            var duration = 0.0;
            // 计算累计时间轴（每帧的结束时间点，毫秒）
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
                duration > 0 ? (int)(_frameCount * 1000 / duration) : 0,
                _codec.RepetitionCount);

            _codecInfo = CreateDecodeInfo(_codec.Info.Width, _codec.Info.Height);

            Frame = CreateNewFrame(_codecInfo.Width, _codecInfo.Height);

            if (_options.Preload)
            {
                try
                {
                    _frameCache = new MMFFrameCache(md5!, _frameCount, _codec.Info.BytesSize);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {e.Message}将使用即时解码");
                }
            }

            if (_frameCache != null)
            {
                LoadAsync();
                while (_frameCache.LoadedCount < 1)
                {
                    Thread.Sleep(10);
                }
            }

            Render(0);
        }

        private bool _disposed;
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (disposing)
            {
                _renderToken?.Cancel();
                _renderToken?.Dispose();
                _loadToken?.Cancel();
                _loadToken?.Dispose();
                _codec?.Dispose();
                _durations.Clear();
                _frameCache?.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool IsAnimatable => base.IsAnimatable && _frameCount > 0;

        /// <summary>
        /// 跳转到指定时间点（毫秒）
        /// </summary>
        internal override void SeekTime(double milliseconds)
        {
            if (!IsAnimatable)
                return;

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
                var success = false;
                var rect = _frameInfo[index].FrameRect;

                if (_frameCache != null)
                {
                    _renderToken?.Cancel();

                    // 阶段1：无锁等待缓存就绪
                    if (!_frameCache.Contains(index))
                    {
                        var time = _frameInfo[index].Duration * 0.8;
                        _renderToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(time));
                        while (!_renderToken.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(5, _renderToken.Token);
                                if (_frameCache.Contains(index))
                                    break;
                            }
                            catch (OperationCanceledException) { break; }
                        }
                    }

                    // 阶段2：锁定并拷贝
                    using var b = Frame.LockScope();
                    success = _frameCache.TryGet(index, b.Address);
                    if (success)
                    {
                        b.Update(rect);
                        _currentIndex = index;
                    }
                }
                else
                {
                    using var b = Frame.LockScope();
                    success = Decode(index, b.Address, out rect);
                    if (success)
                    {
                        b.Update(rect);
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

        /// <summary>
        /// 将时间（毫秒）映射为帧索引
        /// </summary>
        /// <remarks>
        /// 先尝试基于当前帧的局部判断（n-1,n,n+1），若不命中则使用二分查找
        /// </remarks>
        private int TimeToIndex(double milliseconds)
        {
            if (milliseconds == 0 || _durations.Count <= 1)
                return 0;

            // 快速判断邻近帧，减少二分查找开销
            var index = _currentIndex > -1 ? _currentIndex : 0;
            if (index >= _durations.Count)
                index %= _durations.Count;

            if (milliseconds < _durations[index])
            {
                if (index == 0)
                    return 0;
                if (index > 0 && milliseconds >= _durations[index - 1])
                    return index;
                if (index > 1 && milliseconds >= _durations[index - 2] && milliseconds < _durations[index - 1])
                    return index - 1;
            }
            else if (index < _durations.Count - 1 && milliseconds < _durations[index + 1])
            {
                return index + 1;
            }

            // 二分查找第一个 >= milliseconds 的位置
            index = _durations.BinarySearch(milliseconds);
            if (index < 0)
                index = ~index;
            else
                index++; // 精确匹配时，取下一个帧

            if (index >= _durations.Count)
                index = 0;

            return index;
        }

        private bool Decode(int index, nint address, out SKRectI rect)
        {
            rect = new SKRectI(0, 0, 1, 1);

            if (_disposed || _codec == null || address == IntPtr.Zero)
                return false;

            var frameInfo = _frameInfo[index];
            var requiredFrame = frameInfo.RequiredFrame;

            if (index == 0 || index < _currentIndex || requiredFrame == -1)
            {
                Debug.WriteLineIf(index < _currentIndex, $"{DateTimeOffset.Now:HH:mm:ss.fff} 回退解码：{_currentIndex}->{index}");
                var result = _codec.GetPixels(_codecInfo, address, new SKCodecOptions(index, -1) { ZeroInitialized = SKZeroInitialized.No });
                if (result == SKCodecResult.Success)
                {
                    rect = frameInfo.FrameRect;
                }
                return result == SKCodecResult.Success;
            }

            var priorFrame = _currentIndex;
            if (priorFrame > requiredFrame)
            {
                // 把参考帧重置为-1，让解码器自动处理
                priorFrame = -1;
            }
            var decodeResult = _codec.GetPixels(_codecInfo, address, new SKCodecOptions(index, priorFrame));
            if (decodeResult == SKCodecResult.Success)
            {
                rect = frameInfo.FrameRect;
            }
            else if (index - _currentIndex > 1)
            {
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} 跳帧解码：{_currentIndex}->{index}");
                // 若解码失败，且发生了跳帧，则尝试按顺序解码
                var options = new SKCodecOptions();
                for (int i = _currentIndex + 1; i <= index; i++)
                {
                    if (!_codec.GetFrameInfo(i, out var info))
                        continue;

                    // 非Keep的可以跳过
                    if (info.DisposalMethod != SKCodecAnimationDisposalMethod.Keep)
                        continue;

                    options.FrameIndex = i;
                    options.PriorFrame = i - 1;
                    decodeResult = _codec.GetPixels(_codecInfo, address, options);
                    // 若解码失败，使用依赖帧再次尝试
                    if (decodeResult != SKCodecResult.Success)
                    {
                        options.PriorFrame = -1;
                        decodeResult = _codec.GetPixels(_codecInfo, address, options);
                    }
                    // 若还是失败，那就失败了，无可奈何
                    if (decodeResult != SKCodecResult.Success)
                    {
                        _currentIndex = i - 1;
                        break;
                    }
                }
                if (decodeResult == SKCodecResult.Success)
                    rect = _codecInfo.Rect;
            }

            return decodeResult == SKCodecResult.Success;
        }

        private void LoadAsync()
        {
            if (_frameCache == null || !_frameCache.CanWrite)
                return;

            Task.Run(() =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(_codecInfo.BytesSize);
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var address = handle.AddrOfPinnedObject();
                    for (var i = 0; i < _frameCount; i++)
                    {
                        if (_loadToken.IsCancellationRequested)
                            break;

                        var options = new SKCodecOptions(i, i - 1) { ZeroInitialized = SKZeroInitialized.No };
                        var result = _codec!.GetPixels(_codecInfo, address, options);
                        if (result != SKCodecResult.Success && i > 0)
                        {
                            options.PriorFrame = _frameInfo[i].RequiredFrame;
                            if (options.PriorFrame != -1)
                            {
                                if (!_frameCache.TryGet(options.PriorFrame, address))
                                {
                                    options.PriorFrame = -1;
                                }
                            }
                            result = _codec!.GetPixels(_codecInfo, address, options);
                        }
                        if (result == SKCodecResult.Success)
                        {
                            _frameCache.TryAdd(i, address);
                        }
                        else
                        {
                            Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Decode({i})失败");
                        }
                    }
                }
                finally
                {
                    File.SetLastWriteTime(_frameCache.TempPath, DateTime.Now);
                    handle.Free();
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }, _loadToken.Token);
        }
    }
}
