using AnimationImage.Core;
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

namespace AnimationImage.WPF
#endif

#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Vulkan;
using FrameworkElement = Avalonia.Controls.Control;

namespace AnimationImage.Avalonia
#endif
{
    public class SkottieBitmap : AnimatableBitmap
    {
        private Animation Codec;
        private SKImageInfo Info;
        /// <summary>
        /// 渲染比例(0.1,2.0]
        /// 若帧率较低，可以设置小于1.0以提高帧率
        /// </summary>
        public double RenderScale { get; private set; } = 1.0;
        public override bool IsAnimatable => base.IsAnimatable && Codec != null;

        public SkottieBitmap(AnimatableBitmapOptions options) : base(options)
        {
            Codec = Animation.Create(Stream);
            if (Codec == null)
            {
                this.State = AnimationState.Error;
                return;
            }
            RenderScale = Math.Min(2, Math.Max(0.1, options.RenderScale));
            var w = Math.Max(10, (int)(Codec.Size.Width * RenderScale));
            var h = Math.Max(10, (int)(Codec.Size.Height * RenderScale));
            Info = CreateDecodeInfo(w, h);
            this.Metadata = new Metadata((int)Codec.Size.Width,
                                        (int)Codec.Size.Height,
                                        Codec.Duration.TotalMilliseconds,
                                        (int)Math.Ceiling(Codec.Duration.Seconds * Codec.Fps),
                                        (int)Codec.Fps,
                                        0);
            this.State = AnimationState.None;
        }

        public override void AttachTarget(FrameworkElement target)
        {
            this.UpdateSize(target);
            this.Frame = CreateNewFrame(Info.Width, Info.Height);
            target.SizeChanged += OnSizeChanged;
            base.AttachTarget(target);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsDisposed)
                return;
            this.UpdateSize(sender as FrameworkElement);
            if (State != AnimationState.Playing)
                this.SeekTime(CurrentTime);
        }

        private void UpdateSize(FrameworkElement element)
        {
            if (element == null)
                return;
            var w = (double)Codec.Size.Width;
            var h = (double)Codec.Size.Height;

            //使用该方法获取控件真实可用大小，而非(ActualWidth,ActualHeight)，因为可能为0
#if WPF
            var rect = LayoutInformation.GetLayoutSlot(element);
            var width = rect.Width;
            var height = rect.Height;
#endif
#if AVALONIA
            var size = LayoutInformation.GetPreviousMeasureConstraint(element);
            var width = size?.Width ?? 0;
            var height = size?.Height ?? 0;
#endif

            if (width > 0 && height > 0)
            {
                var scaleX = width / w;
                var scaleY = height / h;
                //保持比例
                var scale = Math.Min(scaleX, scaleY);
                //计算实际渲染大小
                w *= scale;
                h *= scale;
            }

            w *= RenderScale;
            h *= RenderScale;
            Info = CreateDecodeInfo((int)Math.Max(10, w), (int)Math.Max(10, h));
        }

        internal override void SeekTime(double milliseconds)
        {
            if (!IsAnimatable)
                return;

            try
            {
                var seconds = milliseconds / 1000.0;
                Codec.SeekFrameTime(seconds);

#if AVALONIA
                var frame = this.Frame.PixelSize.Width != Info.Width || this.Frame.PixelSize.Height != Info.Height
                          ? CreateNewFrame(Info.Width, Info.Height)
                          : this.Frame;
                using (var locker = frame.Lock())
                {
                    using (var surface = SKSurface.Create(Info, locker.Address, Info.RowBytes))
                    {
                        if (surface?.Canvas != null)
                        {
                            surface.Canvas.Clear();
                            Codec.Render(surface.Canvas, Info.Rect);
                        }
                    }
                }

#endif

#if WPF
                var frame = Frame.PixelWidth != Info.Width || Frame.PixelHeight != Info.Height
                          ? CreateNewFrame(Info.Width, Info.Height)
                          : this.Frame;
                frame.Lock();
                using (var surface = SKSurface.Create(Info, frame.BackBuffer, frame.BackBufferStride))
                {
                    if (surface?.Canvas != null)
                    {
                        surface.Canvas.Clear();
                        Codec.Render(surface.Canvas, Info.Rect);
                        frame.Update();
                    }
                }
                frame.Unlock();
#endif
                this.Frame = frame;
            }
            catch
            {

            }
            finally
            {
                base.SeekTime(milliseconds);
            }

        }

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;
            if (disposing)
            {
                if (Target != null)
                    Target.SizeChanged -= OnSizeChanged;

                Codec?.Dispose();
                Codec = null;
#if AVALONIA
                this.Frame?.Dispose();
#endif
            }
            base.Dispose(disposing);
        }

    }
}
