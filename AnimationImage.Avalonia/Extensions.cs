using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        /// <summary>
        /// 异步等待Loaded事件
        /// </summary>
        /// <returns>
        /// <list type="bullet">
        /// <item>
        /// true ：表示在此之前IsLoaded=false，已等待Loaded事件
        /// </item>
        /// <item>
        /// false：表示在此之前IsLoaded=true，没有等待立即返回
        /// </item>
        /// </list>
        /// </returns>
        public static Task<bool> WaitForLoadedAsync(this Control element)
        {
            var tcs = new TaskCompletionSource<bool>();

            if (element.IsLoaded)
            {
                // 已经加载完成，直接返回
                tcs.SetResult(false);
            }
            else
            {
                // 等待 Loaded 事件
                EventHandler<RoutedEventArgs>? handler = null;
                handler = (s, e) =>
                {
                    element.Loaded -= handler;
                    tcs.SetResult(true);
                };
                element.Loaded += handler;
            }

            return tcs.Task;
        }

        public static WriteableBitmap TryFreeze(this WriteableBitmap bitmap)
        {
            //空方法，方便与WPF共享代码
            return bitmap;
        }

        public static WriteableBitmap SafeClone(this WriteableBitmap bitmap)
        {
            //空方法，方便与WPF共享代码
            return bitmap;
        }

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
