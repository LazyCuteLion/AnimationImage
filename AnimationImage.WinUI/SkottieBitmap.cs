using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using SkiaSharp.Skottie;
using System;
using System.Diagnostics;

namespace AnimationImage
{
    /// <summary>
    /// Lottie 动图数据模型。矢量光栅化开销大，默认走 GPU 直渲路径
    /// （<see cref="CompositionHost.TryCreateGpu"/> 建立 D3D12 + D3D11On12 + Skia GRContext + Win2D 管线），
    /// GPU 初始化失败自动回退到 CPU：Skia SKSurface 绑到 <see cref="CompositionHost.PixelAddress"/> BackBuffer，
    /// 再由 <see cref="CompositionHost.Present"/> 提交合成层。
    /// </summary>
    internal partial class SkottieBitmap : AnimatableBitmap
    {
        private readonly bool _useGpu;
        private Animation? _skottie;
        private SKImageInfo _info;
        private bool _disposed;
        private int _lastFrameIndex = -1;   // 上次真正渲染的 Lottie 帧号，用于同帧号短路

        public override bool IsAnimatable => base.IsAnimatable && _skottie != null;

        public SkottieBitmap(AnimatableBitmapOptions options) : base(options)
        {
            _useGpu = options.UseGPU;
            _skottie = Animation.Create(_stream);
            if (_skottie == null)
            {
                State = AnimationState.Error;
                return;
            }
            Metadata = new Metadata((int)_skottie.Size.Width,
                (int)_skottie.Size.Height,
                _skottie.Duration.TotalMilliseconds,
                (int)Math.Ceiling(_skottie.Duration.TotalSeconds * _skottie.Fps),
                (int)_skottie.Fps,
                0);
            UpdateSize(null);
        }

        /// <summary>
        /// 挂载时机是初始化 GPU host 的唯一窗口：
        /// <see cref="Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread"/> 必须在 UI 线程调用，
        /// 而 AttachTarget 只会在 UI 线程执行。
        /// </summary>
        public override void AttachTarget(FrameworkElement target)
        {
            UpdateSize(target);

            if (_useGpu && _host == null && _skottie != null)
            {
                var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
                _host = CompositionHost.TryCreateGpu(compositor, _info.Width, _info.Height);
            }

            target.SizeChanged += OnSizeChanged;
            base.AttachTarget(target);
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_disposed) return;
            UpdateSize(Target);
            if (State != AnimationState.Playing)
                SeekTime(CurrentTime);
        }

        /// <summary>
        /// 根据 target 可用尺寸计算 <see cref="_info"/>；Composition Visual 可任意缩放，
        /// 只需保证 raster 尺寸不至于过小/过大失真。
        /// </summary>
        private void UpdateSize(FrameworkElement? target)
        {
            if (_skottie == null) return;

            var w = (double)_skottie.Size.Width;
            var h = (double)_skottie.Size.Height;

            if (target != null)
            {
                var availW = target.ActualWidth > 0 ? target.ActualWidth : w;
                var availH = target.ActualHeight > 0 ? target.ActualHeight : h;
                if (availW != w || availH != h)
                {
                    var scaleX = Math.Ceiling(availW) / w;
                    var scaleY = Math.Ceiling(availH) / h;
                    var scale = Math.Min(scaleX, scaleY);
                    if (scale <= 0) scale = Math.Max(scaleX, scaleY);
                    w *= scale;
                    h *= scale;
                }
            }

            var width = (int)Math.Max(32, Math.Ceiling(Math.Round(w, 1)));
            var height = (int)Math.Max(32, Math.Ceiling(Math.Round(h, 1)));

            if (_info.Width != width || _info.Height != height)
            {
                _info = CreateDecodeInfo(width, height);
                _host?.EnsureSize(width, height);
                _lastFrameIndex = -1;   // 尺寸变化：即使帧号不变也得重绘
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Skottie 设置大小：{_info.Size}");
            }
        }

        private void Draw(SKCanvas canvas, SKSize size)
        {
            _skottie!.Render(canvas, new SKRect(0, 0, size.Width, size.Height));
        }

        internal override void SeekTime(double milliseconds)
        {
#if DEBUG
            var st = Stopwatch.StartNew();
#endif
            try
            {
                if (_disposed || !IsAnimatable || _host == null) return;

                var seconds = milliseconds / 1000.0;

                // 帧号去重：显示器刷新率通常远高于 Lottie 内部 fps（如 144Hz vs 30fps），
                // 若本次 tick 落在与上次相同的 Lottie 帧号，跳过整个矢量光栅化+GPU 提交管线。
                var fps = _skottie!.Fps;
                var frameIdx = fps > 0 ? (int)(seconds * fps) : 0;
                if (frameIdx == _lastFrameIndex) return;
                _lastFrameIndex = frameIdx;

                _skottie.SeekFrameTime(seconds);

                if (_host.IsGpu)
                {
                    _host.Render(Draw);
                }
                else
                {
                    _host.EnsureSize(_info.Width, _info.Height);
                    var addr = _host.PixelAddress;
                    if (addr == IntPtr.Zero) return;
                    using (var surface = SKSurface.Create(_info, addr, _host.RowBytes))
                    {
                        if (surface == null) return;
                        surface.Canvas.Clear();
                        _skottie.Render(surface.Canvas, _info.Rect);
                        surface.Flush();
                    }
                    _host.Present();
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Lottie 渲染错误：{e.Message}");
            }
            finally
            {
                base.SeekTime(milliseconds);
#if DEBUG
                st.Stop();
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Lottie 渲染耗时：{st.ElapsedMilliseconds}");
#endif
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                if (Target != null)
                    Target.SizeChanged -= OnSizeChanged;
                _skottie?.Dispose();
                _skottie = null;
            }
            base.Dispose(disposing);
        }
    }
}
