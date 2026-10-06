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
| Liste des services DNS avec descriptions détaillées | **Bibliothèque DNS** : 7 services, descriptions reprises mot pour mot du cahier des charges (6 sur 7, le 7ᵉ point étant dnsForge — voir *Écart assumé au cahier des charges*), étiquettes (bloque pubs, bloque malware, sans but lucratif, identifiant de configuration, fermeture imminente) |
| Configuration DoH **et** DoT en un clic | Carte d'un service → **Appliquer**. DoH : cmdlets `*-DnsClientDohServerAddress` + politique `DoHPolicy`. DoT : adresses du service appliquées, port 853 vérifié en TLS réel (voir *Limites*) |
| Test de vitesse / latence par service | **Tests de latence** : requêtes DNS au format wire RFC 8484 (DoH), RFC 7858 (DoT) et UDP simple, médiane sur N échantillons, barres comparatives, arrêt en cours de campagne |
| Sauvegarde / restauration dans le registre | **Sauvegardes** : JSON dans `%LOCALAPPDATA%\SquadDns\backups` **et** miroir `HKCU\Software\SquadDns\Backups\<id>` ; sauvegarde automatique avant chaque application ; restauration des serveurs et de la politique, retour DHCP possible |
| Mode avancé pour une configuration personnalisée | **Paramètres → Mode avancé** : nom, modèle DoH, adresses des résolveurs, serveur et port DoT, puis enregistrement comme service additionnel |
| Détection du système, modification réseau, gestion des droits admin | `OsInfo`, `PowerShellShell` (via `-EncodedCommand`), `Elevation`, bannière **Relancer en administrateur**, relance `--apply` élevée avec compte-rendu |
| Signature numérique | `installer/build.ps1` avec `-PfxFile`, `-CertThumbprint`, `-MetadataFile` (Azure) ou `-GenerateTestCertificate` → `installer/sign.ps1` (SHA-256 + timestamp RFC 3161, binaire avant ISCC puis installeur après) |
| Programme d'installation Windows standard | `installer/setup.iss` (Inno Setup 6, FR + EN, `PrivilegesRequired=admin`) |
| Documentation utilisateur en français et anglais | `docs/guide-fr.md`, `docs/guide-en.md`, installés dans `<Program Files>\SquadDns\docs` |
| Mises à jour automatiques intégrées | `UpdateChecker` + manifeste JSON configurable dans Paramètres (voir *Manifeste de mise à jour*) |
| Page À propos : don crypto cliquable, bouton Swap, lien Xelis | **À propos** : adresse `xel:6mmj85x7504h3z9qwendxhahc4804xgrek59rec3zhhexcdywe8qqvnypnh` copiable, bouton **Trocador Swap** vers `https://trocador.app/?ref=BLbjXxTsoK`, bouton **Site Xelis** vers `https://www.xelis.io` |

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

> **Écart assumé au cahier des charges** : le 6ᵉ service est dnsForge (résolveur allemand d'adminForge, sans
> journalisation, filtre pubs, traqueurs et malwares) et non Mullvad DNS, qui ferme ses serveurs DNS publics
> chiffrés le **2 novembre 2026**. Substitution autorisée le 6 octobre 2026 et signalée à chaque build par
> `installer/audit.ps1`. Le mécanisme d'étiquette « fermeture imminente » reste en place dans le modèle et
> l'interface, mais aucun service du catalogue ne porte plus de date de fermeture.

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

- Windows 10 1809 ou ultérieur, Windows 11, **x64** ;
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

## Signature numérique

```powershell
# bout en bout : les binaires avant ISCC, l'installeur après, puis la vérification
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.0.0 -PfxFile build\release.pfx
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.0.0 -CertThumbprint 0123ABC...
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.0.0 -GenerateTestCertificate

# un fichier déjà publié (l'installeur en cours ne change pas : relancer build.ps1 pour l'embarquer)
powershell -ExecutionPolicy Bypass -File installer\sign.ps1 -Path installer\artifacts\publish -Recurse -PfxFile build\release.pfx

# certificat dans le magasin système ou sur token matériel
powershell -ExecutionPolicy Bypass -File installer\sign.ps1 -Path <fichier> -CertThumbprint 0123ABC...

# Azure Artifact Signing : la cle privee reste chez Microsoft, rien n'est importé dans le magasin
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.0.0 -MetadataFile installer\azure-codesign.json

# contrôle de la chaîne
signtool verify /pa /v <fichier>
```

Sans aucune de ces options, `build.ps1` n'appelle aucun CA et annonce `shipping UNSIGNED` ; `-SkipSigning`
force l'abstention même avec un certificat sous la main.

