using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#if WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

#endif

#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FrameworkElement = Avalonia.Controls.Control;
using DependencyProperty = Avalonia.AvaloniaProperty;
#endif

namespace AnimationImage
{
    public class AnimatableBitmapExtension : MarkupExtension
    {
        [TypeConverter(typeof(UriTypeConverter))]
        public Uri Source { get; set; }

        public int PreloadCount { get; set; } = AnimatableBitmapOptions.Default.PreloadCount;

        public double RenderScale { get; set; } = AnimatableBitmapOptions.Default.RenderScale;

        public bool UseGPU { get; set; } = AnimatableBitmapOptions.Default.UseGPU;

        public AnimatableBitmapOptions ToOptions()
        {
            return new AnimatableBitmapOptions(this.Source, this.PreloadCount, this.RenderScale, this.UseGPU);
        }

        public AnimatableBitmapExtension(Uri source)
        {
            this.Source = source;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (Source == null)
                return null;

            var targetProvider = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;

            if (targetProvider?.TargetObject is FrameworkElement target
             && targetProvider?.TargetProperty is DependencyProperty property)
            {
                var bitmap = AnimatableBitmapFactory.Default.Create(this.ToOptions());
                if (property == AnimationBehavior.AnimatableBitmapProperty)
                {
                    return bitmap;
                }
                else
                {
                    AnimationBehavior.SetAnimatableBitmap(target, bitmap);
                    if (property == Image.SourceProperty)
                    {
                        var binding = new Binding(nameof(bitmap.Frame)) { Source = bitmap, Mode = BindingMode.OneWay };
#if WPF
                        return binding.ProvideValue(serviceProvider);
#endif
#if AVALONIA
                        return binding;
#endif
                    }
                    else if (property == Shape.FillProperty
                        || property == Border.BackgroundProperty
                        || property == Panel.BackgroundProperty)
                    {
                        var brush = new ImageBrush();
                        var binding = new Binding(nameof(bitmap.Frame)) { Source = bitmap, Mode = BindingMode.OneWay };
#if WPF
                        BindingOperations.SetBinding(brush, ImageBrush.ImageSourceProperty, binding);
#endif
#if AVALONIA
                        brush.Bind(ImageBrush.SourceProperty, binding);
#endif
                        return brush;
                    }
                }
            }

            return null;
        }

    }
}
