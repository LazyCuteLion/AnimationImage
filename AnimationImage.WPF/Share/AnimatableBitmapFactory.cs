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
        private readonly Dictionary<string, Func<AnimatableBitmapOptions, AnimatableBitmap>> _registry = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string extension, Func<AnimatableBitmapOptions, AnimatableBitmap> creator)
        {
            _registry[extension] = creator;
        }

        private Func<AnimatableBitmapOptions, AnimatableBitmap>? _defaultCreator;

        private void RegisterDefault(Func<AnimatableBitmapOptions, AnimatableBitmap> creator)
        {
            _defaultCreator = creator;
        }

        public AnimatableBitmap Create(string path)
        {
            return this.Create(new AnimatableBitmapOptions(path));
        }

        public AnimatableBitmap Create(Uri source)
        {
            return this.Create(new AnimatableBitmapOptions(source));
        }

        public AnimatableBitmap Create(AnimatableBitmapOptions options)
        {
            var ext = Path.GetExtension(options.Source.AbsolutePath).ToLower();
            if (_registry.TryGetValue(ext, out var creator))
            {
                return creator(options);
            }
            else if (_defaultCreator != null)
            {
                return _defaultCreator(options);
            }
            throw new NotSupportedException($"不支持的文件类型: {ext}");
        }

        private AnimatableBitmapFactory()
        {
            this.Register(".json", (options) => new SkottieBitmap(options));
            this.RegisterDefault((options) => new SKCodecBitmap(options));
        }

        private static readonly Lazy<AnimatableBitmapFactory> _lazy = new(() => new AnimatableBitmapFactory());
        public static AnimatableBitmapFactory Default => _lazy.Value;
    }
}
