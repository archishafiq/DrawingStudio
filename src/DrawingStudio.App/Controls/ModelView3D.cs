using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DrawingStudio.App.Controls
{
    /// <summary>One category in the 3D viewer, with a visibility toggle.</summary>
    public sealed class ModelLayer : INotifyPropertyChanged
    {
        private bool _visible = true;
        internal GeometryModel3D Model;
        internal Action<ModelLayer> Toggled;

        public string Name { get; internal set; }
        public int Triangles { get; internal set; }
        public Brush Swatch { get; internal set; }

        public bool IsVisible
        {
            get => _visible;
            set
            {
                if (_visible == value) return;
                _visible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
                Toggled?.Invoke(this);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Interactive 3D viewer for the geometry exported by the Revit add-in.
    /// Left-drag orbits, right- or middle-drag pans, the wheel zooms, double-click fits.
    /// </summary>
    public sealed class ModelView3D : Border, INotifyPropertyChanged
    {
        public static readonly DependencyProperty ModelFileProperty = DependencyProperty.Register(
            nameof(ModelFile), typeof(string), typeof(ModelView3D),
            new PropertyMetadata(null, async (d, e) => await ((ModelView3D)d).LoadAsync((string)e.NewValue)));

        public string ModelFile
        {
            get => (string)GetValue(ModelFileProperty);
            set => SetValue(ModelFileProperty, value);
        }

        public ObservableCollection<ModelLayer> Layers { get; } = new ObservableCollection<ModelLayer>();

        private string _info = "No 3D model loaded.";
        public string Info
        {
            get => _info;
            private set { _info = value; OnChanged(); }
        }

        private readonly Viewport3D _viewport = new Viewport3D { ClipToBounds = true };
        private readonly PerspectiveCamera _camera = new PerspectiveCamera { FieldOfView = 45, UpDirection = new Vector3D(0, 0, 1) };
        private readonly Model3DGroup _scene = new Model3DGroup();
        private Point3D _target;
        private double _distance = 50, _yaw = -2.3, _pitch = 0.55, _fitDistance = 50;
        private Point3D _fitTarget;
        private Point? _last;
        private bool _panning;

        public ModelView3D()
        {
            Background = new LinearGradientBrush(Color.FromRgb(0xEE, 0xF2, 0xF5), Colors.White, 90);
            ClipToBounds = true;
            Focusable = true;

            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(0x70, 0x70, 0x70)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0xB0, 0xB0, 0xB0), new Vector3D(-0.6, -0.8, -1.2)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x50, 0x58), new Vector3D(0.7, 0.5, 0.4)));

            var root = new Model3DGroup();
            root.Children.Add(lights);
            root.Children.Add(_scene);
            _viewport.Children.Add(new ModelVisual3D { Content = root });
            _viewport.Camera = _camera;
            Child = _viewport;
            UpdateCamera();
        }

        // ---------------------------------------------------------------- loading

        private sealed class LoadedLayer
        {
            public string Name;
            public Color Color;
            public MeshGeometry3D Mesh;
            public int Triangles;
        }

        private async Task LoadAsync(string file)
        {
            _scene.Children.Clear();
            Layers.Clear();
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                Info = "No 3D model loaded.";
                return;
            }

            Info = "Loading 3D model…";
            List<LoadedLayer> layers;
            Rect3D bounds;
            try
            {
                (layers, bounds) = await Task.Run(() => Read(file));
            }
            catch (Exception ex)
            {
                Info = "Could not read the 3D model: " + ex.Message;
                return;
            }

            int total = 0;
            foreach (var l in layers)
            {
                var brush = new SolidColorBrush(l.Color);
                brush.Freeze();
                var material = new DiffuseMaterial(brush);
                material.Freeze();
                var model = new GeometryModel3D(l.Mesh, material) { BackMaterial = material };
                model.Freeze();
                _scene.Children.Add(model);

                var swatch = new SolidColorBrush(Color.FromRgb(l.Color.R, l.Color.G, l.Color.B));
                swatch.Freeze();
                Layers.Add(new ModelLayer { Name = l.Name, Triangles = l.Triangles, Swatch = swatch, Model = model, Toggled = Toggle });
                total += l.Triangles;
            }

            if (!bounds.IsEmpty)
            {
                _fitTarget = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
                double diag = Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ);
                _fitDistance = Math.Max(diag * 1.1, 1);
                _camera.NearPlaneDistance = Math.Max(diag / 2000, 0.01);
                _camera.FarPlaneDistance = diag * 20;
            }
            ZoomExtents();
            Info = $"{layers.Count} categories  ·  {total:N0} triangles  ·  drag to orbit, right-drag to pan, wheel to zoom, double-click to fit";
        }

        private static (List<LoadedLayer>, Rect3D) Read(string file)
        {
            var result = new List<LoadedLayer>();
            var bounds = Rect3D.Empty;
            using (var r = new BinaryReader(File.OpenRead(file), Encoding.UTF8))
            {
                var magic = r.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != 'D' || magic[1] != 'S' || magic[2] != 'M' || magic[3] != '1')
                    throw new InvalidDataException("not a Drawing Studio model file");
                int count = r.ReadInt32();
                for (int c = 0; c < count; c++)
                {
                    var name = r.ReadString();
                    byte cr = r.ReadByte(), cg = r.ReadByte(), cb = r.ReadByte(), ca = r.ReadByte();
                    int floats = r.ReadInt32();
                    var points = new Point3DCollection(floats / 3);
                    double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                    for (int i = 0; i < floats; i += 3)
                    {
                        double x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
                        points.Add(new Point3D(x, y, z));
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                        if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                    }
                    if (points.Count == 0) continue;
                    points.Freeze();
                    // No indices: WPF treats every three positions as one triangle (flat shading).
                    var mesh = new MeshGeometry3D { Positions = points };
                    mesh.Freeze();
                    bounds.Union(new Rect3D(minX, minY, minZ, maxX - minX, maxY - minY, maxZ - minZ));
                    result.Add(new LoadedLayer { Name = name, Color = Color.FromArgb(ca, cr, cg, cb), Mesh = mesh, Triangles = points.Count / 3 });
                }
            }
            // draw transparent layers (glass) last
            result.Sort((a, b) => (a.Color.A < 255).CompareTo(b.Color.A < 255));
            return (result, bounds);
        }

        private void Toggle(ModelLayer layer)
        {
            if (layer.IsVisible) { if (!_scene.Children.Contains(layer.Model)) _scene.Children.Add(layer.Model); }
            else _scene.Children.Remove(layer.Model);
        }

        public void ShowAll(bool visible)
        {
            foreach (var l in Layers) l.IsVisible = visible;
        }

        // ---------------------------------------------------------------- camera

        public void ZoomExtents()
        {
            _target = _fitTarget;
            _distance = _fitDistance;
            UpdateCamera();
        }

        public void SetView(string name)
        {
            switch (name)
            {
                case "Top": _yaw = -Math.PI / 2; _pitch = 1.5; break;
                case "Front": _yaw = -Math.PI / 2; _pitch = 0.02; break;
                case "Right": _yaw = 0; _pitch = 0.02; break;
                default: _yaw = -2.3; _pitch = 0.55; break; // south-west isometric
            }
            ZoomExtents();
        }

        private void UpdateCamera()
        {
            _pitch = Math.Max(-1.55, Math.Min(1.55, _pitch));
            var dir = new Vector3D(Math.Cos(_pitch) * Math.Cos(_yaw), Math.Cos(_pitch) * Math.Sin(_yaw), Math.Sin(_pitch));
            _camera.Position = _target + dir * _distance;
            _camera.LookDirection = -dir * _distance;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) { ZoomExtents(); return; }
            _last = e.GetPosition(this);
            _panning = e.ChangedButton != MouseButton.Left || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_last == null) return;
            var p = e.GetPosition(this);
            var d = p - _last.Value;
            _last = p;

            if (_panning)
            {
                var look = _camera.LookDirection; look.Normalize();
                var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1));
                if (right.Length < 1e-6) right = new Vector3D(1, 0, 0);
                right.Normalize();
                var up = Vector3D.CrossProduct(right, look);
                double k = _distance * 0.0015;
                _target += (-right * d.X + up * d.Y) * k;
            }
            else
            {
                _yaw -= d.X * 0.008;
                _pitch += d.Y * 0.008;
            }
            UpdateCamera();
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            _last = null;
            ReleaseMouseCapture();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            _distance *= e.Delta > 0 ? 0.85 : 1 / 0.85;
            _distance = Math.Max(_fitDistance * 0.01, Math.Min(_fitDistance * 20, _distance));
            UpdateCamera();
            e.Handled = true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
