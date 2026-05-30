using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AnimationImage
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

        public static ILockedFramebuffer LockScope(this WriteableBitmap bitmap) => bitmap.Lock();

        public static bool EqualsSize(this WriteableBitmap bitmap, int width, int height)
        {
            return bitmap.PixelSize.Width == width && bitmap.PixelSize.Height == height;
        }

        public static bool EqualsSize(this WriteableBitmap bitmap, SKSizeI size)
        {
            return bitmap.PixelSize.Width == size.Width && bitmap.PixelSize.Height == size.Height;
        }

        public static WriteableBitmap TryFreeze(this WriteableBitmap bitmap) => bitmap;

        public static WriteableBitmap SafeClone(this WriteableBitmap source)
        {
            var clone = new WriteableBitmap(
                source.PixelSize,
                source.Dpi,
                source.Format,
                source.AlphaFormat);

            using (var srcLock = source.Lock())
            using (var dstLock = clone.Lock())
            {
                var len = srcLock.RowBytes * srcLock.Size.Height;
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

        public static void Update(this ILockedFramebuffer b, SKRectI rect) { }

        public static void CopyTo(this WriteableBitmap source, WriteableBitmap to)
        {
            using (var srcLock = source.Lock())
            using (var dstLock = to.Lock())
            {
                var len = srcLock.RowBytes * srcLock.Size.Height;
                unsafe
                {
                    Buffer.MemoryCopy(
                        srcLock.Address.ToPointer(),
                        dstLock.Address.ToPointer(),
                        len,
                        len);
                }
            }
        }

        public static WriteableBitmap ToWriteableBitmap(this SKBitmap bitmap)
        {
            var info = AnimatableBitmap.CreateDecodeInfo(bitmap.Width, bitmap.Height);
            var wb = AnimatableBitmap.CreateNewFrame(bitmap.Width, bitmap.Height);
            using (var b = wb.Lock())
            {
                using var s = bitmap.PeekPixels();
                s.ReadPixels(info, b.Address, b.RowBytes, 0, 0);
            }
            return wb;
        }

        public static SKBitmap ToSKBitmap(this WriteableBitmap wb)
        {
            var info = AnimatableBitmap.CreateDecodeInfo(wb.PixelSize.Width, wb.PixelSize.Height);
            var bitmap = new SKBitmap(info);
            using (var b = wb.Lock())
            {
                bitmap.InstallPixels(info, b.Address, b.RowBytes);
            }
            return bitmap;
        }

        public static Size GetLayoutSlot(this Control element)
        {
            var size = LayoutInformation.GetPreviousMeasureConstraint(element);
            return new Size(size?.Width ?? 0, size?.Height ?? 0);
        }

        public static bool IsInWindowViewport(this Control control)
        {
            if (control == null)
                return false;

            if (!control.IsVisible || control.Bounds.Width == 0 || control.Bounds.Height == 0)
                return false;

            var window = TopLevel.GetTopLevel(control) as Window;

            if (window == null || !window.IsVisible || window.WindowState == WindowState.Minimized)
                return false;

            // 当前元素的边界（相对于窗口）
            var elementBounds = new Rect(control.Bounds.Size);
            var topLeft = control.TranslatePoint(new Point(0, 0), window);
            if (topLeft == null)
                return false;

            elementBounds = elementBounds.WithX(topLeft.Value.X).WithY(topLeft.Value.Y);

            // 窗口的可视区域
            var windowBounds = new Rect(window.Bounds.Size);

            // 向上遍历父容器，检查是否在每个容器的可视范围内
            var ancestor = control.GetVisualParent();
            while (ancestor != null && ancestor != window)
            {
                if (ancestor is Control fe)
                {
                    if (!fe.IsVisible || fe.Bounds.Width == 0 || fe.Bounds.Height == 0)
                        return false;

                    var ancestorBounds = new Rect(fe.Bounds.Size);
                    var ancestorTopLeft = control.TranslatePoint(new Point(0, 0), fe);
                    if (ancestorTopLeft == null)
                        return false;

                    var relativeBounds = new Rect(
                        ancestorTopLeft.Value.X,
                        ancestorTopLeft.Value.Y,
                        elementBounds.Width,
                        elementBounds.Height);

                    if (!ancestorBounds.Intersects(relativeBounds))
                        return false;

                    // 更新 elementBounds 为相对于当前 ancestor 的位置
                    elementBounds = relativeBounds;
                }

                ancestor = ancestor.GetVisualParent();
            }

            // 最后检查是否在窗口范围内
            return windowBounds.Intersects(elementBounds);
        }
    }
}
