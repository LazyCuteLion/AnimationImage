using AnimationImage.Core;
using Avalonia;
using Avalonia.Controls;


namespace AnimationImage.Avalonia
{
    public class AnimationBehavior
    {
        // 循环次数
        public static readonly AttachedProperty<int?> LoopCountProperty =
            AvaloniaProperty.RegisterAttached<AnimationBehavior, Control, int?>("LoopCount", null);
        public static int? GetLoopCount(Control obj) => obj.GetValue(LoopCountProperty);
        public static void SetLoopCount(Control obj, int? value) => obj.SetValue(LoopCountProperty, value);

        // 动画时间点
        public static readonly AttachedProperty<double> AnimationTimeProperty =
            AvaloniaProperty.RegisterAttached<AnimationBehavior, Control, double>("AnimationTime", 0.0);
        public static double GetAnimationTime(Control obj) => obj.GetValue(AnimationTimeProperty);
        public static void SetAnimationTime(Control obj, double value) => obj.SetValue(AnimationTimeProperty, value);

        // 自动播放
        public static readonly AttachedProperty<bool> AutoStartProperty =
            AvaloniaProperty.RegisterAttached<AnimationBehavior, Control, bool>("AutoStart", true);
        public static bool GetAutoStart(Control obj) => obj.GetValue(AutoStartProperty);
        public static void SetAutoStart(Control obj, bool value) => obj.SetValue(AutoStartProperty, value);

        // 可动画的位图对象
        public static readonly AttachedProperty<AnimatableBitmap?> AnimatableBitmapProperty =
            AvaloniaProperty.RegisterAttached<AnimationBehavior, Control, AnimatableBitmap?>("AnimatableBitmap");
        public static AnimatableBitmap? GetAnimatableBitmap(Control obj) => obj.GetValue(AnimatableBitmapProperty);
        public static void SetAnimatableBitmap(Control obj, AnimatableBitmap? value) => obj.SetValue(AnimatableBitmapProperty, value);


        static AnimationBehavior()
        {
            AnimationTimeProperty.Changed.AddClassHandler<Control>((s, e) =>
            {
                if (GetAnimatableBitmap(s) is AnimatableBitmap b)
                {
                    b.SeekTime((double)e.NewValue!);
                    s.InvalidateVisual();
                }
            });

#if DEBUG
            AutoStartProperty.Changed.AddClassHandler<Control>((s, e) =>
            {
                if (GetAnimatableBitmap(s) is AnimatableBitmap b)
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

            AnimatableBitmapProperty.Changed.AddClassHandler<Control>(async (s, e) =>
            {
                if (e.OldValue is AnimatableBitmap old)
                    old.Dispose();

                if (e.NewValue is AnimatableBitmap b)
                {
                    b.AttachTarget(s);
                }
            });
        }
    }

}
