using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DrawingStudio.App.Controls
{
    /// <summary>Picture viewer: wheel zooms at the cursor, drag pans, double-click fits.</summary>
    public sealed class ZoomImage : Border
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(ImageSource), typeof(ZoomImage),
            new PropertyMetadata(null, (d, e) => ((ZoomImage)d).OnSourceChanged()));

        public ImageSource Source
        {
            get => (ImageSource)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        private readonly Image _image = new Image { Stretch = Stretch.None, SnapsToDevicePixels = true };
        private readonly MatrixTransform _transform = new MatrixTransform();
        private Point? _dragStart;
        private Matrix _dragMatrix;

        public ZoomImage()
        {
            ClipToBounds = true;
            Background = Brushes.White;
            Cursor = Cursors.Hand;
            _image.RenderTransform = _transform;
            _image.HorizontalAlignment = HorizontalAlignment.Left;
            _image.VerticalAlignment = VerticalAlignment.Top;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            Child = new Canvas { Children = { _image } };
            SizeChanged += (_, __) => { if (!_userMoved) Fit(); };
        }

        private bool _userMoved;

        private void OnSourceChanged()
        {
            _image.Source = Source;
            _userMoved = false;
            Dispatcher.BeginInvoke(new Action(Fit), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public void Fit()
        {
            if (Source == null || ActualWidth < 1 || ActualHeight < 1) return;
            double w = Source.Width, h = Source.Height;
            if (w < 1 || h < 1) return;
            double s = Math.Min(ActualWidth / w, ActualHeight / h) * 0.96;
            var m = Matrix.Identity;
            m.Scale(s, s);
            m.Translate((ActualWidth - w * s) / 2, (ActualHeight - h * s) / 2);
            _transform.Matrix = m;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var p = e.GetPosition(this);
            double f = e.Delta > 0 ? 1.2 : 1 / 1.2;
            var m = _transform.Matrix;
            if (m.M11 * f > 40 || m.M11 * f < 0.02) return;
            m.ScaleAt(f, f, p.X, p.Y);
            _transform.Matrix = m;
            _userMoved = true;
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { _userMoved = false; Fit(); return; }
            _dragStart = e.GetPosition(this);
            _dragMatrix = _transform.Matrix;
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragStart == null) return;
            var d = e.GetPosition(this) - _dragStart.Value;
            var m = _dragMatrix;
            m.Translate(d.X, d.Y);
            _transform.Matrix = m;
            _userMoved = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            _dragStart = null;
            ReleaseMouseCapture();
        }
    }
}
