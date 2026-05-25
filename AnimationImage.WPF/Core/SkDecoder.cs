using AnimationImage.Core;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Collections;
using System.Linq;

#if AVALONIA
using Avalonia.Media.Imaging;
namespace AnimationImage.Avalonia
#endif

#if WPF
using System.Windows.Media.Imaging;
namespace AnimationImage.WPF
#endif

{
    internal record FrameData
    {
        public readonly int Index = -1;
        public readonly WriteableBitmap Bitmap = null;

        public bool IsEmpty => Index < 0;

        public FrameData() { }

        public FrameData(int index, WriteableBitmap bitmap)
        {
            this.Index = index;
            this.Bitmap = bitmap;
        }

        public static FrameData Empty = new();
    }

    internal partial class SkDecoder : IDisposable
    {
        private readonly int FrameCount;
        public int PreloadCount { get; private set; }
        private readonly object CodecLocker = new();
        private ConcurrentDictionary<int, WriteableBitmap> FrameCache = new();

        public SKCodec Codec { get; private set; }

        public SkDecoder(Stream stream, int preloadCount)
        {
            Codec = SKCodec.Create(stream);
            FrameCount = Codec.FrameCount;
            PreloadCount = preloadCount;

            var first = this.DecodeFrame(0, new FrameData(1, null));

            if (FrameCount > 1)
            {
                if (first.IsEmpty)
                {
                    throw new NotSupportedException("解码失败");
                }
                else
                {
                    FrameCache.TryAdd(0, first.Bitmap.TryFreeze());
                }
            }
        }

        public FrameData Get(int index)
        {
            return this.Get(index, FrameData.Empty);
        }

        public FrameData Get(int index, FrameData data)
        {
            if (data.Index == index)
                return data;

            if (FrameCache.TryGetValue(index, out var bitmap))
            {
                return new FrameData(index, bitmap);
            }

            var result = this.Decode(index, data);
            if (!result.IsEmpty && PreloadCount == PreloadOptions.Full || PreloadCount >= FrameCount)
            {
                result.Bitmap.TryFreeze();
                FrameCache.TryAdd(index, result.Bitmap);
            }

            return result;
        }

        internal Task PreloadAsync()
        {
            if (PreloadCount == PreloadOptions.Full || PreloadCount >= FrameCount)
            {
                if (FrameCache.Count < FrameCount)
                {
                    var temp = new FrameData(0, FrameCache[0]);
                    return Task.Run(() =>
                    {
                        for (var i = 1; i < FrameCount; i++)
                        {
                            temp = this.DecodeFrame(i, temp);
                            if (!temp.IsEmpty)
                            {
                                FrameCache.TryAdd(temp.Index, temp.Bitmap.TryFreeze());
                            }
                        }
                    });
                }
            }
            return Task.CompletedTask;
        }

    }
}
