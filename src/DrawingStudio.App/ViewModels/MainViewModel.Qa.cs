using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DrawingStudio.Shared;

namespace DrawingStudio.App.ViewModels
{
    /// <summary>QA tab: drawing-set checks, issue navigation and renumbering.</summary>
    public sealed partial class MainViewModel
    {
        private QaIssue _selectedIssue;
        private string _qaSummary = "Run the checks to see issues.";
        private int _detailStart = 1;

        public ObservableCollection<Checkable<Option>> QaChecksList { get; } = new ObservableCollection<Checkable<Option>>();
        public ObservableCollection<QaIssue> Issues { get; } = new ObservableCollection<QaIssue>();
        public ObservableCollection<Checkable<SheetInfo>> RenumberSheets { get; } = new ObservableCollection<Checkable<SheetInfo>>();

        public QaIssue SelectedIssue { get => _selectedIssue; set => Set(ref _selectedIssue, value); }
        public string QaSummary { get => _qaSummary; private set => Set(ref _qaSummary, value); }
        public int DetailStart { get => _detailStart; set => Set(ref _detailStart, Math.Max(1, value)); }

        public ICommand RunQaCommand { get; private set; }
        public ICommand ShowIssueCommand { get; private set; }
        public ICommand RenumberSheetsCommand { get; private set; }
        public ICommand RenumberDetailsCommand { get; private set; }

        private void InitQa()
        {
            foreach (var (key, label, on) in new[]
                     {
                         (QaChecks.SheetNumbering, "Sheet numbers follow ISO 19650 container naming", true),
                         (QaChecks.SheetParams, "Title block fields are filled in", true),
                         (QaChecks.EmptySheets, "Sheets without views", true),
                         (QaChecks.ViewNaming, "Placed view names follow the naming rule", true),
                         (QaChecks.NoTemplate, "Placed views have a view template", true),
                         (QaChecks.NonStandardScale, "Scales are ISO 5455 preferred scales", true),
                         (QaChecks.UnplacedViews, "Views not on any sheet", false),
                         (QaChecks.UntaggedElements, "Untagged elements (Annotation tab categories)", false)
                     })
                QaChecksList.Add(new Checkable<Option>(new Option(key, label), label, null, on));

            RunQaCommand = new AsyncCommand(_ => RunAsync("Checking drawings", RunQaAsync), CanRun);
            ShowIssueCommand = new AsyncCommand(_ => RunAsync("Showing in Revit", ShowIssueAsync),
                () => CanRun() && SelectedIssue != null && SelectedIssue.ElementId > 0);
            RenumberSheetsCommand = new RelayCommand(_ => Queue(BuildRenumberSheets()), _ => CanPrepare());
            RenumberDetailsCommand = new RelayCommand(_ => Queue(BuildRenumberDetails()), _ => CanPrepare());
        }

        private void LoadQaTab(ModelInfo model)
        {
            var keep = RenumberSheets.Where(s => s.IsChecked).Select(s => s.Item.Id).ToList();
            RenumberSheets.Clear();
            foreach (var s in model.Sheets.OrEmpty())
                RenumberSheets.Add(new Checkable<SheetInfo>(s, s.Number, s.Name, keep.Contains(s.Id)));
        }

        private async Task RunQaAsync()
        {
            var result = await Send<QaRequest, QaResult>(Commands.Qa, new QaRequest
            {
                Checks = QaChecksList.Where(c => c.IsChecked).Select(c => c.Item.Key).ToList(),
                TagCategories = TagCategories.Where(c => c.IsChecked).Select(c => c.Item.Key).ToList(),
                ViewNameRegex = Standards.ViewNameRegex,
                SheetNumberRegex = Naming.SheetNumberPattern(Standards),
                AllowedScales = Standards.GetAllowedScales(),
                RequiredSheetParams = Standards.GetRequiredSheetParams()
            });

            Issues.Clear();
            var order = new Dictionary<string, int> { ["Error"] = 0, ["Warning"] = 1, ["Info"] = 2 };
            foreach (var i in result.Issues.OrEmpty().OrderBy(i => order.TryGetValue(i.Severity, out var o) ? o : 3).ThenBy(i => i.Check))
                Issues.Add(i);

            OpenDoc("Issues", $"QA Issues ({Issues.Count})");
            QaSummary = Issues.Count == 0
                ? "No issues found."
                : $"{Issues.Count(i => i.Severity == "Error")} errors, {Issues.Count(i => i.Severity == "Warning")} warnings, " +
                  $"{Issues.Count(i => i.Severity == "Info")} notes. Double-click a row to open it in Revit.";
        }

        private Task ShowIssueAsync() =>
            Send<ShowElementsRequest, bool>(Commands.ShowElements, new ShowElementsRequest { Ids = new List<long> { SelectedIssue.ElementId } });

        private Prepared BuildRenumberSheets()
        {
            var selected = RenumberSheets.Where(s => s.IsChecked).Select(s => s.Item).ToList();
            if (selected.Count == 0) return Fail("Tick the sheets to renumber.");

            // Numbers held by other sheets, or reserved by prepared changes, stay taken.
            var selectedIds = new HashSet<long>(selected.Select(s => s.Id));
            var used = new HashSet<string>(RenumberSheets.Where(s => !selectedIds.Contains(s.Item.Id)).Select(s => s.Item.Number),
                StringComparer.OrdinalIgnoreCase);
            foreach (var n in PendingSheetNumbers()) used.Add(n ?? "");

            var assignments = new List<IdValue>();
            var changes = new List<string>();
            foreach (var sheet in selected.NaturalOrder(s => s.Number))
            {
                var parts = Naming.ParseSheetNumber(Standards, sheet.Number);
                var level = parts.TryGetValue("Level", out var l) ? l : Standards.DefaultLevelCode;
                var number = Naming.NextSheetNumber(Standards, level, used);
                assignments.Add(new IdValue { Id = sheet.Id, Value = number });
                if (!string.Equals(sheet.Number, number, StringComparison.OrdinalIgnoreCase)) changes.Add($"{sheet.Number}  →  {number}");
            }
            if (changes.Count == 0) return Fail("The ticked sheets already follow the standard sequence.");

            return Make("Numbering", $"Renumber {changes.Count} sheet(s)", Preview(changes), Commands.RenumberSheets,
                new RenumberSheetsRequest { Assignments = assignments });
        }

        private Prepared BuildRenumberDetails()
        {
            var sheets = RenumberSheets.Where(s => s.IsChecked).Select(s => s.Item).ToList();
            if (sheets.Count == 0) return Fail("Tick the sheets whose detail numbers should be reset.");
            return Make("Numbering", $"Renumber details on {sheets.Count} sheet(s) from {DetailStart}",
                Preview(sheets.Select(s => s.ToString()), "Reading order: top row first, left to right"),
                Commands.RenumberViewports, new RenumberViewportsRequest { SheetIds = sheets.Select(s => s.Id).ToList(), Start = DetailStart });
        }
    }
}
