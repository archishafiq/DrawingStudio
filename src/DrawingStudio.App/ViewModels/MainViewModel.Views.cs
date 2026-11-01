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
            CreatePlansCommand = new AsyncCommand(_ => RunAsync("Creating plans", CreatePlansAsync), CanRun);
            CreateSectionsCommand = new AsyncCommand(_ => RunAsync("Creating sections", CreateSectionsAsync), CanRun);
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

        private async Task CreatePlansAsync()
        {
            var levels = CheckedIds(Levels, l => l.Id);
            if (levels.Count == 0) { AddLog("Tick at least one level."); return; }

            var result = await Send<CreatePlansRequest, CreateResult>(Commands.CreatePlans, new CreatePlansRequest
            {
                LevelIds = levels,
                PlanKind = PlanKind.Key,
                ViewTemplateId = PlanTemplate?.Id ?? 0,
                Scale = Standards.PlanScale,
                NameTemplate = Standards.PlanNameTemplate,
                Role = Standards.Role,
                Uppercase = Standards.Uppercase
            });
            foreach (var c in result.Created.OrEmpty()) AddLog("  + " + c.Name);
            await RefreshModelAsync();
        }

        private async Task CreateSectionsAsync()
        {
            var request = new CreateSectionsRequest
            {
                Mode = SectionMode.Key,
                GridIds = CheckedIds(Grids, g => g.Id),
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
            if (IsGridSectionMode && request.GridIds.Count == 0) { AddLog("Tick at least one grid."); return; }

            var result = await Send<CreateSectionsRequest, CreateResult>(Commands.CreateSections, request);
            foreach (var c in result.Created.OrEmpty()) AddLog("  + " + c.Name);
            await RefreshModelAsync();
        }
    }
}
