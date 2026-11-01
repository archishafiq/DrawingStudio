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
            RenumberSheetsCommand = new AsyncCommand(_ => RunAsync("Renumbering sheets", RenumberSheetsAsync), CanRun);
            RenumberDetailsCommand = new AsyncCommand(_ => RunAsync("Renumbering details", RenumberDetailsAsync), CanRun);
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

            QaSummary = Issues.Count == 0
                ? "No issues found."
                : $"{Issues.Count(i => i.Severity == "Error")} errors, {Issues.Count(i => i.Severity == "Warning")} warnings, " +
                  $"{Issues.Count(i => i.Severity == "Info")} notes. Double-click a row to open it in Revit.";
        }

        private Task ShowIssueAsync() =>
            Send<ShowElementsRequest, bool>(Commands.ShowElements, new ShowElementsRequest { Ids = new List<long> { SelectedIssue.ElementId } });

        private async Task RenumberSheetsAsync()
        {
            var selected = RenumberSheets.Where(s => s.IsChecked).Select(s => s.Item).ToList();
            if (selected.Count == 0) { AddLog("Tick the sheets to renumber."); return; }

            // Numbers held by sheets that are not being renumbered stay reserved.
            var selectedIds = new HashSet<long>(selected.Select(s => s.Id));
            var used = new HashSet<string>(RenumberSheets.Where(s => !selectedIds.Contains(s.Item.Id)).Select(s => s.Item.Number),
                StringComparer.OrdinalIgnoreCase);

            var assignments = new List<IdValue>();
            foreach (var sheet in selected.NaturalOrder(s => s.Number))
            {
                var parts = Naming.ParseSheetNumber(Standards, sheet.Number);
                var level = parts.TryGetValue("Level", out var l) ? l : Standards.DefaultLevelCode;
                assignments.Add(new IdValue { Id = sheet.Id, Value = Naming.NextSheetNumber(Standards, level, used) });
            }

            var changes = assignments
                .Select(a => new { Old = selected.First(s => s.Id == a.Id).Number, New = a.Value })
                .Where(c => !string.Equals(c.Old, c.New, StringComparison.OrdinalIgnoreCase)).ToList();
            if (changes.Count == 0) { AddLog("Sheet numbers already follow the standard sequence."); return; }

            var preview = string.Join("\n", changes.Take(12).Select(c => $"{c.Old}  →  {c.New}"));
            if (changes.Count > 12) preview += $"\n… and {changes.Count - 12} more";
            if (!Confirm($"Renumber {changes.Count} sheet(s)?\n\n{preview}")) return;

            await Send<RenumberSheetsRequest, CreateResult>(Commands.RenumberSheets, new RenumberSheetsRequest { Assignments = assignments });
            await RefreshModelAsync();
        }

        private async Task RenumberDetailsAsync()
        {
            var ids = CheckedIds(RenumberSheets, s => s.Id);
            if (ids.Count == 0) { AddLog("Tick the sheets whose detail numbers should be reset."); return; }
            var result = await Send<RenumberViewportsRequest, CreateResult>(Commands.RenumberViewports,
                new RenumberViewportsRequest { SheetIds = ids, Start = DetailStart });
            foreach (var c in result.Created.OrEmpty()) AddLog("  " + c.Name);
        }
    }
}
