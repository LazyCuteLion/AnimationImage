using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using AnimationImage.Core;

#if WPF
namespace AnimationImage.WPF
#endif
#if AVALONIA
namespace AnimationImage.Avalonia
#endif
{
    public class AnimatableBitmapFactory
    {
        private readonly Dictionary<string, Func<Dictionary<string, object>, AnimatableBitmap>> _registry = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string extension, Func<Dictionary<string, object>, AnimatableBitmap> creator)
        {
            _registry[extension] = creator;
        }

        private Func<Dictionary<string, object>, AnimatableBitmap>? _defaultCreator;

        private void RegisterDefault(Func<Dictionary<string, object>, AnimatableBitmap> creator)
        {
            _defaultCreator = creator;
        }

        public AnimatableBitmap Create(Uri source, int preloadCount = PreloadOptions.Auto)
        {
            var ext = Path.GetExtension(source.AbsolutePath).ToLower();
            var args = new Dictionary<string, object>
            {
                { nameof(source), source },
                { nameof(preloadCount), preloadCount }
            };
            if (_registry.TryGetValue(ext, out var creator))
            {
                return creator(args);
            }
            else if (_defaultCreator != null)
            {
                return _defaultCreator(args);
            }
            throw new NotSupportedException($"不支持的文件类型: {ext}");
        }

        private T Create<T>(IDictionary<string, object> args)
        {
            var ctors = typeof(T).GetConstructors();
            foreach (var ctor in ctors)
            {
                var parameters = ctor.GetParameters();
                var values = new object[parameters.Length];
                bool match = true;

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (args.TryGetValue(parameters[i].Name, out var value))
                    {
                        values[i] = Convert.ChangeType(value, parameters[i].ParameterType);
                    }
                    else
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return (T)ctor.Invoke(values);
            }
            throw new InvalidOperationException("与构造函数参数不匹配");
        }

        private AnimatableBitmapFactory()
        {
            this.Register(".json", (args) => this.Create<SkottieBitmap>(args));
            this.RegisterDefault((args) => this.Create<SKCodecBitmap>(args));
        }

        private static readonly Lazy<AnimatableBitmapFactory> _lazy = new(() => new AnimatableBitmapFactory());
        public static AnimatableBitmapFactory Default => _lazy.Value;
    }
}
