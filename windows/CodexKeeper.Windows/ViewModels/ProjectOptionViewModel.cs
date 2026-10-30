using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexKeeper.Windows.Models;
using CodexKeeper.Windows.Services;

namespace CodexKeeper.Windows.ViewModels;

public sealed class ProjectOptionViewModel(Action<ProjectOptionViewModel, bool> selectionChanged) : INotifyPropertyChanged
{
    private bool _isSelected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; private set; } = "";
    public string WorkingDirectory { get; private set; } = "";
    public string SessionSummary { get; private set; } = "";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Notify();
            selectionChanged(this, value);
        }
    }

    public void Update(CodexProject project, bool selected)
    {
        Name = project.Name;
        WorkingDirectory = project.WorkingDirectory;
        SessionSummary = Localization.Format("projects.sessions", project.SessionCount);
        _isSelected = selected;
        Notify(nameof(Name));
        Notify(nameof(WorkingDirectory));
        Notify(nameof(SessionSummary));
        Notify(nameof(IsSelected));
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
