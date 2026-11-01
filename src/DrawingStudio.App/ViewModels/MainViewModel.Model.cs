using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingStudio.App.Services;
using DrawingStudio.Shared;

namespace DrawingStudio.App.ViewModels
{
    /// <summary>One row in the project browser.</summary>
    public sealed class BrowserItem
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
        public string Group { get; set; }
        public int GroupOrder { get; set; }
        public bool Is3D { get; set; }
        public bool IsSheet { get; set; }
    }

    /// <summary>
    /// Model tab: a project browser for plans, sections, elevations, 3D views and sheets.
    /// Pictures are rendered by Revit itself; 3D is the model's real geometry in an orbit viewer.
    /// Both are cached next to the app, so the last model can be browsed while Revit is closed.
    /// </summary>
    public sealed partial class MainViewModel
    {
        private string _browserFilter = "";
        private BrowserItem _selectedBrowserItem;
        private ImageSource _viewImage;
        private string _viewerStatus = "Pick a view, sheet or 3D view on the left.";
        private bool _show3D;
        private string _model3DFile;
        private bool _detachOnOpen;
        private Option _pictureQuality;
        private long _renderWanted = -1, _rendering = -1;

        public ObservableCollection<BrowserItem> BrowserItems { get; } = new ObservableCollection<BrowserItem>();
        public ICollectionView BrowserView { get; private set; }

        public string BrowserFilter { get => _browserFilter; set { if (Set(ref _browserFilter, value)) { BrowserView?.Refresh(); BuildBrowserTree(); } } }
        public ImageSource ViewImage { get => _viewImage; private set => Set(ref _viewImage, value); }
        public string ViewerStatus { get => _viewerStatus; private set => Set(ref _viewerStatus, value); }
        public string Model3DFile { get => _model3DFile; private set => Set(ref _model3DFile, value); }
        public bool DetachOnOpen { get => _detachOnOpen; set => Set(ref _detachOnOpen, value); }

        public Option[] PictureQualities { get; } =
        {
            new Option("1600", "Draft (1600 px)"),
            new Option("3000", "Standard (3000 px)"),
            new Option("6000", "Sharp (6000 px)")
        };
        public Option PictureQuality { get => _pictureQuality; set => Set(ref _pictureQuality, value); }

        public bool Show3D
        {
            get => _show3D;
            set { if (Set(ref _show3D, value)) { OnPropertyChanged(nameof(ShowPicture)); if (value) Use3DCache(); } }
        }
        public bool ShowPicture { get => !_show3D; set => Show3D = !value; }

        public BrowserItem SelectedBrowserItem
        {
            get => _selectedBrowserItem;
            set
            {
                if (!Set(ref _selectedBrowserItem, value) || value == null) return;
                ShowCachedPicture(value);
                if (IsConnected && !Show3D) _ = RenderSelectedAsync();
            }
        }

        public ICommand OpenModelCommand { get; private set; }
        public ICommand RenderPictureCommand { get; private set; }
        public ICommand OpenViewInRevitCommand { get; private set; }
        public ICommand Load3DCommand { get; private set; }

        private void InitModelTab()
        {
            _pictureQuality = PictureQualities[1];
            BrowserView = CollectionViewSource.GetDefaultView(BrowserItems);
            BrowserView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BrowserItem.Group)));
            BrowserView.SortDescriptions.Add(new SortDescription(nameof(BrowserItem.GroupOrder), ListSortDirection.Ascending));
            BrowserView.Filter = o => string.IsNullOrWhiteSpace(BrowserFilter) ||
                                      ((BrowserItem)o).Name.IndexOf(BrowserFilter, StringComparison.OrdinalIgnoreCase) >= 0;

            OpenModelCommand = new AsyncCommand(_ => OpenModelAsync(), () => !IsBusy);
            RenderPictureCommand = new AsyncCommand(_ => RenderSelectedAsync(force: true), () => CanRun() && SelectedBrowserItem != null);
            OpenViewInRevitCommand = new AsyncCommand(_ => RunAsync("Opening in Revit", () =>
                Send<ShowElementsRequest, bool>(Commands.ShowElements, new ShowElementsRequest { Ids = new List<long> { SelectedBrowserItem.Id } })),
                () => CanRun() && SelectedBrowserItem != null);
            Load3DCommand = new AsyncCommand(_ => RunAsync("Reading 3D geometry from Revit", Load3DAsync), CanRun);
        }

        private void LoadModelTab(ModelInfo model)
        {
            var keep = SelectedBrowserItem?.Id;
            BrowserItems.Clear();

            foreach (var s in model.Sheets.OrEmpty())
                BrowserItems.Add(new BrowserItem
                {
                    Id = s.Id, Name = $"{s.Number} - {s.Name}", Group = "Sheets", GroupOrder = 0, IsSheet = true,
                    Detail = $"{s.ViewCount} view(s)" + (s.WidthMm > 0 ? $"  ·  {s.WidthMm:0} × {s.HeightMm:0} mm" : "")
                });

            foreach (var v in model.Views.OrEmpty())
            {
                var (group, order) = GroupOf(v.ViewType);
                BrowserItems.Add(new BrowserItem
                {
                    Id = v.Id, Name = v.Name, Group = group, GroupOrder = order, Is3D = v.ViewType == "ThreeD",
                    Detail = (v.Scale > 0 && v.ViewType != "ThreeD" ? $"1:{v.Scale}" : "") + (v.IsPlaced ? $"  ·  on {v.SheetNumber}" : "")
                });
            }

            _selectedBrowserItem = BrowserItems.FirstOrDefault(b => b.Id == keep);
            OnPropertyChanged(nameof(SelectedBrowserItem));
            if (_selectedBrowserItem != null) ShowCachedPicture(_selectedBrowserItem);

            // keep open view tabs pointing at the fresh items; close tabs whose view is gone
            foreach (var doc in Documents.Where(d => d.Kind == "Picture").ToList())
            {
                var fresh = BrowserItems.FirstOrDefault(b => b.Id == doc.Item?.Id);
                if (fresh == null) CloseDoc(doc);
                else { doc.Item = fresh; doc.Title = fresh.Name; }
            }
            BuildBrowserTree();
        }

        private static (string, int) GroupOf(string viewType)
        {
            switch (viewType)
            {
                case "EngineeringPlan": return ("Structural plans", 1);
                case "FloorPlan": return ("Floor plans", 2);
                case "CeilingPlan": return ("Ceiling plans", 3);
                case "AreaPlan": return ("Area plans", 4);
                case "Section": return ("Sections", 5);
                case "Elevation": return ("Elevations", 6);
                case "Detail": return ("Detail views", 7);
                case "ThreeD": return ("3D views", 8);
                case "DraftingView": return ("Drafting views", 9);
                default: return (viewType, 10);
            }
        }

        // ---------------------------------------------------------------- pictures

        private string CacheDir
        {
            get
            {
                var title = Model?.DocumentTitle ?? "model";
                foreach (var c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');
                var dir = Path.Combine(PortablePaths.DataDir, "cache", title);
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private string PictureFile(long id) => Path.Combine(CacheDir, id + ".png");

        private void ShowCachedPicture(BrowserItem item)
        {
            var file = PictureFile(item.Id);
            if (File.Exists(file))
            {
                ViewImage = LoadImage(file);
                ViewerStatus = $"{item.Name}  ·  picture from {File.GetLastWriteTime(file):dd MMM HH:mm}" +
                               (IsConnected ? "  ·  refreshing from Revit…" : "  ·  offline copy");
            }
            else
            {
                ViewImage = null;
                ViewerStatus = IsConnected ? $"{item.Name}  ·  rendering in Revit…" : $"{item.Name}  ·  connect to Revit to see this view.";
            }
        }

        private static ImageSource LoadImage(string file)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad; // do not lock the file
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(file);
            img.EndInit();
            img.Freeze();
            return img;
        }

        /// <summary>Renders the selected view; if the selection changes meanwhile, renders the newest one next.</summary>
        private async Task RenderSelectedAsync(bool force = false)
        {
            var item = SelectedBrowserItem;
            if (item == null || !IsConnected) return;
            if (!force && File.Exists(PictureFile(item.Id)) && _renderedThisSession.Contains(item.Id)) return;

            _renderWanted = item.Id;
            if (_rendering >= 0) return; // the running loop will pick it up

            while (_renderWanted >= 0)
            {
                var id = _renderWanted;
                _renderWanted = -1;
                _rendering = id;
                var current = BrowserItems.FirstOrDefault(b => b.Id == id);
                try
                {
                    var res = await Send<RenderViewRequest, RenderViewResult>(Commands.RenderView,
                        new RenderViewRequest { ViewId = id, PixelSize = int.Parse(PictureQuality?.Key ?? "3000") });
                    var cached = PictureFile(id);
                    File.Copy(res.FilePath, cached, true);
                    try { Directory.Delete(Path.GetDirectoryName(res.FilePath), true); } catch { /* temp */ }
                    _renderedThisSession.Add(id);
                    if (SelectedBrowserItem?.Id == id)
                    {
                        ViewImage = LoadImage(cached);
                        ViewerStatus = $"{res.ViewName}  ·  rendered by Revit just now  ·  wheel to zoom, drag to pan, double-click to fit";
                    }
                }
                catch (Exception ex)
                {
                    if (SelectedBrowserItem?.Id == id) ViewerStatus = $"{current?.Name}: {ex.Message}";
                }
                finally
                {
                    _rendering = -1;
                }
            }
        }

        private readonly HashSet<long> _renderedThisSession = new HashSet<long>();

        // ---------------------------------------------------------------- 3D

        private string Model3DCache => Path.Combine(CacheDir, "model3d.dsm");

        private void Use3DCache()
        {
            if (Model3DFile == null && File.Exists(Model3DCache))
            {
                Model3DFile = Model3DCache;
                AddLog($"Showing the 3D model saved {File.GetLastWriteTime(Model3DCache):dd MMM HH:mm}. Click “Load 3D from Revit” to refresh it.");
            }
        }

        private async Task Load3DAsync()
        {
            var item = SelectedBrowserItem;
            var viewId = item != null && item.Is3D ? item.Id : 0;
            var res = await Send<Model3DRequest, Model3DResult>(Commands.Model3D, new Model3DRequest { ViewId = viewId, DetailLevel = "Medium" });

            File.Copy(res.FilePath, Model3DCache, true);
            try { File.Delete(res.FilePath); } catch { /* temp */ }
            Model3DFile = null;          // force a reload even if the path is the same
            Model3DFile = Model3DCache;
            OpenDoc("3D", "3D Model");
            AddLog($"3D model from '{res.ViewName}': {res.Elements:N0} elements, {res.Triangles:N0} triangles" +
                   (res.Truncated ? " (trimmed: the model is very large)." : "."));
        }

        // ---------------------------------------------------------------- open a model

        private async Task OpenModelAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open a Revit model",
                Filter = "Revit models (*.rvt)|*.rvt",
                CheckFileExists = true
            };
            if (dialog.ShowDialog() != true) return;
            var path = dialog.FileName;

            if (!IsConnected)
            {
                // No Revit running yet: open the file with Windows, which starts the matching Revit.
                // The add-in loads with it and this app connects by itself.
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                AddLog("Opening " + Path.GetFileName(path) + " in Revit… this window connects automatically when it is ready.");
                Status = "Waiting for Revit to open " + Path.GetFileName(path) + "…";
                return;
            }

            await RunAsync("Opening " + Path.GetFileName(path) + " in Revit", async () =>
            {
                var title = await Send<OpenModelRequest, string>(Commands.OpenModel, new OpenModelRequest { Path = path, Detach = DetachOnOpen });
                AddLog("Opened " + title + " in Revit.");
                _client.TargetDocument = null; // the model just opened is now the active one
                Model3DFile = null;
                _renderedThisSession.Clear();
                await RefreshModelAsync();
                await RefreshOpenModelsAsync();
            });
        }
    }
}
