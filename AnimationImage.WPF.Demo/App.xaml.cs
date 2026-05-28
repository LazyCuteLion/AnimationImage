using AnimationImage.Core;
using System.Configuration;
using System.Data;
using System.Windows;

namespace AnimationImage.WPF.Demo
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            //对于有复杂场景的Lottie动画，开启显卡加速，帧率提升明显
            //不同于Avalonia，简单场景也有效
            //AnimatableBitmapOptions.Default = new AnimatableBitmapOptions(useGPU: false);
            base.OnStartup(e);
        }
    }

}
