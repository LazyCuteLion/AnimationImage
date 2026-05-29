using AnimationImage.Core;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AnimationImage.Avalonia.Demo
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            //对于有复杂场景（变化较大）的Lottie动画，开启显卡加速，帧率提升明显
            //但是，对于简单的，帧率反而下降，原因未知（大概是因为从显存拷贝像素比较耗时？）
            //又有新发现，似乎是受调试模式影响，若不调试仅启动，则帧率不受影响。
            //AnimatableBitmapOptions.Default = new AnimatableBitmapOptions()
            //{
            //    UseGPU = false,
            //    PreloadCount = PreloadOptions.Full
            //};
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}