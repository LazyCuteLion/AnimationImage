using Microsoft.VisualBasic;
using Microsoft.Win32;
using SkiaSharp;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AnimationImage.WPF.Demo
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog() == true)
            {
                var s = AnimatableBitmapFactory.Default.Create(new AnimatableBitmapOptions(dialog.FileName,
                                                               preloadCount: int.Parse(tbPreloadCount.Text),
                                                               useGPU: cbUseGPU.IsChecked ?? false));
                AnimationBehavior.SetAnimatableBitmap(img, s);
            }
        }
    }
}