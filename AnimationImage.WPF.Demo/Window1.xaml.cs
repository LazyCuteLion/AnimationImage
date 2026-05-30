using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AnimationImage.WPF.Demo
{
    /// <summary>
    /// Window1.xaml 的交互逻辑
    /// </summary>
    public partial class Window1 : Window
    {
        public Window1()
        {
            InitializeComponent();
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource is TabControl tab)
            {
                var item = tab.SelectedItem as TabItem;
                item.Content = Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), item.Tag.ToString(), SearchOption.AllDirectories);
            }
        }

        private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Image img && AnimationBehavior.GetAnimatableBitmap(img) is { } b)
            {
                if (b.State != AnimationState.Playing)
                    b.BeginCommand.Execute(null);
                else
                    b.PauseCommand.Execute(null);
            }
        }
    }
}
