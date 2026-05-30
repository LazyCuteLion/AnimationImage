using System;

namespace AnimationImage
{
    /// <summary>
    /// 初始化配置
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// PreloadCount：预加载帧数，默认自动设置，对GIF/WebP有效。预定义值查看<seealso cref="PreloadOptions"/>
    /// </item>
    /// <item>
    /// RenderScale：渲染比例（相对于渲染器，默认1.0），范围0.1~2.0。对Lottie有效，若帧率较低，可以设置小于1.0以提高帧率。
    /// </item>
    /// <item>
    /// UseGPU：启用GPU加速。对于Lottie有效，默认启用。
    /// </item>
    /// </list>
    /// </remarks>
    public record AnimatableBitmapOptions(Uri Source, int PreloadCount, double RenderScale, bool UseGPU)
    {
        public AnimatableBitmapOptions(Uri source, int? preloadCount = null, double? renderScale = null, bool? useGPU = null)
           : this(source, preloadCount ?? Default.PreloadCount, renderScale ?? Default.RenderScale, useGPU ?? Default.UseGPU) { }

        public AnimatableBitmapOptions(string path, int? preloadCount = null, double? renderScale = null, bool? useGPU = null)
            : this(new Uri(path), preloadCount ?? Default.PreloadCount, renderScale ?? Default.RenderScale, useGPU ?? Default.UseGPU) { }

        public AnimatableBitmapOptions() : this(default(Uri), PreloadOptions.Auto, 1.0, true) { }

        public static AnimatableBitmapOptions Default { get; set; } = new();
    }
}