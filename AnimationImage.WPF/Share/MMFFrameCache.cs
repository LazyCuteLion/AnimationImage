using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

#if WPF
using System.Windows;
#endif

#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
#endif

namespace AnimationImage
{
    public class MMFFrameCache : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly int _frameSize;
        private readonly int _frameCount;
        private readonly int _headerSize;
        private IntPtr _address;
        private bool _disposed;
        private int _loadedCount;

        public int LoadedCount => _loadedCount;
        public string TempPath { get; }
        public bool CanWrite { get; private set; }

        internal MMFFrameCache(string md5, int count, int frameSize)
        {
            try
            {
                _frameCount = count;
                _frameSize = frameSize;
                _headerSize = sizeof(byte) * _frameCount;
                var name = $"{md5}_{count}_{frameSize}";
                var totalSize = _headerSize + ((long)_frameCount * _frameSize);
                TempPath = Path.Combine(TempDirectory, name + ".tmp");
                Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} MMFFrameCache：" + TempPath);

                var exists = File.Exists(TempPath);

                using var stream = new FileStream(TempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                _mmf = MemoryMappedFile.CreateFromFile(stream, null, totalSize, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);

                _accessor = _mmf.CreateViewAccessor();

                CanWrite = !exists;

                unsafe
                {
                    byte* ptr = null;
                    _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    if (ptr != null)
                    {
                        _address = (IntPtr)ptr;
                    }
                }

                if (exists)
                {
                    var load = 0;
                    for (int i = 0; i < _frameCount; i++)
                    {
                        if (_accessor.ReadByte(i) == 1)
                            load++;
                    }
                    Interlocked.Exchange(ref _loadedCount, load);
                    if (_loadedCount < _frameCount)
                    {
                        var info = new FileInfo(TempPath);
                        if ((DateTime.Now - info.LastWriteTime).TotalMinutes > 1)
                        {
                            File.SetLastWriteTime(TempPath, DateTime.Now);
                            CanWrite = true;
                        }
                    }
                }
            }
            catch
            {
                throw new NotSupportedException("不支持MemoryMappedFile。");
            }
        }

        public bool Contains(int index)
        {
            if (_disposed)
                return false;
            if (_accessor == null || _mmf == null)
                return false;
            try
            {
                return _accessor.ReadByte(index) == 1;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
                _address = IntPtr.Zero;
                _accessor?.Dispose();
                _mmf?.Dispose();

                Delete(TempPath);
            }
            catch { }
        }

        public unsafe bool TryAdd(int index, IntPtr src)
        {
            if (!CanWrite)
                return false;

            if (_disposed
                || index < 0
                || index >= _frameCount
                || src == IntPtr.Zero
                || _address == IntPtr.Zero)
                return false;

            var dst = (byte*)_address;
            try
            {
                var offset = _headerSize + ((long)index * _frameSize);
                Buffer.MemoryCopy(src.ToPointer(), dst + offset, _frameSize, _frameSize);
                *(dst + index) = 1;
                Interlocked.Increment(ref _loadedCount);
                return true;
            }
            catch
            {
                *(dst + index) = 0;
                return false;
            }
        }

        public unsafe bool TryGet(int index, IntPtr dst)
        {
            if (_disposed
                || index < 0
                || index >= _frameCount
                || dst == IntPtr.Zero)
                return false;

            try
            {
                var src = (byte*)_address;
                if (*(src + index) != 1)
                    return false;
                var offset = _headerSize + ((long)index * _frameSize);
                Buffer.MemoryCopy(src + offset, dst.ToPointer(), _frameSize, _frameSize);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryAdd(int index, byte[] data)
        {
            if (!CanWrite)
                return false;

            if (_disposed
               || _accessor == null
               || index < 0
               || index >= _frameCount
               || Contains(index)
               || data == null
               || data.Length != _frameSize)
                return false;

            try
            {
                var position = _headerSize + (long)index * _frameSize;
                _accessor.WriteArray(position, data, 0, data.Length);
                _accessor.Write(index, 1);
                Interlocked.Increment(ref _loadedCount);
                return true;
            }
            catch { return false; }
        }

        public bool TryGet(int index, byte[] data)
        {
            if (_disposed
               || _accessor == null
               || index < 0
               || index >= _frameCount
               || !Contains(index)
               || data == null
               || data.Length != _frameSize)
                return false;

            try
            {
                var position = _headerSize + (long)index * _frameSize;
                var len = _accessor.ReadArray(position, data, 0, data.Length);
                return len == _frameSize;
            }
            catch { return false; }
        }

        public static readonly string TempDirectory = Path.Combine(Path.GetTempPath(), "AnimationImage.MMFFrameCache");

        static MMFFrameCache()
        {
            if (!Directory.Exists(TempDirectory))
                Directory.CreateDirectory(TempDirectory);

#if WPF
            Application.Current.Exit += (_, _) => Clear();
#endif

#if AVALONIA
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime app)
            {
                app.Exit += (_, _) => Clear();
            }
#endif
        }

        public static void Clear()
        {
            var files = Directory.GetFiles(TempDirectory, "*.tmp");
            foreach (var f in files)
            {
                Delete(f);
            }
        }

        public static void Delete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
