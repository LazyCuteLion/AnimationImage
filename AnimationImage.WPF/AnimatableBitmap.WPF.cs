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
            if (GetAutoStart(target))
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
                    Target.BeginAnimation(AnimationTimeProperty, null);
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
            var repeatBehavior = GetRepeatBehavior(Target)
                ?? (Metadata.LoopCount == -1
                    ? RepeatBehavior.Forever
                    : new RepeatBehavior(Metadata.LoopCount + 1));
            _storyboard = new Storyboard()
            {
                RepeatBehavior = repeatBehavior,
                FillBehavior = FillBehavior.Stop
            };
            var animation = new DoubleAnimation(0, Metadata.Duration, TimeSpan.FromMilliseconds(Metadata.Duration));
            Storyboard.SetTargetProperty(animation, new PropertyPath(AnimationTimeProperty));
            Storyboard.SetTarget(animation, Target);
            _storyboard.Children.Add(animation);
            var forceFPS = GetForceFPS(Target);
            Timeline.SetDesiredFrameRate(_storyboard, forceFPS > 0 ? forceFPS : Metadata.FPS);
            _storyboard.Completed += OnCompleted;
        }

        protected virtual void OnCompleted(object? sender, EventArgs e)
        {
            var time = CurrentTime;
            State = AnimationState.Completed;
            SetAnimationTime(Target, time);
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
                _storyboard.RepeatBehavior = GetRepeatBehavior(Target)
                    ?? (Metadata.LoopCount == -1
                        ? RepeatBehavior.Forever
                        : new RepeatBehavior(Metadata.LoopCount + 1));

                var forceFPS = GetForceFPS(Target);
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
            _waitForResume = false;
            if (Target != null)
            {
                Target.BeginAnimation(AnimationTimeProperty, null);
                SetAnimationTime(Target, 0.0);
            }
            UpdateCommandState();
        }

        internal static WriteableBitmap CreateNewFrame(int width, int height)
        {
            return new WriteableBitmap(width, height, 96d, 96d, PixelFormats.Pbgra32, null);
        }

        #region Attached Properties

        /// <summary>
        /// 动画源，设置 AnimatableBitmap 实例或 Uri 路径（通过 TypeConverter 自动转换）
        /// </summary>
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.RegisterAttached("Source", typeof(AnimatableBitmap), typeof(AnimatableBitmap),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, (s, e) =>
                {
                    if (e.OldValue is AnimatableBitmap old)
                        old.Dispose();
                    if (e.NewValue is AnimatableBitmap b && s is FrameworkElement el)
                        b.AttachTarget(el);
                }));
        public static AnimatableBitmap GetSource(DependencyObject obj) => (AnimatableBitmap)obj.GetValue(SourceProperty);
        public static void SetSource(DependencyObject obj, AnimatableBitmap value) => obj.SetValue(SourceProperty, value);

        /// <summary>
        /// 动画时间点
        /// </summary>
        public static readonly DependencyProperty AnimationTimeProperty =
            DependencyProperty.RegisterAttached("AnimationTime", typeof(double), typeof(AnimatableBitmap),
                new PropertyMetadata(0.0, (s, e) =>
                {
                    if (GetSource(s) is AnimatableBitmap b)
                        b.SeekTime((double)e.NewValue);
                }));
        public static double GetAnimationTime(DependencyObject obj) => (double)obj.GetValue(AnimationTimeProperty);
        public static void SetAnimationTime(DependencyObject obj, double value) => obj.SetValue(AnimationTimeProperty, value);

        /// <summary>
        /// 是否自动开始播放
        /// </summary>
        public static readonly DependencyProperty AutoStartProperty =
            DependencyProperty.RegisterAttached("AutoStart", typeof(bool), typeof(AnimatableBitmap),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, (s, e) =>
                {
#if DEBUG
                    if (GetSource(s) is AnimatableBitmap b)
                    {
                        if (DesignerProperties.GetIsInDesignMode(s))
                        {
                            if (e.NewValue.Equals(true))
                                b.BeginCommand.Execute(null);
                            else
                                b.StopCommand.Execute(null);
                        }
                    }
#endif
                }));
        public static bool GetAutoStart(DependencyObject obj) => (bool)obj.GetValue(AutoStartProperty);
        public static void SetAutoStart(DependencyObject obj, bool value) => obj.SetValue(AutoStartProperty, value);

        /// <summary>
        /// 强制使用指定帧率，0 表示使用动画本身的帧率
        /// </summary>
        public static readonly DependencyProperty ForceFPSProperty =
            DependencyProperty.RegisterAttached("ForceFPS", typeof(int), typeof(AnimatableBitmap),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.Inherits));
        public static int GetForceFPS(DependencyObject obj) => (int)obj.GetValue(ForceFPSProperty);
        public static void SetForceFPS(DependencyObject obj, int value) => obj.SetValue(ForceFPSProperty, value);

        /// <summary>
        /// 循环行为
        /// </summary>
        public static readonly DependencyProperty RepeatBehaviorProperty =
            DependencyProperty.RegisterAttached("RepeatBehavior", typeof(RepeatBehavior?), typeof(AnimatableBitmap),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
        public static RepeatBehavior? GetRepeatBehavior(DependencyObject obj) => (RepeatBehavior?)obj.GetValue(RepeatBehaviorProperty);
        public static void SetRepeatBehavior(DependencyObject obj, RepeatBehavior? value) => obj.SetValue(RepeatBehaviorProperty, value);

        #endregion
    }
}
