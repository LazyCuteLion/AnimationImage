using AnimationImage.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using SkiaSharp.Skottie;
using Vortice.Direct3D12;


namespace AnimationImage.Avalonia
{
    public partial class SkottieBitmap
    {
        private void Render()
        {
            var frame = this.Frame.PixelSize.Width != _info.Width || this.Frame.PixelSize.Height != _info.Height
                          ? CreateNewFrame(_info.Width, _info.Height)
                          : this.Frame;

            if (_gpuSurface != null)
            {
                _gpuSurface.Canvas.Clear();
                _animation.Render(_gpuSurface.Canvas, _info.Rect);//渲染后，需要把数据从GPU复制到CPU
                _gpuSurface.Flush();
                using var locker = frame.Lock();
                _gpuSurface.ReadPixels(_info, locker.Address, _info.RowBytes, 0, 0);//复制像素，1080x700复杂动画，耗时10~15
            }
            else
            {
                using var locker = frame.Lock();
                using var surface = SKSurface.Create(_info, locker.Address, _info.RowBytes);
                surface.Canvas.Clear();
                _animation.Render(surface.Canvas, _info.Rect);
            }
            this.Frame = frame;
        }

    }
}
