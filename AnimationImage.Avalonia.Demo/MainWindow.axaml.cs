using AnimationImage.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering;
using Avalonia.Threading;
using SkiaSharp;
using System;

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
                var bitmap = AnimatableBitmapFactory.Default.Create(file[0].Path);
                AnimationBehavior.SetAnimatableBitmap(view, bitmap);
            }
        }

    }
}