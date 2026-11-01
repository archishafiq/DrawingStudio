using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DrawingStudio.App.Services;
using DrawingStudio.Shared;

namespace DrawingStudio.App.ViewModels
{
    /// <summary>Connection, model data, logging and standards. Each tab lives in its own partial file.</summary>
    public sealed partial class MainViewModel : ObservableObject
    {
        private readonly BridgeClient _client = new BridgeClient();
        private SessionInfo _selectedSession;
        private bool _isConnected;
        private bool _isBusy;
        private string _status = "Not connected";
        private string _documentTitle = "No model";
        private StandardsConfig _standards;

        public MainViewModel(int? revitProcessId)
        {
            _standards = StandardsStore.Load();
            InitViews();
            InitSheets();
            InitAnnotation();
            InitQa();
            InitChanges();
            InitPreviews();
            InitModelTab();
            InitLayout();

            RefreshSessionsCommand = new RelayCommand(_ => RefreshSessions());
            ConnectCommand = new AsyncCommand(_ => ConnectAsync(), () => SelectedSession != null && !IsBusy);
            RefreshModelCommand = new AsyncCommand(_ => RunAsync("Reading model", RefreshModelAsync), CanRun);
            SaveStandardsCommand = new RelayCommand(_ => SaveStandards());
            ResetStandardsCommand = new RelayCommand(_ => ResetStandards());
            CheckAllCommand = new RelayCommand(p => CheckHelper.SetAll(p, true));
            CheckNoneCommand = new RelayCommand(p => CheckHelper.SetAll(p, false));
            ClearLogCommand = new RelayCommand(_ => Log.Clear());

            LoadSnapshot();
            _preferredPid = revitProcessId;
            RefreshSessions();
            _ = AutoConnectTickAsync();

            // Keep watching: connect as soon as Revit starts, reconnect if it restarts.
            _watcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _watcher.Tick += async (_, __) => await AutoConnectTickAsync();
            _watcher.Start();
        }

        // ---------------------------------------------------------------- automatic connection

        private readonly DispatcherTimer _watcher;
        private readonly HashSet<int> _autoTried = new HashSet<int>();
        private int? _preferredPid;

        private async Task AutoConnectTickAsync()
        {
            if (IsBusy) return;
            if (IsConnected) WarnAboutUnlinkedRevits();

            if (IsConnected)
            {
                // Revit closed (or crashed) under us
                if (SelectedSession == null || !SessionLocator.IsAlive(SelectedSession.ProcessId) || !_client.IsConnected)
                {
                    _client.Close();
                    IsConnected = false;
                    _modelIsLive = false;
                    if (Model != null) DocumentTitle = Model.DocumentTitle + "  ·  offline (changes can still be prepared)";
                    AddLog("Revit session ended. You can keep preparing changes; they apply when Revit is back.");
                    RefreshSessions();
                }
                else if (!_modelIsLive)
                {
                    // Connected before a project was open: pick the model up once one is.
                    IsBusy = true;
                    try { await RefreshModelAsync(); Status = "Ready"; }
                    catch { /* still no project open */ }
                    finally { IsBusy = false; CommandManager.InvalidateRequerySuggested(); }
                }
                return;
            }

            RefreshSessions();
            var target = Sessions.FirstOrDefault(s => s.ProcessId == _preferredPid)
                         ?? (Sessions.Count == 1 ? Sessions[0] : null);
            if (target == null || _autoTried.Contains(target.ProcessId)) return;

            _autoTried.Add(target.ProcessId); // one automatic attempt per Revit session; Connect button retries
            SelectedSession = target;
            await ConnectAsync();
        }

        // ---------------------------------------------------------------- properties

        public ObservableCollection<SessionInfo> Sessions { get; } = new ObservableCollection<SessionInfo>();
        public ObservableCollection<string> Log { get; } = new ObservableCollection<string>();

        public SessionInfo SelectedSession { get => _selectedSession; set => Set(ref _selectedSession, value); }
        public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
        public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string DocumentTitle { get => _documentTitle; private set => Set(ref _documentTitle, value); }
        public ModelInfo Model { get; private set; }

        public StandardsConfig Standards
        {
            get => _standards;
            private set { _standards = value; OnPropertyChanged(); }
        }

