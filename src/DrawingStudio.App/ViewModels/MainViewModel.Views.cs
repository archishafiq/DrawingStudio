using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DrawingStudio.Shared;

namespace DrawingStudio.App.ViewModels
{
    /// <summary>Views tab: batch plans per level and sections along grids or elements.</summary>
    public sealed partial class MainViewModel
    {
        private static readonly ViewTemplateInfo NoTemplate = new ViewTemplateInfo { Id = 0, Name = "<None>" };

        private Option _planKind;
        private Option _sectionMode;
        private ViewTemplateInfo _planTemplate = NoTemplate;
        private ViewTemplateInfo _sectionTemplate = NoTemplate;
        private bool _sectionFlip;

        public ObservableCollection<Checkable<LevelInfo>> Levels { get; } = new ObservableCollection<Checkable<LevelInfo>>();
        public ObservableCollection<Checkable<GridInfo>> Grids { get; } = new ObservableCollection<Checkable<GridInfo>>();
        public ObservableCollection<ViewTemplateInfo> PlanTemplates { get; } = new ObservableCollection<ViewTemplateInfo>();
        public ObservableCollection<ViewTemplateInfo> SectionTemplates { get; } = new ObservableCollection<ViewTemplateInfo>();

        public Option[] PlanKinds { get; } =
        {
            new Option("StructuralPlan", "Structural plan"),
            new Option("FloorPlan", "Floor plan"),
            new Option("CeilingPlan", "Reflected ceiling plan")
        };

        public Option[] SectionModes { get; } =
        {
            new Option("Grids", "Building sections along checked grids"),
            new Option("ElementsLongitudinal", "Long sections along elements selected in Revit"),
            new Option("ElementsCross", "Cross sections through elements selected in Revit")
        };

        public Option PlanKind { get => _planKind; set => Set(ref _planKind, value); }
        public Option SectionMode
        {
            get => _sectionMode;
            set { if (Set(ref _sectionMode, value)) OnPropertyChanged(nameof(IsGridSectionMode)); }
        }
        public bool IsGridSectionMode => SectionMode?.Key == "Grids";
        public ViewTemplateInfo PlanTemplate { get => _planTemplate; set => Set(ref _planTemplate, value); }
        public ViewTemplateInfo SectionTemplate { get => _sectionTemplate; set => Set(ref _sectionTemplate, value); }
        public bool SectionFlip { get => _sectionFlip; set => Set(ref _sectionFlip, value); }

        public ICommand CreatePlansCommand { get; private set; }
        public ICommand CreateSectionsCommand { get; private set; }

        private void InitViews()
        {
            _planKind = PlanKinds[0];
            _sectionMode = SectionModes[0];
            CreatePlansCommand = new RelayCommand(_ => QueuePlans(), _ => CanPrepare());
            // Grid sections can be prepared offline; element sections read the Revit selection.
            CreateSectionsCommand = new AsyncCommand(_ => RunAsync("Reading selection", QueueSectionsAsync),
                () => CanPrepare() && (IsGridSectionMode || IsConnected));
        }

        private void LoadViewsTab(ModelInfo model)
        {
            Levels.Clear();
            foreach (var l in model.Levels.OrEmpty())
                Levels.Add(new Checkable<LevelInfo>(l, l.Name, $"{l.ElevationMm:0} mm  ·  code {l.Code}"));

            Grids.Clear();
            foreach (var g in model.Grids.OrEmpty().Where(g => g.IsLinear))
                Grids.Add(new Checkable<GridInfo>(g, g.Name));

            var keepPlan = PlanTemplate?.Id ?? 0;
            var keepSection = SectionTemplate?.Id ?? 0;
            PlanTemplates.Clear();
            SectionTemplates.Clear();
            PlanTemplates.Add(NoTemplate);
            SectionTemplates.Add(NoTemplate);
            foreach (var t in model.Templates.OrEmpty())
            {
                if (t.ViewType.Contains("Plan")) PlanTemplates.Add(t);
                if (t.ViewType == "Section" || t.ViewType == "Detail") SectionTemplates.Add(t);
            }
            PlanTemplate = PlanTemplates.FirstOrDefault(t => t.Id == keepPlan) ?? NoTemplate;
            SectionTemplate = SectionTemplates.FirstOrDefault(t => t.Id == keepSection) ?? NoTemplate;
        }

