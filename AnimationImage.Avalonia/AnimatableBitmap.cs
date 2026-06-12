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
            if (GetAutoStart(target))
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

        private IterationCount ToIterationCount(RepeatBehavior behavior)
        {
            if (behavior.IsForever) return IterationCount.Infinite;
            return new IterationCount((ulong)behavior.Count);
        }

        private void CreateAnimation()
        {
            var repeatBehavior = GetRepeatBehavior(Target);
            var loopCount = repeatBehavior != null
                         ? ToIterationCount(repeatBehavior.Value)
                         : (Metadata.LoopCount >= 0 ? new IterationCount((ulong)(Metadata.LoopCount + 1)) : IterationCount.Infinite);
            _animation = new Animation()
            {
                Duration = TimeSpan.FromMilliseconds(Metadata.Duration),
                IterationCount = loopCount,
            };
            if (CurrentTime > 0 && CurrentTime < Metadata.Duration)
            {
                // 从暂停处恢复：当前时间=》结束时间&归零=》当前时间
                var currentTime = CurrentTime;
                _animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(0.0),
                    Setters =
                    {
                        new Setter(AnimationTimeProperty, currentTime)
                    }
                });
                var timeNode = (Metadata.Duration - currentTime) / Metadata.Duration;
                _animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(timeNode),
                    Setters =
                    {
                        new Setter(AnimationTimeProperty, Metadata.Duration)
                    }
                });
                _animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(timeNode),
                    Setters =
                    {
                        new Setter(AnimationTimeProperty, 0.0)
                    }
                });
                _animation.Children.Add(new KeyFrame()
                {
                    Cue = new Cue(1.0),
                    Setters =
                    {
                        new Setter(AnimationTimeProperty, currentTime)
                    }
                });
            }
            else
            {
                CurrentTime = 0;
                _animation.Children.Add(new KeyFrame
                {
                    Cue = new Cue(0.0),
                    Setters =
                    {
                        new Setter(AnimationTimeProperty, 0.0)
                    }
                });
                _animation.Children.Add(new KeyFrame
                {
                    Cue = new Cue(1.0),
                    Setters =
                    {
                        new Setter(AnimationTimeProperty, Metadata.Duration)
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
                    SetAnimationTime(Target, Metadata.Duration);
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
            SetAnimationTime(Target, currentTime);
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
            SetAnimationTime(Target, 0.0);
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

        #region Attached Properties

        /// <summary>
        /// 动画源，设置 AnimatableBitmap 实例或 Uri 路径（通过 TypeConverter 自动转换）
        /// </summary>
        public static readonly AttachedProperty<AnimatableBitmap?> SourceProperty =
            AvaloniaProperty.RegisterAttached<AnimatableBitmap, Control, AnimatableBitmap?>("Source");
        public static AnimatableBitmap? GetSource(Control obj) => obj.GetValue(SourceProperty);
        public static void SetSource(Control obj, AnimatableBitmap? value) => obj.SetValue(SourceProperty, value);

        /// <summary>
        /// 动画时间点
        /// </summary>
        public static readonly AttachedProperty<double> AnimationTimeProperty =
            AvaloniaProperty.RegisterAttached<AnimatableBitmap, Control, double>("AnimationTime", 0.0);
        public static double GetAnimationTime(Control obj) => obj.GetValue(AnimationTimeProperty);
        public static void SetAnimationTime(Control obj, double value) => obj.SetValue(AnimationTimeProperty, value);

        /// <summary>
        /// 是否自动播放
        /// </summary>
        public static readonly AttachedProperty<bool> AutoStartProperty =
            AvaloniaProperty.RegisterAttached<AnimatableBitmap, Control, bool>("AutoStart", true, true);
        public static bool GetAutoStart(Control obj) => obj.GetValue(AutoStartProperty);
        public static void SetAutoStart(Control obj, bool value) => obj.SetValue(AutoStartProperty, value);

        /// <summary>
        /// 循环行为
        /// </summary>
        public static readonly AttachedProperty<RepeatBehavior?> RepeatBehaviorProperty =
            AvaloniaProperty.RegisterAttached<AnimatableBitmap, Control, RepeatBehavior?>("RepeatBehavior", null, true);
        public static RepeatBehavior? GetRepeatBehavior(Control obj) => obj.GetValue(RepeatBehaviorProperty);
        public static void SetRepeatBehavior(Control obj, RepeatBehavior? value) => obj.SetValue(RepeatBehaviorProperty, value);

        static AnimatableBitmap()
        {
            AnimationTimeProperty.Changed.AddClassHandler<Control>((s, e) =>
            {
                if (GetSource(s) is AnimatableBitmap b)
                {
                    b.SeekTime((double)e.NewValue!);
                    s.InvalidateVisual();
                }
            });

#if DEBUG
            AutoStartProperty.Changed.AddClassHandler<Control>((s, e) =>
            {
                if (GetSource(s) is AnimatableBitmap b)
                {
                    if (Design.IsDesignMode)
                    {
                        if ((bool)e.NewValue!)
                            b.BeginCommand.Execute(null);
                        else
                            b.StopCommand.Execute(null);
                    }
                }
            });
#endif

            SourceProperty.Changed.AddClassHandler<Control>(async (s, e) =>
            {
                if (e.OldValue is AnimatableBitmap old)
                    old.Dispose();
                // 切换源时重置 AnimationTime，防止旧动画残留时间通过 Changed 回调写入新实例
                SetAnimationTime(s, 0.0);
                if (e.NewValue is AnimatableBitmap b)
                    b.AttachTarget(s);
            });

#if DEBUG
            EnableTPS = true;
#endif
        }

        #endregion
    }
}
