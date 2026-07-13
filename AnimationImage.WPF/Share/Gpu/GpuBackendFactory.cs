using System;

namespace AnimationImage
{
    /// <summary>
    /// 根据运行时平台创建合适的 <see cref="IGpuBackend"/>。
    /// </summary>
    /// <remarks>
    /// 分派策略：
    ///   Windows       → D3D12 首选（Silk.NET.Direct3D12）；失败兜底 SDL/GL；再失败→CPU
    ///   Linux/Android → SDL/GL（Silk.NET.SDL 隐藏窗口 + OpenGL 上下文）
    ///   其它平台      → 尝试 SDL/GL；不可用时返回 null → 走 CPU 兜底
    /// </remarks>
    internal static class GpuBackendFactory
    {
        /// <summary>
        /// 创建当前平台上首选的 GPU 后端。若均不可用返回 null。
        /// </summary>
        public static IGpuBackend? Create()
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows 首选 D3D12；TryInitialize 失败时自动切换到 SDL/GL；均失败则上层落 CPU
                return SdlGlBackend.IsAvailable
                    ? new FallbackBackend(new D3D12Backend(), new SdlGlBackend())
                    : new D3D12Backend();
            }
            // Linux / Android / macOS 等均首选 SDL + GL（Silk.NET.SDL 携带 SDL2 native）
            return SdlGlBackend.IsAvailable ? new SdlGlBackend() : null;
        }
    }

    /// <summary>
    /// 后端串联包装：先尝试 primary；失败则自动切换到 fallback。
    /// </summary>
    internal sealed class FallbackBackend : IGpuBackend
    {
        private IGpuBackend? _primary;
        private IGpuBackend? _fallback;
        private IGpuBackend? _active;

        public FallbackBackend(IGpuBackend primary, IGpuBackend fallback)
        {
            _primary = primary;
            _fallback = fallback;
        }

        public SkiaSharp.GRContext? Context => _active?.Context;
        public SkiaSharp.SKSurface? Surface => _active?.Surface;

        public bool TryInitialize(SkiaSharp.SKImageInfo info)
        {
            if (_primary != null && _primary.TryInitialize(info))
            {
                _active = _primary;
                // 释放未启用的候选，避免持有多余资源
                _fallback?.Dispose();
                _fallback = null;
                return true;
            }

            _primary?.Dispose();
            _primary = null;

            if (_fallback != null && _fallback.TryInitialize(info))
            {
                _active = _fallback;
                return true;
            }

            _fallback?.Dispose();
            _fallback = null;
            return false;
        }

        public void Resize(SkiaSharp.SKImageInfo info)
        {
            _active?.Resize(info);
        }

        public void Dispose()
        {
            _active?.Dispose();
            _active = null;
            _primary?.Dispose();
            _primary = null;
            _fallback?.Dispose();
            _fallback = null;
        }
    }
}
