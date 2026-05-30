using SkiaSharp;
using SkiaSharp.Skottie;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vortice.Direct3D12;
using Vortice.Direct3D;
using Vortice.DXGI;
using Vortice;
using System.Xml.Linq;


#if WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
#endif

#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Vulkan;
using FrameworkElement = Avalonia.Controls.Control;
#endif

namespace AnimationImage
{
    public partial class SkottieBitmap : AnimatableBitmap
    {
        private Animation? _animation;
        private SKImageInfo _info;
        private GRContext? _gpuContext;
        private SKSurface? _gpuSurface;
        private double _renderScale;

        public override bool IsAnimatable => base.IsAnimatable && _animation != null;

        public SkottieBitmap(AnimatableBitmapOptions options) : base(options)
        {
            _animation = Animation.Create(_stream);
            if (_animation == null)
            {
                this.State = AnimationState.Error;
                return;
            }
            _renderScale = Math.Min(2, Math.Max(0.1, options.RenderScale));
            this.Metadata = new Metadata((int)_animation.Size.Width,
                                        (int)_animation.Size.Height,
                                        _animation.Duration.TotalMilliseconds,
                                        (int)Math.Ceiling(_animation.Duration.Seconds * _animation.Fps),
                                        (int)_animation.Fps,
                                        0);
            this.State = AnimationState.None;
            if (options.UseGPU)
                this.TryUseGPU();
        }

        public override void AttachTarget(FrameworkElement target)
        {
            //初始化时，获取控件的可用大小，而非其被分配的大小
            this.UpdateSize(target.GetLayoutSlot());
            this.Frame = CreateNewFrame(_info.Width, _info.Height);
            target.SizeChanged += OnSizeChanged;
            base.AttachTarget(target);
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_disposed)
                return;
            this.UpdateSize(e.NewSize);
            if (State != AnimationState.Playing)
                this.SeekTime(CurrentTime);
        }

        private void UpdateSize(Size size)
        {
            if (_animation == null)
                return;

            var w = (double)_animation.Size.Width;
            var h = (double)_animation.Size.Height;

            if (size.Width != w || size.Height != h)
            {
                var scaleX = size.Width / w;
                var scaleY = size.Height / h;
                //保持比例
                var scale = Math.Min(scaleX, scaleY);
                if (scale == 0)
                    scale = Math.Max(scaleX, scaleY);
                //等比例计算宽高
                w *= scale;
                h *= scale;
            }

            //计算相对于“渲染器”的宽高
            w *= _renderScale;
            h *= _renderScale;

            //限制不要过小
            var width = (int)Math.Ceiling(Math.Max(32, w));
            var height = (int)Math.Ceiling(Math.Max(32, h));

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
            var st = Stopwatch.StartNew();
            try
            {
                if (!IsAnimatable)
                    return;
                var seconds = milliseconds / 1000.0;
                _animation!.SeekFrameTime(seconds);

                var frame = !this.Frame.EqualsSize(_info.Width, _info.Height)
                            ? CreateNewFrame(_info.Width, _info.Height)
                            : this.Frame;

                if (_gpuSurface != null)
                {
                    _gpuSurface.Canvas.Clear();
                    _animation.Render(_gpuSurface.Canvas, _info.Rect);//渲染后，需要把数据从GPU复制到CPU
                    _gpuSurface.Flush();
                    using var locker = frame.LockScope();
                    _gpuSurface.ReadPixels(_info, locker.Address, locker.RowBytes, 0, 0);//复制像素，1080x700复杂动画，耗时10~15
                }
                else
                {
                    using var locker = frame.LockScope();
                    using var surface = SKSurface.Create(_info, locker.Address, locker.RowBytes);
                    surface.Canvas.Clear();
                    _animation.Render(surface.Canvas, _info.Rect);
                }

                this.Frame = frame;
            }
            catch
            {

            }
            finally
            {
                base.SeekTime(milliseconds);
                st.Stop();
                Debug.WriteLine($"渲染耗时：{st.ElapsedMilliseconds}");
            }

        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            if (disposing)
            {
                if (Target != null)
                    Target.SizeChanged -= OnSizeChanged;

                _animation?.Dispose();
                _animation = null;

                _gpuContext?.Dispose();
                _gpuContext = null;

                _gpuSurface?.Dispose();
                _gpuSurface = null;

#if AVALONIA
                this.Frame?.Dispose();
#endif
            }
            base.Dispose(disposing);
        }

        private void TryUseGPU()
        {
            if (D3D12.D3D12CreateDevice(null, FeatureLevel.Level_12_0, out ID3D12Device? device).Failure)
                return;

            using var dxgiFactory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            if (dxgiFactory.EnumAdapterByLuid<IDXGIAdapter1>(Luid.FromInt64(device!.AdapterLuid), out var adapter).Failure)
                return;

            var queueDesc = new CommandQueueDescription(CommandListType.Direct);
            using var commandQueue = device.CreateCommandQueue(queueDesc);

            using var backendContext = new GRD3DBackendContext()
            {
                Device = device.NativePointer,
                Adapter = adapter!.NativePointer,
                Queue = commandQueue.NativePointer,
            };

            //利用GPU加速
            _gpuContext = GRContext.CreateDirect3D(backendContext);
            _gpuSurface = SKSurface.Create(_gpuContext, false, _info);

            adapter?.Dispose();
            device?.Dispose();
        }
    }
}
