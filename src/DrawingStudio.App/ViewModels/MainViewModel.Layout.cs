using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using DrawingStudio.App.Controls;
using DrawingStudio.Shared;

namespace DrawingStudio.App.ViewModels
{
    /// <summary>A tab in the centre area, like Revit's open-view tabs.</summary>
    public sealed class DocTab : ObservableObject
    {
        private string _title;
        public string Title { get => _title; set => Set(ref _title, value); }
        /// <summary>Picture | 3D | Preview | Changes | Issues | Standards</summary>
        public string Kind { get; set; }
        public BrowserItem Item { get; set; }
        public string Glyph => Kind == "3D" ? "◆" : Kind == "Preview" ? "◇" : Item?.IsSheet == true ? "▭" : "▦";
    }

    /// <summary>A node in the Project Browser tree.</summary>
    public sealed class BrowserNode : ObservableObject
    {
        private bool _expanded;
        public string Name { get; set; }
        public string Detail { get; set; }
        public BrowserItem Item { get; set; }
        public ObservableCollection<BrowserNode> Children { get; } = new ObservableCollection<BrowserNode>();
        public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
        public bool IsLeaf => Item != null;
    }

    /// <summary>
    /// Revit-style shell: ribbon tools open in the Properties palette, views open as tabs in
    /// the centre, and the active tool's live preview opens as its own tab.
    /// </summary>
    public sealed partial class MainViewModel
    {
        private string _activeTool = "None";
        private DocTab _activeDoc;
        private bool _showLog = true;

        public ObservableCollection<DocTab> Documents { get; } = new ObservableCollection<DocTab>();
        public ObservableCollection<BrowserNode> BrowserTree { get; } = new ObservableCollection<BrowserNode>();
        public ObservableCollection<NameValue> PropertyRows { get; } = new ObservableCollection<NameValue>();

        public ICommand OpenToolCommand { get; private set; }
        public ICommand CloseToolCommand { get; private set; }
        public ICommand CloseDocCommand { get; private set; }
        public ICommand Open3DCommand { get; private set; }
        public ICommand ToggleLogCommand { get; private set; }
        public ICommand OpenSectionsCommand { get; private set; }
        public ICommand AnnotatePresetCommand { get; private set; }
        public ICommand OpenIssuesCommand { get; private set; }

        public bool ShowLog { get => _showLog; set => Set(ref _showLog, value); }

        /// <summary>None | Plans | Sections | Sheets | Place | Annotate | Renumber | QA | Changes | Standards</summary>
        public string ActiveTool
        {
            get => _activeTool;
            set
            {
                if (!Set(ref _activeTool, value ?? "None")) return;
                OnPropertyChanged(nameof(ToolTitle));
                OnPropertyChanged(nameof(IsToolActive));
                OnPropertyChanged(nameof(ToolDiagram));
                switch (_activeTool)
                {
                    case "Plans":
                    case "Sections":
                    case "Sheets":
                    case "Place":
                        OpenDoc("Preview", "Preview: " + ToolTitle);
                        break;
                    case "Changes":
                        OpenDoc("Changes", "Changes");
                        break;
                    case "Standards":
                        OpenDoc("Standards", "Standards (ISO 19650)");
                        break;
                }
                UpdatePreviewsSafe();
            }
        }

        public bool IsToolActive => _activeTool != "None";

        public string ToolTitle
        {
            get
            {
                switch (_activeTool)
                {
                    case "Plans": return "Create Plans";
                    case "Sections": return "Create Sections";
                    case "Sheets": return "New Sheets";
                    case "Place": return "Place Views on Sheet";
                    case "Annotate": return "Annotate Views";
                    case "Renumber": return "Renumber";
                    case "QA": return "Check Drawings";
                    case "Changes": return "Apply Changes";
                    case "Standards": return "Standards";
                    default: return SelectedBrowserItem?.IsSheet == true ? "Sheet" : "View";
                }
            }
        }

        /// <summary>The drawing shown in the Preview tab for the active tool.</summary>
        public Diagram ToolDiagram
        {
            get
            {
                switch (_activeTool)
                {
                    case "Plans": return LevelsDiagram;
                    case "Sections": return PlanDiagram;
                    case "Sheets":
                    case "Place": return SheetDiagram;
                    default: return null;
                }
            }
        }

