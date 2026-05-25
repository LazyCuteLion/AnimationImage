using AnimationImage.Core;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System;

#if WPF
namespace AnimationImage.WPF
#endif
#if AVALONIA
namespace AnimationImage.Avalonia
#endif
{
    /// <summary>
    /// 基于 SkiaSharp.SKCodec 的动画
    /// </summary>
    /// <remarks>
    /// <para>注意：</para>
    /// <list type="bullet">
    ///   <item>启用全量帧缓存，内存占用会很大，但可以极大提高帧率，很好的支持进度条控制、反向播放</item>
    /// </list>
    /// </remarks>
    internal class SKCodecBitmap : AnimatableBitmap
    {
        #region 字段
        private readonly SkDecoder Decoder;
        private int FrameCount => Decoder.Codec?.FrameCount ?? 0;
        private readonly List<double> Durations = new();
        private int CurrentIndex = -1;
        private bool IsLoading = false;
        #endregion

        public SKCodecBitmap(Uri source, int preloadCount = PreloadOptions.Auto) : base(source)
        {
            //暂时先只处理本地文件
            Decoder = new SkDecoder(Stream, preloadCount);
            if (Decoder.Codec == null)
            {
                this.State = AnimationState.Error;
                return;
            }

            var duration = 0.0;
            // 计算累计时间轴（每帧的结束时间点，毫秒）
            if (FrameCount > 1)
            {
                for (int i = 0; i < FrameCount; i++)
                {
                    var info = Decoder.Codec.FrameInfo[i];
                    duration += info.Duration;
                    Durations.Add(duration);
                }
            }

            this.Metadata = new Metadata(Decoder.Codec.Info.Width,
                                         Decoder.Codec.Info.Height,
                                         duration,
                                         FrameCount,
                                         duration > 0 ? (int)(FrameCount * 1000 / duration) : 0,
                                         Decoder.Codec.RepetitionCount);

            var data = Decoder.Get(0);
            this.Frame = !data.IsEmpty ? data.Bitmap : CreateNewFrame(Metadata.PixelWidth, Metadata.PixelHeight);
            CurrentIndex = data.Index;
        }

        public override void Dispose()
        {
            Decoder.Dispose();
            Durations.Clear();
            base.Dispose();
        }

        public override bool IsAnimatable => base.IsAnimatable
                            && Decoder.Codec != null
                            && FrameCount > 0
                            && Target.IsVisible;

        /// <summary>
        /// 跳转到指定时间点（毫秒）
        /// </summary>
        /// <param name="milliseconds"></param>
        internal override void SeekTime(double milliseconds)
        {
            if (!this.IsAnimatable || IsLoading)
                return;
            var index = TimeToIndex(milliseconds);
            try
            {
                if (index < 0 || index > FrameCount - 1 || index == CurrentIndex)
                    return;

                var data = Decoder.Get(index, new FrameData(CurrentIndex, this.Frame));
                if (!data.IsEmpty && data.Bitmap != this.Frame)
                {
                    this.Frame = data.Bitmap;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"SeekTime({milliseconds}->{index})错误：{e.Message}");
            }
            finally
            {
                CurrentIndex = index;
                base.SeekTime(milliseconds);
            }

        }

        /// <summary>
        /// 将时间（毫秒）映射为帧索引
        /// </summary>
        /// <remarks>
        /// 先尝试基于当前帧的局部判断（n-1,n,n+1），若不命中则使用二分查找
        /// </remarks>
        /// <param name="milliseconds">时间（毫秒）</param>
        /// <returns>帧索引</returns>
        private int TimeToIndex(double milliseconds)
        {
            if (milliseconds == 0 || Durations.Count <= 1)
                return 0;

            // 快速判断邻近帧，减少二分查找开销
            var index = CurrentIndex > -1 ? CurrentIndex : 0;
            if (index >= Durations.Count)
                index %= Durations.Count;

            if (milliseconds < Durations[index])
            {
                if (index == 0)
                    return 0;
                if (index > 0 && milliseconds >= Durations[index - 1])
                    return index;
                if (index > 1 && milliseconds >= Durations[index - 2] && milliseconds < Durations[index - 1])
                    return index - 1;
            }
            else if (index < Durations.Count - 1 && milliseconds < Durations[index + 1])
            {
                return index + 1;
            }

            // 二分查找第一个 >= milliseconds 的位置
            index = Durations.BinarySearch(milliseconds);
            if (index < 0)
                index = ~index;
            else
                index++; // 精确匹配时，取下一个帧

            if (index >= Durations.Count)
                index = 0;

            return index;
        }

        protected override async void BeginAnimation()
        {
            if (!IsAnimatable
               || State == AnimationState.Playing
               || State == AnimationState.Error
               || IsLoading)
                return;

            if (State != AnimationState.Paused)
            {
                IsLoading = true;
                await Decoder.PreloadTask;
                IsLoading = false;
            }
            Decoder.Start();
            base.BeginAnimation();
        }

        protected override void PauseAnimation()
        {
            Decoder.Stop();
            base.PauseAnimation();
        }

        protected override void StopAnimation()
        {
            Decoder.Stop();
            base.StopAnimation();
        }
    }
}
