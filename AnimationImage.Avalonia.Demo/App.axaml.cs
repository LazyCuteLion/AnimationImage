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
            //AnimatableBitmapOptions.Default = new AnimatableBitmapOptions(useGPU: false);
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