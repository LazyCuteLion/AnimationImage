using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace AnimationImage.Avalonia
{
    internal static class Extensions
    {
        public static WriteableBitmap Clone(this WriteableBitmap source)
        {
            var clone = new WriteableBitmap(
                source.PixelSize,
                source.Dpi,
                source.Format,
                source.AlphaFormat);

            using (var srcLock = source.Lock())
            using (var dstLock = clone.Lock())
            {
                var len = dstLock.RowBytes * srcLock.Size.Height;
                unsafe
                {
                    Buffer.MemoryCopy(
                        srcLock.Address.ToPointer(),
                        dstLock.Address.ToPointer(),
                        len,
                        len);
                }
            }

            return clone;
        }

        public static WriteableBitmap ToWriteableBitmap(this SKBitmap skBitmap)
        {
            var info = AnimatableBitmap.CreateDecodeInfo(skBitmap.Width, skBitmap.Height);
            var wb = AnimatableBitmap.CreateNewFrame(skBitmap.Width, skBitmap.Height);
            using (var fb = wb.Lock())
            {
                using var s = skBitmap.PeekPixels();
                s.ReadPixels(info, fb.Address, fb.RowBytes, 0, 0);
            }
            return wb;
        }

        public static SKBitmap ToSKBitmap(this WriteableBitmap wb)
        {
            var info = AnimatableBitmap.CreateDecodeInfo(wb.PixelSize.Width, wb.PixelSize.Height);
            var skBitmap = new SKBitmap(info);
            using (var fb = wb.Lock())
            {
                skBitmap.InstallPixels(info, fb.Address, fb.RowBytes);
            }
            return skBitmap;
        }
    }
}
