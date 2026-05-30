using SkiaSharp;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace AnimationImage
{
    public interface ILockedFramebuffer : IDisposable
    {
        IntPtr Address { get; }
        int RowBytes { get; }
        void Update(SKRectI rect);
    }

    public sealed class WriteableBitmapLockScope : ILockedFramebuffer
    {
        private readonly WriteableBitmap _bitmap;
        private bool _disposed;
        private Int32Rect? _rect;

        public IntPtr Address { get; private set; }

        public int RowBytes { get; private set; }

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
    }

    public static class Extensions
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
        public static Task<bool> WaitForLoadedAsync(this FrameworkElement element)
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
                RoutedEventHandler? handler = null;
                handler = (s, e) =>
                {
                    element.Loaded -= handler;
                    tcs.SetResult(true);
                };
                element.Loaded += handler;
            }

            return tcs.Task;
        }

        public static ILockedFramebuffer LockScope(this WriteableBitmap bitmap)
        {
            return new WriteableBitmapLockScope(bitmap);
        }

        public static ILockedFramebuffer Lock(this WriteableBitmap bitmap, Int32Rect rect)
        {
            return new WriteableBitmapLockScope(bitmap, rect);
        }

        public static bool EqualsSize(this WriteableBitmap bitmap, int width, int height)
        {
            return bitmap.PixelWidth == width && bitmap.PixelHeight == height;
        }

        public static bool EqualsSize(this WriteableBitmap bitmap, SKSizeI size)
        {
            return bitmap.PixelWidth == size.Width && bitmap.PixelHeight == size.Height;
        }

        /// <summary>
        /// 尝试冻结图像以提高性能以及跨线程访问
        /// </summary>
        /// <returns></returns>
        public static WriteableBitmap TryFreeze(this WriteableBitmap self)
        {
            if (self?.CanFreeze == true)
                self.Freeze();
            return self;
        }

        /// <summary>
        /// 只更新1x1像素的区域，触发UI刷新，但不会引起性能问题（相较于更新整个图像）。适用于频繁更新的场景。
        /// </summary>
        /// <returns></returns>
        public static WriteableBitmap TryUpdate(this WriteableBitmap self)
        {
            self.Lock();
            self.AddDirtyRect(new Int32Rect(0, 0, 1, 1));
            self.Unlock();
            return self;
        }

        /// <summary>
        /// 标记整个图像为脏区域，使用前请确保图像已锁定（Lock）
        /// </summary>
        public static void Update(this WriteableBitmap self)
        {
            self.AddDirtyRect(new Int32Rect(0, 0, self.PixelWidth, self.PixelHeight));
        }

        public static void Update(this WriteableBitmap bitmap, SKRectI rect)
        {
            bitmap.AddDirtyRect(rect.ToInt32Rect());
        }

        /// <summary>
        /// 复制图像并返回冻结的对象（islock指示当前是否已锁定）
        /// </summary>
        /// <returns></returns>
        public static WriteableBitmap SafeClone(this WriteableBitmap self, bool islock = false)
        {
            if (!islock)
            {
                return new WriteableBitmap(self).TryFreeze();
            }
            else
            {
                var rect = new Int32Rect(0, 0, self.PixelWidth, self.PixelHeight);
                var clone = new WriteableBitmap(self.PixelWidth, self.PixelHeight, self.DpiX, self.DpiY, self.Format, self.Palette);
                clone.Lock();
                clone.WritePixels(rect, self.BackBuffer, self.PixelHeight * self.BackBufferStride, self.BackBufferStride);
                clone.Unlock();
                return clone.TryFreeze();
            }
        }

        /// <summary>
        /// 复制像素数据到目标（islock指示目标是否已锁定）
        /// </summary>
        public static bool CopyTo(this WriteableBitmap self, WriteableBitmap to, bool islock = false)
        {
            try
            {
                var rect = new Int32Rect(0, 0, self.PixelWidth, self.PixelHeight);
                var lockState = !islock && to.TryLock(TimeSpan.FromMilliseconds(30));
                if (!islock && !lockState)
                    return false;
                self.CopyPixels(rect, to.BackBuffer, self.PixelHeight * self.BackBufferStride, self.BackBufferStride);
                to.AddDirtyRect(rect);
                if (lockState) to.Unlock();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 快速填充指定颜色到矩形区域
        /// </summary>
        public static WriteableBitmap FillRect(this WriteableBitmap self, Color color, int x, int y, int width, int height)
        {
            if (self.Format != PixelFormats.Bgra32
             && self.Format != PixelFormats.Pbgra32)
            {
                return self;
            }
            else
            {
                self.Lock();
                var rect = new Int32Rect(x, y, width, height);
                int colorValue = (color.B << 0) | (color.G << 8) | (color.R << 16) | (color.A << 24);
                int bufferSize = height * self.BackBufferStride / 4;
                var buffer = ArrayPool<int>.Shared.Rent(bufferSize);
                buffer.AsSpan().Fill(colorValue);
                self.WritePixels(rect, buffer, self.BackBufferStride, 0);
                self.Unlock();
                return self;
            }
        }

        /// <summary>
        /// 快速清除整个区域为指定颜色（islock指示当前是否已锁定）
        /// </summary>
        public static WriteableBitmap Clear(this WriteableBitmap self, Color color, bool islock = false)
        {
            var lockState = !islock && self.TryLock(TimeSpan.FromMilliseconds(30));
            if (!islock && !lockState)
                return self;
            int colorValue = (color.B << 0) | (color.G << 8) | (color.R << 16) | (color.A << 24);
            int bufferSize = self.PixelHeight * self.BackBufferStride / 4;
            var buffer = ArrayPool<int>.Shared.Rent(bufferSize);
            buffer.AsSpan().Fill(colorValue);
            self.WritePixels(new Int32Rect(0, 0, self.PixelWidth, self.PixelHeight), buffer, self.BackBufferStride, 0);
            if (lockState) self.Unlock();
            return self;
        }

        public static bool IsInWindowViewport(this FrameworkElement element)
        {
            if (element == null
                || !element.IsVisible
                || element.ActualWidth == 0
                || element.ActualHeight == 0)
                return false;

            Window window = Window.GetWindow(element);
            if (window == null) return false;

            // 如果窗口最小化或不可见，直接返回 false
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
                {
                    break;
                }
            }

            return true;
        }

        public static SKRectI ToSKRectI(this SKRect rect)
        {
            return new SKRectI((int)rect.Left, (int)rect.Top, (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
        }

        public static Int32Rect ToInt32Rect(this SKRectI rect)
        {
            return new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height);
        }

        public static Size GetLayoutSlot(this FrameworkElement element)
        {
            return LayoutInformation.GetLayoutSlot(element).Size;
        }
    }
}
