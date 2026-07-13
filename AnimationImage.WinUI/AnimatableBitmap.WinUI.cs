using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace AnimationImage
{
    /// <summary>
    /// 动图数据模型 POCO 基类。
    /// 呈现路径：派生类把每帧像素写入 <see cref="CompositionHost"/> BackBuffer（CPU 分支）
    /// 或通过 <see cref="CompositionHost.Render"/> 直接绘制到 GPU 纹理（GPU 分支），
    /// 由 <see cref="ElementCompositionPreview.SetElementChildVisual"/> 把 host 的
    /// <see cref="SpriteVisual"/> 挂到目标元素上完成合成。
    /// </summary>
    public abstract partial class AnimatableBitmap
    {
        /// <summary>Composition 呈现宿主；派生类通过 <see cref="EnsureHost"/> 创建或替换。</summary>
        /// <remarks>字段为 internal（而非 protected）：<see cref="CompositionHost"/> 是实现细节保持 internal，
        /// 同 assembly 内的派生类（SKCodecBitmap / ApngBitmap / SkottieBitmap）可直接访问。</remarks>
        internal CompositionHost? _host;

        #region 释放

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (!disposing) return;

            CompositionTarget.Rendering -= OnRendering;

            if (Target != null)
            {
                if (_host != null)
                    ElementCompositionPreview.SetElementChildVisual(Target, null);
                Target.SizeChanged -= OnTargetSizeChanged;
                Target.Unloaded -= OnTargetUnloaded;
                if (Target.XamlRoot != null)
                    Target.XamlRoot.Changed -= OnXamlRootChanged;
                Target = null;
            }

            _host?.Dispose();
            _host = null;

            _stream?.Dispose();
            _stream = null;
            _tpsWatcher?.Stop();
        }

        #endregion

        #region 呈现挂载

        /// <summary>
        /// 挂到宿主元素上：把内部 <see cref="CompositionHost.Visual"/> 通过
        /// <see cref="ElementCompositionPreview.SetElementChildVisual"/> 挂到目标，
        /// 同时订阅 SizeChanged / Unloaded / XamlRoot.Changed 生命周期事件。
        /// </summary>
        public virtual void AttachTarget(FrameworkElement target)
        {
            if (Target == target) return;
            Target = target;

            EnsureHost();
            if (_host != null)
                ElementCompositionPreview.SetElementChildVisual(target, _host.Visual);
            SyncHostSize();
            SyncHostStretch();

            target.SizeChanged += OnTargetSizeChanged;
            target.Unloaded += OnTargetUnloaded;
            if (target.XamlRoot != null)
                target.XamlRoot.Changed += OnXamlRootChanged;

            if (GetAutoStart(target))
                BeginAnimation();
            else
                SeekTime(0);
        }

        /// <summary>
        /// 首帧解码/渲染前调用；若 host 已存在（如 GPU 派生类先行创建过）则短路，不覆盖。
        /// </summary>
        protected void EnsureHost(int? width = null, int? height = null)
        {
            if (_host != null) return;
            var w = width ?? (Metadata?.PixelWidth ?? 1);
            var h = height ?? (Metadata?.PixelHeight ?? 1);
            if (w <= 0) w = 1;
            if (h <= 0) h = 1;
            _host = new CompositionHost(w, h);
        }

        private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) => SyncHostSize();

        private void SyncHostSize()
        {
            if (_host == null || Target == null) return;
            var w = (float)Target.ActualWidth;
            var h = (float)Target.ActualHeight;
            if (w > 0 && h > 0) _host.SetVisualSize(w, h);
        }

        private void SyncHostStretch()
        {
            if (_host == null || Target == null) return;
            _host.SetStretch(GetStretch(Target));
        }

        private void OnTargetUnloaded(object sender, RoutedEventArgs e) => Dispose();

        private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            if (!sender.IsHostVisible)
            {
                if (State == AnimationState.Playing)
                {
                    PauseAnimation();
                    _waitForResume = true;
                }
            }
            else if (_waitForResume)
            {
                _waitForResume = false;
                BeginAnimation();
            }
        }

        #endregion

        #region 时间线驱动

        // WinUI 3 已知限制（microsoft-ui-xaml#8462）：
        // Storyboard 无法对自定义 DependencyObject/DependencyProperty 做代码级动画。
        // 因此用 CompositionTarget.Rendering 逐帧驱动。

        private DateTimeOffset _startTime;
        private int _loop;                   // 剩余循环次数（-1 = Forever）

        private void OnRendering(object? sender, object args)
        {
            if (_disposed || Target == null || Metadata == null) return;
            var duration = Metadata.Duration;
            if (duration <= 0) { SetAnimationTime(Target, 0); return; }

            var elapsed = (DateTimeOffset.Now - _startTime).TotalMilliseconds;

            if (elapsed >= duration)
            {
                // 完成一轮：起点前移 duration，剩余次数-1
                _startTime = DateTimeOffset.Now;
                elapsed -= duration;
                if (_loop > 0 && --_loop == 0)
                {
                    SetAnimationTime(Target, duration);
                    CompositionTarget.Rendering -= OnRendering;
                    State = AnimationState.Completed;
                    return;
                }
            }

            SetAnimationTime(Target, elapsed);
        }

        private int ResolveLoop()
        {
            var rb = Target != null ? GetRepeatBehavior(Target) : null;
            if (rb.HasValue)
            {
                if (rb.Value == RepeatBehavior.Forever) return -1;
                if (rb.Value.Type == RepeatBehaviorType.Count)
                    return Math.Max(1, (int)rb.Value.Count);
                return 1;
            }
            if (Metadata == null) return 1;
            return Metadata.LoopCount == -1 ? -1 : Math.Max(1, Metadata.LoopCount + 1);
        }

        protected virtual void BeginAnimation()
        {
            if (!IsAnimatable || State == AnimationState.Playing) return;
            if (Target == null || Metadata == null) return;

            if (State == AnimationState.Completed) CurrentTime = 0;

            // 暂停恢复 / 首次播放：起点 = 现在 - 已过时间
            _startTime = DateTimeOffset.Now.AddMilliseconds(-CurrentTime);
            _loop = ResolveLoop();
            CompositionTarget.Rendering += OnRendering;
            State = AnimationState.Playing;
        }

        protected virtual void PauseAnimation()
        {
            if (State != AnimationState.Playing) return;
            // 记录当前 elapsed 到 CurrentTime，下次 Resume 时用于重建起点
            CurrentTime = (DateTimeOffset.Now - _startTime).TotalMilliseconds;
            CompositionTarget.Rendering -= OnRendering;
            State = AnimationState.Paused;
        }

        protected virtual void StopAnimation()
        {
            CompositionTarget.Rendering -= OnRendering;
            State = AnimationState.Stopped;
            _waitForResume = false;
            if (Target != null) SetAnimationTime(Target, 0);
        }

        #endregion

        #region 附加属性

        /// <summary>动图数据模型附加到目标元素；值为 <see cref="AnimatableBitmap"/> 实例。</summary>
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.RegisterAttached("Source", typeof(AnimatableBitmap), typeof(AnimatableBitmap),
                new PropertyMetadata(null, (s, e) =>
                {
                    if (e.OldValue is AnimatableBitmap old)
                        old.Dispose();
                    if (e.NewValue is AnimatableBitmap b && s is FrameworkElement el)
                    {
                        if (el.IsLoaded) b.AttachTarget(el);
                        else
                        {
                            RoutedEventHandler? handler = null;
                            handler = (_, _) =>
                            {
                                el.Loaded -= handler;
                                b.AttachTarget(el);
                            };
                            el.Loaded += handler;
                        }
                    }
                }));
        public static AnimatableBitmap GetSource(DependencyObject obj) => (AnimatableBitmap)obj.GetValue(SourceProperty);
        public static void SetSource(DependencyObject obj, AnimatableBitmap value) => obj.SetValue(SourceProperty, value);

        /// <summary>动画时间点（毫秒）：附加属性，由 CompositionTarget.Rendering 逐帧写入，也支持双向绑定进度条。</summary>
        public static readonly DependencyProperty AnimationTimeProperty =
            DependencyProperty.RegisterAttached("AnimationTime", typeof(double), typeof(AnimatableBitmap),
                new PropertyMetadata(0.0, (s, e) =>
                {
                    if (GetSource(s) is AnimatableBitmap b)
                        b.SeekTime((double)e.NewValue);
                }));
        public static double GetAnimationTime(DependencyObject obj) => (double)obj.GetValue(AnimationTimeProperty);
        public static void SetAnimationTime(DependencyObject obj, double value) => obj.SetValue(AnimationTimeProperty, value);

        /// <summary>是否自动开始播放。</summary>
        public static readonly DependencyProperty AutoStartProperty =
            DependencyProperty.RegisterAttached("AutoStart", typeof(bool), typeof(AnimatableBitmap),
                new PropertyMetadata(true));
        public static bool GetAutoStart(DependencyObject obj) => (bool)obj.GetValue(AutoStartProperty);
        public static void SetAutoStart(DependencyObject obj, bool value) => obj.SetValue(AutoStartProperty, value);

        /// <summary>循环行为；未设置时按图源 LoopCount 决定。</summary>
        public static readonly DependencyProperty RepeatBehaviorProperty =
            DependencyProperty.RegisterAttached("RepeatBehavior", typeof(RepeatBehavior?),
                typeof(AnimatableBitmap), new PropertyMetadata(null));
        public static RepeatBehavior? GetRepeatBehavior(DependencyObject obj)
            => (RepeatBehavior?)obj.GetValue(RepeatBehaviorProperty);
        public static void SetRepeatBehavior(DependencyObject obj, RepeatBehavior? value)
            => obj.SetValue(RepeatBehaviorProperty, value);

        /// <summary>拉伸模式；映射为 <see cref="CompositionSurfaceBrush.Stretch"/>。</summary>
        public static readonly DependencyProperty StretchProperty =
            DependencyProperty.RegisterAttached("Stretch", typeof(Stretch), typeof(AnimatableBitmap),
                new PropertyMetadata(Stretch.Uniform, (s, e) =>
                {
                    if (GetSource(s) is AnimatableBitmap b)
                        b._host?.SetStretch((Stretch)e.NewValue);
                }));
        public static Stretch GetStretch(DependencyObject obj) => (Stretch)obj.GetValue(StretchProperty);
        public static void SetStretch(DependencyObject obj, Stretch value) => obj.SetValue(StretchProperty, value);

        #endregion
    }
}
