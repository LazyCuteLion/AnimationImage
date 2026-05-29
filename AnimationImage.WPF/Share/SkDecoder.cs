using AnimationImage.Core;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Collections;
using System.Linq;
using static System.Runtime.InteropServices.JavaScript.JSType;
using Vortice.Direct3D12;
using System.Collections.Generic;




#if AVALONIA
using Avalonia.Media.Imaging;
namespace AnimationImage.Avalonia
#endif

#if WPF
using System.Windows.Media.Imaging;
namespace AnimationImage.WPF
#endif

{
    internal record FrameData
    {
        public int Index { get; } = -1;
        public WriteableBitmap Bitmap { get; }
        public bool IsEmpty => Index < 0;
        public FrameData(int index, WriteableBitmap bitmap)
        {
            this.Index = index;
            this.Bitmap = bitmap;
        }
        public static FrameData Empty = new(-1, null);
    }

    internal partial class SkDecoder : IDisposable
    {
        private readonly int _frameCount;
        private int _preloadCount;
        private readonly object _lock = new();
        private ConcurrentDictionary<int, WriteableBitmap> _frameCache = new();
        private readonly SKImageInfo _codecInfo;
        private SKCodec _codec;
        private Task _preloadTask;
        private CancellationTokenSource _preloadToken;

        public SKCodec SKCodec => _codec;

        public int CacheCount => _frameCache.Count;

        public SkDecoder(Stream stream, int preloadCount)
        {
            _codec = SKCodec.Create(stream);
            _frameCount = _codec.FrameCount;

            _codecInfo = AnimatableBitmap.CreateDecodeInfo(_codec.Info.Width, _codec.Info.Height);

            var first = this.DecodeFrame(0, new FrameData(1, null));
            if (first.IsEmpty)
            {
                throw new NotSupportedException("解码失败");
            }
            else
            {
                _frameCache.TryAdd(0, first.Bitmap.TryFreeze());
            }

            _preloadCount = preloadCount;
            this.PreloadAsync(preloadCount);
        }

        private WriteableBitmap CreateNewFrame()
        {
            return AnimatableBitmap.CreateNewFrame(_codec.Info.Width, _codec.Info.Height);
        }

        private WriteableBitmap GetFrame(WriteableBitmap bitmap)
        {
#if AVALONIA
            return bitmap?.SafeClone() ?? this.CreateNewFrame();
#endif

#if WPF
            if (bitmap == null)
                return this.CreateNewFrame();
            else if (bitmap.IsFrozen)
                return new WriteableBitmap(bitmap);
            else
                return bitmap;
#endif
        }

        public FrameData Get(int index)
        {
            return this.Get(index, FrameData.Empty);
        }

        public FrameData Get(int index, FrameData data)
        {
            if (data.Index == index)
                return data;

            if (_frameCache.TryGetValue(index, out var bitmap))
            {
                if (_preloadCount == PreloadOptions.Disable)
                {
                    _frameCache.TryRemove(index, out _);
                }
                return new FrameData(index, bitmap);
            }

            var result = this.Decode(index, data);
            if (!result.IsEmpty
                && _preloadCount != PreloadOptions.Disable
                && !_frameCache.ContainsKey(index))
            {
                _frameCache.TryAdd(index, result.Bitmap.TryFreeze());
            }

            return result;
        }

        private FrameData Decode(int index, FrameData data)
        {
            if (_codec == null)
                return FrameData.Empty;

            var st = Stopwatch.StartNew();
            var result = FrameData.Empty;
            try
            {
                if (data.Index == _frameCount - 1)
                {
                    data = new FrameData(0, data.Bitmap);
                }

                if (data.Index > index)
                {
                    //回退只能从头解码，参考帧设为-1，让解码器自动处理，可能会耗时较长
                    var frame = this.GetFrame(data.Bitmap);
                    var r = SKCodecResult.Unimplemented;
                    using var b = frame.LockScope();
                    lock (_lock)
                    {
                        r = _codec.GetPixels(_codecInfo, b.Address, new SKCodecOptions(index, -1));
                    }
                    if (r == SKCodecResult.Success)
                        b.Update(_codec.FrameInfo[index].FrameRect);
                    if (r == SKCodecResult.Success)
                        return new FrameData(index, frame);
                }

                result = this.DecodeFrame(index, data);

                //解码失败且跳帧，尝试循环解码
                if (result.IsEmpty
                 && index - data.Index > 1)
                {
                    var frame = this.GetFrame(data.Bitmap);
                    var temp = new FrameData(data.Index, frame);
                    for (int i = data.Index + 1; i <= index; i++)
                    {
                        if (_codec.FrameInfo[i].DisposalMethod == SKCodecAnimationDisposalMethod.Keep)
                        {
                            temp = this.DecodeFrame(i, temp);
                            if (temp.IsEmpty)
                                break;
                        }
                    }
                    if (temp.Index == index)
                        result = temp;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[{index:000}]解码失败：{e.Message}");
            }
            finally
            {
                st.Stop();
                Debug.WriteLineIf(st.ElapsedMilliseconds > 16, $"[{index:000}<-{data.Index:000}]解码{(result.Index == index ? "成功" : "失败")}，耗时：{st.ElapsedMilliseconds}");
            }
            return result;
        }

        private FrameData DecodeFrame(int index, FrameData data)
        {
            if (data.Index == index)
                return data;
            try
            {
                if (_codec == null)
                    return FrameData.Empty;

                if (index == 0 && data.Index > 0)
                {
                    data = new FrameData(-1, data.Bitmap);
                }

                var frame = this.GetFrame(data.Bitmap);
                var result = SKCodecResult.Unimplemented;
                var frameInfo = _codec.FrameInfo[index];

                if (data.Index > index)
                {
                    //回退只能从头解码，参考帧设为-1，让解码器自动处理，可能会耗时较长
                    using var b = frame.LockScope();
                    lock (_lock)
                    {
                        result = _codec.GetPixels(_codecInfo, b.Address, new SKCodecOptions(index, -1));
                    }

                    if (result == SKCodecResult.Success)
                    {
                        b.Update(frameInfo.FrameRect);
                        return new FrameData(index, frame);
                    }
                    else
                    {
                        return FrameData.Empty;
                    }
                }

                var requiredFrame = frameInfo.RequiredFrame;

                //独立帧
                if (requiredFrame == -1)
                {
                    using var b = frame.LockScope();
                    lock (_lock)
                    {
                        result = _codec.GetPixels(_codecInfo, b.Address, new SKCodecOptions(index, -1));
                    }
                    if (result == SKCodecResult.Success)
                        b.Update(frameInfo.FrameRect);
                }
                else
                {
                    //RequiredFrame <= PriorFrame < index
                    var priorFrame = data.Index;

                    //关键帧在参考帧前面，需要把画布重置到关键帧，
                    if (requiredFrame < priorFrame)
                    {
                        //查找缓存
                        if (_frameCache.TryGetValue(priorFrame, out var cache))
                        {
                            cache.CopyTo(frame);
                        }
                        else
                        {
                            //把参考帧重置为-1，让解码器自动处理
                            priorFrame = -1;
                        }
                    }

                    using var b = frame.LockScope();
                    lock (_lock)
                    {
                        result = _codec.GetPixels(_codecInfo, b.Address, new SKCodecOptions(index, priorFrame));
                    }
                    if (result == SKCodecResult.Success)
                        b.Update(frameInfo.FrameRect);
                }

                if (result == SKCodecResult.Success)
                {
                    return new FrameData(index, frame);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[{index:000}]<-[{data.Index:000}]解码错误：{e.Message}");
            }

            return FrameData.Empty;
        }

        public Task PreloadAsync() => this.PreloadAsync(_preloadCount);
        public Task PreloadAsync(int count)
        {
            if (count == PreloadOptions.Disable)
                return Task.CompletedTask;

            if (_preloadTask != null)
            {
                return _preloadTask;
            }

            _preloadToken = new();
            _preloadTask = Task.Run(() =>
             {
                 var frame = this.GetFrame(_frameCache[0]);
                 var start = new FrameData(0, frame);
                 if (count <= PreloadOptions.Auto)
                 {
                     if (_frameCount > 10)
                     {
                         //先加载10帧，看看耗时
                         var st = Stopwatch.StartNew();
                         for (var i = 1; i < 11; i++)
                         {
                             if (_frameCache.ContainsKey(i))
                                 continue;
                             start = this.DecodeFrame(i, start);
                             if (!start.IsEmpty)
                             {
                                 _frameCache.TryAdd(start.Index, start.Bitmap.TryFreeze());
                             }
                         }
                         st.Stop();
                         var cps = 10 / st.Elapsed.TotalSeconds;
                         var duration = _codec.FrameInfo.Sum(d => d.Duration);
                         var fps = _frameCount * 1000.0 / duration;
                         Debug.WriteLine($"预加载10帧，耗时：{st.ElapsedMilliseconds}，解码速度：{cps}");
                         if (fps > cps)
                         {
                             var wantCount = (int)Math.Ceiling((1 - (cps / fps)) * _frameCount) + (int)(fps * 0.1);//多准备0.1秒的帧数
                             Debug.WriteLine($"至少要预加载：{wantCount}，还需加载：{wantCount - _frameCache.Count}");
                             //继续加载
                             if (wantCount > _frameCache.Count)
                             {
                                 st.Start();
                                 for (var i = start.Index + 1; i < wantCount; i++)
                                 {
                                     if (_frameCache.ContainsKey(i))
                                         continue;
                                     start = this.DecodeFrame(i, start);
                                     if (!start.IsEmpty)
                                     {
                                         _frameCache.TryAdd(start.Index, start.Bitmap.TryFreeze());
                                     }
                                 }
                                 st.Stop();
                                 Debug.WriteLine($"已加载[{_frameCache.Count}]帧，耗时：{st.ElapsedMilliseconds}，后台将持续加载[{_frameCount - _frameCache.Count}]帧");
                             }
                             _preloadCount = _frameCount;
                             //启动一个后台线程，持续解码
                             Task.Run(() =>
                             {
                                 for (var i = start.Index + 1; i < _preloadCount; i++)
                                 {
                                     if (_frameCache.ContainsKey(i))
                                         continue;
                                     start = this.DecodeFrame(i, start);
                                     if (!start.IsEmpty)
                                     {
                                         _frameCache.TryAdd(start.Index, start.Bitmap.TryFreeze());
                                     }
                                 }
                                 Debug.WriteLine($"共[{_frameCount}]帧，已加载[{_frameCache.Count}]");
                             }, _preloadToken.Token);
                             return;
                         }
                         else
                         {
                             //解码速度大于帧率
                             _preloadCount = 0;
                         }
                     }
                     else
                     {
                         //只有不到10帧，直接全部缓存
                         _preloadCount = _frameCount;
                     }
                 }
                 else
                 {
                     _preloadCount = count == PreloadOptions.Full ? _frameCount : Math.Min(_frameCount, count);
                 }

                 if (_frameCache.Count < _preloadCount)
                 {
                     for (var i = start.Index + 1; i < _preloadCount; i++)
                     {
                         if (_frameCache.ContainsKey(i))
                             continue;
                         start = this.DecodeFrame(i, start);
                         if (!start.IsEmpty)
                         {
                             _frameCache.TryAdd(start.Index, start.Bitmap.TryFreeze());
                         }
                     }
                     if (_preloadCount < _frameCount)
                     {
                         //启动一个后台线程，持续解码
                         Task.Run(() =>
                         {
                             for (var i = start.Index + 1; i < _frameCount; i++)
                             {
                                 if (_frameCache.ContainsKey(i))
                                     continue;
                                 start = this.DecodeFrame(i, start);
                                 if (!start.IsEmpty)
                                 {
                                     _frameCache.TryAdd(start.Index, start.Bitmap.TryFreeze());
                                 }
                             }
                             Debug.WriteLine($"共[{_frameCount}]帧，已加载[{_frameCache.Count}]");
                         }, _preloadToken.Token);
                     }
                 }

             }, _preloadToken.Token);

            return _preloadTask;
        }



        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        private bool _disposed;
        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            if (disposing)
            {
                _preloadToken?.Cancel();
                _preloadToken?.Dispose();

                _codec?.Dispose();
                _codec = null;
#if AVALONIA
                foreach (var cache in _frameCache.Values.ToArray())
                {
                    cache?.Dispose();
                }
#endif
                _frameCache.Clear();
            }
            _disposed = true;
        }
    }
}
