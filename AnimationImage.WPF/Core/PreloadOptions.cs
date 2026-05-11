namespace AnimationImage.Core;

internal static class PreloadOptions
{
    /// <summary>
    /// 自动计算并设置
    /// </summary>
    public const int Auto = -2;
    /// <summary>
    /// 缓存全部帧，内存占用较大，但可以极大提高帧率，很好的支持进度条控制、反向播放等复杂场景，大尺寸文件慎用
    /// </summary>
    public const int Full = -1;
    /// <summary>
    /// 禁用预加载/缓存，完全依赖机器性能
    /// </summary>
    public const int Disable = 0;
}