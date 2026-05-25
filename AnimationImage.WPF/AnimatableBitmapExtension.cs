using AnimationImage.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace AnimationImage.WPF
{
    public class AnimatableBitmapExtension : MarkupExtension
    {
        [TypeConverter(typeof(UriTypeConverter))]
        public Uri Source { get; set; }

        public int PreloadCount { get; set; } = PreloadOptions.Disable;

        public AnimatableBitmapExtension(Uri source)
        {
            this.Source = source;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var targetProvider = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;

            if (targetProvider?.TargetObject is FrameworkElement target
             && targetProvider?.TargetProperty is DependencyProperty property)
            {
                var source = AnimatableBitmapFactory.Default.Create(this.Source, this.PreloadCount);
                if (property == AnimationBehavior.AnimatableBitmapProperty)
                {
                    return source;
                }
                else
                {
                    AnimationBehavior.SetAnimatableBitmap(target, source);
                    if (property == Image.SourceProperty)
                    {
                        var binding = new Binding(nameof(source.Frame)) { Source = source, Mode = BindingMode.OneWay };
                        return binding.ProvideValue(serviceProvider);
                    }
                    else if (property == Shape.FillProperty
                        || property == Border.BackgroundProperty
                        || property == Panel.BackgroundProperty)
                    {
                        var brush = new ImageBrush();
                        var binding = new Binding(nameof(source.Frame)) { Source = source, Mode = BindingMode.OneWay };
                        BindingOperations.SetBinding(brush, ImageBrush.ImageSourceProperty, binding);
                        return brush;
                    }
                }
            }

            return null;
        }
    }
}
