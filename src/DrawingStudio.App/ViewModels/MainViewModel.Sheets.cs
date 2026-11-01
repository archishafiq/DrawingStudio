using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
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
            CreateSheetsCommand = new RelayCommand(_ => QueueSheets(),
                _ => CanPrepare() && PlannedSheets.Any(r => r.Include));
            PlaceOnSheetCommand = new RelayCommand(_ => QueuePlace(false), _ => CanPrepare() && TargetSheet != null);
            RelayoutSheetCommand = new RelayCommand(_ => QueuePlace(true), _ => CanPrepare() && TargetSheet != null);
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

        private void QueueSheets()
        {
            var rows = PlannedSheets.Where(r => r.Include).ToList();
            var dupes = rows.GroupBy(r => r.Number, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0) { AddLog("Duplicate sheet numbers in the list: " + string.Join(", ", dupes)); return; }

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

            Queue("Sheets", $"Create {rows.Count} sheet(s)",
                Preview(rows.Select(r => $"{r.Number}  {r.Name}" + (AutoPlace && !string.IsNullOrEmpty(r.ViewsText) ? $"   ← {r.ViewsText}" : "")), header),
                Commands.CreateSheets, request);

            foreach (var r in rows) PlannedSheets.Remove(r);
        }

        private void QueuePlace(bool relayoutOnly)
        {
            var views = relayoutOnly ? new List<ViewInfo>() : SheetViews.Where(v => v.IsChecked).Select(v => v.Item).ToList();
            if (!relayoutOnly && views.Count == 0) { AddLog("Tick the views to place first."); return; }

            var request = new PlaceViewsRequest
            {
                SheetId = TargetSheet.Id,
                ViewIds = views.Select(v => v.Id).ToList(),
                Layout = Standards.ToLayout(RenumberDetails),
                RelayoutExisting = true
            };
            if (relayoutOnly)
                Queue("Layout", $"Re-arrange views on {TargetSheet.Number}", "Lay out every viewport inside the border, clear of the title strip.",
                    Commands.PlaceViews, request);
            else
                Queue("Layout", $"Place {views.Count} view(s) on {TargetSheet.Number}", Preview(views.Select(v => v.Name), TargetSheet.ToString()),
                    Commands.PlaceViews, request);
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
