using System;

namespace AnimationImage
{
    /// <summary>
    /// 初始化配置
    /// </summary>
    /// <param name="UseGPU">
    /// 是否启用GPU加速。对于Lottie有效，默认启用。
    /// </param>
    /// <param name="Preload">
    /// 是否预先解析所有帧画面到内存映射文件。对GIF/WebP有效，默认启用。
    /// </param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// UseGPU：是否启用GPU加速。对于Lottie有效，默认启用。
    /// </item>
    /// <item>
    /// Preload：是否预先解析所有帧画面到内存映射文件。对GIF/WebP有效，默认启用。
    /// </item>
    /// </list>
    /// </remarks>
    public record AnimatableBitmapOptions(Uri Source, bool UseGPU, bool Preload)
    {
        public AnimatableBitmapOptions(Uri source, bool? useGPU = null, bool? preload = null)
           : this(source, useGPU ?? Default.UseGPU, preload ?? Default.Preload) { }

        public AnimatableBitmapOptions(string path, bool? useGPU = null, bool? preload = null)
            : this(new Uri(path), useGPU ?? Default.UseGPU, preload ?? Default.Preload) { }

        public AnimatableBitmapOptions() : this(default(Uri), true, true) { }

        public static AnimatableBitmapOptions Default { get; set; } = new();
    }
}