        public string[] SuitabilityCodes => StandardsConfig.SuitabilityCodes;
        public string[] TitleStripSides { get; } = { "Right", "Bottom" };

        public ICommand RefreshSessionsCommand { get; }
        public ICommand ConnectCommand { get; }
        public ICommand RefreshModelCommand { get; }
        public ICommand SaveStandardsCommand { get; }
        public ICommand ResetStandardsCommand { get; }
        public ICommand CheckAllCommand { get; }
        public ICommand CheckNoneCommand { get; }
        public ICommand ClearLogCommand { get; }

        // ---------------------------------------------------------------- connection

        private bool CanRun() => IsConnected && !IsBusy;

        /// <summary>Preparing changes only needs model data (live or snapshot), not a live connection.</summary>
        private bool CanPrepare() => Model != null && !IsBusy;

        private void RefreshSessions()
        {
            var keep = SelectedSession?.ProcessId;
            Sessions.Clear();
            foreach (var s in SessionLocator.List()) Sessions.Add(s);
            SelectedSession = Sessions.FirstOrDefault(s => s.ProcessId == keep) ?? Sessions.FirstOrDefault();
            if (Sessions.Count == 0 && !IsBusy) Status = "Waiting for Revit… start Revit and open a project.";
            WarnAboutUnlinkedRevits();
        }

        private readonly HashSet<int> _warnedUnlinked = new HashSet<int>();
        private string _emptyHint = DefaultEmptyHint;
        private const string DefaultEmptyHint = "Open a view or sheet from the Project Browser, or pick a tool on the ribbon.";

        /// <summary>Message in the empty view area; explains what to do when Revit has no project.</summary>
        public string EmptyHint { get => _emptyHint; private set => Set(ref _emptyHint, value); }

        /// <summary>
        /// A Revit started before Drawing Studio added itself has no link. Say so once per window,
        /// naming it by its title (which shows the open model), instead of failing silently.
        /// </summary>
        private void WarnAboutUnlinkedRevits()
        {
            List<SessionLocator.Unlinked> unlinked;
            try { unlinked = SessionLocator.UnlinkedRevits(Sessions); } catch { return; }
            foreach (var u in unlinked.Where(u => _warnedUnlinked.Add(u.ProcessId)))
            {
                AddLog($"“{u.Title}” (process {u.ProcessId}) is not linked: it was started before Drawing Studio was added. " +
                       "Save, close and reopen that Revit to link it.");
                EmptyHint = $"“{u.Title}” is not linked yet. Save, close and reopen that Revit, then it connects by itself.";
            }
        }

