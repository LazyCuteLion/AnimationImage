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
using FrameworkElement = Avalonia.Controls.Control;

namespace AnimationImage
{
    public abstract partial class AnimatableBitmap
    {
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            if (disposing)
            {
                //释放托管资源
                _animationToken?.Cancel();
                _animationToken?.Dispose();

                if (Target != null)
                {
                    Target.PropertyChanged -= Target_PropertyChanged;
                    if (TopLevel.GetTopLevel(Target) is Window win)
                    {
                        win.PropertyChanged -= Target_PropertyChanged;
                    }
                    Target = null;
                }

                this.Frame?.Dispose();

                _stream?.Dispose();

                _tpsWatcher?.Stop();
            }
            //释放非托管资源
            _disposed = true;
        }

        public virtual async void AttachTarget(FrameworkElement target)
        {
            Target = target;
            if (Target is Image img)
            {
                img.Bind(Image.SourceProperty, new Binding(nameof(this.Frame)) { Source = this });
            }
            await Target.WaitForLoadedAsync();
            Target.PropertyChanged += Target_PropertyChanged;
            Target.DetachedFromVisualTree += Target_DetachedFromVisualTree;
            if (TopLevel.GetTopLevel(Target) is Window win)
            {
                win.PropertyChanged += Target_PropertyChanged;
            }
            if (AnimationBehavior.GetAutoStart(target))
                this.BeginAnimation();
        }

        private void Target_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is FrameworkElement el)
            {
                el.DetachedFromVisualTree -= Target_DetachedFromVisualTree;
                this.Dispose(true);
            }
        }

        private void Target_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (Target == null) return;
            if (e.Property == FrameworkElement.IsVisibleProperty)
            {
                if (e.NewValue?.Equals(false) == false && State == AnimationState.Playing)
                {
                    this.PauseAnimation();
                    _waitForResume = true;
                }
                else if (_waitForResume)
                {
                    _waitForResume = false;
                    this.BeginAnimation();
                }
            }
            else if (e.Property == Window.WindowStateProperty)
            {
                if (e.NewValue is WindowState state)
                {
                    if (state == WindowState.Minimized && State == AnimationState.Playing)
                    {
                        this.PauseAnimation();
                        _waitForResume = true;
                    }
                    else if (_waitForResume)
                    {
                        _waitForResume = false;
                        this.BeginAnimation();
                    }
                }
            }
        }

        private Animation _animation;
        private CancellationTokenSource _animationToken;

        private void CreateAnimation()
        {
            var loopCount = AnimationBehavior.GetLoopCount(Target) 
                         ?? (Metadata.LoopCount >= 0 ? Metadata.LoopCount + 1 : Metadata.LoopCount);
            _animation = new Animation()
            {
                Duration = TimeSpan.FromMilliseconds(Metadata.Duration),
                IterationCount = new IterationCount((ulong)loopCount),
            };
            if (CurrentTime == 0)
            {
                _animation.Children.Add(new KeyFrame
                {
                    Cue = new Cue(0.0),
                    Setters =
                        {
                            new Setter(AnimationBehavior.AnimationTimeProperty, 0.0)
                        }
                });
                _animation.Children.Add(new KeyFrame
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
                _animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(0.0),
                    Setters =
                        {
                           new Setter(AnimationBehavior.AnimationTimeProperty, currentTime)
                        }
                });
                var timeNode = (Metadata.Duration - currentTime) / Metadata.Duration;
                _animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(timeNode),
                    Setters =
                        {
                           new Setter(AnimationBehavior.AnimationTimeProperty, Metadata.Duration)
                        }
                });
                _animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(timeNode),
                    Setters =
                        {
                           new Setter(AnimationBehavior.AnimationTimeProperty, 0.0)
                        }
                });
                _animation.Children.Add(new KeyFrame()
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
                || _animationToken != null)
                return;

            try
            {
                _animationToken = new CancellationTokenSource();
                this.State = AnimationState.Playing;
                this.UpdateCommandState();
                this.CreateAnimation();
                await _animation.RunAsync(Target, _animationToken.Token);
                if (!_animationToken.IsCancellationRequested)
                {
                    State = AnimationState.Completed;//播放到自然结束
                    AnimationBehavior.SetAnimationTime(Target, Metadata.Duration);
                }
                _animationToken.Dispose();
                _animationToken = null;
            }
            catch { }
        }

        protected virtual void PauseAnimation()
        {
            if (State != AnimationState.Playing)
                return;

            var currentTime = this.CurrentTime;
            _animationToken?.Cancel();
            this.State = AnimationState.Paused;
            this.UpdateCommandState();
            AnimationBehavior.SetAnimationTime(Target, currentTime);

        }

        protected virtual void StopAnimation()
        {
            _animationToken?.Cancel();
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

        internal static WriteableBitmap CreateNewFrame(int width, int height)
        {
            return new WriteableBitmap(new PixelSize(width, height), new Vector(96d, 96d), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
    }
}
