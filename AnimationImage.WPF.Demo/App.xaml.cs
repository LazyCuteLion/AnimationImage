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
            //    Preload = false,//禁用预解析帧数据到内存映射
            //};
            base.OnStartup(e);
        }
    }

}