`sign.ps1` localise `signtool.exe` (PATH ou Windows Kits — testé avec le SDK 10.0.26100 présent ici), signe en
**SHA-256** avec **timestamp RFC 3161** (`http://timestamp.digicert.com` par défaut), puis vérifie avec
`signtool verify /pa /v`. Le **HTTP et non l'HTTPS** est délibéré : `https://timestamp.digicert.com` expire sur
ce réseau (mesuré le 6 octobre 2026) et signtool rend alors le message trompeur `Invalid Timestamp URL`, qui
ressemble à une URL malformée alors que c'est une coupure réseau ; `-TimestampUrl` permet de basculer
(par exemple `http://time.certum.pl`), de passer par l'horodatage Azure (`http://timestamp.acs.microsoft.com`,
pris automatiquement en mode `-MetadataFile`), ou de désactiver l'horodatage avec `-TimestampUrl ''`. Le mot de
passe du PFX ne transite jamais en ligne de commande : le certificat est importé dans `Cert:\CurrentUser\My`,
utilisé par empreinte, puis retiré (`-KeepImportedCert` pour le laisser en place). Sans certificat, le script
liste les fichiers ciblés, affiche les trois options et sort en code 1 : c'est le garde-fou qui prouve que la
chaîne d'outils est en place avant d'appeler un CA.

L'ordre compte : `build.ps1` signe **avant** `ISCC` pour que l'installeur embarque des fichiers déjà signés, puis
signe l'installeur lui-même. Les fichiers signés sont détruits à chaque `publish`, donc une signature posée à la
main sur `artifacts\publish` disparaît au build suivant.

`-GenerateTestCertificate` crée un certificat auto-signé **non reconnu**, et le recharge s'il reste valide plus
de trente jours : le magasin n'accumule pas un certificat par build. Ça valide le script et la chaîne d'outils,
rien de plus. La répétition du 6 octobre 2026 a signé les trois binaires et l'installeur avec un horodatage
DigiCert vérifié ; en échange `Get-AuthenticodeSignature` rend `UnknownError` et `signtool verify /pa`
« A certificate chain processed, but terminated in a root certificate which is not trusted » — c'est précisément
ce qu'un auto-signé doit produire. Pour distribuer, il faut un certificat délivré par une autorité de confiance
(DigiCert, GlobalSign, SSL.com, Certum, ou Azure Artifact Signing) ; sinon Windows affiche « éditeur inconnu ».

### Les deux formes d'un vrai certificat

Un certificat CA arrive d'une de ces deux manières, et le projet gère les deux :