        private void QueuePlans()
        {
            var levels = Levels.Where(c => c.IsChecked).Select(c => c.Item).ToList();
            if (levels.Count == 0) { AddLog("Tick at least one level."); return; }

            var request = new CreatePlansRequest
            {
                LevelIds = levels.Select(l => l.Id).ToList(),
                PlanKind = PlanKind.Key,
                ViewTemplateId = PlanTemplate?.Id ?? 0,
                Scale = Standards.PlanScale,
                NameTemplate = Standards.PlanNameTemplate,
                Role = Standards.Role,
                Uppercase = Standards.Uppercase
            };

            var names = levels.Select(l => Naming.Clean(Naming.Fill(Standards.PlanNameTemplate, new Dictionary<string, string>
            {
                ["Role"] = Standards.Role,
                ["PlanKind"] = Naming.PlanKindLabel(PlanKind.Key),
                ["LevelName"] = l.Name,
                ["LevelCode"] = l.Code,
                ["Scale"] = Naming.ScaleLabel(Standards.PlanScale)
            }), Standards.Uppercase));

            Queue("Plans", $"Create {levels.Count} {PlanKind.Label.ToLowerInvariant()}(s)",
                Preview(names, $"Scale 1:{Standards.PlanScale}  ·  template: {PlanTemplate?.Name ?? "<None>"}"),
                Commands.CreatePlans, request);
        }

        private async Task QueueSectionsAsync()
        {
            var request = new CreateSectionsRequest
            {
                Mode = SectionMode.Key,
                GridIds = new List<long>(),
                ElementIds = new List<long>(),
                DepthMm = Standards.SectionDepthMm,
                OffsetMm = Standards.SectionOffsetMm,
                PaddingMm = Standards.SectionPaddingMm,
                Flip = SectionFlip,
                ViewTemplateId = SectionTemplate?.Id ?? 0,
                Scale = Standards.SectionScale,
                NameTemplate = Standards.SectionNameTemplate,
                Role = Standards.Role,
                Uppercase = Standards.Uppercase
            };
            var header = $"Scale 1:{Standards.SectionScale}  ·  far clip {Standards.SectionDepthMm:0} mm  ·  template: {SectionTemplate?.Name ?? "<None>"}";

            if (IsGridSectionMode)
            {
                var grids = Grids.Where(g => g.IsChecked).Select(g => g.Item).ToList();
                if (grids.Count == 0) { AddLog("Tick at least one grid."); return; }
                request.GridIds = grids.Select(g => g.Id).ToList();

                int index = 1;
                var names = grids.Select(g => Naming.Clean(Naming.Fill(Standards.SectionNameTemplate, new Dictionary<string, string>
                {
                    ["Role"] = Standards.Role, ["Kind"] = "SECTION", ["Ref"] = g.Name, ["Category"] = "GRID",
                    ["Index"] = (index++).ToString("00"), ["Scale"] = Naming.ScaleLabel(Standards.SectionScale)
                }), Standards.Uppercase)).ToList();

                Queue("Sections", $"Create {grids.Count} building section(s) along grids", Preview(names, header),
                    Commands.CreateSections, request);
                return;
            }

            // Element sections: capture what is selected in Revit right now, so the change is fixed.
            var selected = await Send<List<CreatedItem>>(Commands.Selection);
            if (selected == null || selected.Count == 0) { AddLog("Select beams, walls, columns or footings in Revit first, then click again."); return; }
            request.ElementIds = selected.Select(e => e.Id).ToList();

            var kind = SectionMode.Key == "ElementsCross" ? "cross" : "long";
            Queue("Sections", $"Create {selected.Count} {kind} section(s) through selected elements",
                Preview(selected.Select(e => e.Name), header), Commands.CreateSections, request);
        }
    }
}
