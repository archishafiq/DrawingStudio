using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DrawingStudio.App.Services;
using DrawingStudio.Shared;

namespace DrawingStudio.App.ViewModels
{
    /// <summary>One prepared change in the list.</summary>
    public sealed class ChangeItem : ObservableObject
    {
        private string _status = "Ready";
        private string _error;

        public ChangeItem(PendingChange data) { Data = data; }

        public PendingChange Data { get; }
        public string Kind => Data.Kind;
        public string Title => Data.Title;
        public string Summary => Data.Summary;
        public string Created => Data.CreatedLocal;

        public bool Included
        {
            get => Data.Included;
            set { if (Data.Included != value) { Data.Included = value; OnPropertyChanged(); } }
        }

        public string Status { get => _status; set => Set(ref _status, value); }
        public string Error { get => _error; set => Set(ref _error, value); }
    }

    /// <summary>
    /// Changes tab: everything is prepared in the app first, reviewed here, and only then
    /// applied to Revit in one go (one transaction group, so a single Ctrl+Z undoes it).
    /// </summary>
    public sealed partial class MainViewModel
    {
        private ChangeItem _selectedChange;
        private bool _allOrNothing = true;
        private int _selectedTab;

        public ObservableCollection<ChangeItem> Changes { get; } = new ObservableCollection<ChangeItem>();

        public ChangeItem SelectedChange { get => _selectedChange; set => Set(ref _selectedChange, value); }
        public bool AllOrNothing { get => _allOrNothing; set => Set(ref _allOrNothing, value); }
        public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

        public int ReadyCount => Changes.Count(c => c.Included);
        public string ApplyLabel => ReadyCount == 0 ? "Apply to Revit" : $"Apply {ReadyCount} change{(ReadyCount == 1 ? "" : "s")} to Revit";
        public string ChangesTabHeader => Changes.Count == 0 ? "Changes" : $"Changes ({Changes.Count})";

        public ICommand ApplyChangesCommand { get; private set; }
        public ICommand RemoveChangeCommand { get; private set; }
        public ICommand MoveChangeUpCommand { get; private set; }
        public ICommand MoveChangeDownCommand { get; private set; }
        public ICommand ClearChangesCommand { get; private set; }
        public ICommand ReviewChangesCommand { get; private set; }

        private static string ChangesFile => Path.Combine(PortablePaths.DataDir, "changes.json");

        private void InitChanges()
        {
            ApplyChangesCommand = new AsyncCommand(_ => RunAsync("Applying changes to Revit", ApplyChangesAsync),
                () => CanRun() && Changes.Any(c => c.Included));
            RemoveChangeCommand = new RelayCommand(p => { if (p is ChangeItem c) { Changes.Remove(c); ChangesChanged(); } });
            MoveChangeUpCommand = new RelayCommand(p => Move(p as ChangeItem, -1));
            MoveChangeDownCommand = new RelayCommand(p => Move(p as ChangeItem, +1));
            ClearChangesCommand = new RelayCommand(_ =>
            {
                if (Changes.Count > 0 && Confirm($"Remove all {Changes.Count} prepared change(s)?")) { Changes.Clear(); ChangesChanged(); }
            }, _ => Changes.Count > 0);
            ReviewChangesCommand = new RelayCommand(_ => SelectedTab = ChangesTabIndex);

            Changes.CollectionChanged += (_, __) => RaiseChangeCounts();
            LoadChanges();
        }

        /// <summary>Index of the Changes tab in MainWindow.xaml.</summary>
        private const int ChangesTabIndex = 4;

