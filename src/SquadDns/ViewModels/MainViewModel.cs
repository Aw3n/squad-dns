using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using SquadDns.Core.Backup;
using SquadDns.Core.Catalog;
using SquadDns.Core.Diagnostics;
using SquadDns.Core.Io;
using SquadDns.Core.Models;
using SquadDns.Core.Network;
using SquadDns.Core.Settings;
using SquadDns.Core.Shell;
using SquadDns.Core.Testing;
using SquadDns.Core.Updates;
using SquadDns.Infrastructure;

namespace SquadDns.ViewModels;

public enum StatusLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppPaths _paths;
    private readonly IRegistryAccess _registry;
    private readonly IShell _shell;
    private readonly InterfaceBrowser _browser;
    private readonly DnsConfigurator _configurator;
    private readonly BackupStore _backups;
    private readonly LatencyTester _tester;
    private readonly AppLog _log;
    private readonly Action _persistSettings;
    private readonly List<DnsProfile> _sourceProfiles = new();

    private CancellationTokenSource? _runCancellation;
    private int _section;
    private string _searchText = string.Empty;
    private AdapterRow? _selectedAdapter;
    private ProfileCard? _selectedProfile;
    private BackupRow? _selectedBackup;
    private string _statusLine = string.Empty;
    private StatusLevel _statusLevel = StatusLevel.Info;
    private string _policyLine = string.Empty;
    private string _activeServersLine = string.Empty;
    private string _securityStateLine = string.Empty;
    private string _bestLine = string.Empty;
    private string _updateLine = string.Empty;
    private string _osText = string.Empty;
    private string _customName = string.Empty;
    private string _customTemplate = string.Empty;
    private string _customAddresses = string.Empty;
    private string _customDotServer = string.Empty;
    private string _customDotPort = "853";
    private bool _isBusy;
    private bool _testsRunning;
    private bool _previewOnly;
    private double _testProgress;

    public MainViewModel(
        AppSettings settings,
        AppPaths paths,
        IRegistryAccess registry,
        IShell shell,
        BackupStore backups,
        AppLog log,
        Action persistSettings)
    {
        Settings = settings;
        _paths = paths;
        _registry = registry;
        _shell = shell;
        _browser = new InterfaceBrowser(shell);
        _configurator = new DnsConfigurator(shell, registry);
        _backups = backups;
        _tester = new LatencyTester();
        _log = log;
        _persistSettings = persistSettings;

        Adapters = new ObservableCollection<AdapterRow>();
        Profiles = new ObservableCollection<ProfileCard>();
        AllProfiles = new ObservableCollection<ProfileCard>();
        Backups = new ObservableCollection<BackupRow>();
        Steps = new ObservableCollection<StepRow>();

        RefreshCommand = new AsyncRelayCommand(async _ => await RefreshAsync());
        ApplyCommand = new AsyncRelayCommand(async parameter => await ApplyAsync(parameter as ProfileCard));
        TestProfileCommand = new AsyncRelayCommand(async parameter => await TestAsync(parameter as ProfileCard));
        TestAllCommand = new AsyncRelayCommand(async _ => await TestAllAsync());
        StopTestsCommand = new RelayCommand(_ => _runCancellation?.Cancel());
        CreateBackupCommand = new AsyncRelayCommand(async _ => await CreateBackupAsync(SelectedProfile?.Profile ?? ProviderCatalog.BuiltIn[0], silent: false));
        RestoreBackupCommand = new AsyncRelayCommand(async parameter => await RestoreBackupAsync(parameter as BackupRow));
        DeleteBackupCommand = new AsyncRelayCommand(async parameter => await DeleteBackupAsync(parameter as BackupRow));
        ExportBackupCommand = new RelayCommand(_ => ExportBackupToFile(SelectedBackup));
        SaveCustomCommand = new RelayCommand(_ => SaveCustomProfile());
        CheckUpdateCommand = new AsyncRelayCommand(async _ => await CheckUpdateAsync());
        SelfTestCommand = new AsyncRelayCommand(async _ => await SelfTestAsync());
        ElevateCommand = new RelayCommand(_ => ElevateNow());
        CopyDonationCommand = new RelayCommand(_ => CopyToClipboard(AppInfo.DonationAddress, "about.copied"));
        OpenSwapCommand = new RelayCommand(_ => OpenExternal(AppInfo.SwapUrl));
        OpenXelisCommand = new RelayCommand(_ => OpenExternal(AppInfo.XelisUrl));
        OpenLogsCommand = new RelayCommand(_ => OpenExternal(_paths.Logs));

        LocalizationManager.Changed += RefreshLocalizedTexts;
        LoadProfiles();
    }

    public AppSettings Settings { get; }

    public ObservableCollection<AdapterRow> Adapters { get; }
    public ObservableCollection<ProfileCard> Profiles { get; }
    public ObservableCollection<ProfileCard> AllProfiles { get; }
    public ObservableCollection<BackupRow> Backups { get; }
    public ObservableCollection<StepRow> Steps { get; }

    public bool HasBackups => Backups.Count > 0;

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand TestProfileCommand { get; }
    public AsyncRelayCommand TestAllCommand { get; }
    public RelayCommand StopTestsCommand { get; }
    public AsyncRelayCommand CreateBackupCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }
    public AsyncRelayCommand DeleteBackupCommand { get; }
    public RelayCommand ExportBackupCommand { get; }
    public RelayCommand SaveCustomCommand { get; }
    public AsyncRelayCommand CheckUpdateCommand { get; }
    public AsyncRelayCommand SelfTestCommand { get; }
    public RelayCommand ElevateCommand { get; }
    public RelayCommand CopyDonationCommand { get; }
    public RelayCommand OpenSwapCommand { get; }
    public RelayCommand OpenXelisCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    public int Section { get => _section; set => Set(ref _section, value); }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public AdapterRow? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (Set(ref _selectedAdapter, value))
            {
                Settings.PreferredInterfaceIndex = value?.Index;
                _persistSettings();
                _ = RefreshStateAsync();
            }
        }
    }

    public ProfileCard? SelectedProfile { get => _selectedProfile; set => Set(ref _selectedProfile, value); }
    public BackupRow? SelectedBackup { get => _selectedBackup; set => Set(ref _selectedBackup, value); }

    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }
    public StatusLevel StatusLevelValue { get => _statusLevel; private set => Set(ref _statusLevel, value); }
    public string PolicyLine { get => _policyLine; private set => Set(ref _policyLine, value); }
    public string ActiveServersLine { get => _activeServersLine; private set => Set(ref _activeServersLine, value); }
    public string SecurityStateLine { get => _securityStateLine; private set => Set(ref _securityStateLine, value); }
    public string BestLine { get => _bestLine; private set => Set(ref _bestLine, value); }
    public string UpdateLine { get => _updateLine; private set => Set(ref _updateLine, value); }
    public string OsText { get => _osText; private set => Set(ref _osText, value); }

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool TestsRunning { get => _testsRunning; private set => Set(ref _testsRunning, value); }
    public double TestProgress { get => _testProgress; private set => Set(ref _testProgress, value); }

    public bool PreviewOnly { get => _previewOnly; set => Set(ref _previewOnly, value); }

    public bool IsElevated => Elevation.IsElevated();
    public string ElevationText => LocalizationManager.T(IsElevated ? "status.elevated" : "status.standard");
    public bool ShowElevationBanner => !IsElevated;

    public string CustomName { get => _customName; set => Set(ref _customName, value); }
    public string CustomTemplate { get => _customTemplate; set => Set(ref _customTemplate, value); }
    public string CustomAddresses { get => _customAddresses; set => Set(ref _customAddresses, value); }
    public string CustomDotServer { get => _customDotServer; set => Set(ref _customDotServer, value); }
    public string CustomDotPort { get => _customDotPort; set => Set(ref _customDotPort, value); }

    public string DonationAddress => AppInfo.DonationAddress;
    public string SwapUrl => AppInfo.SwapUrl;
    public string XelisUrl => AppInfo.XelisUrl;
    public string VersionText => AppInfo.Version;

    public string LogsPath => _paths.Logs;
    public string BackupsPath => _paths.Backups;
    public string SettingsPath => _paths.SettingsFile;
    public string RegistryPathDisplay => $"HKCU\\{BackupStore.RegistryPath}";

    public bool AdvancedMode
    {
        get => Settings.AdvancedMode;
        set
        {
            Settings.AdvancedMode = value;
            Persist();
        }
    }

    public bool AutoBackup
    {
        get => Settings.AutoBackupBeforeApply;
        set
        {
            Settings.AutoBackupBeforeApply = value;
            Persist();
        }
    }

    public bool UseDotAddresses
    {
        get => Settings.UseDotEndpoints;
        set
        {
            Settings.UseDotEndpoints = value;
            Persist();
        }
    }

    public string SampleCountText
    {
        get => Settings.TestSampleCount.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                Settings.TestSampleCount = Math.Clamp(parsed, 1, 20);
            }

            Persist();
        }
    }

    public string TestDomain
    {
        get => Settings.TestDomain;
        set
        {
            Settings.TestDomain = string.IsNullOrWhiteSpace(value) ? "example.com" : value.Trim();
            Persist();
        }
    }

    public string FeedUrl
    {
        get => Settings.UpdateFeedUrl;
        set
        {
            Settings.UpdateFeedUrl = value?.Trim() ?? string.Empty;
            Persist();
        }
    }

    public bool UpdatesEnabled
    {
        get => Settings.UpdatesEnabled;
        set
        {
            Settings.UpdatesEnabled = value;
            Persist();
        }
    }

    public bool EncryptedOnlyChecked
    {
        get => Settings.Mode == DnsSecurityMode.EncryptedOnly;
        set => SetMode(DnsSecurityMode.EncryptedOnly, value);
    }

    public bool EncryptedPreferredChecked
    {
        get => Settings.Mode == DnsSecurityMode.EncryptedPreferred;
        set => SetMode(DnsSecurityMode.EncryptedPreferred, value);
    }

    public bool UnencryptedChecked
    {
        get => Settings.Mode == DnsSecurityMode.Unencrypted;
        set => SetMode(DnsSecurityMode.Unencrypted, value);
    }

    public bool RainEnabled
    {
        get => ThemeManager.IsDark;
        set
        {
            SetTheme(value ? "dark" : "light");
            Raise(nameof(RainEnabled));
        }
    }

    public bool FrenchChecked
    {
        get => LocalizationManager.Language != "en";
        set
        {
            if (value)
            {
                SetLanguage("fr");
            }
            else
            {
                Raise();
            }
        }
    }

    public bool EnglishChecked
    {
        get => LocalizationManager.Language == "en";
        set
        {
            if (value)
            {
                SetLanguage("en");
            }
            else
            {
                Raise();
            }
        }
    }

    public bool DarkChecked
    {
        get => ThemeManager.IsDark;
        set
        {
            if (value)
            {
                SetTheme("dark");
            }
            else
            {
                Raise();
            }
        }
    }

    public bool LightChecked
    {
        get => !ThemeManager.IsDark;
        set
        {
            if (value)
            {
                SetTheme("light");
            }
            else
            {
                Raise();
            }
        }
    }

    public string InterfaceSummary => _selectedAdapter is null
        ? LocalizationManager.T("status.noAdapter")
        : $"{_selectedAdapter.Alias} (#{_selectedAdapter.Index})";

    public async Task InitializeAsync()
    {
        var os = await OsInfo.DetectAsync(_registry, _shell);
        OsText = string.Format(
            CultureInfo.CurrentUICulture,
            "{0} - DoH {1} - DoT {2}",
            os.FriendlyName,
            os.DohCmdletsAvailable ? LocalizationManager.T("common.on") : LocalizationManager.T("library.mode.plain"),
            os.DotSupportedBySystemResolver ? LocalizationManager.T("common.on") : LocalizationManager.T("common.off"));

        await RefreshAsync();

        if (Settings.UpdatesEnabled)
        {
            _ = CheckUpdateAsync();
        }
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var adapters = await _browser.ListAsync();
            Adapters.Clear();

            foreach (var adapter in adapters.Where(a => a.Family == "IPv4" || a.Servers.Count > 0))
            {
                Adapters.Add(new AdapterRow(adapter));
            }

            var preferred = Settings.PreferredInterfaceIndex is { } index
                ? Adapters.FirstOrDefault(a => a.Index == index)
                : null;

            _selectedAdapter = preferred
                ?? Adapters.FirstOrDefault(a => a.IsDefaultCandidate)
                ?? Adapters.FirstOrDefault();
            Raise(nameof(SelectedAdapter), nameof(InterfaceSummary));

            await RefreshStateAsync();
            ReloadBackups();
            _log.Write("refresh", $"{Adapters.Count} adapters, selected={_selectedAdapter?.Alias ?? "none"}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshStateAsync()
    {
        Raise(nameof(InterfaceSummary));

        if (_selectedAdapter is null)
        {
            PolicyLine = LocalizationManager.T("status.noAdapter");
            ActiveServersLine = "-";
            SecurityStateLine = LocalizationManager.T("status.unknown");
            return;
        }

        var state = await _configurator.ReadStateAsync(_selectedAdapter.Index, _selectedAdapter.Alias);

        PolicyLine = LocalizationManager.T(state.PolicyText);
        ActiveServersLine = state.Servers.Count == 0 ? "-" : string.Join(", ", state.Servers);
        SecurityStateLine = state.PolicyValue switch
        {
            3 => LocalizationManager.T("status.secured"),
            2 => LocalizationManager.T("status.fallback"),
            1 => LocalizationManager.T("status.plain"),
            _ => state.DohEntries.Count > 0 && state.Servers.Count > 0
                ? LocalizationManager.T("status.fallback")
                : LocalizationManager.T("status.unknown")
        };

        foreach (var card in AllProfiles)
        {
            card.IsCurrent = state.PolicyValue != 1
                && state.Servers.Any(server => card.Profile.ResolverAddresses.Contains(server, StringComparer.OrdinalIgnoreCase));
        }

        Raise(nameof(ElevationText), nameof(ShowElevationBanner));
    }

    private void LoadProfiles()
    {
        _sourceProfiles.Clear();
        _sourceProfiles.AddRange(ProviderCatalog.BuiltIn);

        var custom = LoadCustomProfile();
        if (custom is not null)
        {
            _sourceProfiles.Add(custom);
        }

        AllProfiles.Clear();
        foreach (var profile in _sourceProfiles.OrderBy(p => p.SortOrder))
        {
            AllProfiles.Add(new ProfileCard(profile));
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Profiles.Clear();
        var term = _searchText.Trim();

        foreach (var card in AllProfiles)
        {
            if (term.Length == 0
                || card.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || card.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
                || card.DoHTemplate.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                Profiles.Add(card);
            }
        }

        if (SelectedProfile is null || !Profiles.Contains(SelectedProfile))
        {
            SelectedProfile = Profiles.FirstOrDefault();
        }
    }

    private DnsProfile? LoadCustomProfile()
    {
        if (string.IsNullOrWhiteSpace(Settings.CustomProfileJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DnsProfile>(Settings.CustomProfileJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SaveCustomProfile()
    {
        var template = _customTemplate.Trim();

        if (!Uri.TryCreate(template, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(LocalizationManager.T("custom.invalid"), StatusLevel.Error);
            return;
        }

        var addresses = _customAddresses
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim());

        var profile = ProviderCatalog.FromCustom(_customName, template, addresses);
        var dotServer = string.IsNullOrWhiteSpace(_customDotServer) ? profile.DoTServer : _customDotServer.Trim();
        var dotPort = int.TryParse(_customDotPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            ? Math.Clamp(port, 1, 65535)
            : 853;

        profile = profile with { DoTServer = dotServer, DoTPort = dotPort };

        Settings.CustomProfileJson = JsonSerializer.Serialize(profile);
        Persist();

        LoadProfiles();
        SelectedProfile = AllProfiles.LastOrDefault(c => c.Profile.IsCustom);
        SetStatus(LocalizationManager.T("custom.added"), StatusLevel.Success);
    }

    public async Task ApplyAsync(ProfileCard? card)
    {
        if (card is null)
        {
            return;
        }

        if (_selectedAdapter is null)
        {
            SetStatus(LocalizationManager.T("status.noAdapter"), StatusLevel.Error);
            return;
        }

        IsBusy = true;
        var profile = card.Profile;
        var mode = Settings.Mode;

        // L'interface est figee avant le premier await : un clic sur « Actualiser » pendant la
        // sauvegarde automatique ou l'attente d'elevation mettraient _selectedAdapter a null, et
        // la relecture plus bas produirait une NullReferenceException brute.
        var adapter = _selectedAdapter;

        try
        {
            if (!PreviewOnly && AutoBackup)
            {
                await CreateBackupAsync(profile, silent: true);
            }

            var result = await _configurator.ApplyAsync(profile, adapter.Index, adapter.Alias, mode, PreviewOnly);

            if (result.Status == ApplyStatus.NeedsElevation)
            {
                var elevated = await ApplyWithElevationAsync(profile, mode, adapter);
                if (elevated is null)
                {
                    SetStatus(LocalizationManager.T("apply.elevationCanceled"), StatusLevel.Warning);
                    return;
                }

                result = elevated;
            }

            ShowSteps(result);

            // Sans le detail des etapes en echec, le journal dit seulement « Failed » et la faute reste
            // introuvable une fois la fenetre fermee : la premiere ligne stderr de chaque etape ko y va.
            var faults = string.Join(" | ", result.Steps
                .Where(step => step.Executed && !step.Success)
                .Select(step => $"{step.Name}: {(string.IsNullOrWhiteSpace(step.Error) ? "aucun message" : step.Error)}"));
            _log.Write("apply", $"{profile.Id} on {adapter.Alias} -> {result.Status}" +
                                (faults.Length == 0 ? string.Empty : " :: " + faults));
            Settings.LastProfileId = profile.Id;
            Persist();
            await RefreshStateAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"{LocalizationManager.T("common.error")}: {ex.Message}", StatusLevel.Error);
            _log.Write("apply-error", ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowSteps(ApplyResult result)
    {
        Steps.Clear();
        foreach (var step in result.Steps)
        {
            Steps.Add(new StepRow(step));
        }

        var message = LocalizationManager.T(result.SummaryKey);
        if (!string.IsNullOrEmpty(result.Detail))
        {
            message = $"{message}  [{result.Detail}]";
        }

        SetStatus(message, result.Status switch
        {
            ApplyStatus.Success => StatusLevel.Success,
            ApplyStatus.DryRun => StatusLevel.Info,
            ApplyStatus.PartialSuccess => StatusLevel.Warning,
            _ => StatusLevel.Error
        });
    }

    private async Task<ApplyResult?> ApplyWithElevationAsync(DnsProfile profile, DnsSecurityMode mode, AdapterRow adapter)
    {
        try
        {
            if (File.Exists(_paths.LastApplyFile))
            {
                File.Delete(_paths.LastApplyFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("elevation", "stale handshake file: " + ex.Message);
        }

        var executable = Environment.ProcessPath ?? "SquadDns.exe";
        var arguments = $"--apply {profile.Id} {adapter.Index} \"{adapter.Alias}\" {mode}"
            + (PreviewOnly ? " --preview" : string.Empty);

        if (!Elevation.StartProcessElevated(executable, arguments))
        {
            return null;
        }

        SetStatus(LocalizationManager.T("apply.elevatedLaunched"), StatusLevel.Info);

        for (var attempt = 0; attempt < 240; attempt++)
        {
            await Task.Delay(500);

            if (!File.Exists(_paths.LastApplyFile))
            {
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_paths.LastApplyFile);
                var parsed = JsonSerializer.Deserialize<ApplyHandshake>(json);
                if (parsed is null)
                {
                    continue;
                }

                TryDelete(_paths.LastApplyFile);
                return parsed.ToResult();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // the elevated child is still writing the handshake file
            }
        }

        return null;
    }

    public async Task<ApplyResult> ApplyFromCommandLineAsync(string profileId, int interfaceIndex, string interfaceAlias, DnsSecurityMode mode, bool preview)
    {
        var profile = ProviderCatalog.Find(profileId) ?? LoadCustomProfile();

        if (profile is null)
        {
            return new ApplyResult(ApplyStatus.Failed, "apply.noServers", Array.Empty<ApplyStep>(), interfaceAlias);
        }

        return await _configurator.ApplyAsync(profile, interfaceIndex, interfaceAlias, mode, preview);
    }

    public static void WriteHandshake(AppPaths paths, ApplyResult result)
    {
        var payload = JsonSerializer.Serialize(ApplyHandshake.From(result));
        File.WriteAllText(paths.LastApplyFile, payload);
    }

    private async Task TestAsync(ProfileCard? card)
    {
        if (card is null)
        {
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_runCancellation?.Token ?? CancellationToken.None);
        card.IsTesting = true;

        try
        {
            var result = await _tester.RunAsync(card.Profile, Settings.TestDomain, Settings.TestSampleCount, true, cancellation.Token);
            ApplyTestResult(card, result);
            SetStatus($"{card.Name}: {result.MedianDoH?.ToString(CultureInfo.InvariantCulture) ?? LocalizationManager.T("tests.failed")} {LocalizationManager.T("common.ms")}",
                result.DoHWorks ? StatusLevel.Success : StatusLevel.Warning);
        }
        catch (OperationCanceledException)
        {
            SetStatus(LocalizationManager.T("tests.stop"), StatusLevel.Info);
        }
        catch (Exception ex)
        {
            SetStatus($"{LocalizationManager.T("common.error")}: {ex.Message}", StatusLevel.Error);
        }
        finally
        {
            card.IsTesting = false;
        }
    }

    private async Task TestAllAsync()
    {
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();

        TestsRunning = true;
        TestProgress = 0;

        try
        {
            var cards = Profiles.ToList();
            for (var i = 0; i < cards.Count; i++)
            {
                if (_runCancellation.IsCancellationRequested)
                {
                    break;
                }

                var card = cards[i];
                card.IsTesting = true;

                try
                {
                    var result = await _tester.RunAsync(card.Profile, Settings.TestDomain, Settings.TestSampleCount, true, _runCancellation.Token);
                    ApplyTestResult(card, result);
                }
                catch (Exception ex)
                {
                    card.MedianDoH = LocalizationManager.T("tests.failed");
                    _log.Write("test-error", $"{card.Id}: {ex.Message}");
                }
                finally
                {
                    card.IsTesting = false;
                    TestProgress = (i + 1) * 100.0 / Math.Max(1, cards.Count);
                }
            }

            var baseline = await _tester.TestSystemResolverAsync(Settings.TestDomain);
            BestLine = BuildBestLine(baseline);
            SetStatus($"{LocalizationManager.T("tests.summary")}: {BestLine}", StatusLevel.Info);
            Section = 1;
        }
        catch (Exception ex)
        {
            // Sans ce garde-fou, TestsRunning resterait a true apres une exception : la barre de
            // progression se figerait et plus aucun test ne serait possible sans redemarrage.
            SetStatus($"{LocalizationManager.T("common.error")}: {ex.Message}", StatusLevel.Error);
            _log.Write("test-all-error", ex.ToString());
        }
        finally
        {
            TestsRunning = false;
        }
    }

    private string BuildBestLine(TestSample baseline)
    {
        var ranked = AllProfiles
            .Select(card => (Card: card, Value: TryParseMs(card.MedianDoH)))
            .Where(pair => pair.Value.HasValue)
            .OrderBy(pair => pair.Value!.Value)
            .ToList();

        var systemText = baseline.Success
            ? $"{LocalizationManager.T("tests.system")}: {baseline.LatencyMs.ToString("F0", CultureInfo.InvariantCulture)} {LocalizationManager.T("common.ms")}"
            : $"{LocalizationManager.T("tests.system")}: {LocalizationManager.T("tests.failed")}";

        if (ranked.Count == 0)
        {
            return systemText;
        }

        var best = ranked[0];
        return $"{LocalizationManager.T("tests.best")}: {best.Card.Name} {best.Value!.Value.ToString("F0", CultureInfo.InvariantCulture)} {LocalizationManager.T("common.ms")} - {systemText}";
    }

    private static double? TryParseMs(string text) =>
        double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static void ApplyTestResult(ProfileCard card, ProviderTestResult result)
    {
        card.MedianDoH = Format(result.MedianDoH);
        card.MedianDoT = Format(result.MedianDoT);
        card.MedianPlain = Format(result.MedianPlain);
        card.Note = result.Note;
        card.SetCounts(result.SuccessCount, result.TotalCount);
    }

    private static string Format(double? value) =>
        value.HasValue
            ? value.Value.ToString("F0", CultureInfo.InvariantCulture)
            : LocalizationManager.T("tests.failed");

    private async Task CreateBackupAsync(DnsProfile profile, bool silent)
    {
        var adapter = _selectedAdapter;
        if (adapter is null)
        {
            SetStatus(LocalizationManager.T("status.noAdapter"), StatusLevel.Error);
            return;
        }

        try
        {
            var state = await _configurator.ReadStateAsync(adapter.Index, adapter.Alias);
            var backup = _backups.Capture(adapter.Adapter, state, profile, profile.ResolverAddresses);
            ReloadBackups();

            if (!silent)
            {
                SetStatus($"{LocalizationManager.T("backup.auto.created")} {backup.CreatedAtText}", StatusLevel.Success);
            }
        }
        catch (Exception ex)
        {
            _log.Write("backup-error", ex.ToString());

            // silent = sauvegarde automatique demandee par ApplyAsync avant ecriture : elle sert de
            // filet de securite, donc l'ecriture doit s'arreter si elle echoue (ApplyAsync l'affiche
            // dans la barre d'etat, sans boite d'erreur).
            if (silent)
            {
                throw;
            }

            SetStatus($"{LocalizationManager.T("common.error")}: {ex.Message}", StatusLevel.Error);
        }
    }

    private async Task RestoreBackupAsync(BackupRow? row)
    {
        if (row is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _configurator.RestoreAsync(row.Backup.ToPlan(), PreviewOnly);

            if (result.Status == ApplyStatus.NeedsElevation)
            {
                SetStatus(LocalizationManager.T("apply.needsElevation"), StatusLevel.Warning);
                return;
            }

            ShowSteps(result);
            await RefreshStateAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"{LocalizationManager.T("common.error")}: {ex.Message}", StatusLevel.Error);
            _log.Write("restore-error", ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteBackupAsync(BackupRow? row)
    {
        if (row is null)
        {
            return;
        }

        _backups.Delete(row.Id);
        ReloadBackups();
        await Task.CompletedTask;
    }

    private void ExportBackupToFile(BackupRow? row)
    {
        if (row is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationManager.T("backups.export"),
            FileName = $"squadns-backup-{row.Id}.json",
            InitialDirectory = _paths.Backups,
            DefaultExt = "json",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var path = _backups.Export(row.Id, dialog.FileName);
            SetStatus($"{LocalizationManager.T("backups.export")}: {path}", StatusLevel.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"{LocalizationManager.T("common.error")}: {ex.Message}", StatusLevel.Error);
        }
    }

    private void ReloadBackups()
    {
        var selectedId = SelectedBackup?.Id;
        Backups.Clear();

        foreach (var backup in _backups.List())
        {
            Backups.Add(new BackupRow(backup));
        }

        SelectedBackup = Backups.FirstOrDefault(b => b.Id == selectedId) ?? Backups.FirstOrDefault();
        Raise(nameof(HasBackups));
    }

    private async Task CheckUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.UpdateFeedUrl))
        {
            UpdateLine = LocalizationManager.T("settings.updates.noUrl");
            return;
        }

        var checker = new UpdateChecker();
        UpdateInfo? info = null;

        try
        {
            info = await checker.CheckAsync(Settings.UpdateFeedUrl, AppInfo.Version);
        }
        catch (Exception ex)
        {
            _log.Write("update", ex.Message);
        }

        UpdateLine = info is null
            ? LocalizationManager.T("settings.updates.none")
            : $"{LocalizationManager.T("settings.updates.available")} {info.Version}";
    }

    private async Task SelfTestAsync()
    {
        IsBusy = true;
        try
        {
            var selfTest = new SelfTest(_paths, _registry, _shell);
            var report = await selfTest.RunAsync(Settings.TestDomain, Settings.TestSampleCount, probeLatency: false);
            OsText = report.OsLine;
            SetStatus($"{LocalizationManager.T("settings.selftestDone")} ({report.Interfaces.Count} adapters)", StatusLevel.Info);
        }
        catch (Exception ex)
        {
            SetStatus($"{LocalizationManager.T("common.error")}: {ex.Message}", StatusLevel.Error);
            _log.Write("selftest-error", ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ElevateNow()
    {
        var executable = Environment.ProcessPath ?? "SquadDns.exe";

        if (Elevation.StartProcessElevated(executable, "--elevated"))
        {
            SetStatus(LocalizationManager.T("apply.elevatedLaunched"), StatusLevel.Info);
        }
        else
        {
            SetStatus(LocalizationManager.T("apply.elevationCanceled"), StatusLevel.Warning);
        }
    }

    private void SetStatus(string message, StatusLevel level)
    {
        StatusLine = message;
        StatusLevelValue = level;
    }

    private void CopyToClipboard(string text, string statusKey)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus(LocalizationManager.T(statusKey), StatusLevel.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"{LocalizationManager.T("common.error")}: {ex.Message}", StatusLevel.Error);
        }
    }

    private void OpenExternal(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"{LocalizationManager.T("common.error")}: {ex.Message}", StatusLevel.Error);
            _log.Write("open-external", ex.Message);
        }
    }

    private void SetLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == LocalizationManager.Language)
        {
            return;
        }

        LocalizationManager.Apply(code);
        Settings.Language = code;
        Persist();
        Raise(nameof(FrenchChecked), nameof(EnglishChecked));
    }

    private void SetMode(DnsSecurityMode mode, bool value)
    {
        if (!value || Settings.Mode == mode)
        {
            return;
        }

        Settings.SecurityMode = mode.ToString();
        Persist();
        Raise(
            nameof(EncryptedOnlyChecked),
            nameof(EncryptedPreferredChecked),
            nameof(UnencryptedChecked));
    }

    private void SetTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
        {
            return;
        }

        ThemeManager.Apply(theme);
        Settings.Theme = theme;
        Persist();
        Raise(nameof(RainEnabled), nameof(DarkChecked), nameof(LightChecked));
    }

    private void Persist()
    {
        _persistSettings();
        Raise(nameof(AdvancedMode), nameof(AutoBackup), nameof(UseDotAddresses), nameof(SampleCountText),
            nameof(TestDomain), nameof(FeedUrl), nameof(UpdatesEnabled));
    }

    private void RefreshLocalizedTexts()
    {
        foreach (var card in AllProfiles)
        {
            card.RefreshTexts();
        }

        foreach (var row in Backups)
        {
            row.RefreshTexts();
        }

        Raise(
            nameof(ElevationText),
            nameof(InterfaceSummary),
            nameof(PolicyLine),
            nameof(SecurityStateLine),
            nameof(StatusLine),
            nameof(BestLine),
            nameof(FrenchChecked),
            nameof(EnglishChecked),
            nameof(PathsText));
    }

    public string PathsText =>
        $"{LocalizationManager.T("settings.pathsLogs")}: {_paths.Logs}{Environment.NewLine}" +
        $"{LocalizationManager.T("settings.pathsBackups")}: {_paths.Backups}{Environment.NewLine}" +
        $"{LocalizationManager.T("settings.pathsSettings")}: {_paths.SettingsFile}";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // ignore
        }
    }

    public void Dispose()
    {
        LocalizationManager.Changed -= RefreshLocalizedTexts;
        _runCancellation?.Dispose();
    }
}

public sealed class ApplyHandshake
{
    public ApplyStatus Status { get; set; }
    public string SummaryKey { get; set; } = "apply.failed";
    public string? Detail { get; set; }
    public List<StepHandshake> Steps { get; set; } = new();

    public ApplyResult ToResult() =>
        new(Status, SummaryKey, Steps.Select(step => step.ToStep()).ToList(), Detail);

    public static ApplyHandshake From(ApplyResult result) => new()
    {
        Status = result.Status,
        SummaryKey = result.SummaryKey,
        Detail = result.Detail,
        Steps = result.Steps.Select(StepHandshake.From).ToList()
    };
}

public sealed class StepHandshake
{
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public bool Executed { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }

    public ApplyStep ToStep() => new(Name, Command, Executed, Success, Error);

    public static StepHandshake From(ApplyStep step) => new()
    {
        Name = step.Name,
        Command = step.Command,
        Executed = step.Executed,
        Success = step.Success,
        Error = step.Error
    };
}
