using AnimationImage.Core;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AnimationImage.WPF
{
    internal record FrameData
    {
        public readonly int Index = -1;
        public readonly WriteableBitmap Bitmap = null;

        public bool IsEmpty => Index < 0;

        public FrameData() { }

        public FrameData(int index, WriteableBitmap bitmap)
        {
            this.Index = index;
            this.Bitmap = bitmap;
        }

        public static FrameData Empty = new();
    }

    internal class SkDecoder : IDisposable
    {
        private FrameData Last;
        private FrameData First;
        private readonly int FrameCount;
        public int PreloadCount { get; private set; }
        private readonly object CodecLocker = new();
        private ConcurrentQueue<FrameData> FrameQueue = new();
        private ConcurrentDictionary<int, WriteableBitmap> FrameCache = new();
        private CancellationTokenSource DecodeLoopToken;
        private CancellationTokenSource PreloadToken;
        private Task _preloadTask;
        public Task PreloadTask
        {
            get
            {
                lock (TaskLocker)
                {
                    return _preloadTask ?? Task.CompletedTask;
                }
            }
        }
        private readonly object TaskLocker = new();

        public SKCodec Codec { get; private set; }

        public SkDecoder(Stream stream, int preloadCount)
        {
            Codec = SKCodec.Create(stream);
            FrameCount = Codec.FrameCount;
            if (FrameCount == 0)
            {
                this.First = this.DecodeFrame(0, new FrameData(1, null));
            }
            else
            {
                var first = this.DecodeFrame(0, FrameData.Empty);
                if (first.IsEmpty)
                {
                    //throw new InvalidOperationException("解码失败");
                }
                else
                {
                    first.Bitmap.TryFreeze();
                    FrameQueue.Enqueue(first);
                    First = first;
                    Last = first;
                    this.TryPreload(preloadCount);
                }
            }
        }

        public WriteableBitmap CreateNewFrame()
        {
            return AnimatableBitmap.CreateNewFrame(Codec.Info.Width, Codec.Info.Height);
        }

        public FrameData Get(int index)
        {
            return this.Get(index, FrameData.Empty);
        }

        public FrameData Get(int index, FrameData data)
        {
            if (data.Index == index)
                return data;

            if (index == First.Index)
                return First;

            if (!FrameCache.IsEmpty && FrameCache.TryGetValue(index, out var bitmap))
            {
                //可能超前FrameQueue.TryDequeue后临时存入，命中后移除
                if (PreloadCount != FrameCount)
                {
                    FrameCache.TryRemove(index, out _);
                }
                return new FrameData(index, bitmap);
            }
            else if (PreloadCount == FrameCount)
            {
                //实时解码并缓存
                var result = this.Decode(index, data);
                if (!result.IsEmpty)
                {
                    result.Bitmap.TryFreeze();
                    FrameCache.TryAdd(index, result.Bitmap);
                }
            }

            //先从缓存中取，要保证缓存中的帧，总是>=index
            if (!FrameQueue.IsEmpty)
            {
                while (FrameQueue.TryDequeue(out FrameData cache))
                {
                    if (cache.Index > index)
                    {
                        //索引比目标还要大，需要放入缓存，在下次命中后再移除
                        FrameCache.TryAdd(cache.Index, cache.Bitmap);
                        break;
                    }
                    if (cache.Index == index)
                    {
                        Debug.WriteLine($"预加载剩余：{FrameQueue.Count}");
                        return cache;
                    }
                }
            }

            if (PreloadCount > 0)
            {
                Debug.WriteLine($"[{index:000}]没有命中缓存");
                return FrameData.Empty;
            }
            else
            {
                return this.Decode(index, data);
            }

        }

        private FrameData Decode(int index, FrameData data)
        {
            var st = Stopwatch.StartNew();
            var result = FrameData.Empty;
            try
            {
                if (data.Index == FrameCount - 1)
                {
                    data = new FrameData(0, data.Bitmap);
                }

                if (data.Index > index)
                {
                    //回退只能从头解码，参考帧设为-1，让解码器自动处理，可能会耗时较长
                    var canvas = data.Bitmap ?? this.CreateNewFrame();
                    var r = SKCodecResult.Unimplemented;
                    canvas.Lock();
                    lock (CodecLocker)
                    {
                        r = Codec.GetPixels(AnimatableBitmap.CreateDecodeInfo(canvas.PixelWidth, canvas.PixelHeight), canvas.BackBuffer, new SKCodecOptions(index, -1));
                    }
                    if (r == SKCodecResult.Success)
                        canvas.Update();
                    canvas.Unlock();
                    if (r == SKCodecResult.Success)
                        return new FrameData(index, canvas);
                }

                result = this.DecodeFrame(index, data);
                if (result.Index == -1
                 && index - data.Index > 1)
                {
                    //跳帧解码失败，尝试循环解码
                    var temp = new FrameData(data.Index, new WriteableBitmap(data.Bitmap));
                    for (int i = data.Index + 1; i <= index; i++)
                    {
                        if (Codec.FrameInfo[i].DisposalMethod == SKCodecAnimationDisposalMethod.Keep)
                        {
                            temp = this.DecodeFrame(i, temp);
                            if (temp.Index < 0)
                                break;
                        }
                    }
                    if (temp.Index == index)
                        result = temp;
                    Debug.WriteLine($"[{index:000}<-{data.Index:000}]跳帧解码失败，尝试逐帧解码{(result.IsEmpty ? "失败" : "成功")}，耗时：{st.ElapsedMilliseconds}");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[{index:000}]解码失败：{e.Message}");
            }
            finally
            {
                st.Stop();
                Debug.WriteLineIf(st.ElapsedMilliseconds > 20, $"[{index:000}]解码{(result.Index == index ? "成功" : "失败")}，耗时：{st.ElapsedMilliseconds}");
            }
            return result;
        }

        private FrameData DecodeFrame(int index, FrameData data)
        {
            if (data.Index == index)
                return data;

            if (index == 0 && data.Index > 0)
            {
                data = new FrameData(-1, data.Bitmap);
            }

            if (data.Index > index)
            {
                //表示回退，但这里不做处理（该方法提供给预加载/缓存使用，只考虑时间线前进）
                //return FrameData.Empty;
                var canvas = data.Bitmap ?? this.CreateNewFrame();
                var codecInfo = AnimatableBitmap.CreateDecodeInfo(Codec.Info.Size.Width, Codec.Info.Size.Height);
                var r = SKCodecResult.Unimplemented;
                canvas.Lock();
                lock (CodecLocker)
                {
                    r = Codec.GetPixels(codecInfo, canvas.BackBuffer, new SKCodecOptions(index, -1));
                }
                if (r == SKCodecResult.Success)
                    canvas.Update();
                canvas.Unlock();

                if (r == SKCodecResult.Success)
                {
                    return new FrameData(index, canvas);
                }
                else
                {
                    return FrameData.Empty;
                }
            }
            try
            {
                var result = SKCodecResult.Unimplemented;
                var canvas = data.Bitmap ?? this.CreateNewFrame();
                if (canvas.IsFrozen)
                    canvas = new WriteableBitmap(canvas);
                var codecInfo = AnimatableBitmap.CreateDecodeInfo(canvas.PixelWidth, canvas.PixelHeight);
                var requiredFrame = Codec.FrameInfo[index].RequiredFrame;

                //独立帧
                if (requiredFrame == -1)
                {
                    canvas.Lock();
                    lock (CodecLocker)
                    {
                        result = Codec.GetPixels(codecInfo, canvas.BackBuffer, new SKCodecOptions(index, -1));
                    }
                    if (result == SKCodecResult.Success)
                        canvas.Update();
                    canvas.Unlock();
                }
                else
                {
                    //PriorFrame范围[RequiredFrame,index)，也就是，data.Index>=RequiredFrame,<index
                    var priorFrame = data.Index;

                    //关键帧在前，需要把画布重置到关键帧，但是没有画布历史，所以这里直接把参考帧重置为-1，让解码器自动处理
                    if (requiredFrame < priorFrame)
                        priorFrame = -1;

                    canvas.Lock();
                    lock (CodecLocker)
                    {
                        result = Codec.GetPixels(codecInfo, canvas.BackBuffer, new SKCodecOptions(index, priorFrame));
                    }
                    //若是跳帧(index-priorFrame>1)则可能失败，在外部使用for循环进行处理
                    if (result == SKCodecResult.Success)
                        canvas.Update();
                    canvas.Unlock();
                }

                if (result == SKCodecResult.Success)
                {
                    return new FrameData(index, canvas);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[{index:000}]<-[{data.Index:000}]解码错误：{e.Message}");
            }

            return FrameData.Empty;
        }

        private void DecodeAndSave()
        {
            var n = Last.Index + 1;
            if (n >= FrameCount)
                n %= FrameCount;
            try
            {
                var data = this.DecodeFrame(n, Last);
                if (!data.IsEmpty)
                {
                    data.Bitmap.TryFreeze();
                    FrameQueue.Enqueue(data);
                    if (data.Index >= FrameCount - 1)
                        Last = FrameData.Empty;
                    else
                        Last = data;
                }
                else
                {
                    Debug.WriteLine($"[{n:000}]解码失败");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[{n:000}]解码失败：{e.Message}");
            }
        }


        private void CacheAll()
        {
            var last = 0;
            while (FrameQueue.TryDequeue(out FrameData cache))
            {
                if (PreloadToken == null || PreloadToken.IsCancellationRequested)
                    break;
                FrameCache.TryAdd(cache.Index, cache.Bitmap.TryFreeze());
                last = cache.Index;
            }
            while (FrameCache.Count < FrameCount)
            {
                if (PreloadToken == null || PreloadToken.IsCancellationRequested)
                    break;
                var temp = new FrameData(last, FrameCache[last]);
                for (var i = last + 1; i < FrameCount; i++)
                {
                    temp = this.DecodeFrame(i, temp);
                    if (!temp.IsEmpty)
                    {
                        FrameCache.TryAdd(temp.Index, temp.Bitmap.TryFreeze());
                    }
                }
            }
        }

        /**
         * 消费速度>生产速度
         * 对于无限循环，有严重缺陷：只要多循环几次，预加载量就会被消耗完！
         * 这种情况无解，只能全量缓存；或者升级CPU，提高生产速度（但若是生产速度够快，又何必预加载？🤣）
         * 只适用于播放一次的动图！
        **/
        private void TryPreload(int count)
        {
            if (count == PreloadOptions.Disable)
            {
                PreloadCount = 0;
                return;
            }

            //限定预加载量取值范围
            if (count == PreloadOptions.Full)
            {
                PreloadCount = FrameCount;
            }
            else if (count == PreloadOptions.Auto)
            {
                //自动预测，先设置初始加载量为10，记录耗时，计算解码速度
                PreloadCount = Math.Min(10, FrameCount);
            }
            else
            {
                //>0，限定不能大于总帧数
                PreloadCount = Math.Min(count, FrameCount);
            }
            PreloadToken = new CancellationTokenSource();
            //全量缓存
            if (PreloadCount == FrameCount)
            {
                _preloadTask = Task.Run(() => this.CacheAll(), PreloadToken.Token);
            }
            else if (count == PreloadOptions.Auto)
            {
                _preloadTask = Task.Run(() =>
                {
                    var loadCount = PreloadCount - FrameQueue.Count;
                    var st = Stopwatch.StartNew();
                    try
                    {
                        //初始加载，记录耗时
                        while (FrameQueue.Count < PreloadCount)
                        {
                            this.DecodeAndSave();
                            if (PreloadToken.IsCancellationRequested)
                                return;
                        }
                        st.Stop();

                        //计算解码速度
                        var cps = loadCount / st.Elapsed.TotalSeconds * 0.8;//取80%的速度
                        var duration = Codec.FrameInfo.Sum(d => d.Duration);
                        var fps = FrameCount * 1000.0 / duration;

                        //文件要求帧率大于解码速度
                        if (fps > cps)
                        {
                            var bestCount = (int)Math.Ceiling((1 - (cps / fps)) * FrameCount) + (int)(fps * 0.1);//多准备0.1秒的帧数
                            //限定最小预加载量为3
                            if (bestCount < 3)
                                bestCount = 3;
                            //机器性能太差，无法满足文件要求帧率，启用全量缓存
                            if (bestCount > FrameCount)
                            {
                                PreloadCount = FrameCount;
                                this.CacheAll();
                                return;
                            }
                            //比初始预加载量更大，继续缓存
                            else if (bestCount > PreloadCount)
                            {
                                PreloadCount = bestCount;
                                //继续加载
                                st.Start();
                                while (FrameQueue.Count < PreloadCount)
                                {
                                    this.DecodeAndSave();
                                    if (PreloadToken.IsCancellationRequested)
                                        return;
                                }
                            }
                        }
                        else
                        {
                            PreloadCount = 0;
                        }
                        //预加载量大于0，则启用后台线程，持续预加载
                        if (PreloadCount > 0)
                        {
                            this.Start();
                        }
                    }
                    finally
                    {
                        st.Stop();
                        Debug.WriteLine($"自动计算预加载量：[{PreloadCount}]，已加载：[{(FrameCache.Count > 0 ? FrameCache.Count : FrameQueue.Count)}]，耗时：{st.ElapsedMilliseconds}");
                    }

                }, PreloadToken.Token);
            }
            else if (PreloadCount > 0)
            {
                this.Start();
            }
        }

        public void Dispose()
        {
            PreloadToken?.Cancel();
            PreloadToken?.Dispose();
            DecodeLoopToken?.Cancel();
            DecodeLoopToken?.Dispose();
            Codec.Dispose();
            FrameQueue.Clear();
            FrameCache.Clear();
        }

        /// <summary>
        /// 播放暂停/停止，则停止生产
        /// </summary>
        public void Stop()
        {
            DecodeLoopToken?.Cancel();
            DecodeLoopToken?.Dispose();
            DecodeLoopToken = null;
        }

        public void Start()
        {
            if (DecodeLoopToken != null || PreloadCount == 0)
                return;
            DecodeLoopToken = new CancellationTokenSource();
            Task.Run(() =>
            {
                while (DecodeLoopToken != null && !DecodeLoopToken.IsCancellationRequested)
                {
                    //消费速度大于生产速度，猛猛干就完了，不要管消费
                    this.DecodeAndSave();
                }
            }, DecodeLoopToken.Token);
        }
    }
}
