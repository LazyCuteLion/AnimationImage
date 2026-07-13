using AnimationImage.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AnimationImage.WinUI.Demo
{
    public sealed partial class MainWindow : Window
    {
        /// <summary>Stretch 枚举的下拉显示包装（规避 WinRT 值类型 ToString 显示全名）。</summary>
        public sealed class StretchOption
        {
            public StretchOption(Stretch value) => Value = value;
            public Stretch Value { get; }
            public override string ToString() => Value.ToString();
        }

        public List<StretchOption> StretchValues { get; } =
            Enum.GetValues<Stretch>().Select(v => new StretchOption(v)).ToList();

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".apng");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                var options = new AnimatableBitmapOptions(file.Path,
                    useGPU: cbUseGPU.IsChecked ?? false,
                    preload: cbPreload.IsChecked ?? false);
                var bitmap = AnimatableBitmapFactory.Default.Create(options);
                AnimatableBitmap.SetSource(img, bitmap);
            }
        }
    }
}
