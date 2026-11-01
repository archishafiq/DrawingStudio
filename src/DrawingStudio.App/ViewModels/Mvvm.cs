using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DrawingStudio.App.ViewModels
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object parameter) => _execute(parameter);
    }

    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<object, Task> _execute;
        private readonly Func<bool> _canExecute;

        public AsyncCommand(Func<object, Task> execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        public async void Execute(object parameter) => await _execute(parameter);
    }

    public interface ICheckable
    {
        bool IsChecked { get; set; }
    }

    /// <summary>List row with a checkbox.</summary>
    public sealed class Checkable<T> : ObservableObject, ICheckable
    {
        private bool _isChecked;

        public Checkable(T item, string label, string detail = null, bool isChecked = false)
        {
            Item = item;
            Label = label;
            Detail = detail;
            _isChecked = isChecked;
        }

        public T Item { get; }
        public string Label { get; }
        public string Detail { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set => Set(ref _isChecked, value);
        }
    }

    public sealed class Option
    {
        public Option(string key, string label) { Key = key; Label = label; }
        public string Key { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    /// <summary>Editable row in the planned-sheets grid.</summary>
    public sealed class SheetRow : ObservableObject
    {
        private bool _include = true;
        private string _number;
        private string _name;

        public bool Include { get => _include; set => Set(ref _include, value); }
        public string Number { get => _number; set => Set(ref _number, value); }
        public string Name { get => _name; set => Set(ref _name, value); }
        public List<long> ViewIds { get; set; } = new List<long>();
        public string ViewsText { get; set; }
    }

    internal static class CheckHelper
    {
        public static void SetAll(object source, bool value)
        {
            // Works with an ICollectionView too, so only visible (filtered) rows change.
            if (source is IEnumerable items)
                foreach (var item in items)
                    if (item is ICheckable c) c.IsChecked = value;
        }
    }
}
