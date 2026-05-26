using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#if WPF
namespace AnimationImage.WPF
#endif
#if AVALONIA
namespace AnimationImage.Avalonia
#endif
{
    public class AnimatableBitmapConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        {
            if (sourceType == typeof(Uri) || sourceType == typeof(string))
                return true;
            return base.CanConvertFrom(context, sourceType);
        }

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if (value is Uri uri)
            {
                return AnimatableBitmapFactory.Default.Create(uri);
            }
            else if (value is string str)
            {
                return AnimatableBitmapFactory.Default.Create(str);
            }
            return null;
        }

        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        {
            return false;
        }
    }
}
