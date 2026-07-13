using SkiaSharp;
using SkiaSharp.Skottie;
using System;
using System.Diagnostics;

#if WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#endif

#if AVALONIA
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FrameworkElement = Avalonia.Controls.Control;
using Size = Avalonia.Size;
#endif

namespace AnimationImage
{
    public partial class SkottieBitmap : AnimatableBitmap
    {
        private Animation? _skottie;
        private SKImageInfo _info;
        private IGpuBackend? _gpu;
        private int _lastFrameIndex = -1;   // 上次真正渲染的 Lottie 帧号，用于同帧号短路

        public override bool IsAnimatable => base.IsAnimatable && _skottie != null;

        public SkottieBitmap(AnimatableBitmapOptions options) : base(options)
        {
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
            if (_skottie == null)
                return;

            var w = (double)_skottie.Size.Width;
            var h = (double)_skottie.Size.Height;

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
                _lastFrameIndex = -1;   // 尺寸变化：即使帧号不变也得重绘
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

                // 帧号去重：ForceFPS 高于内容 FPS 时（如 144 vs 30）Storyboard 会按 ForceFPS 频率回调，
                // 同一帧号会被重复矢量光栅化 + GPU->CPU 回读，无任何必要。同帧直接短路。
                var fps = _skottie!.Fps;
                var frameIdx = fps > 0 ? (int)(seconds * fps) : 0;
                if (frameIdx == _lastFrameIndex) return;
                _lastFrameIndex = frameIdx;

                _skottie.SeekFrameTime(seconds);

                var frame = Frame == null || !Frame.EqualsSize(_info.Width, _info.Height)
                    ? CreateNewFrame(_info.Width, _info.Height)
                    : Frame;

                var gpuSurface = _gpu?.Surface;
                if (gpuSurface != null)
                {
                    gpuSurface.Canvas.Clear();
                    _skottie.Render(gpuSurface.Canvas, _info.Rect);
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
                    _skottie.Render(surface.Canvas, _info.Rect);
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

                _skottie?.Dispose();
                _skottie = null;

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
