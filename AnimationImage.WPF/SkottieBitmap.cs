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
{
    public class SkottieBitmap : AnimatableBitmap
    {
        private Animation Codec;
        public override bool IsAnimatable => base.IsAnimatable && Codec != null;

        public SkottieBitmap(Uri source) : base(source)
        {
            Codec = Animation.Create(Stream);
            if (Codec == null)
            {
                this.State = AnimationState.Error;
                return;
            }
            this.Metadata = new Metadata((int)Codec.Size.Width,
                                        (int)Codec.Size.Height,
                                        Codec.Duration.TotalMilliseconds,
                                        (int)(Codec.Duration.Seconds * Codec.Fps),
                                        (int)Codec.Fps,
                                        0);
            this.State = AnimationState.None;

        }

        public override void AttachTarget(FrameworkElement target)
        {
            base.AttachTarget(target);
            var (w, h) = GetSize(target);
            this.Frame = CreateNewFrame(w, h);
            target.SizeChanged += OnSizeChanged;
        }

        private (int, int) GetSize(FrameworkElement element)
        {
            var w = (double)Codec.Size.Width;
            var h = (double)Codec.Size.Height;
            var rect = LayoutInformation.GetLayoutSlot(element);
            if (rect.Width > w || rect.Height > h)
            {
                var scaleX = rect.Width / w;
                var scaleY = rect.Height / h;
                var scale = Math.Min(scaleX, scaleY);
                w *= scale;
                h *= scale;
            }
            return ((int)w, (int)h);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var (w, h) = GetSize(Target);
            if (Frame == null || w != Frame.PixelWidth || h != Frame.PixelHeight)
            {
                Frame = CreateNewFrame(w, h);
                if (State != AnimationState.Error && State != AnimationState.Playing)
                    this.SeekTime(CurrentTime);
            }
        }

        internal override void SeekTime(double milliseconds)
        {
            if (!IsAnimatable)
                return;
            try
            {
                var seconds = milliseconds / 1000.0;
                Codec.SeekFrameTime(seconds);
                var info = CreateDecodeInfo(this.Frame.PixelWidth, this.Frame.PixelHeight);
                var rect = new Int32Rect(0, 0, this.Frame.PixelWidth, this.Frame.PixelHeight);
                this.Frame.Lock();
                using (var surface = SKSurface.Create(info, this.Frame.BackBuffer, this.Frame.BackBufferStride))
                {
                    if (surface?.Canvas != null)
                    {
                        surface.Canvas.Clear();
                        Codec.Render(surface.Canvas, info.Rect);
                        this.Frame.AddDirtyRect(rect);
                    }
                }
                this.Frame.Unlock();
            }
            catch
            {

            }
            finally
            {
                base.SeekTime(milliseconds);
            }
        }

        public override void Dispose()
        {
            if (Target != null)
                Target.SizeChanged -= OnSizeChanged;
            Codec?.Dispose();
            Codec = null;
            base.Dispose();
        }

    }
}