| Forme | Ce que vous détenez | Invocation |
|---|---|---|
| **PFX / token** (DigiCert, GlobalSign, SSL.com, Certum) | un fichier `.pfx` ou une cle sur HSM/YubiKey, présente dans `Cert:\` | `-PfxFile build\release.pfx` ou `-CertThumbprint <empreinte>` |
| **Azure Artifact Signing** (anciennement *Trusted Signing*) | rien : la cle reste dans le coffre Microsoft, `signtool` charge leur DLL | `-MetadataFile installer\azure-codesign.json` |

`installer\azure-codesign.example.json` est le gabarit du second cas : à copier en `azure-codesign.json` avec
**votre** nom de compte et de profil. Ce fichier-là ne contient pas de secret (la cle reste chez Microsoft) mais
il porte l'identité de votre compte Azure, donc le vôtre n'a rien à faire dans le dépôt — seul le gabarit y est.
`Endpoint` doit correspondre à la **région du compte** : `https://codesigning.azure.net` (global),
`https://neu.codesigning.azure.net` ou `https://weu.codesigning.azure.net` (Europe),
`https://eus.codesigning.azure.net` (États-Unis) — une région erronée répond `403`. Le client s'installe avec
`winget install -e --id Microsoft.Azure.ArtifactSigningClientTools`, qui pose
`Azure.CodeSigning.Dlib.dll` sous `Program Files (x86)\Azure` ; `sign.ps1` la trouve tout seul, exige un
PowerShell 64 bits et un `signtool` ≥ 10.0.2261.755 (celui d'ici, 10.0.26100.7175, passe).

Ce qui est **prouvé sur ce poste** (répétition du 6 octobre 2026, 29 contrôles) : le mode Azure détecté, les quatre
garde-fous (DLL absente → identifiant `winget` fourni ; `-MetadataFile` combiné avec `-PfxFile` → refus avant
toute demande de mot de passe ; `-Dlib` sans `-MetadataFile` → nommé ; champ `Endpoint` manquant dans le JSON →
nommé), l'argv réellement construit
(`sign /fd sha256 /dlib … /dmdf … /d /du /tr http://timestamp.acs.microsoft.com /td sha256`, sans `/sha1` ni
`/as`), un échec qui s'arrête proprement quand le chargeur de signature n'est pas le DLL Microsoft — la cible
reste intacte, octet pour octet — et le chemin certificat du magasin toujours en état de signer et d'horodater.
S'y ajoutent les trois règles d'horodatage, vérifiées chacune sur un fichier neuf : absent → l'autorité du mode
(retenu par défaut), valeur explicite → celle-ci (`build.ps1 -TimestampUrl http://time.certum.pl` a rendu
l'installeur et les trois binaires horodatés Certum, en plus des 57 tests unitaires), chaîne vide → signature
sans horodatage, annoncée. Ce qui **ne peut pas** être prouvé ici : la signature Azure elle-même, qui exige un
compte payant, un profil validé, et le DLL officiel.

Ce n'est pas qu'une formalité d'achat : **Azure Artifact Signing en « Public Trust » n'est ouvert aux personnes
physiques qu'aux États-Unis et au Canada** (les organisations y ont droit dans
l'UE, le Royaume-Uni, l'Australie, la Nouvelle-Zélande, le Japon, la Corée, Singapour, la Norvège et Israël).
Un développeur individuel hors Amérique du Nord doit donc soit créer une structure, soit prendre un certificat
CA classique : `SSL.com Individual Validated` couvre les personnes physiques sans registre de commerce (pièce
d'identité gouvernementale, à partir d'environ 129 USD/an), mais impose une **cle privée matériellement scellée**
— pas de PFX exportable, d'où `-CertThumbprint`/token plutôt que `-PfxFile`. La validation d'identité Azure prend
1 à 20 jours ouvrés, le lien reçu par mail expire au bout de 7 jours, et trois tentatives de documents sont
accordées.

## Manifeste de mise à jour

La vérification de mise à jour est désactivée par défaut : sans URL renseignée, l'application ne contacte
rien. Dans **Paramètres** : cocher *Mises à jour automatiques* et renseigner *URL du manifeste*.

Format attendu (GET HTTP/HTTPS, JSON) :

```json
{
  "version": "1.0.1",
  "url": "https://github.com/Aw3n/squad-dns/releases/download/v1.0.1/SquadDNS-Setup-1.0.1.exe",
  "notesFr": "Nouvelle version : …",
  "notesEn": "New release: …",
  "releaseDate": "2026-10-06"
}
```

La comparaison porte sur les numéros de version (composants manquants complétés par `.0`, `v` initial et
suffixes `-beta` tolérés). Si la version distante est plus récente, l'application affiche le numéro, les
notes dans la langue courante et **ouvre le lien** : le téléchargement et l'installation restent manuels.
L'URL d'exemple présente dans le code (`https://example.invalid/squadns/update.json`) n'est qu'un espace
réservé : remplacez-la par votre dépôt.

---

## Limites, déclarées à l'utilisateur plutôt que masquées

1. **DoT n'est pas natif dans le resolver Windows.** Windows 10/11 sait configurer DoH ; il ne chiffre pas
   les requêtes DoT sortantes du système. Squad DNS applique les adresses du service, vérifie réellement le
   port 853 en TLS (SNI inclus) et affiche le résultat — mais **le chiffrement DoT complet suppose un
   resolver tiers** (AdGuard Home, Pi-hole + dnscrypt-proxy, resolver d'entreprise). Cette limite est écrite
   sur la page *À propos*, dans l'onglet *Tests* et dans les notes de résultats.
2. **La politique DoH est une clé de strate.** `HKLM\...\DNSClient\DoHPolicy` est aussi utilisée par la
   gestion d'entreprise : une GPO peut la réinitialiser ou la rendre en lecture seule.
3. **L'atteinte des points d'entrée dépend du réseau.** Sur la machine de développement,
   `dns.quad9.net:5053` n'était pas joignable et `dns64.dns.verisign.com` ne se résolvait pas : Quad9 bascule
   automatiquement sur son modèle secondaire (`https://dns.quad9.net/dns-query`) et Verisign est signalé
   « DoH injoignable » / « DoT injoignable », au lieu d'afficher une latence inventée.
4. **NextDNS** répond sans identifiant de configuration, mais sans filtrage personnalisé : la note
   « identifiant de configuration requis » le dit.
5. **dnsForge remplace Mullvad DNS** depuis le 6 octobre 2026 (Mullvad ferme ses serveurs publics chiffrés le
   2 novembre 2026). Les variantes « clean » et « hard » de dnsForge sont des politiques de filtrage distinctes,
   donc absentes du catalogue : utilisez le mode avancé pour les déclarer comme services additionnels.
6. **Signature** : `build.ps1` ne signe que ce qu'on lui donne (`-PfxFile`, `-CertThumbprint`,
   `-MetadataFile`, `-GenerateTestCertificate`) ; sans option, la sortie est explicitement `shipping UNSIGNED`. Un certificat
   auto-signé signe et horodate vraiment, mais ne convainc ni `signtool verify /pa` ni SmartScreen. L'URL
   d'horodatation est une dépendance réseau : sur le réseau de développement, `https://timestamp.digicert.com`
   est injoignable et seul le point d'entrée HTTP fonctionne. Enfin, **aucune de ces options ne délivre un
   certificat** : le marché l'impose, et l'option la moins chère (Azure Artifact Signing) est fermée aux
   personnes physiques hors États-Unis et Canada en « Public Trust ». La ligne de commande est prête, la
   souscription reste un acte humain.
7. **x64 uniquement**, une interface réseau par application (la carte *Interface* liste les cartes ;
   choisissez la bonne, le VPN et la secondaire y passent aussi).

## Diagnostic

Journaux : `%LOCALAPPDATA%\SquadDns\logs\squadns-YYYYMMDD.log` (actions, erreurs d'interface, lancements
élevés) et `selftest-*.json` (système, cartes réseau, catalogue, état relu, plan de commandes, latences).
Boutons **Ouvrir le dossier des journaux** et **Lancer l'auto-diagnostic** dans Paramètres.

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
