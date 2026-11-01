using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using DrawingStudio.App.Controls;
using DrawingStudio.Shared;

namespace DrawingStudio.App.ViewModels
{
    /// <summary>Sheets tab: plan a sheet list with ISO 19650 numbers, create it, place views.</summary>
    public sealed partial class MainViewModel
    {
        private bool _onlyUnplaced = true;
        private string _sheetViewFilter = "";
        private bool _oneSheetPerView = true;
        private string _combinedSheetName = "GENERAL ARRANGEMENT";
        private TitleBlockInfo _titleBlock;
        private bool _autoPlace = true;
        private bool _renumberDetails = true;
        private SheetInfo _targetSheet;
        private SheetRow _selectedPlannedSheet;

        public ObservableCollection<Checkable<ViewInfo>> SheetViews { get; } = new ObservableCollection<Checkable<ViewInfo>>();
        public ICollectionView SheetViewsView { get; private set; }
        public ObservableCollection<TitleBlockInfo> TitleBlocks { get; } = new ObservableCollection<TitleBlockInfo>();
        public ObservableCollection<SheetRow> PlannedSheets { get; } = new ObservableCollection<SheetRow>();
        public ObservableCollection<SheetInfo> ExistingSheets { get; } = new ObservableCollection<SheetInfo>();

        public bool OnlyUnplaced { get => _onlyUnplaced; set { if (Set(ref _onlyUnplaced, value)) SheetViewsView?.Refresh(); } }
        public string SheetViewFilter { get => _sheetViewFilter; set { if (Set(ref _sheetViewFilter, value)) SheetViewsView?.Refresh(); } }
        public bool OneSheetPerView
        {
            get => _oneSheetPerView;
            set { if (Set(ref _oneSheetPerView, value)) OnPropertyChanged(nameof(CombineOnOneSheet)); }
        }
        public bool CombineOnOneSheet { get => !_oneSheetPerView; set => OneSheetPerView = !value; }
        public string CombinedSheetName { get => _combinedSheetName; set => Set(ref _combinedSheetName, value); }
        public TitleBlockInfo TitleBlock { get => _titleBlock; set => Set(ref _titleBlock, value); }
        public bool AutoPlace { get => _autoPlace; set => Set(ref _autoPlace, value); }
        public bool RenumberDetails { get => _renumberDetails; set => Set(ref _renumberDetails, value); }
        public SheetInfo TargetSheet { get => _targetSheet; set => Set(ref _targetSheet, value); }
        public SheetRow SelectedPlannedSheet { get => _selectedPlannedSheet; set => Set(ref _selectedPlannedSheet, value); }

        public ICommand GenerateSheetListCommand { get; private set; }
        public ICommand ClearSheetListCommand { get; private set; }
        public ICommand CreateSheetsCommand { get; private set; }
        public ICommand PlaceOnSheetCommand { get; private set; }
        public ICommand RelayoutSheetCommand { get; private set; }

        private void InitSheets()
        {
            SheetViewsView = CollectionViewSource.GetDefaultView(SheetViews);
            SheetViewsView.Filter = o =>
            {
                var c = (Checkable<ViewInfo>)o;
                if (OnlyUnplaced && c.Item.IsPlaced) return false;
                return string.IsNullOrWhiteSpace(SheetViewFilter) ||
                       c.Label.IndexOf(SheetViewFilter, StringComparison.OrdinalIgnoreCase) >= 0;
            };

            GenerateSheetListCommand = new RelayCommand(_ => GenerateSheetList(), _ => Model != null);
            ClearSheetListCommand = new RelayCommand(_ => PlannedSheets.Clear());
            CreateSheetsCommand = new RelayCommand(_ => { if (Queue(BuildSheets())) RemoveQueuedRows(); },
                _ => CanPrepare() && PlannedSheets.Any(r => r.Include));
            PlaceOnSheetCommand = new RelayCommand(_ => Queue(BuildPlace(false)), _ => CanPrepare() && TargetSheet != null);
            RelayoutSheetCommand = new RelayCommand(_ => Queue(BuildPlace(true)), _ => CanPrepare() && TargetSheet != null);
        }

