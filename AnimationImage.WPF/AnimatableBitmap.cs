using SkiaSharp;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AnimationImage
{
    public abstract partial class AnimatableBitmap
    {
        public virtual async void AttachTarget(FrameworkElement target)
        {
            Target = target;
            if (Target is Image img)
            {
                img.SetBinding(Image.SourceProperty, new Binding(nameof(Frame)) { Source = this });
            }
            await Target.WaitForLoadedAsync();
            Target.IsVisibleChanged += Target_IsVisibleChanged;
            Target.Unloaded += Target_Unloaded;
            if (Window.GetWindow(Target) is Window win)
            {
                win.StateChanged += Window_StateChanged;
            }
            if (AnimationBehavior.GetAutoStart(target))
                BeginAnimation();
        }

        private void Target_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el)
            {
                el.Unloaded -= Target_Unloaded;
                Dispose(true);
            }
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (Target == null) return;
            if (sender is Window win)
            {
                if (win.WindowState == WindowState.Minimized && State == AnimationState.Playing)
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

        private void Target_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (Target == null) return;
            if (e.NewValue.Equals(false) && State == AnimationState.Playing)
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

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (disposing)
            {
                if (_storyboard != null)
                {
                    _storyboard.Completed -= OnCompleted;
                    _storyboard.Stop();
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

                Frame = null;

                _stream?.Dispose();

                _tpsWatcher?.Stop();
            }
        }

        protected Storyboard _storyboard;
        private void CreateAnimation()
        {
            var repeatBehavior = AnimationBehavior.GetRepeatBehavior(Target)
                ?? (Metadata.LoopCount == -1
                    ? RepeatBehavior.Forever
                    : new RepeatBehavior(Metadata.LoopCount + 1));
            _storyboard = new Storyboard()
            {
                RepeatBehavior = repeatBehavior,
                FillBehavior = FillBehavior.Stop
            };
            var animation = new DoubleAnimation(0, Metadata.Duration, TimeSpan.FromMilliseconds(Metadata.Duration));
            Storyboard.SetTargetProperty(animation, new PropertyPath(AnimationBehavior.AnimationTimeProperty));
            Storyboard.SetTarget(animation, Target);
            _storyboard.Children.Add(animation);
            var forceFPS = AnimationBehavior.GetForceFPS(Target);
            Timeline.SetDesiredFrameRate(_storyboard, forceFPS > 0 ? forceFPS : Metadata.FPS);
            _storyboard.Completed += OnCompleted;
        }

        protected virtual void OnCompleted(object? sender, EventArgs e)
        {
            var time = CurrentTime;
            State = AnimationState.Completed;
            AnimationBehavior.SetAnimationTime(Target, time);
            CommandManager.InvalidateRequerySuggested();
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

            if (State == AnimationState.Paused && _storyboard != null)
            {
                _storyboard.Resume();
                State = AnimationState.Playing;
                return;
            }

            if (_storyboard == null)
                CreateAnimation();
            else
            {
                _storyboard.RepeatBehavior = AnimationBehavior.GetRepeatBehavior(Target)
                    ?? (Metadata.LoopCount == -1
                        ? RepeatBehavior.Forever
                        : new RepeatBehavior(Metadata.LoopCount + 1));

                var forceFPS = AnimationBehavior.GetForceFPS(Target);
                Timeline.SetDesiredFrameRate(_storyboard, forceFPS > 0 ? forceFPS : Metadata.FPS);
            }

            _storyboard.Begin();

            if (CurrentTime > 0)
            {
                _storyboard.Seek(TimeSpan.FromMilliseconds(CurrentTime));
            }

            State = AnimationState.Playing;
            UpdateCommandState();
        }

        protected virtual void PauseAnimation()
        {
            if (State != AnimationState.Playing)
                return;
            _storyboard?.Pause();
            State = AnimationState.Paused;
            UpdateCommandState();
        }

        protected virtual void StopAnimation()
        {
            _storyboard?.Stop();
            State = AnimationState.Stopped;
            if (Target != null)
            {
                Target.BeginAnimation(AnimationBehavior.AnimationTimeProperty, null);
                AnimationBehavior.SetAnimationTime(Target, 0.0);
            }
            UpdateCommandState();
        }

        protected void UpdateCommandState()
        {
            CommandManager.InvalidateRequerySuggested();
        }

        internal static WriteableBitmap CreateNewFrame(int width, int height)
        {
            return new WriteableBitmap(width, height, 96d, 96d, PixelFormats.Pbgra32, null);
        }
    }
}
