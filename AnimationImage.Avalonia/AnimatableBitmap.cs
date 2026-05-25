using AnimationImage.Core;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using SkiaSharp;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AnimationImage.Avalonia
{
    [TypeConverter(typeof(AnimatableBitmapConverter))]
    public abstract class AnimatableBitmap : INotifyPropertyChanged
    {
        protected Stream Stream;
        protected double CurrentTime { get; private set; }
        protected Control Target;
        private Stopwatch TPSwatcher;
        private int TPSCount;

        private WriteableBitmap _frame;
        public WriteableBitmap Frame
        {
            get => _frame;
            protected set
            {
                if (_frame != value)
                {
                    _frame = value;
                    this.RasiePropertyChanged();
                }
            }
        }

        private AnimationState _state = AnimationState.None;
        public AnimationState State
        {
            get => _state;
            protected set
            {
                if (_state != value)
                {
                    _state = value;
                    this.RasiePropertyChanged();
                }
            }
        }

        public Metadata Metadata { get; protected set; }

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
                    _tps = Math.Round(value, 1);
                    this.RasiePropertyChanged();
                }
            }
        }

        public virtual bool IsAnimatable => Frame != null
                                         && Target != null
                                         && Target.IsVisible
                                         && State != AnimationState.Error;

        public ICommand BeginCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand StopCommand { get; }

        public AnimatableBitmap(Uri source)
        {
            if (source.Scheme == Uri.UriSchemeHttp || source.Scheme == Uri.UriSchemeHttps)
            {
                using var client = new HttpClient();
                using var rsp = client.GetAsync(source).Result;
                if (rsp?.IsSuccessStatusCode == true)
                {
                    Stream = new MemoryStream();
                    rsp.Content.CopyToAsync(Stream).Wait();
                    Stream.Position = 0;
                }
            }
            else if (source.Scheme == "avares")
            {
                Stream = AssetLoader.Open(source);
            }
            else if (source.IsFile)
            {
                Stream = File.OpenRead(source.LocalPath);
            }

            if (Stream == null)
            {
                throw new IOException($"读取资源失败：{source}");
            }

            this.BeginCommand = new RelayCommand(this.BeginAnimation, () => this.IsAnimatable && State != AnimationState.Playing);
            this.PauseCommand = new RelayCommand(this.PauseAnimation, () => State == AnimationState.Playing);
            this.StopCommand = new RelayCommand(this.StopAnimation);

            if (EnableTPS)
            {
                TPSwatcher = Stopwatch.StartNew();
                TPSCount = 0;
            }
        }

        public virtual void AttachTarget(Control target)
        {
            Target = target;
            if (Target is Image img)
            {
                img.Bind(Image.SourceProperty, new Binding(nameof(this.Frame)) { Source = this });
            }
            if (AnimationBehavior.GetAutoStart(target))
                this.BeginAnimation();
        }

        public virtual void Dispose()
        {
            AnimationToken?.Cancel();
            AnimationToken?.Dispose();

            if (Target != null)
            {
                Target = null;
            }

            this.Frame = null;

            Stream?.Dispose();

            TPSwatcher?.Stop();
        }

        private Animation Animation;
        private CancellationTokenSource AnimationToken;
        private void CreateAnimation()
        {
            var loopCount = AnimationBehavior.GetLoopCount(Target) ?? (Metadata.LoopCount >= 0 ? Metadata.LoopCount + 1 : Metadata.LoopCount);
            Animation = new Animation()
            {
                Duration = TimeSpan.FromMilliseconds(Metadata.Duration),
                IterationCount = new IterationCount((ulong)loopCount),
            };
            if (CurrentTime == 0)
            {
                Animation.Children.Add(new KeyFrame
                {
                    Cue = new Cue(0.0),
                    Setters =
                        {
                            new Setter(AnimationBehavior.AnimationTimeProperty, 0.0)
                        }
                });
                Animation.Children.Add(new KeyFrame
                {
                    Cue = new Cue(1.0),
                    Setters =
                        {
                            new Setter(AnimationBehavior.AnimationTimeProperty, Metadata.Duration)
                        }
                });
            }
            else
            {
                //当前时间=》结束时间&归零=》当前时间
                var currentTime = CurrentTime;
                Animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(0.0),
                    Setters =
                        {
                           new Setter(AnimationBehavior.AnimationTimeProperty, currentTime)
                        }
                });
                var timeNode = (Metadata.Duration - currentTime) / Metadata.Duration;
                Animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(timeNode),
                    Setters =
                        {
                           new Setter(AnimationBehavior.AnimationTimeProperty, Metadata.Duration)
                        }
                });
                Animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(timeNode),
                    Setters =
                        {
                           new Setter(AnimationBehavior.AnimationTimeProperty, 0.0)
                        }
                });
                Animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(1.0),
                    Setters =
                        {
                           new Setter(AnimationBehavior.AnimationTimeProperty, currentTime)
                        }
                });
            }
        }

        protected virtual async void BeginAnimation()
        {
            if (!IsAnimatable
                || State == AnimationState.Playing
                || State == AnimationState.Error
                || AnimationToken != null)
                return;

            try
            {
                AnimationToken = new CancellationTokenSource();
                this.State = AnimationState.Playing;
                this.UpdateCommandState();
                this.CreateAnimation();
                await Animation.RunAsync(Target, AnimationToken.Token);
                if (!AnimationToken.IsCancellationRequested)
                {
                    State = AnimationState.Completed;//播放到自然结束
                    AnimationBehavior.SetAnimationTime(Target, Metadata.Duration);
                }
                AnimationToken.Dispose();
                AnimationToken = null;
            }
            catch { }
        }

        protected virtual void PauseAnimation()
        {
            if (State != AnimationState.Playing)
                return;

            var currentTime = this.CurrentTime;
            AnimationToken?.Cancel();
            this.State = AnimationState.Paused;
            this.UpdateCommandState();
            AnimationBehavior.SetAnimationTime(Target, currentTime);

        }

        protected virtual void StopAnimation()
        {
            AnimationToken?.Cancel();
            this.State = AnimationState.Stopped;
            this.UpdateCommandState();
            AnimationBehavior.SetAnimationTime(Target, 0.0);
        }

        protected void UpdateCommandState()
        {
            (this.BeginCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (this.PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (this.StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        internal virtual void SeekTime(double milliseconds)
        {
            this.CurrentTime = milliseconds;

            if (EnableTPS)
            {
                TPSCount++;
                if (TPSwatcher.ElapsedMilliseconds >= 1000)
                {
                    this.TPS = TPSCount * 1000.0 / TPSwatcher.ElapsedMilliseconds;
                    TPSwatcher.Restart();
                    TPSCount = 0;
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void RasiePropertyChanged([CallerMemberName] string name = null)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        #region static
        /// <summary>
        /// 是否启用TPS（每秒更新次数）统计，启用后可以通过绑定TPS属性来监控动画的实际更新频率
        /// </summary>
        /// <remarks>
        /// 默认在调试模式下启用，发布模式下禁用。
        /// </remarks>
        public static bool EnableTPS { get; set; }

        internal static WriteableBitmap CreateNewFrame(int width, int height)
        {
            return new WriteableBitmap(new PixelSize(width, height), new Vector(96d, 96d), PixelFormats.Bgra8888, AlphaFormat.Premul);
        }

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
