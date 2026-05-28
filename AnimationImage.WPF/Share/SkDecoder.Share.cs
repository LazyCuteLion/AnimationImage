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
        private readonly int _frameCount;
        public int PreloadCount { get; private set; }
        private readonly object _codecLocker = new();
        private ConcurrentDictionary<int, WriteableBitmap> _frameCache = new();

        public SKCodec Codec { get; private set; }

        public SkDecoder(Stream stream, int preloadCount)
        {
            Codec = SKCodec.Create(stream);
            _frameCount = Codec.FrameCount;
            PreloadCount = preloadCount == PreloadOptions.Full ? _frameCount : preloadCount;

            var first = this.DecodeFrame(0, new FrameData(1, null));

            if (first.IsEmpty)
            {
                throw new NotSupportedException("解码失败");
            }
            else
            {
                _frameCache.TryAdd(0, first.Bitmap.TryFreeze());
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

            if (_frameCache.TryGetValue(index, out var bitmap))
            {
                return new FrameData(index, bitmap);
            }

            var result = this.Decode(index, data);
            if (!result.IsEmpty && PreloadCount >= _frameCount)
            {
                if (!_frameCache.ContainsKey(index))
                {
                    result.Bitmap.TryFreeze();
                    _frameCache.TryAdd(index, result.Bitmap);
                }
            }

            return result;
        }

        internal Task PreloadAsync()
        {
            if (PreloadCount >= _frameCount)
            {
                if (_frameCache.Count < _frameCount)
                {
                    var temp = new FrameData(0, _frameCache[0]);
                    return Task.Run(() =>
                    {
                        for (var i = 1; i < _frameCount; i++)
                        {
                            if (_frameCache.ContainsKey(i))
                                continue;
                            temp = this.DecodeFrame(i, temp);
                            if (!temp.IsEmpty)
                            {
                                _frameCache.TryAdd(temp.Index, temp.Bitmap.TryFreeze());
                            }
                        }
                    });
                }
            }
            return Task.CompletedTask;
        }

        private bool IsDisposed;
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