        public DocTab ActiveDoc
        {
            get => _activeDoc;
            set
            {
                if (!Set(ref _activeDoc, value)) return;
                ApplyDoc();
            }
        }

        // centre area mode
        public bool CenterPicture => _activeDoc?.Kind == "Picture";
        public bool Center3D => _activeDoc?.Kind == "3D";
        public bool CenterPreview => _activeDoc?.Kind == "Preview";
        public bool CenterChanges => _activeDoc?.Kind == "Changes";
        public bool CenterIssues => _activeDoc?.Kind == "Issues";
        public bool CenterStandards => _activeDoc?.Kind == "Standards";
        public bool CenterEmpty => _activeDoc == null;

        private void InitLayout()
        {
            OpenToolCommand = new RelayCommand(p => ActiveTool = p as string);
            CloseToolCommand = new RelayCommand(_ => ActiveTool = "None");
            CloseDocCommand = new RelayCommand(p => CloseDoc(p as DocTab));
            Open3DCommand = new RelayCommand(_ => { Use3DCache(); OpenDoc("3D", "3D Model"); });
            ToggleLogCommand = new RelayCommand(_ => ShowLog = !ShowLog);
            // Views ribbon: Grid / Long / Cross section buttons open the same tool in that mode
            OpenSectionsCommand = new RelayCommand(p =>
            {
                SectionMode = SectionModes.FirstOrDefault(m => m.Key == p as string) ?? SectionModes[0];
                ActiveTool = "Sections";
            });
            // Annotate ribbon: each button opens the annotation tool with that option preset
            AnnotatePresetCommand = new RelayCommand(p =>
            {
                var what = p as string;
                DimensionGrids = what == "Dims" || what == "All";
                GridBubbles = what == "Heads" || what == "All";
                TagElements = what == "Tags" || what == "All";
                ActiveTool = "Annotate";
            });
            OpenIssuesCommand = new RelayCommand(_ => OpenDoc("Issues", $"QA Issues ({Issues.Count})"));
        }

        // ---------------------------------------------------------------- documents

        private void OpenDoc(string kind, string title, BrowserItem item = null)
        {
            var doc = Documents.FirstOrDefault(d => d.Kind == kind && (item == null || d.Item?.Id == item.Id));
            if (doc == null)
            {
                doc = new DocTab { Kind = kind, Title = title, Item = item };
                Documents.Add(doc);
            }
            else doc.Title = title;
            ActiveDoc = doc;
        }

        /// <summary>Opens a view or sheet from the Project Browser as a tab.</summary>
        public void OpenBrowserNode(BrowserNode node)
        {
            if (node?.Item == null) return;
            OpenDoc("Picture", node.Item.Name, node.Item);
        }

        private void CloseDoc(DocTab doc)
        {
            if (doc == null) return;
            int i = Documents.IndexOf(doc);
            Documents.Remove(doc);
            if (ActiveDoc == doc || ActiveDoc == null)
                ActiveDoc = Documents.Count == 0 ? null : Documents[Math.Min(Math.Max(i - 1, 0), Documents.Count - 1)];
            if (doc.Kind == "Preview" && ActiveTool != "None") ActiveTool = "None";
        }

        private void ApplyDoc()
        {
            foreach (var n in new[] { nameof(CenterPicture), nameof(Center3D), nameof(CenterPreview), nameof(CenterChanges),
                                      nameof(CenterIssues), nameof(CenterStandards), nameof(CenterEmpty) })
                OnPropertyChanged(n);

            if (_activeDoc == null) { BuildPropertyRows(); return; }
            switch (_activeDoc.Kind)
            {
                case "Picture":
                    if (Show3D) Show3D = false;
                    SelectedBrowserItem = _activeDoc.Item;
                    break;
                case "3D":
                    if (!Show3D) Show3D = true;
                    break;
            }
            BuildPropertyRows();
            OnPropertyChanged(nameof(ToolTitle));
        }

