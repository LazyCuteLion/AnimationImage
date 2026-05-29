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
            //修改默认设置
            //AnimatableBitmapOptions.Default = new AnimatableBitmapOptions()
            //{
            //    UseGPU = false,//禁用显卡加速
            //    PreloadCount = PreloadOptions.Disable,//禁用预加载和缓存
            //};
            base.OnStartup(e);
        }
    }

}
