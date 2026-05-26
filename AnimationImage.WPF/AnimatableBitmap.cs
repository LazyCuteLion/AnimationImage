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
    public abstract partial class AnimatableBitmap
    {
        public virtual async void AttachTarget(FrameworkElement target)
        {
            Target = target;
            if (Target is Image img)
            {
                img.SetBinding(Image.SourceProperty, new Binding(nameof(this.Frame)) { Source = this });
            }
            await Target.WaitForLoadedAsync();
            Target.IsVisibleChanged += Target_IsVisibleChanged;
            Target.Unloaded += Target_Unloaded;
            if (Window.GetWindow(Target) is Window win)
            {
                win.StateChanged += Window_StateChanged;
            }
            if (AnimationBehavior.GetAutoStart(target))
                this.BeginAnimation();
        }

        private void Target_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el)
            {
                el.Unloaded -= Target_Unloaded;
                this.Dispose(true);
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
                    WaitForResume = true;
                }
                else if (WaitForResume)
                {
                    WaitForResume = false;
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
                WaitForResume = true;
            }
            else if (WaitForResume)
            {
                WaitForResume = false;
                this.BeginAnimation();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;
            if (disposing)
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
            IsDisposed = true;
        }

        protected Storyboard Storyboard;
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
            Timeline.SetDesiredFrameRate(Storyboard, forceFPS > 0 ? forceFPS : this.Metadata.FPS);
            Storyboard.Completed += OnCompleted;
        }

        protected virtual void OnCompleted(object? sender, EventArgs e)
        {
            var time = this.CurrentTime;
            this.State = AnimationState.Completed;
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
                Timeline.SetDesiredFrameRate(Storyboard, forceFPS > 0 ? forceFPS : this.Metadata.FPS);
            }

            Storyboard.Begin();

            if (CurrentTime > 0)
            {
                Storyboard.Seek(TimeSpan.FromMilliseconds(CurrentTime));
            }

            this.State = AnimationState.Playing;
            this.UpdateCommandState();
        }

        protected virtual void PauseAnimation()
        {
            if (State != AnimationState.Playing)
                return;
            Storyboard?.Pause();
            this.State = AnimationState.Paused;
            this.UpdateCommandState();
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
            this.UpdateCommandState();
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
