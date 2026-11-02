using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexKeeper.Windows.Models;
using CodexKeeper.Windows.Services;

namespace CodexKeeper.Windows.ViewModels;

public sealed class TaskOptionViewModel(Action<TaskOptionViewModel, bool> selectionChanged) : INotifyPropertyChanged
{
    private bool _isSelected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string ThreadId { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public string WorkingDirectory { get; private set; } = "";
    public string Status { get; private set; } = "";
    public bool CanSelect { get; private set; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value || !CanSelect) return;
            _isSelected = value;
            Notify();
            selectionChanged(this, value);
        }
    }

    public void Update(CodexTask task, bool selected)
    {
        ThreadId = task.ThreadId;
        Title = task.Title;
        WorkingDirectory = task.WorkingDirectory;
        var time = task.UpdatedAt.ToLocalTime().ToString(Localization.IsChinese ? "M月d日 HH:mm" : "MMM d, h:mm tt", CultureInfo.CurrentCulture);
        var shortId = task.ThreadId[..Math.Min(8, task.ThreadId.Length)];
        Detail = $"{task.Project}  ·  {time}  ·  {shortId}";
        CanSelect = task.CanSelect;
        _isSelected = selected && CanSelect;
        Status = task.UnavailableReason ?? (!CanSelect ? Localization.Text("tasks.unverified")
            : task.IsRunning ? Localization.Text("tasks.running")
            : task.IsQuotaBlocked ? Localization.Text("tasks.blocked")
            : Localization.Text("tasks.idle"));
        foreach (var property in new[] { nameof(ThreadId), nameof(Title), nameof(WorkingDirectory), nameof(Detail), nameof(Status), nameof(CanSelect), nameof(IsSelected) }) Notify(property);
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
