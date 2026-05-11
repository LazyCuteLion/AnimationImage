using AnimationImage.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Vulkan;
using SkiaSharp;
using SkiaSharp.Skottie;
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;


namespace AnimationImage.Avalonia
{
    internal class SkottieBitmap : AnimatableBitmap
    {
        private Animation Codec;
        private PixelSize Size;
        private SKImageInfo CodecInfo;
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

        public override void AttachTarget(Control target)
        {
            this.UpdateSize(target);
            if (Size.Width > 0 && Size.Height > 0)
            {
                this.Frame = CreateNewFrame(Size.Width, Size.Height);
            }
            target.SizeChanged += OnSizeChanged;
            base.AttachTarget(target);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            this.UpdateSize(Target);
        }

        private void UpdateSize(Control target)
        {
            if (Codec == null || target == null)
                return;

            var w = (double)Codec.Size.Width;
            var h = (double)Codec.Size.Height;
            var size = LayoutInformation.GetPreviousMeasureConstraint(target);
            if (size?.Width > w || size?.Height > h)
            {
                var scaleX = size.Value.Width / w;
                var scaleY = size.Value.Height / h;
                var scale = Math.Min(scaleX, scaleY);
                w *= scale;
                h *= scale;
            }
            this.Size = new PixelSize((int)Math.Ceiling(w), (int)Math.Ceiling(h));
            CodecInfo = CreateDecodeInfo(Size.Width, Size.Height);
        }

        internal override void SeekTime(double milliseconds)
        {
            if (!IsAnimatable)
                return;
            try
            {
                var seconds = milliseconds / 1000.0;
                Codec.SeekFrameTime(seconds);
                var frame = this.Frame.PixelSize != this.Size
                          ? CreateNewFrame(Size.Width, Size.Height)
                          : this.Frame;
                using (var locker = frame.Lock())
                {
                    using (var surface = SKSurface.Create(CodecInfo, locker.Address, CodecInfo.RowBytes))
                    {
                        if (surface?.Canvas != null)
                        {
                            surface.Canvas.Clear();
                            Codec.Render(surface.Canvas, CodecInfo.Rect);
                        }
                    }
                }
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
