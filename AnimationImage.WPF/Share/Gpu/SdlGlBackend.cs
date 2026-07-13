using SkiaSharp;
using System;
using System.Diagnostics;
using Silk.NET.SDL;

namespace AnimationImage
{
    /// <summary>
    /// 基于 Silk.NET.SDL 的跨平台 OpenGL / OpenGL ES GPU 后端。
    /// </summary>
    /// <remarks>
    /// 通过 SDL2 隐藏窗口 + GL 上下文提供跨平台 Skia GPU 加速：
    ///   Windows：走系统 opengl32.dll（若显式请求 GLES，SDL 会尝试加载 ANGLE 的 libEGL/libGLESv2）
    ///   Linux：走 Mesa 或厂商驱动
    ///   Android：走系统 GLES
    ///   macOS：走系统 OpenGL 2.1（弃用但仍可用）
    /// SDL2 native 二进制由 Silk.NET.SDL NuGet 通过 runtimes/ 目录自动分发，无需额外部署。
    /// </remarks>
    internal sealed unsafe class SdlGlBackend : IGpuBackend
    {
        private Sdl? _sdl;
        private Window* _window;
        private void* _glContext;
        private bool _videoInitedByUs;

        public GRContext? Context { get; private set; }
        public SKSurface? Surface { get; private set; }

        /// <summary>
        /// 在不触发异常的前提下探测 Silk.NET.SDL native 是否可加载。结果缓存一次。
        /// </summary>
        public static bool IsAvailable { get; } = ProbeSdl();

        private static bool ProbeSdl()
        {
            try
            {
                // Sdl.GetApi() 会触发 native SDL2 加载；任何加载失败都视为不可用
                var sdl = Sdl.GetApi();
                return sdl != null;
            }
            catch
            {
                return false;
            }
        }

        public bool TryInitialize(SKImageInfo info)
        {
            if (!IsAvailable)
                return false;

            try
            {
                _sdl = Sdl.GetApi();

                // 只初始化 Video 子系统；若已被外部初始化则复用
                if (_sdl.WasInit(Sdl.InitVideo) == 0)
                {
                    if (_sdl.InitSubSystem(Sdl.InitVideo) != 0)
                    {
                        Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} SDL InitSubSystem 失败：{_sdl.GetErrorS()}");
                        return false;
                    }
                    _videoInitedByUs = true;
                }

                // 请求 8-8-8-8 位色 + 双缓冲
                _sdl.GLSetAttribute(GLattr.RedSize, 8);
                _sdl.GLSetAttribute(GLattr.GreenSize, 8);
                _sdl.GLSetAttribute(GLattr.BlueSize, 8);
                _sdl.GLSetAttribute(GLattr.AlphaSize, 8);
                _sdl.GLSetAttribute(GLattr.Doublebuffer, 1);

                // 创建 1×1 隐藏 GL 窗口作为默认表面
                const uint flags = (uint)(WindowFlags.Hidden | WindowFlags.Opengl);
                _window = _sdl.CreateWindow(
                    "AnimationImage.GL",
                    Sdl.WindowposUndefined,
                    Sdl.WindowposUndefined,
                    1, 1, flags);

                if (_window == null)
                {
                    Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} SDL CreateWindow 失败：{_sdl.GetErrorS()}");
                    return false;
                }

                _glContext = _sdl.GLCreateContext(_window);
                if (_glContext == null)
                {
                    Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} SDL GLCreateContext 失败：{_sdl.GetErrorS()}");
                    return false;
                }

                if (_sdl.GLMakeCurrent(_window, _glContext) != 0)
                {
                    Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} SDL GLMakeCurrent 失败：{_sdl.GetErrorS()}");
                    return false;
                }

                // 用 SDL 的 GetProcAddress 作为装载器搭建 Skia GR 接口
                using var glInterface = GRGlInterface.CreateOpenGl(name =>
                {
                    var sdl = _sdl;
                    if (sdl == null) return IntPtr.Zero;
                    return (IntPtr)sdl.GLGetProcAddress(name);
                });
                if (glInterface == null)
                    return false;

                Context = GRContext.CreateGl(glInterface);
                if (Context == null)
                    return false;

                Surface = SKSurface.Create(Context, false, info);
                return Surface != null;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} SDL GL GPU 初始化失败：{e.Message}");
                DisposeInternal();
                return false;
            }
        }

        public void Resize(SKImageInfo info)
        {
            Surface?.Dispose();
            Surface = null;
            if (Context == null)
                return;

            Surface = SKSurface.Create(Context, false, info);
            if (Surface == null)
            {
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} GPU Surface 重建失败（SDL/GL），释放 GPU 资源");
                DisposeInternal();
            }
        }

        public void Dispose() => DisposeInternal();

        private void DisposeInternal()
        {
            // 先释放 Skia GPU 资源
            Surface?.Dispose();
            Surface = null;
            Context?.Dispose();
            Context = null;

            if (_sdl != null)
            {
                try
                {
                    if (_glContext != null)
                    {
                        _sdl.GLDeleteContext(_glContext);
                        _glContext = null;
                    }
                }
                catch { }

                try
                {
                    if (_window != null)
                    {
                        _sdl.DestroyWindow(_window);
                        _window = null;
                    }
                }
                catch { }

                try
                {
                    if (_videoInitedByUs)
                    {
                        _sdl.QuitSubSystem(Sdl.InitVideo);
                        _videoInitedByUs = false;
                    }
                }
                catch { }

                _sdl = null;
            }
        }
    }
}
