using SkiaSharp;
using System;

namespace AnimationImage
{
    /// <summary>
    /// SkiaSharp GRContext 的 GPU 后端抽象。
    /// 用于屏蔽不同图形 API（D3D12 / OpenGL(ES) / Vulkan / Metal）的初始化与生命周期差异。
    /// </summary>
    internal interface IGpuBackend : IDisposable
    {
        /// <summary>
        /// 与图形 API 关联的 SkiaSharp GPU 上下文。初始化失败时为 null。
        /// </summary>
        GRContext? Context { get; }

        /// <summary>
        /// 当前用于绘制的 GPU Surface，尺寸由最近一次 <see cref="TryInitialize"/> 或 <see cref="Resize"/> 决定。
        /// </summary>
        SKSurface? Surface { get; }

        /// <summary>
        /// 初始化底层图形设备并按给定尺寸创建 Surface。失败时应保证自身处于可安全 Dispose 的状态。
        /// </summary>
        /// <returns>初始化成功且 <see cref="Surface"/> 可用时返回 true。</returns>
        bool TryInitialize(SKImageInfo info);

        /// <summary>
        /// 按新的尺寸重建 Surface。若重建失败会释放整个 GPU 资源，随后 <see cref="Surface"/> 为 null。
        /// </summary>
        void Resize(SKImageInfo info);
    }
}