        /// <summary>Adds a prepared change. Nothing is sent to Revit until Apply.</summary>
        private void Queue<TRequest>(string kind, string title, string summary, string command, TRequest request)
        {
            var item = new ChangeItem(new PendingChange
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Title = title,
                Summary = summary,
                Command = command,
                Payload = Json.Serialize(request),
                Document = Model?.DocumentTitle,
                Included = true,
                CreatedLocal = DateTime.Now.ToString("dd MMM HH:mm")
            });
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChangeItem.Included)) ChangesChanged(); };
            Changes.Add(item);
            ChangesChanged();
            AddLog($"Prepared: {title}. Review it on the Changes tab, then Apply to Revit.");
        }

        private void Move(ChangeItem item, int delta)
        {
            if (item == null) return;
            int i = Changes.IndexOf(item), j = i + delta;
            if (i < 0 || j < 0 || j >= Changes.Count) return;
            Changes.Move(i, j);
            ChangesChanged();
        }

        private void ChangesChanged()
        {
            RaiseChangeCounts();
            SaveChanges();
            CommandManager.InvalidateRequerySuggested();
        }

        private void RaiseChangeCounts()
        {
            OnPropertyChanged(nameof(ReadyCount));
            OnPropertyChanged(nameof(ApplyLabel));
            OnPropertyChanged(nameof(ChangesTabHeader));
        }

        private async Task ApplyChangesAsync()
        {
            var items = Changes.Where(c => c.Included).ToList();
            if (items.Count == 0) return;

            var otherDoc = items.Where(i => !string.IsNullOrEmpty(i.Data.Document) && Model != null
                                            && !string.Equals(i.Data.Document, Model.DocumentTitle, StringComparison.OrdinalIgnoreCase))
                                .Select(i => i.Data.Document).Distinct().ToList();
            if (otherDoc.Count > 0 &&
                !Confirm($"Some changes were prepared for '{string.Join("', '", otherDoc)}' but Revit has '{Model.DocumentTitle}' open.\n\nApply them to the open model anyway?"))
                return;

            foreach (var i in items) { i.Status = "Applying…"; i.Error = null; }

            var result = await Send<BatchRequest, BatchResult>(Commands.Batch, new BatchRequest
            {
                AllOrNothing = AllOrNothing,
                Items = items.Select(i => new BridgeRequest { Id = i.Data.Id, Command = i.Data.Command, Payload = i.Data.Payload }).ToList()
            });

            var byId = result.Items.OrEmpty().ToDictionary(r => r.Id, r => r);
            var applied = new List<ChangeItem>();
            foreach (var item in items)
            {
                if (!byId.TryGetValue(item.Data.Id, out var r))
                {
                    item.Status = result.RolledBack ? "Not applied" : "Skipped";
                    continue;
                }
                if (r.Ok && !result.RolledBack)
                {
                    applied.Add(item);
                    LogCreated(item, r.Payload);
                }
                else
                {
                    item.Status = r.Ok ? "Rolled back" : "Failed";
                    item.Error = r.Error;
                }
            }

            foreach (var a in applied) Changes.Remove(a);
            ChangesChanged();

            AddLog(result.RolledBack
                ? "Nothing was applied: fix or untick the failed change and apply again."
                : $"Applied {applied.Count} change(s) to Revit. Ctrl+Z in Revit undoes them in one step.");

            await RefreshModelAsync();
        }

        private void LogCreated(ChangeItem item, string payload)
        {
            AddLog("✓ " + item.Title);
            try
            {
                var created = Json.Deserialize<CreateResult>(payload);
                foreach (var c in created?.Created.OrEmpty().Take(30) ?? Enumerable.Empty<CreatedItem>())
                    AddLog("    + " + c.Name);
            }
            catch { /* not a CreateResult (e.g. annotation) */ }
        }

        private void SaveChanges()
        {
            try
            {
                File.WriteAllText(ChangesFile, Json.SerializeIndented(new PendingChangeList { Items = Changes.Select(c => c.Data).ToList() }));
            }
            catch { /* not critical */ }
        }

        private void LoadChanges()
        {
            try
            {
                if (!File.Exists(ChangesFile)) return;
                var list = Json.Deserialize<PendingChangeList>(File.ReadAllText(ChangesFile));
                foreach (var data in list?.Items.OrEmpty() ?? new List<PendingChange>())
                {
                    var item = new ChangeItem(data);
                    item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChangeItem.Included)) ChangesChanged(); };
                    Changes.Add(item);
                }
                if (Changes.Count > 0) AddLog($"{Changes.Count} prepared change(s) from last time are waiting on the Changes tab.");
            }
            catch { /* ignore a bad file */ }
        }

        /// <summary>Sheet numbers already reserved by prepared (not yet applied) changes.</summary>
        private IEnumerable<string> PendingSheetNumbers()
        {
            foreach (var c in Changes)
            {
                if (c.Data.Command == Commands.CreateSheets)
                {
                    var req = Json.Deserialize<CreateSheetsRequest>(c.Data.Payload);
                    foreach (var s in req?.Sheets.OrEmpty() ?? new List<SheetPlan>()) yield return s.Number;
                }
                else if (c.Data.Command == Commands.RenumberSheets)
                {
                    var req = Json.Deserialize<RenumberSheetsRequest>(c.Data.Payload);
                    foreach (var a in req?.Assignments.OrEmpty() ?? new List<IdValue>()) yield return a.Value;
                }
            }
        }

        /// <summary>"first, second, third … and 12 more" preview text.</summary>
        private static string Preview(IEnumerable<string> lines, string header = null, int max = 15)
        {
            var list = lines.ToList();
            var text = string.Join("\n", list.Take(max).Select(l => "• " + l));
            if (list.Count > max) text += $"\n… and {list.Count - max} more";
            return string.IsNullOrEmpty(header) ? text : header + "\n" + text;
        }
    }
}
