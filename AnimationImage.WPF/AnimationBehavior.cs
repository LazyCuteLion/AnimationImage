using AnimationImage.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AnimationImage.WPF
{
    public class AnimationBehavior
    {
        /**
         * 强制使用指定帧率，依赖于显示器刷新率以及机器性能
         * 0：默认值，表示使用动画本身的帧率
         **/
        public static int GetForceFPS(DependencyObject obj)
        {
            return (int)obj.GetValue(ForceFPSProperty);
        }
        public static void SetForceFPS(DependencyObject obj, int value)
        {
            obj.SetValue(ForceFPSProperty, value);
        }
        public static readonly DependencyProperty ForceFPSProperty =
            DependencyProperty.RegisterAttached("ForceFPS", typeof(int), typeof(AnimationBehavior), new PropertyMetadata(0));

        /**
         * 循环次数
         * null：默认值，表示使用动画本身的循环设置
         **/
        public static RepeatBehavior? GetRepeatBehavior(DependencyObject obj)
        {
            return (RepeatBehavior?)obj.GetValue(RepeatBehaviorProperty);
        }
        public static void SetRepeatBehavior(DependencyObject obj, RepeatBehavior? value)
        {
            obj.SetValue(RepeatBehaviorProperty, value);
        }
        public static readonly DependencyProperty RepeatBehaviorProperty =
            DependencyProperty.RegisterAttached("RepeatBehavior", typeof(RepeatBehavior?), typeof(AnimationBehavior), new PropertyMetadata(null));

        /**
         * 动画时间点
         * */
        public static double GetAnimationTime(DependencyObject obj)
        {
            return (double)obj.GetValue(AnimationTimeProperty);
        }
        public static void SetAnimationTime(DependencyObject obj, double value)
        {
            obj.SetValue(AnimationTimeProperty, value);
        }
        public static readonly DependencyProperty AnimationTimeProperty =
            DependencyProperty.RegisterAttached("AnimationTime", typeof(double), typeof(AnimationBehavior), new PropertyMetadata(0.0, (s, e) =>
            {
                if (GetAnimatableBitmap(s) is AnimatableBitmap b)
                {
                    b.SeekTime((double)e.NewValue);
                }
            }));

        /**
         * 是否自动开始播放
         * 在设计器模式下，播放/停止
         * */
        public static bool GetAutoStart(DependencyObject obj)
        {
            return (bool)obj.GetValue(AutoStartProperty);
        }
        public static void SetAutoStart(DependencyObject obj, bool value)
        {
            obj.SetValue(AutoStartProperty, value);
        }
        public static readonly DependencyProperty AutoStartProperty =
            DependencyProperty.RegisterAttached("AutoStart", typeof(bool), typeof(AnimationBehavior), new PropertyMetadata(true, (s, e) =>
            {
                if (GetAnimatableBitmap(s) is AnimatableBitmap b)
                {
                    if (DesignerProperties.GetIsInDesignMode(s))
                    {
                        // 设计器模式下，修改该值即时生效，当成播放控制
                        if (e.NewValue.Equals(true))
                            b.BeginCommand.Execute(null);
                        else
                            b.StopCommand.Execute(null);
                    }
                }
            }));


        /**
         * 获取或设置可动画的位图对象
         * */
        public static AnimatableBitmap GetAnimatableBitmap(DependencyObject obj)
        {
            return (AnimatableBitmap)obj.GetValue(AnimatableBitmapProperty);
        }
        public static void SetAnimatableBitmap(DependencyObject obj, AnimatableBitmap value)
        {
            obj.SetValue(AnimatableBitmapProperty, value);
        }
        public static readonly DependencyProperty AnimatableBitmapProperty =
            DependencyProperty.RegisterAttached("AnimatableBitmap", typeof(AnimatableBitmap), typeof(AnimationBehavior), new PropertyMetadata(null, async (s, e) =>
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

                        await el.WaitForLoadedAsync();
                        if (GetAutoStart(s))
                            b.BeginCommand.Execute(null);
                    }
                }
            }));

    }
}
