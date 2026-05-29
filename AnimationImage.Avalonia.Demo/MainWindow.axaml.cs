using AnimationImage.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AnimationImage.Avalonia.Demo
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            this.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.Fps;

        }

        private async void Button_Click(object? sender, RoutedEventArgs e)
        {
            var file = await GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
            {
                //FileTypeFilter =
                //[
                //    new FilePickerFileType("动画文件")
                //    {
                //        Patterns = ["*.json","*.gif","*.webp","*.png","*.avif"]
                //    }
                //],
            });
            if (file?.Count > 0)
            {
                //查看启用显卡加速的影响
                var bitmap = AnimatableBitmapFactory.Default.Create(new AnimatableBitmapOptions(file[0].Path,
                                                                    preloadCount: int.Parse(tbPreloadCount.Text),
                                                                    useGPU: cbUseGPU.IsChecked ?? false));
                AnimationBehavior.SetAnimatableBitmap(view, bitmap);
            }
        }

    }
}