using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SquadDns.Core.Backup;
using SquadDns.Core.Catalog;
using SquadDns.Core.Diagnostics;
using SquadDns.Core.Io;
using SquadDns.Core.Models;
using SquadDns.Core.Network;
using SquadDns.Core.Proxy;
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

        if (args.Length > 0 && args[0].Equals("--restore", StringComparison.OrdinalIgnoreCase))
        {
            RunHeadlessRestore(args);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--proxy", StringComparison.OrdinalIgnoreCase))
        {
            RunHeadlessProxy(args);
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
                    var faults = string.Join(" | ", task.Result.Steps
                        .Where(step => step.Executed && !step.Success)
                        .Select(step => $"{step.Name}: {(string.IsNullOrWhiteSpace(step.Error) ? "aucun message" : step.Error)}"));
                    _log.Write("headless", $"apply {profileId} -> {task.Result.Status}" +
                                           (faults.Length == 0 ? string.Empty : " :: " + faults));
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

    // --restore <backupId> : restauration elevee avec poignee de main, pour les parcours ou le
    // parent n'est pas administrateur (repli du proxy local, restauration depuis la liste).
    private void RunHeadlessRestore(string[] args)
    {
        if (args.Length < 2)
        {
            _log.Write("headless", "invalid --restore arguments");
            Shutdown(2);
            return;
        }

        var registry = new WindowsRegistryAccess();
        IShell shell = new PowerShellShell(new ProcessRunner());
        var backups = new BackupStore(_paths, registry);
        var backup = backups.Load(args[1]);

        if (backup is null)
        {
            WriteFailure(preview: false, $"backup {args[1]} not found");
            Shutdown(2);
            return;
        }

        var configurator = new DnsConfigurator(shell, registry);

        _ = configurator.RestoreAsync(backup.ToPlan())
            .ContinueWith(task =>
            {
                try
                {
                    if (task.IsFaulted)
                    {
                        _log.Write("headless-error", task.Exception?.ToString() ?? "faulted");
                        WriteFailure(preview: false, task.Exception?.GetBaseException().Message ?? "error");
                        return;
                    }

                    MainViewModel.WriteHandshake(_paths, task.Result);
                    var faults = string.Join(" | ", task.Result.Steps
                        .Where(step => step.Executed && !step.Success)
                        .Select(step => $"{step.Name}: {(string.IsNullOrWhiteSpace(step.Error) ? "aucun message" : step.Error)}"));
                    _log.Write("headless", $"restore {args[1]} -> {task.Result.Status}" +
                                           (faults.Length == 0 ? string.Empty : " :: " + faults));
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

    // --proxy <profileId> <mode> : relais DNS local (127.0.0.1:53 -> DoH). Processus detache,
    // lance par l'application principale : le chiffrement survit a la fermeture de la fenetre.
    // Aucune fenetre n'est creee : la boucle de message WPF tourne a vide et le process vit
    // jusqu'a ce que --proxy-stop (depuis l'application) ou l'utilisateur le tue.
    private void RunHeadlessProxy(string[] args)
    {
        // --proxy <profileId> <mode>
        if (args.Length < 3)
        {
            _log.Write("proxy", "invalid --proxy arguments");
            Shutdown(2);
            return;
        }

        var profile = ProviderCatalog.Find(args[1]) ?? LoadCustomProfile();

        if (profile is null)
        {
            _log.Write("proxy", "unknown profile " + args[1]);
            Shutdown(2);
            return;
        }

        IDnsUpstream primary = new DohUpstream(profile.DoHTemplate, profile.ResolverAddresses);
        IDnsUpstream? fallback = null;

        if (Enum.TryParse<DnsSecurityMode>(args[2], true, out var proxyMode)
            && proxyMode == DnsSecurityMode.EncryptedPreferred)
        {
            fallback = new PlainUdpUpstream(profile.ResolverAddresses);
        }

        LocalDnsProxy proxy;

        try
        {
            proxy = new LocalDnsProxy(primary, fallback);
            proxy.Start(LocalDnsProxy.DefaultPort);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // Port 53 occupe (exclusion Hyper-V, autre resolveur) : le parent s'en apercevra
            // par l'absence du fichier d'etat et déclenchera le repli.
            _log.Write("proxy", "bind failed: " + ex.Message);
            Shutdown(3);
            return;
        }

        ProxyStateStore.Write(_paths.ProxyStateFile, new LocalProxyState(profile.Id, args[2], Environment.ProcessId));
        _log.Write("proxy", $"listening on 127.0.0.1:{proxy.Port} for {profile.Id} pid={Environment.ProcessId}");

        // Nettoyage en sortie gracieuse seulement ; un processus tue laisse un fichier d'etat
        // avec un pid mort, que les lecteurs verifient toujours avant d'y croire.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            proxy.Dispose();
            ProxyStateStore.Clear(_paths.ProxyStateFile);
        };
    }

    private DnsProfile? LoadCustomProfile()
    {
        if (string.IsNullOrWhiteSpace(_settings.CustomProfileJson))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<DnsProfile>(_settings.CustomProfileJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
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

            // Une boite de dialogue qui echoue (plus aucune fenetre, station sans bureau) ne doit pas
            // transformer une erreur recuperable en arret brutal du processus.
            MessageBox.Show(
                e.Exception.Message,
                LocalizationManager.T("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception reportFault)
        {
            Console.WriteLine("error reporting failed: " + reportFault);
        }
        finally
        {
            _uiErrorBusy = false;
        }
    }
}
