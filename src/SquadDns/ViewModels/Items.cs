using System.Collections.ObjectModel;
using SquadDns.Core.Models;
using SquadDns.Core.Network;
using SquadDns.Infrastructure;

namespace SquadDns.ViewModels;

public sealed class AdapterRow : ObservableObject
{
    public AdapterRow(NetworkAdapter adapter) => Adapter = adapter;

    public NetworkAdapter Adapter { get; }

    public int Index => Adapter.Index;
    public string Alias => Adapter.Alias;
    public string Label => $"{Adapter.Alias}  #{Adapter.Index}  [{Adapter.Family}]  {(Adapter.HasDefaultRoute ? "route par defaut" : string.Empty)}";
    public string ServersText => Adapter.Servers.Count == 0 ? "-" : string.Join(", ", Adapter.Servers);
    public string StateText => Adapter.State;
    public string DhcpText => Adapter.Dhcp;
    public bool IsDefaultCandidate => Adapter.IsConnected && Adapter.HasDefaultRoute;
}

public sealed class StepRow
{
    public StepRow(ApplyStep step) => Step = step;

    public ApplyStep Step { get; }
    public string Name => LocalizationManager.F("library.step") + " " + Step.Name;
    public string Command => Step.Command;
    public string State => Step.Executed ? (Step.Success ? "ok" : "ko") : "planned";
    public string? Error => Step.Error;
    public bool HasError => !string.IsNullOrWhiteSpace(Step.Error);
}

public sealed class ProfileCard : ObservableObject
{
    private readonly DnsProfile _profile;
    private string? _medianDoH;
    private string? _medianDoT;
    private string? _medianPlain;
    private string? _note;
    private bool _isTesting;
    private bool _isCurrent;
    private int _successCount;
    private int _totalCount;

    public ProfileCard(DnsProfile profile)
    {
        _profile = profile;
        Detail = new ObservableCollection<string>();
    }

    public DnsProfile Profile => _profile;
    public ObservableCollection<string> Detail { get; }

    public string Name => _profile.Name;
    public string Id => _profile.Id;
    public string Description => LocalizationManager.Language == "en" ? _profile.DescriptionEn : _profile.DescriptionFr;
    public string DoHTemplate => _profile.DoHTemplate;
    public string DotEndpoint => $"{_profile.DoTServer}:{_profile.DoTPort}";
    public string ServersText => string.Join(", ", _profile.ResolverAddresses);

    public bool ShowAds => _profile.BlocksAds;
    public bool ShowMalware => _profile.BlocksMalware;
    public bool ShowNonProfit => _profile.NonCommercial;
    public bool ShowConfigId => _profile.RequiresConfigurationId;
    public bool ShowSunset => _profile.Sunset is not null;

    public string SunsetText
    {
        get
        {
            if (_profile.Sunset is not { } sunset)
            {
                return string.Empty;
            }

            var passed = DateTimeOffset.UtcNow > sunset;
            var label = LocalizationManager.T(passed ? "library.tags.sunsetPassed" : "library.tags.sunset");
            return $"{label} {sunset.ToString("yyyy-MM-dd")}";
        }
    }

    public string MedianDoH
    {
        get => _medianDoH ?? LocalizationManager.T("tests.na");
        set { if (Set(ref _medianDoH, value)) { Raise(nameof(MedianDoH), nameof(HasResult), nameof(ScoreBar)); } }
    }

    public string MedianDoT
    {
        get => _medianDoT ?? LocalizationManager.T("tests.na");
        set { if (Set(ref _medianDoT, value)) { Raise(nameof(MedianDoT), nameof(HasResult)); } }
    }

    public string MedianPlain
    {
        get => _medianPlain ?? LocalizationManager.T("tests.na");
        set { if (Set(ref _medianPlain, value)) { Raise(nameof(MedianPlain), nameof(HasResult)); } }
    }

    public string? Note
    {
        get
        {
            if (_note is null)
            {
                return string.Empty;
            }

            var text = LocalizationManager.T(_note);
            return _note == "note.sunsetSoon" && _profile.Sunset is { } sunset
                ? $"{text} {sunset.ToString("yyyy-MM-dd")}"
                : text;
        }
        set { if (Set(ref _note, value)) { Raise(nameof(Note), nameof(HasNote)); } }
    }

    public bool HasNote => !string.IsNullOrEmpty(_note);
    public bool IsTesting { get => _isTesting; set => Set(ref _isTesting, value); }
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public bool HasResult => _medianDoH is not null || _medianPlain is not null;

    public string TestSummary
    {
        get => _totalCount == 0 ? string.Empty : $"{_successCount}/{_totalCount}";
        set => Raise(nameof(TestSummary));
    }

    public double ScoreBar
    {
        get
        {
            if (!double.TryParse(_medianDoH, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ms) || ms <= 0)
            {
                return 0;
            }

            return Math.Clamp(100.0 - Math.Min(ms, 500.0) / 5.0, 2, 100);
        }
    }

    public void RefreshTexts()
    {
        Raise(nameof(Description), nameof(SunsetText), nameof(MedianDoH), nameof(MedianDoT), nameof(MedianPlain), nameof(Note), nameof(ScoreBar));
    }

    public void SetCounts(int success, int total)
    {
        _successCount = success;
        _totalCount = total;
        Raise(nameof(TestSummary));
    }
}

public sealed class BackupRow : ObservableObject
{
    public BackupRow(Core.Backup.DnsBackup backup) => Backup = backup;

    public Core.Backup.DnsBackup Backup { get; }

    public string Id => Backup.Id;
    public string CreatedAt => Backup.CreatedAtText;
    public string InterfaceAlias => Backup.InterfaceAlias;
    public string Label => Backup.Label;
    public string Servers => Backup.UseDhcp
        ? LocalizationManager.T("backups.dhcp")
        : (Backup.Servers.Count == 0 ? "-" : string.Join(", ", Backup.Servers));
    public string PolicyText => LocalizationManager.T(
        Backup.PreviousPolicy switch
        {
            3 => "policy.require",
            2 => "policy.auto",
            1 => "policy.disabled",
            _ => "policy.notConfigured"
        });

    public void RefreshTexts() => Raise(nameof(Servers), nameof(PolicyText));
}
