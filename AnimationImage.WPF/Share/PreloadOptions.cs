
namespace AnimationImage;

public static class PreloadOptions
{
    /// <summary>
    /// 自动计算是否需要预加载（及缓存）
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>
    /// 若文件帧率大于解码速度，则预加载一定帧数（完成后可以播放），后台持续解码并缓存。
    /// </item>
    /// <item>
    /// 若文件帧率小于解码速度，则不启用缓存。
    /// </item>
    /// </list>
    /// </remarks>
    public const int Auto = -2;
    /// <summary>
    /// 缓存全部帧，可以极大提高帧率，很好的支持进度条控制、反向播放等复杂场景。
    /// </summary>
    /// <remarks>
    /// 注意：需要执行完缓存任务才能播放，大尺寸文件要注意内存溢出。
    /// </remarks>
    public const int Full = -1;
    /// <summary>
    /// 禁用预加载/缓存，完全依赖机器性能
    /// </summary>
    public const int Disable = 0;

    //>0的值：在加载完指定量后，后台继续解码并缓存。相当于全量缓存，但是指定先加载一部分，以便动画可以更快开始播放。
}