using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DrawingStudio.App.Controls;
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
        private List<SelectedElement> _selectedElements = new List<SelectedElement>();
        private string _selectionText = "No Revit selection read yet.";

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
            set
            {
                if (!Set(ref _sectionMode, value)) return;
                OnPropertyChanged(nameof(IsGridSectionMode));
                OnPropertyChanged(nameof(IsElementSectionMode));
            }
        }
        public bool IsGridSectionMode => SectionMode?.Key == "Grids";
        public bool IsElementSectionMode => !IsGridSectionMode;
        public ViewTemplateInfo PlanTemplate { get => _planTemplate; set => Set(ref _planTemplate, value); }
        public ViewTemplateInfo SectionTemplate { get => _sectionTemplate; set => Set(ref _sectionTemplate, value); }
        public bool SectionFlip { get => _sectionFlip; set => Set(ref _sectionFlip, value); }
        public string SelectionText { get => _selectionText; private set => Set(ref _selectionText, value); }

        public ICommand CreatePlansCommand { get; private set; }
        public ICommand CreateSectionsCommand { get; private set; }
        public ICommand ReadSelectionCommand { get; private set; }

        private void InitViews()
        {
            _planKind = PlanKinds[0];
            _sectionMode = SectionModes[0];
            CreatePlansCommand = new RelayCommand(_ => Queue(BuildPlans()), _ => CanPrepare());
            // Grid sections can be prepared offline; element sections need a Revit selection.
            CreateSectionsCommand = new AsyncCommand(_ => RunAsync("Preparing sections", QueueSectionsAsync),
                () => CanPrepare() && (IsGridSectionMode || IsConnected || _selectedElements.Count > 0));
            ReadSelectionCommand = new AsyncCommand(_ => RunAsync("Reading Revit selection", ReadSelectionAsync), CanRun);
        }

        private void LoadViewsTab(ModelInfo model)
        {
            var keepLevels = Levels.Where(l => l.IsChecked).Select(l => l.Item.Id).ToList();
            Levels.Clear();
            foreach (var l in model.Levels.OrEmpty())
                Levels.Add(new Checkable<LevelInfo>(l, l.Name, $"{l.ElevationMm:0} mm  ·  code {l.Code}", keepLevels.Contains(l.Id)));

            var keepGrids = Grids.Where(g => g.IsChecked).Select(g => g.Item.Id).ToList();
            Grids.Clear();
            foreach (var g in model.Grids.OrEmpty().Where(g => g.IsLinear))
                Grids.Add(new Checkable<GridInfo>(g, g.Name, null, keepGrids.Contains(g.Id)));

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

        // ---------------------------------------------------------------- plans

        private Prepared BuildPlans()
        {
            var levels = Levels.Where(c => c.IsChecked).Select(c => c.Item).ToList();
            if (levels.Count == 0) return Fail("Tick at least one level.");

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

            var existing = new HashSet<string>(Model?.Views.OrEmpty().Select(v => v.Name) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var names = levels.Select(l =>
            {
                var name = Naming.Clean(Naming.Fill(Standards.PlanNameTemplate, new Dictionary<string, string>
                {
                    ["Role"] = Standards.Role,
                    ["PlanKind"] = Naming.PlanKindLabel(PlanKind.Key),
                    ["LevelName"] = l.Name,
                    ["LevelCode"] = l.Code,
                    ["Scale"] = Naming.ScaleLabel(Standards.PlanScale)
                }), Standards.Uppercase);
                return existing.Contains(name) ? name + "   (exists: will be named “… (2)”)" : name;
            });

            return Make("Plans", $"Create {levels.Count} {PlanKind.Label.ToLowerInvariant()}(s)",
                Preview(names, $"Scale 1:{Standards.PlanScale}  ·  template: {PlanTemplate?.Name ?? "<None>"}"),
                Commands.CreatePlans, request);
        }

        // ---------------------------------------------------------------- sections

        private async Task ReadSelectionAsync()
        {
            var selected = await Send<List<SelectedElement>>(Commands.Selection);
            _selectedElements = selected ?? new List<SelectedElement>();
            SelectionText = _selectedElements.Count == 0
                ? "Nothing usable is selected in Revit. Select beams, walls, columns or footings."
                : $"{_selectedElements.Count} element(s) from Revit: " + string.Join(", ", _selectedElements.Take(6).Select(e => e.Name))
                  + (_selectedElements.Count > 6 ? " …" : "");
        }

        private Prepared BuildSections()
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
                if (grids.Count == 0) return Fail("Tick at least one grid.");
                request.GridIds = grids.Select(g => g.Id).ToList();

                int index = 1;
                var names = grids.Select(g => SectionName("SECTION", g.Name, "GRID", index++)).ToList();
                return Make("Sections", $"Create {grids.Count} building section(s) along grids", Preview(names, header),
                    Commands.CreateSections, request);
            }

            if (_selectedElements.Count == 0)
                return Fail("Select beams, walls, columns or footings in Revit, then click “Use Revit selection”.");
            request.ElementIds = _selectedElements.Select(e => e.Id).ToList();

            var cross = SectionMode.Key == "ElementsCross";
            int i = 1;
            var labels = _selectedElements.Select(e => SectionName(cross ? "CROSS SECTION" : "LONG SECTION", e.Name.Split(' ').Last(), "", i++)
                                                       + "   ← " + e.Name);
            return Make("Sections", $"Create {_selectedElements.Count} {(cross ? "cross" : "long")} section(s) through selected elements",
                Preview(labels, header), Commands.CreateSections, request);
        }

        private string SectionName(string kind, string reference, string category, int index) =>
            Naming.Clean(Naming.Fill(Standards.SectionNameTemplate, new Dictionary<string, string>
            {
                ["Role"] = Standards.Role,
                ["Kind"] = kind,
                ["Ref"] = reference,
                ["Category"] = category,
                ["Index"] = index.ToString("00"),
                ["Scale"] = Naming.ScaleLabel(Standards.SectionScale)
            }), Standards.Uppercase);

        private async Task QueueSectionsAsync()
        {
            if (!IsGridSectionMode && IsConnected) await ReadSelectionAsync(); // use what is selected right now
            if (Queue(BuildSections()) && !IsGridSectionMode)
            {
                _selectedElements = new List<SelectedElement>();
                SelectionText = "Added. Select the next elements in Revit if you need more sections.";
            }
        }

        // ---------------------------------------------------------------- diagrams

        /// <summary>Plan of the grids with the section cuts (red) and their looking direction.</summary>
        private Diagram BuildPlanDiagram()
        {
            var d = new Diagram { Empty = "Grids and section lines appear here once a model is loaded." };
            var grids = Grids.Select(g => (g.Item, g.IsChecked)).ToList();
            var key = $"{SectionMode?.Key}|{SectionFlip}|{Standards.SectionOffsetMm}|{Standards.SectionDepthMm}|{Standards.SectionPaddingMm}|" +
                      string.Join(",", grids.Where(g => g.IsChecked).Select(g => g.Item.Id)) + "|" +
                      string.Join(",", _selectedElements.Select(e => e.Id)) + "|" + grids.Count;
            d.Key = key;
            if (grids.Count == 0 && _selectedElements.Count == 0) return d;

            // span of the drawing, for arrow sizes
            var xs = grids.SelectMany(g => new[] { g.Item.X1, g.Item.X2 }).Concat(_selectedElements.SelectMany(e => new[] { e.X1, e.X2 })).ToList();
            var ys = grids.SelectMany(g => new[] { g.Item.Y1, g.Item.Y2 }).Concat(_selectedElements.SelectMany(e => new[] { e.Y1, e.Y2 })).ToList();
            double span = Math.Max(Math.Max(xs.Max() - xs.Min(), ys.Max() - ys.Min()), 1000);
            double arrow = span * 0.04;

            bool gridMode = IsGridSectionMode;
            foreach (var (g, on) in grids)
            {
                d.Lines.Add(new DLine { X1 = g.X1, Y1 = g.Y1, X2 = g.X2, Y2 = g.Y2, Kind = gridMode && on ? DKind.GridActive : DKind.Grid });
                d.Texts.Add(new DText { X = g.X2, Y = g.Y2, Text = g.Name, Kind = gridMode && on ? DKind.TextStrong : DKind.TextMuted, Dy = -9 });
            }

            if (gridMode)
            {
                int index = 1;
                foreach (var (g, on) in grids.Where(x => x.IsChecked))
                    AddSection(d, g.X1, g.Y1, g.X2, g.Y2, 0, SectionName("SECTION", g.Name, "GRID", index++), arrow);
            }
            else
            {
                bool cross = SectionMode?.Key == "ElementsCross";
                int index = 1;
                foreach (var e in _selectedElements)
                {
                    d.Lines.Add(new DLine { X1 = e.X1, Y1 = e.Y1, X2 = e.X2, Y2 = e.Y2, Kind = DKind.Element });
                    double sx = e.X1, sy = e.Y1, ex = e.X2, ey = e.Y2;
                    if (cross)
                    {
                        double dx = e.X2 - e.X1, dy = e.Y2 - e.Y1, len = Math.Sqrt(dx * dx + dy * dy);
                        if (len < 1e-6) continue;
                        double nx = -dy / len, ny = dx / len, mx = (e.X1 + e.X2) / 2, my = (e.Y1 + e.Y2) / 2;
                        sx = mx - nx * e.HalfWidthMm; sy = my - ny * e.HalfWidthMm;
                        ex = mx + nx * e.HalfWidthMm; ey = my + ny * e.HalfWidthMm;
                    }
                    var name = SectionName(cross ? "CROSS SECTION" : "LONG SECTION", e.Name.Split(' ').Last(), "", index++);
                    AddSection(d, sx, sy, ex, ey, Standards.SectionPaddingMm, name, arrow);
                }
                if (_selectedElements.Count == 0)
                    d.Texts.Add(new DText { X = (xs.Min() + xs.Max()) / 2, Y = ys.Min(), Text = "Select elements in Revit, then “Use Revit selection”", Kind = DKind.TextAlert, Dy = 14 });
            }
            return d;
        }

        /// <summary>Same geometry as the Revit add-in: cut line, look direction (arrows) and far-clip zone.</summary>
        private void AddSection(Diagram d, double x1, double y1, double x2, double y2, double padding, string label, double arrow)
        {
            if (SectionFlip) { (x1, x2) = (x2, x1); (y1, y2) = (y2, y1); }
            double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return;
            dx /= len; dy /= len;
            double vx = dy, vy = -dx;          // towards the viewer
            double lx = -vx, ly = -vy;          // looking direction
            double off = Standards.SectionOffsetMm, depth = Math.Max(0, Standards.SectionDepthMm);

            double sx = x1 - dx * padding + vx * off, sy = y1 - dy * padding + vy * off;
            double ex = x2 + dx * padding + vx * off, ey = y2 + dy * padding + vy * off;

            // far-clip zone as a shaded strip behind the cut
            d.Rects.Add(DepthRect(sx, sy, ex, ey, lx, ly, depth));
            d.Lines.Add(new DLine { X1 = sx, Y1 = sy, X2 = ex, Y2 = ey, Kind = DKind.Section });
            foreach (var (px, py) in new[] { (sx, sy), (ex, ey) })
                d.Lines.Add(new DLine { X1 = px, Y1 = py, X2 = px + lx * arrow, Y2 = py + ly * arrow, Kind = DKind.Arrow });
            d.Texts.Add(new DText { X = sx, Y = sy, Text = label, Kind = DKind.TextAlert, Dx = 0, Dy = 12 });
        }

        /// <summary>Axis-aligned approximation of the far-clip strip (exact for orthogonal grids).</summary>
        private static DRect DepthRect(double sx, double sy, double ex, double ey, double lx, double ly, double depth)
        {
            var px = new[] { sx, ex, sx + lx * depth, ex + lx * depth };
            var py = new[] { sy, ey, sy + ly * depth, ey + ly * depth };
            return new DRect { X = px.Min(), Y = py.Min(), W = px.Max() - px.Min(), H = py.Max() - py.Min(), Kind = DKind.SectionDepth };
        }

        /// <summary>Level stack: ticked levels (blue) get a plan.</summary>
        private Diagram BuildLevelsDiagram()
        {
            var d = new Diagram { Empty = "Levels appear here once a model is loaded." };
            var levels = Levels.ToList();
            d.Key = string.Join(",", levels.Select(l => l.Item.Id + (l.IsChecked ? "+" : "-"))) + "|" + PlanKind?.Key + Standards.PlanNameTemplate + Standards.Uppercase;
            if (levels.Count == 0) return d;

            double min = levels.Min(l => l.Item.ElevationMm), max = levels.Max(l => l.Item.ElevationMm);
            double h = Math.Max(max - min, 3000);
            double w = h * 0.9;
            // spread levels that are very close so labels stay readable
            double minGap = h / Math.Max(levels.Count * 1.6, 1);
            double lastY = double.MinValue;
            foreach (var l in levels.OrderBy(l => l.Item.ElevationMm))
            {
                double y = Math.Max(l.Item.ElevationMm, lastY + minGap);
                lastY = y;
                d.Lines.Add(new DLine { X1 = 0, Y1 = y, X2 = w, Y2 = y, Kind = l.IsChecked ? DKind.LevelActive : DKind.Level });
                d.Texts.Add(new DText
                {
                    X = 0, Y = y, Dx = 4, Dy = -8,
                    Text = $"{l.Item.Name}  ({l.Item.ElevationMm:0})",
                    Kind = l.IsChecked ? DKind.TextStrong : DKind.TextMuted
                });
            }
            // nudge labels right of the line start
            foreach (var t in d.Texts) t.X = w * 0.5;
            return d;
        }
    }
}
