using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DrawingStudio.App.Services;
using DrawingStudio.Shared;

namespace DrawingStudio.App.ViewModels
{
    /// <summary>One open project in one Revit session.</summary>
    public sealed class ModelChoice
    {
        public SessionInfo Session { get; set; }
        public OpenModelInfo Model { get; set; }

        public string Display =>
            $"{Model.Title}   ·   Revit {Session.RevitVersion}" + (Model.IsActive ? "   (active in Revit)" : "");

        public override string ToString() => Display;
    }

    /// <summary>
    /// Model picker: every project open in every linked Revit. Picking one switches the
    /// connection (if it is in another Revit) and points every request at that model, even
    /// when it is not the one in front in Revit.
    /// </summary>
    public sealed partial class MainViewModel
    {
        private ModelChoice _selectedModelChoice;
        private bool _suppressModelSwitch;
        private string _openModelsSummary = "";
        private int _modelListTicks;

        public ObservableCollection<ModelChoice> OpenModels { get; } = new ObservableCollection<ModelChoice>();
        public string OpenModelsSummary { get => _openModelsSummary; private set => Set(ref _openModelsSummary, value); }
        public ICommand RefreshModelsCommand { get; private set; }

        public ModelChoice SelectedModelChoice
        {
            get => _selectedModelChoice;
            set
            {
                if (!Set(ref _selectedModelChoice, value) || _suppressModelSwitch || value == null) return;
                _ = SwitchModelAsync(value);
            }
        }

        private void InitModels()
        {
            RefreshModelsCommand = new AsyncCommand(_ => RefreshOpenModelsAsync(), () => !IsBusy);
        }

        /// <summary>Asks each linked Revit which projects it has open.</summary>
        private async Task RefreshOpenModelsAsync()
        {
            var sessions = SessionLocator.List();
            var found = new List<ModelChoice>();
            foreach (var s in sessions)
            {
                try
                {
                    var current = IsConnected && SelectedSession?.ProcessId == s.ProcessId;
                    var models = current
                        ? await _client.SendAsync<List<OpenModelInfo>>(Commands.ListModels)
                        : await BridgeClient.ListModelsAsync(s.PipeName);
                    foreach (var m in models ?? new List<OpenModelInfo>())
                        found.Add(new ModelChoice { Session = s, Model = m });
                }
                catch
                {
                    // that Revit is busy or closing; it is listed again next time
                }
            }

            // keep the list in a stable order: by Revit, then by model name
            found = found.OrderBy(c => c.Session.RevitVersion).ThenBy(c => c.Model.Title, StringComparer.OrdinalIgnoreCase).ToList();
            var currentKey = _client.TargetDocument;
            var selected = found.FirstOrDefault(c => IsConnected && c.Session.ProcessId == SelectedSession?.ProcessId &&
                                                     (currentKey != null ? c.Model.Key == currentKey : c.Model.Title == Model?.DocumentTitle))
                           ?? found.FirstOrDefault(c => IsConnected && c.Session.ProcessId == SelectedSession?.ProcessId && c.Model.IsActive);

            // only touch the list when it changed, so an open drop-down does not flicker
            var newKeys = string.Join("|", found.Select(c => c.Session.ProcessId + ":" + c.Model.Key + ":" + c.Model.IsActive));
            var oldKeys = string.Join("|", OpenModels.Select(c => c.Session.ProcessId + ":" + c.Model.Key + ":" + c.Model.IsActive));
            _suppressModelSwitch = true;
            try
            {
                if (newKeys != oldKeys)
                {
                    OpenModels.Clear();
                    foreach (var c in found) OpenModels.Add(c);
                }
                SelectedModelChoice = selected == null ? null
                    : OpenModels.FirstOrDefault(c => c.Session.ProcessId == selected.Session.ProcessId && c.Model.Key == selected.Model.Key);
            }
            finally
            {
                _suppressModelSwitch = false;
            }

            int revits = found.Select(c => c.Session.ProcessId).Distinct().Count();
            OpenModelsSummary = found.Count == 0 ? "No open models found"
                : $"{found.Count} model(s) open in {revits} Revit session(s)";
        }

        /// <summary>Points Drawing Studio at another open model (in this Revit or another one).</summary>
        private async Task SwitchModelAsync(ModelChoice choice)
        {
            // close the tabs of the previous model's views; their ids mean nothing in the new model
            foreach (var d in Documents.Where(d => d.Kind == "Picture" || d.Kind == "3D").ToList()) CloseDoc(d);

            _client.TargetDocument = choice.Model.Key;
            AddLog($"Switching to {choice.Model.Title} (Revit {choice.Session.RevitVersion}).");

            if (!IsConnected || SelectedSession?.ProcessId != choice.Session.ProcessId)
            {
                _suppressModelSwitch = true;
                try { SelectedSession = Sessions.FirstOrDefault(s => s.ProcessId == choice.Session.ProcessId) ?? choice.Session; }
                finally { _suppressModelSwitch = false; }
                _autoTried.Add(choice.Session.ProcessId);
                await ConnectAsync(keepTarget: true); // connects and reads the chosen model
            }
            else
            {
                await RunAsync("Loading " + choice.Model.Title, RefreshModelAsync);
            }
        }

        /// <summary>Called by the watcher: keeps the model list fresh (every ~12 s).</summary>
        private async Task TickModelListAsync()
        {
            if (++_modelListTicks % 4 != 1) return;
            try { await RefreshOpenModelsAsync(); } catch { /* next time */ }
        }
    }
}
