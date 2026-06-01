using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AnimationImage
{
    public class AnimationBehavior
    {
        /// <summary>
        /// 强制使用指定帧率，依赖于显示器刷新率以及机器性能。
        /// 0：默认值，表示使用动画本身的帧率
        /// </summary>
        public static int GetForceFPS(DependencyObject obj)
        {
            return (int)obj.GetValue(ForceFPSProperty);
        }
        public static void SetForceFPS(DependencyObject obj, int value)
        {
            obj.SetValue(ForceFPSProperty, value);
        }
        public static readonly DependencyProperty ForceFPSProperty =
            DependencyProperty.RegisterAttached("ForceFPS", typeof(int), typeof(AnimationBehavior),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>
        /// 循环次数。
        /// null：默认值，表示使用动画本身的循环设置
        /// </summary>
        public static RepeatBehavior? GetRepeatBehavior(DependencyObject obj)
        {
            return (RepeatBehavior?)obj.GetValue(RepeatBehaviorProperty);
        }
        public static void SetRepeatBehavior(DependencyObject obj, RepeatBehavior? value)
        {
            obj.SetValue(RepeatBehaviorProperty, value);
        }
        public static readonly DependencyProperty RepeatBehaviorProperty =
            DependencyProperty.RegisterAttached("RepeatBehavior", typeof(RepeatBehavior?), typeof(AnimationBehavior),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>
        /// 是否自动开始播放。
        /// 在设计器模式下，播放/停止
        /// </summary>
        public static bool GetAutoStart(DependencyObject obj)
        {
            return (bool)obj.GetValue(AutoStartProperty);
        }
        public static void SetAutoStart(DependencyObject obj, bool value)
        {
            obj.SetValue(AutoStartProperty, value);
        }
        public static readonly DependencyProperty AutoStartProperty =
            DependencyProperty.RegisterAttached("AutoStart", typeof(bool), typeof(AnimationBehavior),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, (s, e) =>
                {
#if DEBUG
                    if (GetAnimatableBitmap(s) is AnimatableBitmap b)
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

        /// <summary>
        /// 动画时间点
        /// </summary>
        public static double GetAnimationTime(DependencyObject obj)
        {
            return (double)obj.GetValue(AnimationTimeProperty);
        }
        public static void SetAnimationTime(DependencyObject obj, double value)
        {
            obj.SetValue(AnimationTimeProperty, value);
        }
        public static readonly DependencyProperty AnimationTimeProperty =
            DependencyProperty.RegisterAttached("AnimationTime", typeof(double), typeof(AnimationBehavior),
                new PropertyMetadata(0.0, (s, e) =>
                {
                    if (GetAnimatableBitmap(s) is AnimatableBitmap b)
                    {
                        b.SeekTime((double)e.NewValue);
                    }
                }));

        /// <summary>
        /// 获取或设置可动画的位图对象
        /// </summary>
        public static AnimatableBitmap GetAnimatableBitmap(DependencyObject obj)
        {
            return (AnimatableBitmap)obj.GetValue(AnimatableBitmapProperty);
        }
        public static void SetAnimatableBitmap(DependencyObject obj, AnimatableBitmap value)
        {
            obj.SetValue(AnimatableBitmapProperty, value);
        }
        public static readonly DependencyProperty AnimatableBitmapProperty =
            DependencyProperty.RegisterAttached("AnimatableBitmap", typeof(AnimatableBitmap), typeof(AnimationBehavior),
                new PropertyMetadata(null, (s, e) =>
                {
                    if (e.OldValue is AnimatableBitmap old)
                    {
                        old.Dispose();
                    }

                    if (e.NewValue is AnimatableBitmap b)
                    {
                        if (s is FrameworkElement el)
                        {
                            b.AttachTarget(el);
                        }
                    }
                }));
    }
}
