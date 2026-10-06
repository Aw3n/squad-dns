using SquadDns.Core.Models;

namespace SquadDns.Core.Catalog;

public static class ProviderCatalog
{
    public static IReadOnlyList<DnsProfile> BuiltIn { get; } = new List<DnsProfile>
    {
        new()
        {
            Id = "cloudflare",
            Name = "Cloudflare",
            SortOrder = 1,
            Website = "https://developers.cloudflare.com/1.1.1.1/",
            DoHTemplate = "https://cloudflare-dns.com/dns-query",
            DoTServer = "1.1.1.1",
            DoTSni = "cloudflare-dns.com",
            ResolverAddresses = new[] { "1.1.1.1", "1.0.0.1" },
            DescriptionFr = "L'un des DNS les plus rapides selon les classements DNSPerf. Prend en charge DNS over HTTPS (DoH) et DNS over TLS (DoT). S'engage à ne pas enregistrer les données de navigation et à ne pas vendre ces informations à des tiers.",
            DescriptionEn = "One of the fastest resolvers according to DNSPerf rankings. Supports DNS over HTTPS (DoH) and DNS over TLS (DoT). Commits to not logging browsing data and not selling it to third parties."
        },
        new()
        {
            Id = "quad9",
            Name = "Quad9",
            SortOrder = 2,
            Website = "https://quad9.net/",
            DoHTemplate = "https://dns.quad9.net:5053/dns-query",
            SecondaryDoHTemplate = "https://dns.quad9.net/dns-query",
            DoTServer = "9.9.9.9",
            DoTSni = "dns.quad9.net",
            ResolverAddresses = new[] { "9.9.9.9", "149.112.112.112" },
            BlocksMalware = true,
            NonCommercial = true,
            DescriptionFr = "Organisation à but non lucratif financée par des dons, sans motivation commerciale. Bloque automatiquement les domaines dangereux et malveillants.",
            DescriptionEn = "Non-profit organisation funded by donations, with no commercial motive. Automatically blocks dangerous and malicious domains."
        },
        new()
        {
            Id = "dnssb",
            Name = "DNS.SB",
            SortOrder = 3,
            Website = "https://dns.sb/",
            DoHTemplate = "https://dns.sb/dns-query",
            DoTServer = "185.222.222.222",
            DoTSni = "dns.sb",
            ResolverAddresses = new[] { "185.222.222.222" },
            DescriptionFr = "Service gratuit axé sur le respect de la vie privée. Opéré par xTom, société allemande avec un réseau anycast important.",
            DescriptionEn = "Free service focused on privacy. Operated by xTom, a German company with a large anycast network."
        },
        new()
        {
            Id = "nextdns",
            Name = "NextDNS",
            SortOrder = 4,
            Website = "https://nextdns.io/",
            DoHTemplate = "https://dns.nextdns.io/",
            DoTServer = "45.90.28.0",
            DoTPort = 853,
            DoTSni = "dns.nextdns.io",
            ResolverAddresses = new[] { "45.90.28.0", "45.90.30.0" },
            RequiresConfigurationId = true,
            BlocksAds = true,
            BlocksMalware = true,
            DescriptionFr = "Offre des options de configuration avancées via des contrôles parentaux et des restrictions de sites. Permet de bloquer des catégories entières de contenu.",
            DescriptionEn = "Offers advanced configuration options through parental controls and site restrictions. Can block entire categories of content.",
            SunsetIso = null
        },
        new()
        {
            Id = "adguard",
            Name = "AdGuard DNS",
            SortOrder = 5,
            Website = "https://adguard-dns.io/",
            DoHTemplate = "https://dns.adguard.com/dns-query",
            DoTServer = "94.140.14.14",
            DoTSni = "dns.adguard.com",
            ResolverAddresses = new[] { "94.140.14.14", "94.140.15.15" },
            BlocksAds = true,
            BlocksMalware = true,
            DescriptionFr = "Propose un blocage intégré des publicités et des trackers. Service gratuit avec options de filtrage personnalisables.",
            DescriptionEn = "Provides built-in blocking of advertisements and trackers. Free service with customizable filtering options."
        },
        new()
        {
            Id = "dnsforge",
            Name = "dnsForge",
            SortOrder = 6,
            Website = "https://dnsforge.de/",
            DoHTemplate = "https://dnsforge.de/dns-query",
            DoTServer = "49.12.67.122",
            DoTSni = "dnsforge.de",
            ResolverAddresses = new[] { "49.12.67.122", "91.99.154.175" },
            BlocksAds = true,
            BlocksMalware = true,
            NonCommercial = true,
            DescriptionFr = "Résolveur allemand gratuit exploité par l'équipe adminForge, sans journalisation des requêtes. Filtre les publicités, les traqueurs et les malwares. Des variantes « clean » (malwares seuls) et « hard » (blocage large) existent sur des points d'entrée distincts.",
            DescriptionEn = "Free German resolver run by the adminForge team with no query logging. Filters advertisements, trackers and malware. Separate 'clean' (malware only) and 'hard' (broad blocking) entry points exist."
        },
        new()
        {
            Id = "verisign",
            Name = "Verisign Public DNS",
            SortOrder = 7,
            Website = "https://www.verisign.com/en_US/online-solutions/resolver-technologies/index.xhtml",
            DoHTemplate = "https://dns64.dns.verisign.com/",
            DoTServer = "64.6.64.6",
            DoTSni = "dns.verisign.com",
            ResolverAddresses = new[] { "64.6.64.6", "64.6.65.6" },
            BlocksMalware = true,
            DescriptionFr = "Mise sur la stabilité, la sécurité et le respect de la vie privée. Offre une protection robuste contre les principaux types d'attaques et programmes malveillants.",
            DescriptionEn = "Focuses on stability, security and privacy. Offers robust protection against the main types of attacks and malware."
        }
    };

    public static DnsProfile? Find(string id) =>
        BuiltIn.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static DnsProfile FromCustom(string name, string dohTemplate, IEnumerable<string> addresses)
    {
        var list = addresses
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DnsProfile
        {
            Id = "custom",
            Name = string.IsNullOrWhiteSpace(name) ? "Custom" : name.Trim(),
            SortOrder = int.MaxValue,
            Website = "",
            DoHTemplate = dohTemplate.Trim(),
            DoTServer = list.FirstOrDefault() ?? "127.0.0.1",
            DoTSni = TryHost(dohTemplate) ?? list.FirstOrDefault() ?? "localhost",
            ResolverAddresses = list,
            IsCustom = true,
            DescriptionFr = "Configuration personnalisée définie par l'utilisateur.",
            DescriptionEn = "Custom configuration defined by the user."
        };
    }

    private static string? TryHost(string template)
    {
        if (Uri.TryCreate(template, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            return uri.Host;
        }

        return null;
    }
}