        /// <summary>Properties palette rows for the open view or sheet (shown when no tool is active).</summary>
        private void BuildPropertyRows()
        {
            PropertyRows.Clear();
            void Row(string name, string value) => PropertyRows.Add(new NameValue { Name = name, Value = value ?? "" });

            var item = _activeDoc?.Kind == "Picture" ? _activeDoc.Item : null;
            if (item == null)
            {
                if (Model != null)
                {
                    Row("Model", Model.DocumentTitle);
                    Row("Revit", Model.RevitVersion);
                    Row("Levels", Model.Levels.OrEmpty().Count.ToString());
                    Row("Grids", Model.Grids.OrEmpty().Count.ToString());
                    Row("Views", Model.Views.OrEmpty().Count.ToString());
                    Row("Sheets", Model.Sheets.OrEmpty().Count.ToString());
                }
                return;
            }

            if (item.IsSheet)
            {
                var s = Model?.Sheets.OrEmpty().FirstOrDefault(x => x.Id == item.Id);
                if (s == null) return;
                Row("Sheet Number", s.Number);
                Row("Sheet Name", s.Name);
                Row("Views on sheet", s.ViewCount.ToString());
                Row("Paper size", s.WidthMm > 0 ? $"{s.WidthMm:0} × {s.HeightMm:0} mm" : "No title block");
                var parts = Naming.ParseSheetNumber(Standards, s.Number);
                Row("ISO 19650 number", parts.Count > 0 ? "Valid" : "Does not follow the standard");
                foreach (var vp in s.Viewports.OrEmpty().Take(12)) Row("  View", vp.Name);
            }
            else
            {
                var v = Model?.Views.OrEmpty().FirstOrDefault(x => x.Id == item.Id);
                if (v == null) return;
                Row("View Name", v.Name);
                Row("View Type", Pretty(v.ViewType));
                Row("View Scale", v.Scale > 0 ? "1 : " + v.Scale : "");
                Row("Associated Level", v.LevelName);
                Row("View Template", v.HasTemplate ? "Assigned" : "<None>");
                Row("Sheet Number", v.IsPlaced ? v.SheetNumber : "Not on a sheet");
                Row("Size on sheet", v.PaperWidthMm > 0 ? $"{v.PaperWidthMm:0} × {v.PaperHeightMm:0} mm" : "");
                Row("Scale is ISO 5455", Standards.GetAllowedScales().Contains(v.Scale) ? "Yes" : "No");
            }
        }

        // ---------------------------------------------------------------- project browser

        private void BuildBrowserTree()
        {
            var expanded = new HashSet<string>(Flatten(BrowserTree).Where(n => n.IsExpanded).Select(n => n.Name));
            bool filtering = !string.IsNullOrWhiteSpace(BrowserFilter);
            bool Match(BrowserItem i) => !filtering || i.Name.IndexOf(BrowserFilter, StringComparison.OrdinalIgnoreCase) >= 0;

            BrowserTree.Clear();
            var views = new BrowserNode { Name = "Views (all)", IsExpanded = true };
            foreach (var g in BrowserItems.Where(i => !i.IsSheet && Match(i)).GroupBy(i => (i.GroupOrder, i.Group)).OrderBy(g => g.Key.GroupOrder))
            {
                var group = new BrowserNode { Name = g.Key.Group, IsExpanded = filtering || expanded.Contains(g.Key.Group) };
                foreach (var i in g.NaturalOrder(i => i.Name))
                    group.Children.Add(new BrowserNode { Name = i.Name, Detail = i.Detail, Item = i });
                views.Children.Add(group);
            }
            BrowserTree.Add(views);

            var sheets = new BrowserNode { Name = "Sheets (all)", IsExpanded = filtering || expanded.Contains("Sheets (all)") || BrowserTree.Count == 1 };
            foreach (var i in BrowserItems.Where(i => i.IsSheet && Match(i)).NaturalOrder(i => i.Name))
                sheets.Children.Add(new BrowserNode { Name = i.Name, Detail = i.Detail, Item = i });
            BrowserTree.Add(sheets);
        }

        private static IEnumerable<BrowserNode> Flatten(IEnumerable<BrowserNode> nodes)
        {
            foreach (var n in nodes)
            {
                yield return n;
                foreach (var c in Flatten(n.Children)) yield return c;
            }
        }
    }
}
