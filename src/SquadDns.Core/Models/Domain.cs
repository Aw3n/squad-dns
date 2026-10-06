using System.Globalization;

namespace SquadDns.Core.Models;

public enum DnsSecurityMode
{
    EncryptedOnly = 0,
    EncryptedPreferred = 1,
    Unencrypted = 2
}

public enum TransportKind
{
    DoH,
    DoT,
    Plain,
    SystemResolver
}

public enum ApplyStatus
{
    Success,
    PartialSuccess,
    Failed,
    DryRun,
    NeedsElevation
}

public sealed record ApplyStep(string Name, string Command, bool Executed, bool Success, string? Error)
{
    public override string ToString() => $"{Name}: {(Success ? "ok" : "ko")}";
}

public sealed record ApplyResult(ApplyStatus Status, string SummaryKey, IReadOnlyList<ApplyStep> Steps, string? Detail)
{
    public bool Ok => Status is ApplyStatus.Success or ApplyStatus.PartialSuccess or ApplyStatus.DryRun;
}

public sealed record InterfaceDnsState(
    int InterfaceIndex,
    string InterfaceAlias,
    bool Enabled,
    IReadOnlyList<string> ServerAddresses,
    bool DohActive,
    string? DohTemplate);

public sealed record DnsProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DoHTemplate { get; init; }
    public required string DoTServer { get; init; }
    public int DoTPort { get; init; } = 853;
    public required string DoTSni { get; init; }
    public required IReadOnlyList<string> ResolverAddresses { get; init; }
    public required string DescriptionFr { get; init; }
    public required string DescriptionEn { get; init; }
    public required string Website { get; init; }
    public string? SecondaryDoHTemplate { get; init; }
    public bool BlocksAds { get; init; }
    public bool BlocksMalware { get; init; }
    public bool NonCommercial { get; init; }
    public bool RequiresConfigurationId { get; init; }
    public string? SunsetIso { get; init; }
    public bool IsCustom { get; init; }
    public int SortOrder { get; init; }

    // Une date calendaire, pas un instant : parser en heure locale puis ToOffset(Zero)
    // reculais l'affichage d'une journée (le 2 novembre s'affichait « 2026-11-01 »).
    public DateTimeOffset? Sunset =>
        SunsetIso is null
            ? null
            : DateTimeOffset.Parse(SunsetIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
