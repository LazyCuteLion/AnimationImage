using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AnimationImage.WPF.Demo
{
    class FPSAdorner : Adorner
    {
        private Stopwatch _stopwatch = new Stopwatch();
        private int _frameCount = 0;
        private double _fps = 0;

        public FPSAdorner(UIElement adornedElement) : base(adornedElement)
        {
            _stopwatch.Start();
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            _frameCount++;
            if (_stopwatch.ElapsedMilliseconds >= 1000)
            {
                _fps = _frameCount / (_stopwatch.ElapsedMilliseconds / 1000.0);
                _frameCount = 0;
                _stopwatch.Restart();
                InvalidateVisual();
            }
        }


        protected override void OnRender(DrawingContext drawingContext)
        {
            var text = $"CompositionTarget.Rendering:{_fps:F1}";
            var formattedText = new FormattedText(
                                                   text,
                                                   System.Globalization.CultureInfo.CurrentCulture,
                                                   FlowDirection.LeftToRight,
                                                   new Typeface("Consolas"),
                                                   12,
                                                   Brushes.White,
                                                   VisualTreeHelper.GetDpi(this).PixelsPerDip);

            drawingContext.DrawText(formattedText, new Point(10, 10));
        }

        public void Stop()
        {
            _stopwatch.Stop();
            _stopwatch = null;
        }

    }

    public class RenderingHelper
    {
        public static bool GetShowFPS(DependencyObject obj)
        {
            return (bool)obj.GetValue(ShowFPSProperty);
        }
        public static void SetShowFPS(DependencyObject obj, bool value)
        {
            obj.SetValue(ShowFPSProperty, value);
        }
        public static readonly DependencyProperty ShowFPSProperty =
            DependencyProperty.RegisterAttached("ShowFPS", typeof(bool), typeof(RenderingHelper), new PropertyMetadata(false, async (s, e) =>
            {
                if (s is FrameworkElement element)
                {
                    if (!element.IsLoaded)
                    {
                        await element.WaitForLoadedAsync();
                    }
                    var layer = AdornerLayer.GetAdornerLayer(element);
                    if (layer == null) return;

                    if ((bool)e.NewValue)
                    {
                        if (layer.GetAdorners(element)?.Any(t => t is FPSAdorner) == true)
                            return;
                        var adorner = new FPSAdorner(element);
                        layer.Add(adorner);
                    }
                    else
                    {
                        var adorners = layer.GetAdorners(element);
                        if (adorners?.Length > 0)
                        {
                            foreach (var a in adorners)
                            {
                                if (a is FPSAdorner t)
                                {
                                    t.Stop();
                                    layer.Remove(a);
                                }
                            }
                        }
                    }
                }
            }));
    }
}
