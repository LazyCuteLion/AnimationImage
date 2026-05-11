using AnimationImage.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System;
using System.ComponentModel;

namespace AnimationImage.Avalonia
{
    public class AnimatableBitmapExtension : MarkupExtension
    {
        [TypeConverter(typeof(UriTypeConverter))]
        public Uri Source { get; set; }

        public int PreloadCount { get; set; } = PreloadOptions.Auto;

        public AnimatableBitmapExtension(Uri source)
        {
            this.Source = source;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var targetProvider = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;

            if (targetProvider?.TargetObject is Control target
             && targetProvider?.TargetProperty is AvaloniaProperty property)
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
                        return binding;
                    }
                    else if (property == Shape.FillProperty
                        || property == Border.BackgroundProperty
                        || property == Panel.BackgroundProperty)
                    {
                        var brush = new ImageBrush();
                        var binding = new Binding(nameof(source.Frame)) { Source = source, Mode = BindingMode.OneWay };
                        brush.Bind(ImageBrush.SourceProperty, binding);
                        return brush;
                    }
                }
            }

            return null;
        }
    }
}
