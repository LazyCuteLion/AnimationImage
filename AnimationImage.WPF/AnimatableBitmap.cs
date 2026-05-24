using AnimationImage.Core;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AnimationImage.WPF
{
    [TypeConverter(typeof(AnimatableBitmapConverter))]
    public abstract class AnimatableBitmap : INotifyPropertyChanged
    {
        protected Stream Stream;
        protected double CurrentTime { get; private set; }
        protected FrameworkElement Target;
        protected Storyboard Storyboard;
        private bool Resume = false;
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

        public AnimationState State { get; protected set; } = AnimationState.None;

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
                    _tps = value;
                    this.RasiePropertyChanged();
                }
            }
        }

        public virtual bool IsAnimatable => Frame != null && Target != null && State != AnimationState.Error;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void RasiePropertyChanged([CallerMemberName] string name = null)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

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
                    //Stream = rsp.Content.ReadAsStream();//rsp释放后，Stream也同样被释放了，所以得copy到内存
                }
            }
            else if (source.Scheme == "pack")
            {
                Stream = Application.GetResourceStream(source)?.Stream
                      ?? Application.GetContentStream(source)?.Stream
                      ?? Application.GetRemoteStream(source)?.Stream;
            }
            else if (source.IsFile)
            {
                Stream = File.OpenRead(source.LocalPath);
            }

            if (Stream == null)
            {
                throw new IOException($"读取资源失败：{source}");
            }

            this.BeginCommand = new RelayCommand(this.BeginAnimation, () => IsAnimatable);
            this.PauseCommand = new RelayCommand(this.PauseAnimation, () => State == AnimationState.Playing);
            this.StopCommand = new RelayCommand(this.StopAnimation, () => Target != null);

            if (EnableTPS)
            {
                TPSwatcher = Stopwatch.StartNew();
                TPSCount = 0;
            }
        }

        public virtual async void AttachTarget(FrameworkElement target)
        {
            Target = target;
            if (Target is Image img)
            {
                img.SetBinding(Image.SourceProperty, new Binding(nameof(this.Frame)) { Source = this });
            }
            await Target.WaitForLoadedAsync();
            Target.IsVisibleChanged += Target_IsVisibleChanged;
            if (Window.GetWindow(Target) is Window win)
            {
                win.StateChanged += Window_StateChanged;
            }
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (Target == null) return;
            if (sender is Window win)
            {
                if (win.WindowState == WindowState.Minimized && State == AnimationState.Playing)
                {
                    this.PauseAnimation();
                    Resume = true;
                }
                else if (Resume)
                {
                    Resume = false;
                    this.BeginAnimation();
                }
            }
        }

        private void Target_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (Target == null) return;
            if (e.NewValue.Equals(false) && State == AnimationState.Playing)
            {
                this.PauseAnimation();
                Resume = true;
            }
            else if (Resume)
            {
                Resume = false;
                this.BeginAnimation();
            }
        }

        public virtual void Dispose()
        {
            if (Storyboard != null)
            {
                Storyboard.Completed -= OnCompleted;
                Storyboard.Stop();
            }

            if (Target != null)
            {
                Target.BeginAnimation(AnimationBehavior.AnimationTimeProperty, null);
                Target.IsVisibleChanged -= Target_IsVisibleChanged;
                if (Window.GetWindow(Target) is Window win)
                {
                    win.StateChanged -= Window_StateChanged;
                }
                Target = null;
            }

            this.Frame = null;

            Stream?.Dispose();

            TPSwatcher?.Stop();
        }

        private void CreateAnimation()
        {
            var repeatBehavior = AnimationBehavior.GetRepeatBehavior(Target) ??
                                (this.Metadata.LoopCount == -1
                                  ? RepeatBehavior.Forever
                                  : new RepeatBehavior(this.Metadata.LoopCount + 1));
            Storyboard = new Storyboard()
            {
                RepeatBehavior = repeatBehavior,
                FillBehavior = FillBehavior.Stop
            };
            var animation = new DoubleAnimation(0, this.Metadata.Duration, TimeSpan.FromMilliseconds(this.Metadata.Duration));
            Storyboard.SetTargetProperty(animation, new PropertyPath(AnimationBehavior.AnimationTimeProperty));
            Storyboard.SetTarget(animation, Target);
            Storyboard.Children.Add(animation);
            var forceFPS = AnimationBehavior.GetForceFPS(Target);
            Timeline.SetDesiredFrameRate(Storyboard, forceFPS > 0 ? forceFPS : this.Metadata.Fps);
            Storyboard.Completed += OnCompleted;
        }

        protected virtual void OnCompleted(object? sender, EventArgs e)
        {
            var time = this.CurrentTime;
            this.State = AnimationState.Completed;
            AnimationBehavior.SetAnimationTime(Target, time);
        }

        protected virtual void BeginAnimation()
        {
            if (!IsAnimatable
                || State == AnimationState.Playing
                || State == AnimationState.Error)
                return;

            if (State == AnimationState.Completed)
            {
                CurrentTime = 0;
            }

            if (State == AnimationState.Paused && Storyboard != null)
            {
                Storyboard.Resume();
                State = AnimationState.Playing;
                return;
            }

            if (Storyboard == null)
                this.CreateAnimation();
            else
            {
                Storyboard.RepeatBehavior = AnimationBehavior.GetRepeatBehavior(Target) ??
                                            (this.Metadata.LoopCount == -1
                                              ? RepeatBehavior.Forever
                                              : new RepeatBehavior(this.Metadata.LoopCount + 1));

                var forceFPS = AnimationBehavior.GetForceFPS(Target);
                Timeline.SetDesiredFrameRate(Storyboard, forceFPS > 0 ? forceFPS : this.Metadata.Fps);
            }

            Storyboard.Begin();

            if (CurrentTime > 0)
            {
                Storyboard.Seek(TimeSpan.FromMilliseconds(CurrentTime));
            }

            this.State = AnimationState.Playing;
        }

        protected virtual void PauseAnimation()
        {
            if (State != AnimationState.Playing)
                return;
            Storyboard?.Pause();
            this.State = AnimationState.Paused;
        }

        protected virtual void StopAnimation()
        {
            if (Target != null)
            {
                Target.BeginAnimation(AnimationBehavior.AnimationTimeProperty, null);
                AnimationBehavior.SetAnimationTime(Target, 0);
            }
            Storyboard?.Stop();
            this.State = AnimationState.Stopped;
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
            return new WriteableBitmap(width, height, 96d, 96d, PixelFormats.Pbgra32, null);
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
