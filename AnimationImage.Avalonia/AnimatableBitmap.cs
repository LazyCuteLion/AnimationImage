using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FrameworkElement = Avalonia.Controls.Control;

namespace AnimationImage
{
    public abstract partial class AnimatableBitmap
    {
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (disposing)
            {
                // 释放托管资源
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

                Frame?.Dispose();

                _stream?.Dispose();

                _tpsWatcher?.Stop();
            }
            // 释放非托管资源

        }

        public virtual async void AttachTarget(FrameworkElement target)
        {
            Target = target;
            if (Target is Image img)
            {
                img.Bind(Image.SourceProperty, new Binding(nameof(Frame)) { Source = this });
            }
            await Target.WaitForLoadedAsync();
            Target.PropertyChanged += Target_PropertyChanged;
            Target.DetachedFromVisualTree += Target_DetachedFromVisualTree;
            if (TopLevel.GetTopLevel(Target) is Window win)
            {
                win.PropertyChanged += Target_PropertyChanged;
            }
            if (AnimationBehavior.GetAutoStart(target))
                BeginAnimation();
        }

        private void Target_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is FrameworkElement el)
            {
                el.DetachedFromVisualTree -= Target_DetachedFromVisualTree;
                Dispose(true);
            }
        }

        private void Target_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (Target == null || _disposed) return;
            if (e.Property == FrameworkElement.IsVisibleProperty)
            {
                if (e.NewValue?.Equals(false) == true && State == AnimationState.Playing)
                {
                    PauseAnimation();
                    _waitForResume = true;
                }
                else if (_waitForResume)
                {
                    _waitForResume = false;
                    BeginAnimation();
                }
            }
            else if (e.Property == Window.WindowStateProperty)
            {
                if (e.NewValue is WindowState state)
                {
                    if (state == WindowState.Minimized && State == AnimationState.Playing)
                    {
                        PauseAnimation();
                        _waitForResume = true;
                    }
                    else if (_waitForResume)
                    {
                        _waitForResume = false;
                        BeginAnimation();
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
                // 当前时间=》结束时间&归零=》当前时间
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
                if (State == AnimationState.Completed)
                    CurrentTime = 0;
                State = AnimationState.Playing;
                UpdateCommandState();
                CreateAnimation();
                await _animation.RunAsync(Target, _animationToken.Token);
                if (!_animationToken.IsCancellationRequested)
                {
                    State = AnimationState.Completed; // 播放到自然结束
                    AnimationBehavior.SetAnimationTime(Target, Metadata.Duration);
                }
                _animationToken.Dispose();
                _animationToken = null;
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.WriteLine($"Avalonia动画播放异常：{e.Message}");
            }
        }

        protected virtual void PauseAnimation()
        {
            if (State != AnimationState.Playing)
                return;

            var currentTime = CurrentTime;
            if (_animationToken != null)
            {
                _animationToken.Cancel();
                _animationToken.Dispose();
                _animationToken = null;
            }
            State = AnimationState.Paused;
            UpdateCommandState();
            AnimationBehavior.SetAnimationTime(Target, currentTime);
        }

        protected virtual void StopAnimation()
        {
            if (_animationToken != null)
            {
                _animationToken.Cancel();
                _animationToken.Dispose();
                _animationToken = null;
            }
            State = AnimationState.Stopped;
            _waitForResume = false;
            UpdateCommandState();
            AnimationBehavior.SetAnimationTime(Target, 0.0);
        }

        protected void UpdateCommandState()
        {
            (BeginCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        internal static WriteableBitmap CreateNewFrame(int width, int height)
        {
            return new WriteableBitmap(new PixelSize(width, height), new Vector(96d, 96d), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
    }
}
