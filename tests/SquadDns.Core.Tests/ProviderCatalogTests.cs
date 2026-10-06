using SquadDns.Core.Catalog;
using SquadDns.Core.Models;

namespace SquadDns.Core.Tests;

public class ProviderCatalogTests
{
    [Fact]
    public void Catalog_exposes_the_seven_expected_services()
    {
        Assert.Equal(7, ProviderCatalog.BuiltIn.Count);
        Assert.Equal(new[] { "cloudflare", "quad9", "dnssb", "nextdns", "adguard", "dnsforge", "verisign" },
            ProviderCatalog.BuiltIn.Select(p => p.Id));
        Assert.Equal(Enumerable.Range(1, 7), ProviderCatalog.BuiltIn.Select(p => p.SortOrder));
    }

    [Theory]
    [InlineData("cloudflare", "https://cloudflare-dns.com/dns-query", "1.1.1.1")]
    [InlineData("quad9", "https://dns.quad9.net:5053/dns-query", "9.9.9.9")]
    [InlineData("dnssb", "https://dns.sb/dns-query", "185.222.222.222")]
    [InlineData("nextdns", "https://dns.nextdns.io/", "45.90.28.0")]
    [InlineData("adguard", "https://dns.adguard.com/dns-query", "94.140.14.14")]
    [InlineData("dnsforge", "https://dnsforge.de/dns-query", "49.12.67.122")]
    [InlineData("verisign", "https://dns64.dns.verisign.com/", "64.6.64.6")]
    public void Endpoints_match_the_documented_values_verbatim(string id, string dohTemplate, string dotServer)
    {
        var profile = ProviderCatalog.Find(id);

        Assert.NotNull(profile);
        Assert.Equal(dohTemplate, profile!.DoHTemplate);
        Assert.Equal(dotServer, profile.DoTServer);
        Assert.Equal(853, profile.DoTPort);
    }

    [Fact]
    public void Every_profile_is_self_consistent()
    {
        foreach (var profile in ProviderCatalog.BuiltIn)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Name), profile.Id);
            Assert.False(string.IsNullOrWhiteSpace(profile.DescriptionFr), profile.Id);
            Assert.False(string.IsNullOrWhiteSpace(profile.DescriptionEn), profile.Id);
            Assert.False(string.IsNullOrWhiteSpace(profile.DoTSni), profile.Id);
            Assert.NotEmpty(profile.ResolverAddresses);
            Assert.True(profile.ResolverAddresses.All(a => System.Net.IPAddress.TryParse(a, out _)), profile.Id);
            Assert.True(Uri.TryCreate(profile.DoHTemplate, UriKind.Absolute, out var uri) && uri.Scheme == "https", profile.Id);
            Assert.StartsWith("https://", profile.Website);
        }
    }

    [Fact]
    public void Sunset_is_rendered_as_the_announced_calendar_day()
    {
        // Aucun profil du catalogue ne porte de date de fermeture depuis le remplacement de
        // Mullvad par dnsForge : la protection reste sur un profil synthetique.
        var profile = new DnsProfile
        {
            Id = "labo",
            Name = "Labo",
            DoHTemplate = "https://dns.labo.example/dns-query",
            DoTServer = "10.0.0.2",
            DoTSni = "dns.labo.example",
            ResolverAddresses = new[] { "10.0.0.2" },
            DescriptionFr = "Decrit en francais.",
            DescriptionEn = "Described in English.",
            Website = "https://dns.labo.example",
            SunsetIso = "2026-11-02",
        };

        Assert.NotNull(profile.Sunset);
        Assert.Equal(new DateTime(2026, 11, 2), profile.Sunset!.Value.UtcDateTime.Date);

        // La puce de la fiche et la note de test impriment ce format : c'est cette chaine que
        // l'utilisateur lit, et elle derivait d'une journee sous l'ancien parsing local.
        Assert.Equal("2026-11-02", profile.Sunset!.Value.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void Find_is_case_insensitive_and_rejects_unknown_ids()
    {
        Assert.NotNull(ProviderCatalog.Find("CloudFlare"));
        Assert.Null(ProviderCatalog.Find("nope"));
    }

    [Fact]
    public void FromCustom_derives_a_usable_profile_from_user_input()
    {
        var custom = ProviderCatalog.FromCustom("Labo", "https://dns.labo.example/dns-query", new[] { "10.0.0.2", "10.0.0.2", " 10.0.0.3 " });

        Assert.True(custom.IsCustom);
        Assert.Equal("custom", custom.Id);
        Assert.Equal("Labo", custom.Name);
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.3" }, custom.ResolverAddresses);
        Assert.Equal("10.0.0.2", custom.DoTServer);
        Assert.Equal("dns.labo.example", custom.DoTSni);
    }

    [Fact]
    public void FromCustom_falls_back_when_the_template_is_not_a_url()
    {
        var custom = ProviderCatalog.FromCustom("  ", "not a url", Array.Empty<string>());

        Assert.Equal("Custom", custom.Name);
        Assert.Equal("127.0.0.1", custom.DoTServer);
        Assert.Equal("localhost", custom.DoTSni);
        Assert.Empty(custom.ResolverAddresses);
    }

    [Fact]
    public void Security_modes_map_to_the_documented_DoHPolicy_values()
    {
        Assert.Equal(3, Network.DnsConfigurator.PolicyValueFor(DnsSecurityMode.EncryptedOnly));
        Assert.Equal(2, Network.DnsConfigurator.PolicyValueFor(DnsSecurityMode.EncryptedPreferred));
        Assert.Equal(1, Network.DnsConfigurator.PolicyValueFor(DnsSecurityMode.Unencrypted));

        Assert.Equal("policy.require", Network.DnsConfigurator.PolicyTextFor(3));
        Assert.Equal("policy.auto", Network.DnsConfigurator.PolicyTextFor(2));
        Assert.Equal("policy.disabled", Network.DnsConfigurator.PolicyTextFor(1));
        Assert.Equal("policy.notConfigured", Network.DnsConfigurator.PolicyTextFor(null));
    }
}