        private void LoadSheetsTab(ModelInfo model)
        {
            SheetViews.Clear();
            foreach (var v in model.Views.OrEmpty().Where(v => v.ViewType != "ThreeD"))
            {
                var detail = $"{Pretty(v.ViewType)}  ·  1:{v.Scale}" + (v.IsPlaced ? "  ·  on " + v.SheetNumber : "");
                SheetViews.Add(new Checkable<ViewInfo>(v, v.Name, detail));
            }

            var keepTb = TitleBlock?.Id;
            TitleBlocks.Clear();
            foreach (var t in model.TitleBlocks.OrEmpty()) TitleBlocks.Add(t);
            TitleBlock = TitleBlocks.FirstOrDefault(t => t.Id == keepTb)
                         ?? TitleBlocks.FirstOrDefault(t => (t.Family + t.Type).IndexOf("A1", StringComparison.OrdinalIgnoreCase) >= 0)
                         ?? TitleBlocks.FirstOrDefault();

            var keepSheet = TargetSheet?.Id;
            ExistingSheets.Clear();
            foreach (var s in model.Sheets.OrEmpty()) ExistingSheets.Add(s);
            TargetSheet = ExistingSheets.FirstOrDefault(s => s.Id == keepSheet);
        }

        private void GenerateSheetList()
        {
            var views = SheetViews.Where(c => c.IsChecked).Select(c => c.Item).ToList();
            if (views.Count == 0) { AddLog("Tick the views to put on sheets first."); return; }

            var used = new HashSet<string>(Model.Sheets.OrEmpty().Select(s => s.Number), StringComparer.OrdinalIgnoreCase);
            foreach (var row in PlannedSheets) used.Add(row.Number ?? "");
            foreach (var n in PendingSheetNumbers()) used.Add(n ?? "");

            if (OneSheetPerView)
            {
                foreach (var v in views)
                {
                    var level = Naming.LevelCode(v.LevelName, Standards.DefaultLevelCode);
                    PlannedSheets.Add(new SheetRow
                    {
                        Number = Naming.NextSheetNumber(Standards, level, used),
                        Name = SheetNameFromView(v.Name),
                        ViewIds = new List<long> { v.Id },
                        ViewsText = v.Name
                    });
                }
            }
            else
            {
                var levels = views.Select(v => Naming.LevelCode(v.LevelName, Standards.DefaultLevelCode)).Distinct().ToList();
                // ISO 19650: ZZ = multiple levels
                var level = levels.Count == 1 ? levels[0] : "ZZ";
                PlannedSheets.Add(new SheetRow
                {
                    Number = Naming.NextSheetNumber(Standards, level, used),
                    Name = Standards.Uppercase ? CombinedSheetName.ToUpperInvariant() : CombinedSheetName,
                    ViewIds = views.Select(v => v.Id).ToList(),
                    ViewsText = string.Join("; ", views.Select(v => v.Name))
                });
            }
            CommandManager.InvalidateRequerySuggested();
        }

        private string SheetNameFromView(string viewName)
        {
            // "S - STRUCTURAL PLAN - LEVEL 01" -> "STRUCTURAL PLAN - LEVEL 01"
            var name = Regex.Replace(viewName ?? "", @"^[A-Za-z]{1,2}\s+-\s+", "");
            return Standards.Uppercase ? name.ToUpperInvariant() : name;
        }

