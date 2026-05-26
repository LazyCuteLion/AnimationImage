using System.Text;

namespace AnimationImage.Core
{
    public record Metadata
    {
        public Metadata(int pixelWidth, int pixelHeight, double duration, int frameCount, int fps, int loopCount)
        {
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            Duration = duration;
            FrameCount = frameCount;
            FPS = fps;
            LoopCount = loopCount;
        }

        public int PixelWidth { get; }

        public int PixelHeight { get; }

        /// <summary>
        /// 时长（毫秒）
        /// </summary>
        public double Duration { get; }

        /// <summary>
        /// 总帧数
        /// </summary>
        /// <remarks>
        /// Lottie：Duration*FPS
        /// </remarks>
        public int FrameCount { get; }

        /// <summary>
        /// 动画设定的帧率
        /// </summary>
        /// <remarks>
        /// GIF、WebP：FrameCount/Duration
        /// </remarks>
        public int FPS { get; }

        /// <summary>
        /// 循环次数。
        /// -1 表示无限循环；>= 0 表示播放n+1次。
        /// </summary>
        public int LoopCount { get; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("Size:({0},{1})\r\n", PixelWidth, PixelHeight);
            sb.AppendFormat("FrameCount:{0}\r\n", FrameCount);
            sb.AppendFormat("Duration:{0:F2}(ms)\r\n", Duration);
            sb.AppendFormat("LoopCount:{0}\r\n", LoopCount == -1 ? "Forever" : (LoopCount + 1).ToString());
            sb.AppendFormat("FPS:{0}\r\n", FPS);
            return sb.ToString();
        }
    }

}
