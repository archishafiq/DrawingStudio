using System.Windows;
using System.Windows.Controls;
using DrawingStudio.App.ViewModels;

namespace DrawingStudio.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private MainViewModel Vm => DataContext as MainViewModel;

        // Blue "File" button opens its menu, like Revit's File tab.
        private void FileButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = FileButton.ContextMenu;
            menu.DataContext = DataContext;
            menu.PlacementTarget = FileButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        // Project Browser: clicking a view or sheet opens it as a tab.
        private void BrowserTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is BrowserNode node && node.IsLeaf) Vm?.OpenBrowserNode(node);
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