        private Prepared BuildSheets()
        {
            var rows = PlannedSheets.Where(r => r.Include).ToList();
            if (rows.Count == 0) return Fail("Tick views, then click “Add to list” to plan sheets.");
            var dupes = rows.GroupBy(r => r.Number, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0) return Fail("Duplicate sheet numbers in the list: " + string.Join(", ", dupes));
            var taken = new HashSet<string>(Model?.Sheets.OrEmpty().Select(x => x.Number) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var clash = rows.Where(r => taken.Contains(r.Number)).Select(r => r.Number).ToList();
            if (clash.Count > 0) return Fail("These numbers already exist in the model: " + string.Join(", ", clash));

            var request = new CreateSheetsRequest
            {
                TitleBlockId = TitleBlock?.Id ?? 0,
                Sheets = rows.Select(r => new SheetPlan { Number = r.Number, Name = r.Name, ViewIds = r.ViewIds }).ToList(),
                Parameters = Standards.TitleBlockValues(),
                AutoPlace = AutoPlace,
                Layout = Standards.ToLayout(RenumberDetails)
            };
            var header = $"Title block: {TitleBlock?.ToString() ?? "<none>"}  ·  " +
                         string.Join(", ", request.Parameters.Select(p => p.Name + " = " + p.Value)) +
                         (AutoPlace ? "  ·  views placed automatically" : "");

            return Make("Sheets", $"Create {rows.Count} sheet(s)",
                Preview(rows.Select(r => $"{r.Number}  {r.Name}" + (AutoPlace && !string.IsNullOrEmpty(r.ViewsText) ? $"   ← {r.ViewsText}" : "")), header),
                Commands.CreateSheets, request);
        }

        private void RemoveQueuedRows()
        {
            foreach (var r in PlannedSheets.Where(r => r.Include).ToList()) PlannedSheets.Remove(r);
            SelectedPlannedSheet = null;
        }

        private Prepared BuildPlace(bool relayoutOnly)
        {
            if (TargetSheet == null) return Fail("Choose an existing sheet.");
            var views = relayoutOnly ? new List<ViewInfo>() : SheetViews.Where(v => v.IsChecked).Select(v => v.Item).ToList();
            if (!relayoutOnly && views.Count == 0) return Fail("Tick the views to place on " + TargetSheet.Number + ".");

            var request = new PlaceViewsRequest
            {
                SheetId = TargetSheet.Id,
                ViewIds = views.Select(v => v.Id).ToList(),
                Layout = Standards.ToLayout(RenumberDetails),
                RelayoutExisting = true
            };
            return relayoutOnly
                ? Make("Layout", $"Re-arrange views on {TargetSheet.Number}", "Lay out every viewport inside the border, clear of the title strip.",
                    Commands.PlaceViews, request)
                : Make("Layout", $"Place {views.Count} view(s) on {TargetSheet.Number}", Preview(views.Select(v => v.Name), TargetSheet.ToString()),
                    Commands.PlaceViews, request);
        }

        // ---------------------------------------------------------------- sheet layout picture

        private string _sheetCaption = "";
        public string SheetCaption { get => _sheetCaption; private set => Set(ref _sheetCaption, value); }

        /// <summary>
        /// Draws the sheet the way the add-in will lay it out: the planned sheet selected in the
        /// list (or the first one), otherwise the chosen existing sheet with the ticked views added.
        /// </summary>
        private Diagram BuildSheetDiagram()
        {
            var d = new Diagram { Empty = "Plan a sheet (Add to list) or choose an existing sheet to see its layout." };
            var layout = Standards.ToLayout(RenumberDetails);
            var viewsById = Model?.Views.OrEmpty().ToDictionary(v => v.Id) ?? new Dictionary<long, ViewInfo>();

            string title;
            double w, h;
            var boxes = new List<(string label, double w, double h, bool isNew, bool unknown)>();

            (double, double, bool) Size(long id)
            {
                if (viewsById.TryGetValue(id, out var v) && v.PaperWidthMm > 1 && v.PaperHeightMm > 1)
                    return (v.PaperWidthMm, v.PaperHeightMm, false);
                return (120, 80, true);
            }
            string Label(long id) => viewsById.TryGetValue(id, out var v) ? $"{v.Name}  1:{v.Scale}" : "view " + id;

            var row = SelectedPlannedSheet ?? PlannedSheets.FirstOrDefault();
            if (row != null)
            {
                (w, h) = TitleBlockSize(TitleBlock);
                title = $"{row.Number}  {row.Name}";
                foreach (var id in row.ViewIds)
                {
                    var (bw, bh, unknown) = Size(id);
                    boxes.Add((Label(id), bw, bh, true, unknown));
                }
            }
            else if (TargetSheet != null)
            {
                (w, h) = TargetSheet.WidthMm > 1 ? (TargetSheet.WidthMm, TargetSheet.HeightMm) : TitleBlockSize(TitleBlock);
                title = TargetSheet.ToString();
                foreach (var vp in TargetSheet.Viewports.OrEmpty())
                    boxes.Add((vp.Name, vp.Width, vp.Height, false, false));
                var onSheet = new HashSet<long>(TargetSheet.Viewports.OrEmpty().Select(v => v.ViewId));
                foreach (var v in SheetViews.Where(c => c.IsChecked && !c.Item.IsPlaced && !onSheet.Contains(c.Item.Id)))
                {
                    var (bw, bh, unknown) = Size(v.Item.Id);
                    boxes.Add((Label(v.Item.Id), bw, bh, true, unknown));
                }
            }
            else
            {
                SheetCaption = "";
                return d;
            }

            d.Key = $"{title}|{w}|{h}|{layout.MarginLeftMm}|{layout.MarginOtherMm}|{layout.TitleStripMm}|{layout.TitleStripSide}|{layout.GapMm}|" +
                    string.Join(";", boxes.Select(b => $"{b.label}:{b.w:0}x{b.h:0}:{b.isNew}"));

            var sheet = new LayoutRect { MinX = 0, MinY = 0, MaxX = w, MaxY = h };
            var area = SheetLayout.DrawingArea(sheet, layout, 1);
            d.Rects.Add(new DRect { X = 0, Y = 0, W = w, H = h, Kind = DKind.Sheet });

            bool bottom = string.Equals(layout.TitleStripSide, "Bottom", StringComparison.OrdinalIgnoreCase);
            var strip = bottom
                ? new DRect { X = layout.MarginLeftMm, Y = layout.MarginOtherMm, W = w - layout.MarginLeftMm - layout.MarginOtherMm, H = layout.TitleStripMm }
                : new DRect { X = w - layout.MarginOtherMm - layout.TitleStripMm, Y = layout.MarginOtherMm, W = layout.TitleStripMm, H = h - 2 * layout.MarginOtherMm };
            strip.Kind = DKind.Strip;
            strip.Label = "Title strip";
            d.Rects.Add(strip);
            d.Rects.Add(new DRect { X = area.MinX, Y = area.MinY, W = area.Width, H = area.Height, Kind = DKind.Area });

            var placed = SheetLayout.Pack(area, boxes.Select(b => (b.w, b.h)).ToList(), layout.GapMm);
            int bad = 0, unknownCount = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                var p = placed[i];
                if (!p.Fits) bad++;
                if (b.unknown) unknownCount++;
                d.Rects.Add(new DRect
                {
                    X = p.CenterX - p.Width / 2,
                    Y = p.CenterY - p.Height / 2,
                    W = p.Width,
                    H = p.Height,
                    Kind = !p.Fits ? DKind.ViewportBad : b.isNew ? DKind.ViewportNew : DKind.Viewport,
                    Label = (RenumberDetails ? $"{i + 1}  " : "") + b.label + (b.unknown ? "  (size unknown)" : "")
                });
            }

            var paper = $"{w:0} × {h:0} mm";
            SheetCaption = $"{title}   ·   {paper}   ·   {boxes.Count} view(s)   ·   " +
                           (bad == 0 ? "everything fits" : $"{bad} view(s) do not fit: reduce the scale or crop") +
                           (unknownCount > 0 ? $"   ·   {unknownCount} size(s) unknown until the model is reloaded" : "");
            return d;
        }

        private static (double w, double h) TitleBlockSize(TitleBlockInfo tb)
        {
            if (tb != null && tb.WidthMm > 1 && tb.HeightMm > 1) return (tb.WidthMm, tb.HeightMm);
            var iso = SheetLayout.IsoSizeFromName(tb?.ToString());
            return iso ?? (841, 594); // A1 landscape
        }

        private static string Pretty(string viewType)
        {
            switch (viewType)
            {
                case "EngineeringPlan": return "Structural plan";
                case "FloorPlan": return "Floor plan";
                case "CeilingPlan": return "Ceiling plan";
                case "AreaPlan": return "Area plan";
                case "DraftingView": return "Drafting";
                default: return viewType;
            }
        }
    }
}
