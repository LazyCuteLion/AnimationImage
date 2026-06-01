using SkiaSharp;
using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

#if WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#endif

#if AVALONIA
using Avalonia;
using Avalonia.Layout;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using FrameworkElement = Avalonia.Controls.Control;
using Size = Avalonia.Size;
#endif

namespace AnimationImage
{
#if WPF
    public interface ILockedFramebuffer : IDisposable
    {
        IntPtr Address { get; }
        int RowBytes { get; }
        void Update(SKRectI rect);
        void Update(byte[] pixels);
        (int PixelWidth, int PixelHeight) GetPixelSize();
    }

    public sealed class WriteableBitmapLockScope : ILockedFramebuffer
    {
        private readonly WriteableBitmap _bitmap;
        private bool _disposed;
        private Int32Rect? _rect;

        public IntPtr Address { get; private set; }

        public int RowBytes { get; private set; }

        public (int PixelWidth, int PixelHeight) GetPixelSize()
        {
            return (_bitmap.PixelWidth, _bitmap.PixelHeight);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_rect.HasValue)
                _bitmap.AddDirtyRect(_rect.Value);
            _bitmap.Unlock();
            Address = IntPtr.Zero;
        }

        public WriteableBitmapLockScope(WriteableBitmap bitmap, Int32Rect? rect = null)
        {
            _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
            _rect = rect ?? new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            bitmap.Lock();
            Address = bitmap.BackBuffer;
            RowBytes = bitmap.BackBufferStride;
        }

        public void Update(SKRectI rect)
        {
            _rect = rect.ToInt32Rect();
        }

        public void Update(byte[] pixels)
        {
            Marshal.Copy(pixels, 0, Address, pixels.Length);
        }
    }
#endif

    public static class Extensions
    {
#if WPF
        public static ILockedFramebuffer LockScope(this WriteableBitmap bitmap, Int32Rect rect)
        {
            return new WriteableBitmapLockScope(bitmap, rect);
        }

        public static Int32Rect ToInt32Rect(this SKRectI rect)
        {
            return new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height);
        }
#endif

#if AVALONIA
        public static void Update(this ILockedFramebuffer b, SKRectI rect) { }

        public static (int PixelWidth, int PixelHeight) GetPixelSize(this ILockedFramebuffer b)
        {
            return (b.Size.Width, b.Size.Height);
        }
#endif

        public static ILockedFramebuffer LockScope(this WriteableBitmap bitmap)
        {
#if WPF
            return new WriteableBitmapLockScope(bitmap);
#endif
#if AVALONIA
            return bitmap.Lock();
#endif
        }

        /// <summary>
        /// 异步等待Loaded事件
        /// </summary>
        public static Task WaitForLoadedAsync(this FrameworkElement element)
        {
            if (element.IsLoaded)
                return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>();

#if WPF
            RoutedEventHandler? handler = null;
#endif

#if AVALONIA
            EventHandler<RoutedEventArgs>? handler = null;
#endif
            handler = (s, e) =>
            {
                element.Loaded -= handler;
                tcs.SetResult(true);
            };
            element.Loaded += handler;

            return tcs.Task;
        }

        public static byte[] GetPixels(this WriteableBitmap bitmap)
        {
            using var b = bitmap.LockScope();
            var size = b.RowBytes * b.GetPixelSize().PixelHeight;
            var data = new byte[size];

            try
            {
                unsafe
                {
                    fixed (byte* ptr = data)
                    {
                        Buffer.MemoryCopy(b.Address.ToPointer(), ptr, size, size);
                    }
                }
            }
            catch { }
            return data;
        }

        public static bool EqualsSize(this WriteableBitmap bitmap, int width, int height)
        {
#if WPF
            return bitmap.PixelWidth == width && bitmap.PixelHeight == height;
#endif

#if AVALONIA
            return bitmap.PixelSize.Width == width && bitmap.PixelSize.Height == height;
#endif
        }

        public static bool IsInWindowViewport(this FrameworkElement element)
        {
#if WPF
            if (element == null
                || !element.IsVisible
                || element.ActualWidth == 0
                || element.ActualHeight == 0)
                return false;

            Window window = Window.GetWindow(element);
            if (window == null) return false;

            if (window.WindowState == WindowState.Minimized || !window.IsVisible)
                return false;

            if (window.Content is FrameworkElement root)
            {
                if (!root.IsVisible || root.ActualWidth == 0 || root.ActualHeight == 0)
                    return false;
            }

            DependencyObject ancestor = VisualTreeHelper.GetParent(element);
            Rect elementBounds;

            while (ancestor != null)
            {
                if (ancestor is FrameworkElement fe)
                {
                    if (!fe.IsVisible || fe.ActualWidth == 0 || fe.ActualHeight == 0)
                        return false;

                    elementBounds = element.TransformToAncestor(fe)
                        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

                    Rect containerBounds;

                    if (ancestor is ScrollViewer sv)
                        containerBounds = new Rect(0, 0, sv.ViewportWidth, sv.ViewportHeight);
                    else
                        containerBounds = new Rect(0, 0, fe.ActualWidth, fe.ActualHeight);

                    if (!containerBounds.IntersectsWith(elementBounds))
                        return false;
                }

                ancestor = VisualTreeHelper.GetParent(ancestor);

                if (ancestor is Window)
                    break;
            }

            return true;
#endif

#if AVALONIA
            if (element == null)
                return false;

            if (!element.IsVisible || element.Bounds.Width == 0 || element.Bounds.Height == 0)
                return false;

            var window = TopLevel.GetTopLevel(element) as Window;
            if (window == null || !window.IsVisible || window.WindowState == WindowState.Minimized)
                return false;

            var elementBounds = new Rect(element.Bounds.Size);
            var topLeft = element.TranslatePoint(new Point(0, 0), window);
            if (topLeft == null)
                return false;

            elementBounds = elementBounds.WithX(topLeft.Value.X).WithY(topLeft.Value.Y);

            var windowBounds = new Rect(window.Bounds.Size);

            var ancestor = element.GetVisualParent();
            while (ancestor != null && ancestor != window)
            {
                if (ancestor is FrameworkElement fe)
                {
                    if (!fe.IsVisible || fe.Bounds.Width == 0 || fe.Bounds.Height == 0)
                        return false;

                    var ancestorBounds = new Rect(fe.Bounds.Size);
                    var ancestorTopLeft = element.TranslatePoint(new Point(0, 0), fe);
                    if (ancestorTopLeft == null)
                        return false;

                    var relativeBounds = new Rect(
                        ancestorTopLeft.Value.X,
                        ancestorTopLeft.Value.Y,
                        elementBounds.Width,
                        elementBounds.Height);

                    if (!ancestorBounds.Intersects(relativeBounds))
                        return false;

                    elementBounds = relativeBounds;
                }

                ancestor = ancestor.GetVisualParent();
            }

            return windowBounds.Intersects(elementBounds);
#endif
        }

        public static Size GetLayoutSlot(this FrameworkElement element)
        {
#if WPF
            return LayoutInformation.GetLayoutSlot(element).Size;
#endif

#if AVALONIA
            var size = LayoutInformation.GetPreviousMeasureConstraint(element);
            return new Size(size?.Width ?? 0, size?.Height ?? 0);
#endif
        }

        public static string MD5HexString(this Stream stream)
        {
            var md5 = Convert.ToHexString(MD5.HashData(stream));
            stream.Position = 0;
            return md5;
        }
    }
}
