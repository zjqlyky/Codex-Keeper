using System.Threading;
using System.Windows;
using CodexKeeper.Windows.Services;
using CodexKeeper.Windows.ViewModels;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;
using Localization = CodexKeeper.Windows.Services.Localization;

namespace CodexKeeper.Windows;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;
    private EventWaitHandle? _activationSignal;
    private RegisteredWaitHandle? _activationWait;
    private KeeperService? _keeper;
    private Forms.NotifyIcon? _tray;
    private MainViewModel? _viewModel;
    private bool _explicitExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, "CodexKeeper.Windows.SingleInstance", out var created);
        _ownsSingleInstance = created;
        _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "CodexKeeper.Windows.Activate");
        var background = e.Args.Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
        if (!created)
        {
            if (!background) _activationSignal.Set();
            Shutdown();
            return;
        }

        var store = new SettingsStore();
        _keeper = new KeeperService(store);
        _viewModel = new MainViewModel(store, _keeper);
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Closing += (_, args) =>
        {
            if (_explicitExit) return;
            args.Cancel = true;
            window.Hide();
        };
        _tray = BuildTray(window);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activationSignal, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_explicitExit) ShowWindow(window);
                }));
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        if (background) window.Hide();
        else window.Show();
        _keeper.Start();
    }

    private Forms.NotifyIcon BuildTray(Window window)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Localization.Text("tray.open"), null, (_, _) => ShowWindow(window));
        var enabled = new Forms.ToolStripMenuItem(Localization.Text("tray.enabled"))
        {
            Checked = _viewModel?.IsEnabled == true,
            CheckOnClick = true
        };
        enabled.Click += (_, _) => { if (_viewModel is not null) _viewModel.IsEnabled = enabled.Checked; };
        menu.Items.Add(enabled);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Localization.Text("tray.exit"), null, (_, _) => ExitApplication());
        var icon = new Forms.NotifyIcon
        {
            Text = "Codex Keeper",
            Icon = Environment.ProcessPath is { } path
                ? System.Drawing.Icon.ExtractAssociatedIcon(path) ?? System.Drawing.SystemIcons.Application
                : System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => ShowWindow(window);
        _viewModel!.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsEnabled))
                enabled.Checked = _viewModel.IsEnabled;
            if (args.PropertyName == nameof(MainViewModel.TraySummary))
                icon.Text = _viewModel.TraySummary.Length <= 63 ? _viewModel.TraySummary : _viewModel.TraySummary[..63];
        };
        return icon;
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private async void ExitApplication()
    {
        _explicitExit = true;
        if (_keeper is not null) await _keeper.StopAsync();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        MainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _activationWait?.Unregister(null);
        _activationSignal?.Dispose();
        if (_ownsSingleInstance) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
