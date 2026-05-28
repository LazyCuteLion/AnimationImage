using AnimationImage.Core;
using SkiaSharp;
using SkiaSharp.Skottie;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace AnimationImage.WPF
{
    public partial class SkottieBitmap : AnimatableBitmap
    {
        private void Render()
        {
            var frame = Frame.PixelWidth != _info.Width || Frame.PixelHeight != _info.Height
                      ? CreateNewFrame(_info.Width, _info.Height)
                      : this.Frame;

            if (_gpuSurface != null)
            {
                _gpuSurface.Canvas.Clear();
                _animation.Render(_gpuSurface.Canvas, _info.Rect);//渲染后，需要把数据从GPU复制到CPU
                _gpuSurface.Flush();
                frame.Lock();
                _gpuSurface.ReadPixels(_info, frame.BackBuffer, _info.RowBytes, 0, 0);//复制像素，1080x700复杂动画，耗时10~15
                frame.Update();
                frame.Unlock();
            }
            else
            {
                frame.Lock();
                using var surface = SKSurface.Create(_info, frame.BackBuffer, _info.RowBytes);
                surface.Canvas.Clear();
                _animation.Render(surface.Canvas, _info.Rect);//1080x700复杂动画，耗时40~60，慢4~5倍！
                frame.Update();
                frame.Unlock();
            }

            this.Frame = frame;
        }
    }
}
