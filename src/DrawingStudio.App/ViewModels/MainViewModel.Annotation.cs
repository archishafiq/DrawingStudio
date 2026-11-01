using System.Collections.Generic;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using DrawingStudio.Shared;

namespace DrawingStudio.App.ViewModels
{
    /// <summary>Annotation tab: grid dimensions, grid heads and tags across many views.</summary>
    public sealed partial class MainViewModel
    {
        private string _annotViewFilter = "";
        private bool _dimensionGrids = true;
        private bool _overallDimension = true;
        private bool _bothSides;
        private bool _gridBubbles;
        private bool _tagElements = true;
        private bool _tagLeader;
        private bool _skipTagged = true;

        public ObservableCollection<Checkable<ViewInfo>> AnnotViews { get; } = new ObservableCollection<Checkable<ViewInfo>>();
        public ICollectionView AnnotViewsView { get; private set; }
        public ObservableCollection<Checkable<Option>> TagCategories { get; } = new ObservableCollection<Checkable<Option>>();

        public string AnnotViewFilter { get => _annotViewFilter; set { if (Set(ref _annotViewFilter, value)) AnnotViewsView?.Refresh(); } }
        public bool DimensionGrids { get => _dimensionGrids; set => Set(ref _dimensionGrids, value); }
        public bool OverallDimension { get => _overallDimension; set => Set(ref _overallDimension, value); }
        public bool BothSides { get => _bothSides; set => Set(ref _bothSides, value); }
        public bool GridBubbles { get => _gridBubbles; set => Set(ref _gridBubbles, value); }
        public bool TagElements { get => _tagElements; set => Set(ref _tagElements, value); }
        public bool TagLeader { get => _tagLeader; set => Set(ref _tagLeader, value); }
        public bool SkipTagged { get => _skipTagged; set => Set(ref _skipTagged, value); }

        public ICommand AnnotateCommand { get; private set; }
        public ICommand CheckActiveViewCommand { get; private set; }

        private void InitAnnotation()
        {
            foreach (var (key, label, on) in new[]
                     {
                         ("OST_StructuralColumns", "Structural columns", true),
                         ("OST_StructuralFraming", "Structural framing (beams)", true),
                         ("OST_StructuralFoundation", "Structural foundations", true),
                         ("OST_Walls", "Walls", false),
                         ("OST_Floors", "Floors / slabs", false),
                         ("OST_Doors", "Doors", false),
                         ("OST_Windows", "Windows", false),
                         ("OST_StairsRuns", "Stair runs", false)
                     })
                TagCategories.Add(new Checkable<Option>(new Option(key, label), label, null, on));

            AnnotViewsView = CollectionViewSource.GetDefaultView(AnnotViews);
            AnnotViewsView.Filter = o => string.IsNullOrWhiteSpace(AnnotViewFilter) ||
                                         ((Checkable<ViewInfo>)o).Label.IndexOf(AnnotViewFilter, StringComparison.OrdinalIgnoreCase) >= 0;

            AnnotateCommand = new RelayCommand(_ => QueueAnnotation(), _ => CanPrepare());
            CheckActiveViewCommand = new RelayCommand(_ =>
            {
                foreach (var v in AnnotViews) v.IsChecked = v.Item.Id == Model?.ActiveViewId;
            }, _ => Model != null);
        }

        private void LoadAnnotationTab(ModelInfo model)
        {
            var keep = AnnotViews.Where(v => v.IsChecked).Select(v => v.Item.Id).ToList();
            AnnotViews.Clear();
            foreach (var v in model.Views.OrEmpty().Where(v => v.ViewType != "ThreeD" && v.ViewType != "DraftingView"))
                AnnotViews.Add(new Checkable<ViewInfo>(v, v.Name, $"{Pretty(v.ViewType)}  ·  1:{v.Scale}", keep.Contains(v.Id)));
        }

        private void QueueAnnotation()
        {
            var views = AnnotViews.Where(v => v.IsChecked).Select(v => v.Item).ToList();
            if (views.Count == 0) { AddLog("Tick the views to annotate."); return; }

            var categories = TagCategories.Where(c => c.IsChecked).ToList();
            var request = new AnnotateRequest
            {
                ViewIds = views.Select(v => v.Id).ToList(),
                DimensionGrids = DimensionGrids,
                OverallDimension = OverallDimension,
                BothSides = BothSides,
                GridBubblesBothEnds = GridBubbles,
                TagElements = TagElements,
                TagCategories = categories.Select(c => c.Item.Key).ToList(),
                TagLeader = TagLeader,
                SkipTagged = SkipTagged,
                DimOffsetMm = Standards.DimOffsetMm,
                DimGapMm = Standards.DimGapMm
            };

            var what = new List<string>();
            if (DimensionGrids) what.Add("grid dimensions" + (OverallDimension ? " + overall" : "") + (BothSides ? ", both ends" : ""));
            if (GridBubbles) what.Add("grid heads both ends");
            if (TagElements && categories.Count > 0) what.Add("tags: " + string.Join(", ", categories.Select(c => c.Label.ToLowerInvariant())));
            if (what.Count == 0) { AddLog("Choose at least one annotation option."); return; }

            Queue("Annotation", $"Annotate {views.Count} view(s)", Preview(views.Select(v => v.Name), string.Join("  ·  ", what)),
                Commands.Annotate, request);
        }
    }
}
