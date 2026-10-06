using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SquadDns.Core.Backup;
using SquadDns.Core.Diagnostics;
using SquadDns.Core.Io;
using SquadDns.Core.Models;
using SquadDns.Core.Settings;
using SquadDns.Core.Shell;
using SquadDns.Infrastructure;
using SquadDns.ViewModels;

namespace SquadDns;

public partial class App : Application
{
    private AppPaths _paths = new();
    private AppSettings _settings = new();
    private AppLog _log = null!;
    private bool _uiErrorBusy;
    private string? _lastUiError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _paths = new AppPaths();
        _settings = AppSettings.Load(_paths.SettingsFile);
        _log = new AppLog(_paths);

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log.Write("fatal", args.ExceptionObject?.ToString() ?? "unknown");

        LocalizationManager.Apply(_settings.Language);
        ThemeManager.Apply(_settings.Theme);

        var args = e.Args;
        if (args.Length > 0 && args[0].Equals("--apply", StringComparison.OrdinalIgnoreCase))
        {
            RunHeadlessApply(args);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            RunHeadlessSelfTest(args);
            return;
        }

        ShowInterface();
    }

    private void ShowInterface()
    {
        var registry = new WindowsRegistryAccess();
        IShell shell = new PowerShellShell(new ProcessRunner());
        var backups = new BackupStore(_paths, registry);

        var viewModel = new MainViewModel(
            _settings,
            _paths,
            registry,
            shell,
            backups,
            _log,
            () =>
            {
                try
                {
                    _settings.Save(_paths.SettingsFile);
                }
                catch (Exception ex)
                {
                    _log.Write("settings-save", ex.Message);
                }
            });

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        _log.Write("start", $"Squad DNS {typeof(App).Assembly.GetName().Version} elevated={Elevation.IsElevated()}");
        _ = viewModel.InitializeAsync();
    }

    private void RunHeadlessApply(string[] args)
    {
        // --apply <profileId> <interfaceIndex> <interfaceAlias> <mode> [--preview]
        if (args.Length < 5)
        {
            _log.Write("headless", "invalid --apply arguments");
            Shutdown(2);
            return;
        }

        var profileId = args[1];
        if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var interfaceIndex))
        {
            interfaceIndex = -1;
        }

        var alias = args[3];
        var mode = Enum.TryParse<DnsSecurityMode>(args[4], true, out var parsed)
            ? parsed
            : DnsSecurityMode.EncryptedPreferred;
        var preview = args.Skip(5).Any(a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase));

        var registry = new WindowsRegistryAccess();
        IShell shell = new PowerShellShell(new ProcessRunner());
        var backups = new BackupStore(_paths, registry);

        var viewModel = new MainViewModel(_settings, _paths, registry, shell, backups, _log, () => _settings.Save(_paths.SettingsFile));

        _ = viewModel.ApplyFromCommandLineAsync(profileId, interfaceIndex, alias, mode, preview)
            .ContinueWith(task =>
            {
                try
                {
                    if (task.IsFaulted)
                    {
                        _log.Write("headless-error", task.Exception?.ToString() ?? "faulted");
                        WriteFailure(preview, task.Exception?.GetBaseException().Message ?? "error");
                        return;
                    }

                    MainViewModel.WriteHandshake(_paths, task.Result);
                    _log.Write("headless", $"apply {profileId} -> {task.Result.Status}");
                }
                catch (Exception ex)
                {
                    _log.Write("headless-error", ex.ToString());
                }
                finally
                {
                    ExitApplication(0);
                }
            }, TaskScheduler.Default);
    }

    private void WriteFailure(bool preview, string message)
    {
        try
        {
            MainViewModel.WriteHandshake(_paths, new ApplyResult(
                preview ? ApplyStatus.DryRun : ApplyStatus.Failed,
                "apply.failed",
                Array.Empty<ApplyStep>(),
                message));
        }
        catch (IOException)
        {
            // nothing else to do in the elevated child
        }
    }

    private void RunHeadlessSelfTest(string[] args)
    {
        var registry = new WindowsRegistryAccess();
        IShell shell = new PowerShellShell(new ProcessRunner());
        var probe = args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase));

        var selfTest = new SelfTest(_paths, registry, shell);

        _ = selfTest.RunAsync(_settings.TestDomain, _settings.TestSampleCount, probe)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    _log.Write("selftest-error", task.Exception?.ToString() ?? "faulted");
                }

                ExitApplication(0);
            }, TaskScheduler.Default);
    }

    private void ExitApplication(int exitCode)
    {
        Dispatcher.BeginInvoke(new Action(() => Shutdown(exitCode)));
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var text = e.Exception.ToString();
        e.Handled = true;

        // MessageBox.Show pompe le dispatcher de WPF : sans garde, l'opération en échec se rejoue dans
        // la boucle imbriquée et la récursion aboutit à un stack overflow natif (0xc00000fd).
        if (_uiErrorBusy || text == _lastUiError)
        {
            return;
        }

        _lastUiError = text;
        _uiErrorBusy = true;
        try
        {
            _log.Write("ui-error", text);
            MessageBox.Show(
                e.Exception.Message,
                LocalizationManager.T("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _uiErrorBusy = false;
        }
    }
}
