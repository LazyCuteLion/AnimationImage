using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AnimationImage.Avalonia.Demo
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            //调整默认的初始化设置
            //AnimatableBitmapOptions.Default = new AnimatableBitmapOptions()
            //{
            //    UseGPU = false,//禁用显卡加速
            //    PreloadCount = PreloadOptions.Disable,//禁用预加载和缓存
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