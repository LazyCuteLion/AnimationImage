using AnimationImage.Core;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System;

#if WPF
using System.Windows.Input;
namespace AnimationImage.WPF
#endif
#if AVALONIA
using Avalonia.Input;
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
        private readonly SkDecoder _decoder;
        private int FrameCount => _decoder.Codec?.FrameCount ?? 0;
        private readonly List<double> _durations = new();
        private int _currentIndex = -1;
        private bool _isLoading = false;
        #endregion

        public SKCodecBitmap(AnimatableBitmapOptions options) : base(options)
        {
            //暂时先只处理本地文件
            _decoder = new SkDecoder(_stream, options.PreloadCount);
            if (_decoder.Codec == null)
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
                    var info = _decoder.Codec.FrameInfo[i];
                    duration += info.Duration;
                    _durations.Add(duration);
                }
            }

            this.Metadata = new Metadata(_decoder.Codec.Info.Width,
                                         _decoder.Codec.Info.Height,
                                         duration,
                                         FrameCount,
                                         duration > 0 ? (int)(FrameCount * 1000 / duration) : 0,
                                         _decoder.Codec.RepetitionCount);

            var data = _decoder.Get(0);
            this.Frame = !data.IsEmpty ? data.Bitmap : CreateNewFrame(Metadata.PixelWidth, Metadata.PixelHeight);
            _currentIndex = data.Index;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            if (disposing)
            {
                _decoder.Dispose();
                _durations.Clear();
            }
            base.Dispose(disposing);
        }

        public override bool IsAnimatable => base.IsAnimatable
                            && _decoder.Codec != null
                            && FrameCount > 0
                            && !_isLoading;

        /// <summary>
        /// 跳转到指定时间点（毫秒）
        /// </summary>
        /// <param name="milliseconds"></param>
        internal override void SeekTime(double milliseconds)
        {
            if (!this.IsAnimatable || _isLoading)
                return;
            var index = TimeToIndex(milliseconds);
            try
            {
                if (index < 0 || index > FrameCount - 1 || index == _currentIndex)
                    return;

                var data = _decoder.Get(index, new FrameData(_currentIndex, this.Frame));
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
                _currentIndex = index;
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

        protected override async void BeginAnimation()
        {
            if (!IsAnimatable
               || State == AnimationState.Playing
               || State == AnimationState.Error
               || _isLoading)
                return;

            if (State != AnimationState.Paused)
            {
                _isLoading = true;
                this.UpdateCommandState();
                await _decoder.PreloadAsync();
                _isLoading = false;
                this.UpdateCommandState();
            }
            base.BeginAnimation();
        }
    }
}
