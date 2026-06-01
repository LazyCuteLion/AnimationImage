using SkiaSharp;
using SkiaSharp.Skottie;
using System;
using System.Diagnostics;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

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
        private GRContext? _gpuContext;
        private SKSurface? _gpuSurface;
        private ID3D12Device? _d3dDevice;
        private IDXGIAdapter1? _dxgiAdapter;
        private ID3D12CommandQueue? _commandQueue;

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
                (int)Math.Ceiling(_animation.Duration.Seconds * _animation.Fps),
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
                _gpuSurface?.Dispose();
                _gpuSurface = null;
                if (_gpuContext != null)
                    _gpuSurface = SKSurface.Create(_gpuContext, false, _info);
                Debug.WriteLine($"设置大小：{_info.Size}");
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

                if (_gpuSurface != null)
                {
                    _gpuSurface.Canvas.Clear();
                    _animation.Render(_gpuSurface.Canvas, _info.Rect);
                    _gpuSurface.Flush();
                    using var locker = frame.LockScope();
                    // 渲染后，需要把数据从GPU复制到CPU
                    _gpuSurface.ReadPixels(_info, locker.Address, locker.RowBytes, 0, 0);
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
                Debug.WriteLine($"Lottie渲染错误：{e.Message}");
            }
            finally
            {
                base.SeekTime(milliseconds);
#if DEBUG
                st.Stop();
                Debug.WriteLine($"渲染耗时：{st.ElapsedMilliseconds}");
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

                // 先释放 Skia GPU 资源，再释放 D3D12 底层资源
                _gpuSurface?.Dispose();
                _gpuSurface = null;

                _gpuContext?.Dispose();
                _gpuContext = null;

                _commandQueue?.Dispose();
                _commandQueue = null;
                _dxgiAdapter?.Dispose();
                _dxgiAdapter = null;
                _d3dDevice?.Dispose();
                _d3dDevice = null;
            }
            base.Dispose(disposing);
        }

        private void TryUseGPU()
        {
            try
            {
                if (D3D12.D3D12CreateDevice(null, FeatureLevel.Level_12_0, out ID3D12Device? device).Failure)
                    return;

                using var dxgiFactory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
                if (dxgiFactory.EnumAdapterByLuid<IDXGIAdapter1>(Luid.FromInt64(device!.AdapterLuid), out var adapter).Failure)
                {
                    device?.Dispose();
                    return;
                }

                var queueDesc = new CommandQueueDescription(CommandListType.Direct);
                var commandQueue = device.CreateCommandQueue(queueDesc);

                var backendContext = new GRD3DBackendContext()
                {
                    Device = device.NativePointer,
                    Adapter = adapter!.NativePointer,
                    Queue = commandQueue.NativePointer,
                };

                _gpuContext = GRContext.CreateDirect3D(backendContext);
                _gpuSurface = SKSurface.Create(_gpuContext, false, _info);

                // 保持 D3D12 资源与 GRContext 同生命周期
                _d3dDevice = device;
                _dxgiAdapter = adapter;
                _commandQueue = commandQueue;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"GPU加速初始化失败：{e.Message}");
                _gpuSurface?.Dispose();
                _gpuSurface = null;
                _gpuContext?.Dispose();
                _gpuContext = null;
                _commandQueue?.Dispose();
                _commandQueue = null;
                _dxgiAdapter?.Dispose();
                _dxgiAdapter = null;
                _d3dDevice?.Dispose();
                _d3dDevice = null;
            }
        }
    }
}
