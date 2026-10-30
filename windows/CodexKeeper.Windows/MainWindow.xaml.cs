using System.Windows;
using CodexKeeper.Windows.ViewModels;
using Microsoft.Win32;
using System.Windows.Media.Imaging;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace CodexKeeper.Windows;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Resources/codex-keeper.ico", UriKind.Absolute));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshNowAsync();
    private async void RefreshProjects_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshProjectsAsync();
    private async void SaveSchedule_Click(object sender, RoutedEventArgs e) => await _viewModel.SaveScheduleAsync();

    private async void BrowseCodex_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _viewModel.CliTitle,
            Filter = "Codex CLI (codex.exe)|codex.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await _viewModel.SaveCodexExecutableAsync(dialog.FileName);
    }

    private async void AutoCodex_Click(object sender, RoutedEventArgs e) => await _viewModel.SaveCodexExecutableAsync(null);
}
