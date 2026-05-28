using System;

namespace AnimationImage.Core
{
    /// <summary>
    /// 初始化配置
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// PreloadCount：预加载帧数（默认禁用）。对GIF/WebP有效，目前仅支持全量缓存。预定义值查看<seealso cref="PreloadOptions"/>
    /// </item>
    /// <item>
    /// RenderScale：渲染比例（相对于渲染器可用大小），可设置0.1~2.0。对Lottie有效，若帧率较低，可以设置小于1.0以提高帧率。
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

        public AnimatableBitmapOptions(int preloadCount = PreloadOptions.Disable, double renderScale = 1.0, bool useGPU = true)
            : this(default(Uri), preloadCount, renderScale, useGPU) { }

        public static AnimatableBitmapOptions Default { get; set; } = new();
    }
}