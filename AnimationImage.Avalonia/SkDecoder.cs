using SkiaSharp;
using System;
using System.Diagnostics;
using System.Linq;

namespace AnimationImage.Avalonia
{
    internal partial class SkDecoder 
    {
        private void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;
            if (disposing)
            {
                Codec.Dispose();
                foreach (var cache in FrameCache.Values.ToArray())
                {
                    cache?.Dispose();
                }
                FrameCache.Clear();
            }
            IsDisposed = true;
        }

        public SKBitmap CreateNewFrame(SKImageInfo info)
        {
            return new SKBitmap(info);
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
                    var codecInfo = AnimatableBitmap.CreateDecodeInfo(Codec.Info.Size.Width, Codec.Info.Size.Height);
                    var canvas = data.Bitmap?.ToSKBitmap() ?? this.CreateNewFrame(codecInfo);
                    var r = SKCodecResult.Unimplemented;
                    lock (CodecLocker)
                    {
                        r = Codec.GetPixels(codecInfo, canvas.GetPixels(), new SKCodecOptions(index, -1));
                    }
                    if (r == SKCodecResult.Success)
                        return new FrameData(index, canvas.ToWriteableBitmap());
                    else
                        return FrameData.Empty;
                }

                result = this.DecodeFrame(index, data);
                if (result.Index == -1
                 && index - data.Index > 1)
                {
                    //跳帧解码失败，尝试循环解码
                    var temp = new FrameData(data.Index, data.Bitmap);
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

            if (data.Index == FrameCount - 1)
            {
                data = new FrameData(0, data.Bitmap);
            }

            if (data.Index > index)
            {
                //回退只能从头解码，参考帧设为-1，让解码器自动处理，可能会耗时较长
                var codecInfo = AnimatableBitmap.CreateDecodeInfo(Codec.Info.Size.Width, Codec.Info.Size.Height);
                var canvas = data.Bitmap?.ToSKBitmap() ?? this.CreateNewFrame(codecInfo);
                var r = SKCodecResult.Unimplemented;
                lock (CodecLocker)
                {
                    r = Codec.GetPixels(codecInfo, canvas.GetPixels(), new SKCodecOptions(index, -1));
                }
                if (r == SKCodecResult.Success)
                    return new FrameData(index, canvas.ToWriteableBitmap());
                else
                    return FrameData.Empty;
            }
            try
            {
                var result = SKCodecResult.Unimplemented;
                var codecInfo = AnimatableBitmap.CreateDecodeInfo(Codec.Info.Size.Width, Codec.Info.Size.Height);
                var canvas = data.Bitmap?.ToSKBitmap() ?? this.CreateNewFrame(codecInfo);

                var requiredFrame = Codec.FrameInfo[index].RequiredFrame;

                //独立帧
                if (requiredFrame == -1)
                {
                    lock (CodecLocker)
                    {
                        result = Codec.GetPixels(codecInfo, canvas.GetPixels(), new SKCodecOptions(index, -1));
                    }
                }
                else
                {
                    //PriorFrame范围[RequiredFrame,index)，也就是，data.Index>=RequiredFrame,<index
                    var priorFrame = data.Index;

                    //关键帧在前，需要把画布重置到关键帧，但是没有画布历史，所以这里直接把参考帧重置为-1，让解码器自动处理
                    if (requiredFrame < priorFrame)
                        priorFrame = -1;

                    lock (CodecLocker)
                    {
                        result = Codec.GetPixels(codecInfo, canvas.GetPixels(), new SKCodecOptions(index, priorFrame));
                    }
                    //若是跳帧(index-priorFrame>1)则可能失败，在外部使用for循环进行处理
                }

                if (result == SKCodecResult.Success)
                {
                    return new FrameData(index, canvas.ToWriteableBitmap());
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[{index:000}]<-[{data.Index:000}]解码错误：{e.Message}");
            }

            return FrameData.Empty;
        }
    }

}
