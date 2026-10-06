# Squad DNS

Configureur DNS chiffré pour Windows 10 et 11 : appliquez en un clic un résolveur DoH (DNS over HTTPS)
ou DoT (DNS over TLS), mesurez sa latence, sauvegardez la configuration précédente, revenez en arrière.
Interface bilingue français / anglais commutable à chaud, thème « matrix neon » sombre ou clair.

Natif, sans Electron : **C# / WPF / .NET 8**, et **zéro dépendance tierce dans le noyau**.

Dépôt : [github.com/Aw3n/squad-dns](https://github.com/Aw3n/squad-dns) — l'installeur
`SquadDNS-Setup-<version>.exe` est publié dans les [releases](https://github.com/Aw3n/squad-dns/releases).

```powershell
git clone https://github.com/Aw3n/squad-dns.git
```

```powershell
dotnet build SquadDns.sln -c Release          # compiler
dotnet test  SquadDns.sln -c Release          # 57 tests
dotnet run   --project src/SquadDns           # lancer en mode standard
```

---

## Ce que fait l'application

| Exigence du cahier des charges | Où elle se trouve |
| --- | --- |
| Interface bilingue FR/EN, changement instantané | Boutons FR / EN dans l'en-tête et dans Paramètres ; textes, fiches services et rapports se relocalisent sans redémarrage |
| Liste des services DNS avec descriptions détaillées | **Bibliothèque DNS** : 7 services, descriptions reprises mot pour mot du cahier des charges, étiquettes (bloque pubs, bloque malware, sans but lucratif, identifiant de configuration, fermeture imminente) |
| Configuration DoH **et** DoT en un clic | Carte d'un service → **Appliquer**. DoH : cmdlets `*-DnsClientDohServerAddress` + politique `DoHPolicy`. DoT : adresses du service appliquées, port 853 vérifié en TLS réel |
| Test de vitesse / latence par service | **Tests de latence** : requêtes DNS au format wire RFC 8484 (DoH), RFC 7858 (DoT) et UDP simple, médiane sur N échantillons, barres comparatives, arrêt en cours de campagne |
| Sauvegarde / restauration dans le registre | **Sauvegardes** : JSON dans `%LOCALAPPDATA%\SquadDns\backups` **et** miroir `HKCU\Software\SquadDns\Backups\<id>` ; sauvegarde automatique avant chaque application ; restauration des serveurs et de la politique, retour DHCP possible |
| Mode avancé pour une configuration personnalisée | **Paramètres → Mode avancé** : nom, modèle DoH, adresses des résolveurs, serveur et port DoT, puis enregistrement comme service additionnel |
| Détection du système, modification réseau, gestion des droits admin | `OsInfo`, `PowerShellShell` (via `-EncodedCommand`), `Elevation`, bannière **Relancer en administrateur**, relance `--apply` élevée avec compte-rendu |
| Signature numérique | `installer/build.ps1` avec `-PfxFile`, `-CertThumbprint`, `-MetadataFile` (Azure) ou `-GenerateTestCertificate` → `installer/sign.ps1` (SHA-256 + timestamp RFC 3161, binaire avant ISCC puis installeur après) |
| Programme d'installation Windows standard | `installer/setup.iss` (Inno Setup 6, FR + EN, `PrivilegesRequired=admin`) |
| Documentation utilisateur en français et anglais | `docs/guide-fr.md`, `docs/guide-en.md`, installés dans `<Program Files>\SquadDns\docs` |
| Mises à jour automatiques intégrées | `UpdateChecker` + manifeste JSON configurable dans Paramètres |

### Les 7 services

| Service | DoH | DoT | Adresses des résolveurs |
| --- | --- | --- | --- |
| Cloudflare | `https://cloudflare-dns.com/dns-query` | `1.1.1.1:853` | 1.1.1.1, 1.0.0.1 |
| Quad9 | `https://dns.quad9.net:5053/dns-query` (repli : `https://dns.quad9.net/dns-query`) | `9.9.9.9:853` | 9.9.9.9, 149.112.112.112 |
| DNS.SB | `https://dns.sb/dns-query` | `185.222.222.222:853` | 185.222.222.222 |
| NextDNS | `https://dns.nextdns.io/` | `45.90.28.0:853` | 45.90.28.0, 45.90.30.0 |
| AdGuard DNS | `https://dns.adguard.com/dns-query` | `94.140.14.14:853` | 94.140.14.14, 94.140.15.15 |
| dnsForge | `https://dnsforge.de/dns-query` | `49.12.67.122:853` | 49.12.67.122, 91.99.154.175 |
| Verisign Public DNS | `https://dns64.dns.verisign.com/` | `64.6.64.6:853` | 64.6.64.6, 64.6.65.6 |

---

## Ce que l'écriture modifie réellement sur la machine

Pour l'interface réseau sélectionnée, dans cet ordre — l'aperçu affiche exactement ces commandes sans les
exécuter :

1. `Add-DnsClientDohServerAddress` / `Set-DnsClientDohServerAddress` sur chaque adresse (modèle DoH,
   `-AllowFallbackToUdp` selon le mode, `-AutoUpgrade $True`) ;
2. `Set-DnsClientServerAddress -InterfaceIndex <n> -ServerAddresses <2 adresses>` ;
3. `New-ItemProperty HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient -Name DoHPolicy -Value 3|2|1` ;
4. `Clear-DnsClientCache` ;
5. relecture de l'état effectif (`Get-DnsClientServerAddress`, `Get-DnsClientDohServerAddress`, valeur de
   politique) pour confirmer avant d'afficher « appliquée et vérifiée ».

| Mode de l'application | `DoHPolicy` | Repli UDP non chiffré |
| --- | --- | --- |
| DoH exigé | `3` | non |
| DoH automatique | `2` | oui |
| Non chiffré | `1` | oui |

La restauration remet les serveurs d'origine — ou `ResetServerAddresses` si la carte était en DHCP — et
supprime la valeur `DoHPolicy` si elle n'existait pas.

---

## Compiler et exécuter

### Prérequis

- Windows 10 1809 ou ultérieur, Windows 11, **x64** — le chiffrement DoH nécessite **Windows 11 22H2**
  ou plus récent (les applets `*-DnsClientDohServerAddress` n'existent pas avant) ;
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) pour compiler ;
- **.NET 8 Desktop Runtime (x64)** pour exécuter une build dépendante du framework ;
- droits administrateur **uniquement** au moment d'écrire la configuration DNS.

### Commandes

```powershell
dotnet build SquadDns.sln -c Release
dotnet test  SquadDns.sln -c Release
dotnet run   --project src/SquadDns

# binaire direct
.\src\SquadDns\bin\Release\net8.0-windows\SquadDns.exe

# auto-diagnostic sans interface (JSON dans %LOCALAPPDATA%\SquadDns\logs)
.\src\SquadDns\bin\Release\net8.0-windows\SquadDns.exe --selftest
.\src\SquadDns\bin\Release\net8.0-windows\SquadDns.exe --selftest --probe   # + latence réelle des 7 services
```

Lignes de commande :

```text
SquadDns.exe --apply <profileId> <indexInterface> "<aliasInterface>" <mode> [--preview]
SquadDns.exe --selftest [--probe]
```

`<mode>` : `EncryptedOnly`, `EncryptedPreferred` ou `Unencrypted`. `--preview` n'écrit rien : il produit le
plan de commandes, comme l'option « Aperçu avant application » de l'interface. C'est le chemin utilisé par
la relance élevée.

### Arborescence

```text
SquadDns.sln
src/SquadDns.Core/           noyau sans interface : catalogue, modèles, DoH/DoT wire, PowerShell, registre,
                             sauvegardes, tests de latence, auto-diagnostic, mises à jour — 0 dépendance NuGet
src/SquadDns/                WPF : vues, view-models, thèmes Neon.*, localisation Strings.fr/en.xaml,
                             contrôle MatrixRain, manifeste asInvoker
tests/SquadDns.Core.Tests/   57 tests xUnit (catalogue, wire DNS, cmdlets, sauvegardes, interfaces, quoting)
installer/                   audit.ps1, build.ps1, setup.iss, sign.ps1 + artifacts/
docs/                        guide-fr.md, guide-en.md
tools/                       make-icon.ps1, check-xaml-keys.ps1, check-bindings.ps1
```

`tools/check-xaml-keys.ps1` vérifie que toute ressource référencée en XAML est définie et que le français et
l'anglais sont symétriques ; `tools/check-bindings.ps1` vérifie que tout `{Binding X}` correspond à un membre
réellement déclaré dans les view-models. **Ces deux vérifications sont désormais absorbées par
`installer\audit.ps1`**, qui les ajoute aux contrôles que ni le compilateur ni les tests ne font et qui les fait
échouer la CI ; les scripts de `tools/` restent utiles pour interroger un point précis isolément.

```powershell
powershell -ExecutionPolicy Bypass -File tools\check-xaml-keys.ps1
powershell -ExecutionPolicy Bypass -File tools\check-bindings.ps1
```

---

## Installeur

```powershell
winget install JRSoftware.InnoSetup        # Inno Setup 6.3 ou plus récent
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.0.0
# options : -SelfContained (embarque le runtime), -SkipTests, -SkipInstaller,
#           -Probe (mesure la latence réelle des 7 services pendant le garde-fou)
```

`build.ps1` enchaîne : **audit de câblage** (`installer\audit.ps1`) → tests unitaires → `dotnet publish -r win-x64`
→ **garde-fou auto-diagnostic** sur le binaire publié (il échoue si le catalogue publié ne contient pas exactement
7 services) → `ISCC`. L'audit est aussi exécutable seul :

```powershell
powershell -ExecutionPolicy Bypass -File installer\audit.ps1
```

Il vérifie ce que le compilateur et les tests ne voient pas, parce que cela ne casse qu'à l'affichage : clés de
ressource `StaticResource`/`DynamicResource` non définies, symétrie stricte des 163 clés français/anglais,
dépendance.properties liées **TwoWay par défaut** (`ProgressBar.Value`, `IsChecked`, `TextBox.Text`, `IsSelected`,
`IsOpen`, `IsExpanded`) pointant sur une membre sans setter public, commandes liées à une membre inexistante ou
déclarées mais jamais atteignables, endpoints et descriptions du catalogue comparés verbatim au cahier des
charges — un écart n'y passe que s'il figure dans la liste des substitutions autorisées *et* que son remplacement
est présent, sinon c'est `FAIL` et le build s'arrête — et présence des trois littéraux de la page À propos.
C'est cette dernière classe de défaut qui fit planter la
première fenêtre (`0xc00000fd`, `ProgressBar.Value` lié à `TestProgress` en lecture seule).

Les commandes ci-dessus supposent le dépôt comme dossier courant ; son chemin contient une espace, donc toute
invocation depuis un autre dossier exige des guillemets autour du chemin absolu.

Sortie vérifiée sur cette machine (Inno Setup 6.7.0, .NET SDK 8.0.401) :
`installer\artifacts\output\SquadDNS-Setup-1.0.0.exe`, 2,2 Mo, publication framework-dependent de 0,6 Mo.

Sans Inno Setup, la publication reste utilisable telle quelle : `installer\artifacts\publish\SquadDns.exe`
(dossier portable).

L'installeur est bilingue (français par défaut, sinon la langue de l'interface Windows), exige les droits
administrateur, avertit si le .NET 8 Desktop Runtime est absent en listant les versions registées, installe
les deux guides dans `<dossier>\docs`, et propose à la désinstallation de conserver ou de supprimer les
sauvegardes (dossier + miroir `HKCU\Software\SquadDns`).

## Soutenir le projet

Si vous appréciez ce logiciel, faites-nous une donation en crypto — l'adresse est copiable en un clic depuis
la page *À propos* de l'application :

```text
xel:6mmj85x7504h3z9qwendxhahc4804xgrek59rec3zhhexcdywe8qqvnypnh
```

Échange via [Trocador Swap](https://trocador.app/?ref=BLbjXxTsoK) — site du projet [Xelis](https://www.xelis.io).

## Licence

Aucun fichier de licence n'est fourni dans ce dépôt : choisissez-en une avant de publier. Le noyau
(`SquadDns.Core`) n'utilise aucune dépendance tierce.
