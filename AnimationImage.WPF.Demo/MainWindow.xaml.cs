using AnimationImage.Core;
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
            var s = AnimatableBitmapFactory.Default.Create(new Uri("https://cdn.pixabay.com/animation/2023/11/09/03/05/03-05-45-320_512.gif"));
            AnimationBehavior.SetAnimatableBitmap(img, s);
            //var dialog = new OpenFileDialog();
            //if (dialog.ShowDialog() == true)
            //{
            //    var s = AnimatableBitmapFactory.Default.Create(new Uri(dialog.FileName));
            //    AnimationBehavior.SetAnimatableBitmap(img, s);
            //}
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
        }

       
    }


}