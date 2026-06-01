using SkiaSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.ComponentModel;


#if WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
#endif

#if AVALONIA
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using FrameworkElement = Avalonia.Controls.Control;
#endif

namespace AnimationImage
{
    [TypeConverter(typeof(AnimatableBitmapConverter))]
    public abstract partial class AnimatableBitmap : INotifyPropertyChanged, IDisposable
    {
        private static readonly HttpClient SharedHttpClient = new();

        protected Stream _stream;
        private bool _waitForResume;
        private bool _disposed;

        public double CurrentTime { get; protected set; }

        public FrameworkElement Target { get; private set; }

        private WriteableBitmap _frame;
        public WriteableBitmap Frame
        {
            get => _frame;
            protected set
            {
                if (_frame != value)
                {
                    _frame = value;
                    RaisePropertyChanged();
                }
            }
        }

        public AnimationState State { get; protected set; } = AnimationState.None;

        public Metadata Metadata { get; protected set; }

        #region TPS
        private Stopwatch _tpsWatcher;
        private int _tpsCount;
        private double _tps;
        /// <summary>
        /// 每秒更新次数（Ticks Per Second），表示动画实际更新的频率，数值越高动画越流畅。
        /// 启用TPS统计后可以通过绑定此属性来监控动画的性能表现。
        /// </summary>
        public double TPS
        {
            get => _tps;
            private set
            {
                if (_tps != value)
                {
                    _tps = value;
                    RaisePropertyChanged();
                }
            }
        }
        #endregion

        public virtual bool IsAnimatable => Frame != null
            && Target != null
            && Target.IsVisible
            && State != AnimationState.Error;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public ICommand BeginCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand StopCommand { get; }

        public AnimatableBitmap(AnimatableBitmapOptions options)
        {
            var source = options.Source;
            _stream = LoadStream(source);

            if (_stream == null)
                throw new IOException($"读取资源失败：{source}");

            BeginCommand = new RelayCommand(BeginAnimation, () => IsAnimatable && State != AnimationState.Playing);
            PauseCommand = new RelayCommand(PauseAnimation, () => State == AnimationState.Playing);
            StopCommand = new RelayCommand(StopAnimation);

            if (EnableTPS)
            {
                _tpsWatcher = Stopwatch.StartNew();
                _tpsCount = 0;
            }
        }

        private static Stream? LoadStream(Uri source)
        {
            if (source.Scheme == Uri.UriSchemeHttp || source.Scheme == Uri.UriSchemeHttps)
            {
                using var response = SharedHttpClient.GetAsync(source).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    var stream = new MemoryStream();
                    response.Content.CopyToAsync(stream).GetAwaiter().GetResult();
                    stream.Position = 0;
                    return stream;
                }
                return null;
            }
#if WPF
            else if (source.Scheme == "pack")
            {
                return Application.GetResourceStream(source)?.Stream
                    ?? Application.GetContentStream(source)?.Stream
                    ?? Application.GetRemoteStream(source)?.Stream;
            }
#endif
#if AVALONIA
            else if (source.Scheme == "avares")
            {
                return AssetLoader.Open(source);
            }
#endif
            else if (source.IsFile)
            {
                return File.OpenRead(source.LocalPath);
            }
            return null;
        }

        internal virtual void SeekTime(double milliseconds)
        {
            CurrentTime = milliseconds;
            if (!EnableTPS) return;

            _tpsCount++;
            if (_tpsWatcher.ElapsedMilliseconds >= 1000)
            {
                TPS = _tpsCount * 1000.0 / _tpsWatcher.ElapsedMilliseconds;
                _tpsWatcher.Restart();
                _tpsCount = 0;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #region Static

        /// <summary>
        /// 是否启用TPS（每秒更新次数）统计，启用后可以通过绑定TPS属性来监控动画的实际更新频率
        /// </summary>
        /// <remarks>
        /// 默认在调试模式下启用，发布模式下禁用。
        /// </remarks>
        public static bool EnableTPS { get; set; }

        internal static SKImageInfo CreateDecodeInfo(int width, int height)
        {
            return new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        }

        static AnimatableBitmap()
        {
#if DEBUG
            EnableTPS = true;
#endif
        }

        #endregion
    }
}