        private async Task ConnectAsync()
        {
            if (SelectedSession == null) return;
            IsBusy = true;
            Status = "Connecting to " + SelectedSession + "…";
            try
            {
                await _client.ConnectAsync(SelectedSession.PipeName);
                await _client.SendAsync<string>(Commands.Ping);
                IsConnected = true;
                AddLog("Connected to " + SelectedSession + ".");
                IsBusy = false;
                await RunAsync("Reading model", RefreshModelAsync);
            }
            catch (Exception ex)
            {
                IsConnected = false;
                Status = ex.Message;
                AddLog("ERROR: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private bool _modelIsLive;
        private int _runNumber;
        private string _lastLogged;

        private async Task RefreshModelAsync()
        {
            var model = await Send<ModelInfo>(Commands.ModelInfo);
            _modelIsLive = true;
            _lastLogged = null;
            EmptyHint = DefaultEmptyHint;
            ShowModel(model);
            DocumentTitle = model.DocumentTitle + "  ·  Revit " + model.RevitVersion;
            SaveSnapshot(model);

            AddLog($"Model: {model.Levels.OrEmpty().Count} levels, {model.Grids.OrEmpty().Count} grids, " +
                   $"{model.Views.OrEmpty().Count} views, {model.Sheets.OrEmpty().Count} sheets.");
        }

        private void ShowModel(ModelInfo model)
        {
            if (Model != null && !string.Equals(Model.DocumentTitle, model.DocumentTitle, StringComparison.OrdinalIgnoreCase))
            {
                // another model: its pictures and 3D come from its own cache folder
                _model3DFile = null;
                OnPropertyChanged(nameof(Model3DFile));
                _renderedThisSession.Clear();
            }
            Model = model;
            OnPropertyChanged(nameof(Model));
            LoadViewsTab(model);
            LoadSheetsTab(model);
            LoadAnnotationTab(model);
            LoadQaTab(model);
            LoadModelTab(model);
            BuildPropertyRows();
            CommandManager.InvalidateRequerySuggested();
        }

        // Last model read from Revit, so changes can be prepared while Revit is closed.
        private static string SnapshotFile => System.IO.Path.Combine(PortablePaths.DataDir, "model-snapshot.json");

        private void SaveSnapshot(ModelInfo model)
        {
            try { System.IO.File.WriteAllText(SnapshotFile, Json.Serialize(model)); } catch { /* not critical */ }
        }

        private void LoadSnapshot()
        {
            try
            {
                if (!System.IO.File.Exists(SnapshotFile)) return;
                var model = Json.Deserialize<ModelInfo>(System.IO.File.ReadAllText(SnapshotFile));
                if (model == null) return;
                ShowModel(model);
                var when = System.IO.File.GetLastWriteTime(SnapshotFile);
                DocumentTitle = $"{model.DocumentTitle}  ·  offline snapshot from {when:dd MMM HH:mm}";
                AddLog("Loaded the last model snapshot. Prepare changes now; apply them when Revit is connected.");
            }
            catch { /* ignore a bad snapshot */ }
        }

        // ---------------------------------------------------------------- plumbing

        private async Task RunAsync(string label, Func<Task> work)
        {
            if (IsBusy) return;
            IsBusy = true;
            Status = label + "…";
            int run = ++_runNumber;
            // If Revit has not answered after a few seconds, say why it may be waiting.
            _ = Task.Delay(6000).ContinueWith(_ =>
            {
                if (IsBusy && run == _runNumber)
                    Status = label + "… Revit has not answered yet. If a dialog is open in Revit (for example “Always Load”), answer it; a very large model can take a minute.";
            }, TaskScheduler.FromCurrentSynchronizationContext());
            try
            {
                await work();
                Status = "Ready";
            }
            catch (Exception ex)
            {
                if (ex.Message.IndexOf("no project open", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ex.Message.IndexOf("only has a family", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    EmptyHint = ex.Message;
                    Status = "Connected, but there is no project open in this Revit.";
                    if (_lastLogged != ex.Message) AddLog(ex.Message); // say it once, not on every retry
                    _lastLogged = ex.Message;
                    return;
                }
                AddLog("ERROR: " + ex.Message);
                Status = ex.Message;
                if (!_client.IsConnected)
                {
                    IsConnected = false;
                    Status = "Disconnected. " + ex.Message;
                }
            }
            finally
            {
                IsBusy = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private Task<T> Send<T>(string command) => _client.SendAsync<T>(command, AddLogs);

        private Task<TResult> Send<TRequest, TResult>(string command, TRequest request) =>
            _client.SendAsync<TRequest, TResult>(command, request, AddLogs);

        private void AddLogs(IEnumerable<string> lines)
        {
            foreach (var l in lines) AddLog(l);
        }

        public void AddLog(string message)
        {
            void Write() => Log.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            if (Application.Current?.Dispatcher.CheckAccess() == false) Application.Current.Dispatcher.Invoke(Write);
            else Write();
        }

        private static bool Confirm(string message) =>
            MessageBox.Show(message, "Drawing Studio", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

        private static List<long> CheckedIds<T>(IEnumerable<Checkable<T>> items, Func<T, long> id) =>
            items.Where(i => i.IsChecked).Select(i => id(i.Item)).ToList();

        // ---------------------------------------------------------------- standards

        private void SaveStandards()
        {
            try
            {
                StandardsStore.Save(Standards);
                AddLog("Standards saved to " + StandardsStore.FilePath);
            }
            catch (Exception ex)
            {
                AddLog("ERROR: could not save standards: " + ex.Message);
            }
        }

        private void ResetStandards()
        {
            if (!Confirm("Reset all standards to the ISO 19650 defaults?")) return;
            Standards = StandardsConfig.CreateDefault();
            AddLog("Standards reset to defaults (not saved yet).");
        }

        public void Shutdown()
        {
            _watcher?.Stop();
            _previewTimer?.Stop();
            _client.Dispose();
        }
    }
}
