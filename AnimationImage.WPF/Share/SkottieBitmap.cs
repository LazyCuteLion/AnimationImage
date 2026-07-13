using SkiaSharp;
using SkiaSharp.Skottie;
using System;
using System.Diagnostics;

#if WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
#endif

#if AVALONIA
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using FrameworkElement = Avalonia.Controls.Control;
using Size = Avalonia.Size;
#endif

namespace AnimationImage
{
    public partial class SkottieBitmap : AnimatableBitmap
    {
        private Animation? _animation;
        private SKImageInfo _info;
        private IGpuBackend? _gpu;

        public override bool IsAnimatable => base.IsAnimatable && _animation != null;

        public SkottieBitmap(AnimatableBitmapOptions options) : base(options)
        {
            _animation = Animation.Create(_stream);
            if (_animation == null)
            {
                State = AnimationState.Error;
                return;
            }
            Metadata = new Metadata((int)_animation.Size.Width,
                (int)_animation.Size.Height,
                _animation.Duration.TotalMilliseconds,
                (int)Math.Ceiling(_animation.Duration.TotalSeconds * _animation.Fps),
                (int)_animation.Fps,
                0);
            State = AnimationState.None;
            UpdateSize();
            if (options.UseGPU)
                TryUseGPU();
        }

        public override void AttachTarget(FrameworkElement target)
        {
            // 初始化时，获取控件的可用大小，而非其被分配的大小
            UpdateSize(target.GetLayoutSlot());
            Frame = CreateNewFrame(_info.Width, _info.Height);
            target.SizeChanged += OnSizeChanged;
            base.AttachTarget(target);
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_disposed)
                return;
            UpdateSize(e.NewSize);
            if (State != AnimationState.Playing)
                SeekTime(CurrentTime);
        }

        private void UpdateSize(Size? size = null)
        {
            if (_animation == null)
                return;

            var w = (double)_animation.Size.Width;
            var h = (double)_animation.Size.Height;

            if (size != null && (size.Value.Width != w || size.Value.Height != h))
            {
                var scaleX = Math.Ceiling(size.Value.Width) / w;
                var scaleY = Math.Ceiling(size.Value.Height) / h;
                // 保持比例
                var scale = Math.Min(scaleX, scaleY);
                if (scale == 0)
                    scale = Math.Max(scaleX, scaleY);
                // 等比例计算宽高
                w *= scale;
                h *= scale;
            }

            // 限制不要过小
            var width = (int)Math.Max(32, Math.Ceiling(Math.Round(w, 1)));
            var height = (int)Math.Max(32, Math.Ceiling(Math.Round(h, 1)));

            if (_info.Width != width || _info.Height != height)
            {
                _info = CreateDecodeInfo(width, height);
                _gpu?.Resize(_info);
                if (_gpu != null && _gpu.Surface == null)
                {
                    // 后端已在 Resize 内部自释放，这里同步引用
                    _gpu.Dispose();
                    _gpu = null;
                }
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} 设置大小：{_info.Size}");
            }
        }

        internal override void SeekTime(double milliseconds)
        {
#if DEBUG
            var st = Stopwatch.StartNew();
#endif
            try
            {
                if (!IsAnimatable)
                    return;
                var seconds = milliseconds / 1000.0;
                _animation!.SeekFrameTime(seconds);

                var frame = !Frame.EqualsSize(_info.Width, _info.Height)
                    ? CreateNewFrame(_info.Width, _info.Height)
                    : Frame;

                var gpuSurface = _gpu?.Surface;
                if (gpuSurface != null)
                {
                    gpuSurface.Canvas.Clear();
                    _animation.Render(gpuSurface.Canvas, _info.Rect);
                    gpuSurface.Flush();
                    using var locker = frame.LockScope();
                    // 渲染后，需要把数据从GPU复制到CPU
                    gpuSurface.ReadPixels(_info, locker.Address, locker.RowBytes, 0, 0);
                }
                else
                {
                    using var locker = frame.LockScope();
                    using var surface = SKSurface.Create(_info, locker.Address, locker.RowBytes);
                    surface.Canvas.Clear();
                    _animation.Render(surface.Canvas, _info.Rect);
                }

                Frame = frame;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Lottie渲染错误：{e.Message}");
            }
            finally
            {
                base.SeekTime(milliseconds);
#if DEBUG
                st.Stop();
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} 渲染耗时：{st.ElapsedMilliseconds}");
#endif
            }
        }

        private bool _disposed;
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (disposing)
            {
                if (Target != null)
                    Target.SizeChanged -= OnSizeChanged;

                _animation?.Dispose();
                _animation = null;

                // GPU 后端负责按正确顺序释放 Skia GPU 资源与底层图形设备
                _gpu?.Dispose();
                _gpu = null;
            }
            base.Dispose(disposing);
        }

        private void TryUseGPU()
        {
            var backend = GpuBackendFactory.Create();
            if (backend == null)
                return;

            if (backend.TryInitialize(_info))
            {
                _gpu = backend;
            }
            else
            {
                backend.Dispose();
            }
        }
    }
}
