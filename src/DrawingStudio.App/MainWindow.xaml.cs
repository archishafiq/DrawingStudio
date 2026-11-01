using System.Windows;
using System.Windows.Controls;

namespace DrawingStudio.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        // 3D viewer camera presets (Iso / Top / Front / Right)
        private void View3D_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b) Viewer3D.SetView(b.Tag as string);
        }

        private void Layers_All(object sender, RoutedEventArgs e) => Viewer3D.ShowAll(true);

        private void Layers_None(object sender, RoutedEventArgs e) => Viewer3D.ShowAll(false);
    }
}
