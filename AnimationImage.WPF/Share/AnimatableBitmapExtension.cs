using System;
using System.ComponentModel;

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

#if WINUI
using Microsoft.UI.Xaml.Markup;
#endif

namespace AnimationImage
{
#if WINUI
    [MarkupExtensionReturnType(ReturnType = typeof(AnimatableBitmap))]
    public sealed class AnimatableBitmapExtension : MarkupExtension
#else
    public class AnimatableBitmapExtension : MarkupExtension
#endif
    {
#if !WINUI
        [TypeConverter(typeof(UriTypeConverter))]
#endif
        public Uri? Source { get; set; }

        public bool UseGPU { get; set; } = AnimatableBitmapOptions.Default.UseGPU;

        public bool Preload { get; set; } = AnimatableBitmapOptions.Default.Preload;

        public AnimatableBitmapOptions ToOptions()
            => new(Source!, UseGPU, Preload);

        public AnimatableBitmapExtension() { }

        public AnimatableBitmapExtension(Uri source)
        {
            Source = source;
        }

#if WINUI
        protected override object? ProvideValue()
        {
            if (Source == null) return null;
            return AnimatableBitmapFactory.Default.Create(ToOptions());
        }
#else
        public override object? ProvideValue(IServiceProvider serviceProvider)
        {
            if (Source == null)
                return null;

            var targetProvider = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;

            if (targetProvider?.TargetObject is FrameworkElement target
             && targetProvider?.TargetProperty is DependencyProperty property)
            {
                var bitmap = AnimatableBitmapFactory.Default.Create(ToOptions());
                if (property == AnimatableBitmap.SourceProperty)
                {
                    return bitmap;
                }
                else
                {
                    AnimatableBitmap.SetSource(target, bitmap);
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
#endif
    }
}
