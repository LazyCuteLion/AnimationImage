using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Composition;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AnimationImage
{
    /// <summary>
    /// 位图呈现宿主。持有一块 pinned BGRA8 像素缓冲（BackBuffer），并通过 Win2D
    /// <see cref="CanvasBitmap"/> 桥接到 <see cref="CompositionDrawingSurface"/>，
    /// 再由 <see cref="SpriteVisual"/> + <see cref="CompositionSurfaceBrush"/> 呈现到宿主元素。
    /// <para>
    /// 上层通过 <see cref="PixelAddress"/> / <see cref="RowBytes"/> 直接将解码或渲染结果写入
    /// BackBuffer（语义类似 WPF <c>WriteableBitmap.BackBuffer</c>），写入完成后调用 <see cref="Present"/>
    /// 提交到合成层；每帧只发生一次不可避免的 D3D staging memcpy（<c>CanvasBitmap.SetPixelBytes</c> 内部）。
    /// </para>
    /// <para>
    /// 本类为 <see langword="partial"/>：<c>CompositionHost.Gpu.cs</c> 承载 GPU 直渲分支
    /// （D3D12 + D3D11On12 + Skia GRContext + Win2D wrap），由 <see cref="TryCreateGpu"/> 工厂创建，
    /// <see cref="IsGpu"/> 标志区分。BackBuffer / Present / Upload 语义仅在 CPU 模式下有效。
    /// </para>
    /// </summary>
    internal sealed partial class CompositionHost : IDisposable
    {
        // === 共享字段 ===
        private readonly Compositor _compositor;
        private CompositionDrawingSurface _drawingSurface = null!;
        private CompositionSurfaceBrush _brush = null!;
        private SpriteVisual _visual = null!;
        private int _pixelW, _pixelH;
        private bool _disposed;

        private CanvasDevice? _canvasDevice;
        private CompositionGraphicsDevice? _graphicsDevice;
        private CanvasBitmap? _cache;
        private byte[]? _pixelBuffer;
        private GCHandle _pixelHandle;
        private IntPtr _pixelAddress;

        /// <summary>是否为 GPU 直渲模式（由静态工厂 <see cref="TryCreateGpu"/> 创建时置 true）。</summary>
        public bool IsGpu { get; private set; }

        /// <summary>合成 Visual，供外部通过 <c>ElementCompositionPreview.SetElementChildVisual</c> 挂载。</summary>
        public Visual Visual => _visual;

        /// <summary>BackBuffer 首地址；上层可直接向此地址写入 BGRA8 Premul 像素。GPU 模式下为 <see cref="IntPtr.Zero"/>。</summary>
        public IntPtr PixelAddress => _pixelAddress;

        /// <summary>BackBuffer 行字节数（= <see cref="PixelWidth"/> × 4，无对齐填充）。</summary>
        public int RowBytes => _pixelW * 4;

        /// <summary>源像素宽度。</summary>
        public int PixelWidth => _pixelW;

        /// <summary>源像素高度。</summary>
        public int PixelHeight => _pixelH;

        /// <summary>构造 CPU 分支实例：使用共享 <see cref="CanvasDevice"/>，创建 Composition 呈现骨架。</summary>
        public CompositionHost(int width, int height)
        {
            _pixelW = Math.Max(1, width);
            _pixelH = Math.Max(1, height);

            _compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
            _canvasDevice = CanvasDevice.GetSharedDevice();
            _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(_compositor, _canvasDevice);

            InitCompositionSkeleton(_pixelW, _pixelH);
            EnsureBackBuffer(_pixelW, _pixelH);
        }

        /// <summary>GPU 分支专用私有构造（仅 <see cref="TryCreateGpu"/> 调用，不初始化 CPU 端 CanvasDevice/BackBuffer）。</summary>
        private CompositionHost(Compositor compositor)
        {
            _compositor = compositor;
            IsGpu = true;
        }

        /// <summary>创建 DrawingSurface + SurfaceBrush + SpriteVisual 骨架。调用前需确保 _graphicsDevice 已初始化。</summary>
        private void InitCompositionSkeleton(int width, int height)
        {
            _drawingSurface = _graphicsDevice!.CreateDrawingSurface(
                new Windows.Foundation.Size(width, height),
                Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);

            _brush = _compositor.CreateSurfaceBrush(_drawingSurface);
            _brush.Stretch = CompositionStretch.Uniform;
            _brush.HorizontalAlignmentRatio = 0.5f;
            _brush.VerticalAlignmentRatio = 0.5f;

            _visual = _compositor.CreateSpriteVisual();
            _visual.Brush = _brush;
        }

        /// <summary>设置 Visual 的 UI 尺寸（DIP）。Composition 层按 <see cref="CompositionStretch"/> 自动缩放。</summary>
        public void SetVisualSize(float w, float h)
        {
            if (_disposed) return;
            _visual.Size = new Vector2(Math.Max(1, w), Math.Max(1, h));
        }

        /// <summary>确保 BackBuffer / CanvasBitmap / DrawingSurface 匹配指定源像素尺寸。</summary>
        public void EnsureSize(int width, int height)
        {
            if (_disposed) return;
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (_pixelW == width && _pixelH == height && (IsGpu || _pixelBuffer != null)) return;

            _pixelW = width;
            _pixelH = height;

            CanvasComposition.Resize(_drawingSurface, new Windows.Foundation.Size(width, height));

            if (IsGpu)
            {
                ResizeGpu(width, height);
                return;
            }

            _cache?.Dispose();
            _cache = null;
            EnsureBackBuffer(width, height);
        }

        /// <summary>映射 Xaml <see cref="Microsoft.UI.Xaml.Media.Stretch"/> 到 Composition。</summary>
        public void SetStretch(Microsoft.UI.Xaml.Media.Stretch s)
        {
            if (_disposed) return;
            _brush.Stretch = s switch
            {
                Microsoft.UI.Xaml.Media.Stretch.None => CompositionStretch.None,
                Microsoft.UI.Xaml.Media.Stretch.Fill => CompositionStretch.Fill,
                Microsoft.UI.Xaml.Media.Stretch.UniformToFill => CompositionStretch.UniformToFill,
                _ => CompositionStretch.Uniform,
            };
        }

        /// <summary>把 BackBuffer 当前内容提交到 <see cref="CompositionDrawingSurface"/>（CPU 模式 / Tier 2 内部调用）。</summary>
        public void Present()
        {
            if (_disposed) return;
            if (IsGpu && !_gpuTier2) throw new InvalidOperationException("GPU 模式请调用 Render(Action) 直接绘制到 SKCanvas。");
            PresentCore();
        }

        /// <summary>BackBuffer → CanvasBitmap.SetPixelBytes → DrawingSession → CompositionDrawingSurface。</summary>
        private void PresentCore()
        {
            if (_pixelBuffer == null) return;

            EnsureBitmapCache(_pixelW, _pixelH);
            _cache!.SetPixelBytes(_pixelBuffer);
            using var session = CanvasComposition.CreateDrawingSession(_drawingSurface);
            session.Clear(Colors.Transparent);
            session.DrawImage(_cache);
        }

        /// <summary>(re)分配 pinned BackBuffer。首次或尺寸变化时调用。</summary>
        private void EnsureBackBuffer(int width, int height)
        {
            int size = width * height * 4;
            if (_pixelBuffer != null && _pixelBuffer.Length == size) return;

            if (_pixelHandle.IsAllocated) _pixelHandle.Free();
            _pixelBuffer = new byte[size];
            _pixelHandle = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);
            _pixelAddress = _pixelHandle.AddrOfPinnedObject();
        }

        /// <summary>(re)创建与 BackBuffer 匹配的 CanvasBitmap 缓存（用于每帧 SetPixelBytes 上传）。</summary>
        private void EnsureBitmapCache(int width, int height)
        {
            if (_cache != null
                && (int)_cache.SizeInPixels.Width == width
                && (int)_cache.SizeInPixels.Height == height)
                return;

            _cache?.Dispose();
            _cache = CanvasBitmap.CreateFromBytes(_canvasDevice!,
                _pixelBuffer!, width, height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // GPU 资源优先释放（其内部 Flush + Submit(sync) 保证命令回零后再释放 D3D 对象）
            DisposeGpuResources();
            DisposeCpuResources();

            try { _brush?.Dispose(); } catch { }
            try { _drawingSurface?.Dispose(); } catch { }
            try { _visual?.Dispose(); } catch { }
        }

        private void DisposeCpuResources()
        {
            try { _cache?.Dispose(); } catch { }
            _cache = null;
            // Tier 1 的 _graphicsDevice/_canvasDevice 已在 DisposeGpuResources 中释放；
            // CPU/Tier 2 的 _graphicsDevice 需要释放，_canvasDevice 是共享的不释放。
            try { _graphicsDevice?.Dispose(); } catch { }
            _graphicsDevice = null;
            _canvasDevice = null;

            if (_pixelHandle.IsAllocated) _pixelHandle.Free();
            _pixelBuffer = null;
            _pixelAddress = IntPtr.Zero;
        }

        /// <summary>GPU 分部实现；无 GPU 资源时为空实现。</summary>
        partial void DisposeGpuResources();

        /// <summary>GPU 分部实现；CPU 模式下不会被调用（<see cref="EnsureSize"/> 有 IsGpu 分支）。</summary>
        partial void ResizeGpu(int width, int height);
    }
}
