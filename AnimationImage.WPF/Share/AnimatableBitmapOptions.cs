using System;

namespace AnimationImage.Core
{
    public record AnimatableBitmapOptions(Uri Source, int PreloadCount = PreloadOptions.Disable, double RenderScale = 1.0)
    {
        public AnimatableBitmapOptions(string path, int preloadCount = PreloadOptions.Disable, double renderScale = 1.0)
            : this(new Uri(path), preloadCount, renderScale) { }
    }
}
