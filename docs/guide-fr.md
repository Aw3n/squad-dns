# Squad DNS — Guide d'utilisation

Application Windows 10 / 11 pour configurer un DNS chiffré (DoH ou DoT) en un clic, mesurer sa latence,
sauvegarder l'ancienne configuration et revenir en arrière. Interface disponible en français et en anglais.

---

## 1. Installer et lancer

**Où récupérer l'installeur :** les releases du dépôt
<https://github.com/Aw3n/squad-dns/releases>, fichier `SquadDNS-Setup-<version>.exe`.

**Sur votre PC :**

1. double-cliquez sur `SquadDNS-Setup-1.0.0.exe` ;
2. acceptez la demande de contrôle de compte (l'installation écrit dans *Program Files*) ;
3. à la fin, décochez ou laissez « Lancer Squad DNS ».

**Si l'application refuse de démarrer** avec un message sur le framework : installez le
*.NET 8 Desktop Runtime (x64)* depuis <https://dotnet.microsoft.com/download/dotnet/8.0>.

**Version portable (sans installation) :** dézippez le dossier publié et lancez `SquadDns.exe`.
Le comportement est identique ; seuls les chemins des journaux changent.

**Au premier lancement, l'application tourne en mode standard.** Elle lit et affiche votre configuration,
peut mesurer des latences et générer un aperçu des commandes. Elle **ne peut pas** écrire la configuration
DNS tant que vous n'avez pas accepté l'élévation : la bannière de gauche
*« Les droits administrateur sont requis pour écrire la configuration réseau »* propose
**Relancer en administrateur**. Le compte-rendu de l'opération élevée revient ensuite dans la fenêtre.

## 2. Lire la barre d'état

En bas de fenêtre, en permanence :

| Champ | Ce qu'il indique |
| --- | --- |
| *Interface réseau* | la carte réseau ciblée (nom et index) |
| *DNS actif* | les adresses de résolveurs réellement lues sur cette carte |
| *Politique DoH* | `DoH exigé` (3), `DoH automatique` (2), `DoH désactivé` (1) ou `Non configurée` |
| au centre | le dernier message : succès (vert), avertissement (ambre), erreur (rouge) |
| à droite | la version de l'application |

En haut à gauche, l'en-tête affiche aussi le niveau de droits : *Administrateur* ou *Standard*.

## 3. Langue et thème

- **Changer de langue** : les boutons **FR** / **EN** de l'en-tête, ou *Langue* dans Paramètres. Le
  changement est immédiat — menus, fiches services, descriptions, sauvegardes, messages d'état — sans
  redémarrer.
- **Changer de thème** : **Matrix sombre** ou **Clair** (en-tête ou Paramètres). L'option
  *Pluie matrix en arrière-plan* active ou coupe l'animation de fond.

## 4. Choisir un service et l'appliquer

Onglet **Bibliothèque DNS**.

1. cherchez éventuellement avec *Rechercher un service* ;
2. cliquez une carte : **Cloudflare**, **Quad9**, **DNS.SB**, **NextDNS**, **AdGuard DNS**,
   **dnsForge**, **Verisign Public DNS**. La fiche détaille le modèle DoH, la cible DoT,
   les adresses des résolveurs, le site officiel et la description du service, avec des étiquettes
   (*Bloque les pubs*, *Bloque le malware*, *Sans but lucratif*, *ID de configuration requis*) ;
3. choisissez le **Mode de chiffrement** :
   - **Chiffré uniquement (DoH exigé)** — politique `3` : Windows doit chiffrer, aucun repli UDP ;
   - **Chiffré de préférence, repli autorisé** — politique `2` : DoH essayé, UDP possible si le réseau
     le bloque (choix par défaut, le plus sûr pour ne pas couper Internet) ;
   - **Non chiffré uniquement** — politique `1` : applique les adresses sans activer DoH ;
4. vérifiez l'**Interface cible** (par défaut la carte avec la route par défaut et l'état *Connecté* ;
   si vous avez un VPN ou plusieurs cartes, choisissez la bonne) ;
5. laissez cochée *Sauvegarder avant d'appliquer* ;
6. pour voir ce qui sera écrit sans rien écrire, cochez **Aperçu sans écrire (à sec)** puis **Appliquer** :
   la liste des commandes prévues s'affiche (*Commandes prévues*, *Aucune modification n'a été appliquée*) ;
7. décochez l'aperçu et cliquez **Appliquer**. Acceptez l'élévation si elle est demandée.

Message attendu : *« Configuration appliquée et vérifiée. »* Si le message est
*« Commandes appliquées mais la vérification est incomplète »*, les écritures ont été faites mais la
relecture ne confirme pas tout : consultez le détail des étapes, puis restaurez si nécessaire.

## 5. Tester la latence

Onglet **Tests de latence**.

- **Lancer les tests** mesure les 7 services à la suite ; **Arrêter** interrompt la campagne.
- Une carte peut aussi être testée seule avec **Tester** depuis la Bibliothèque.
- Colonnes : **DoH (médiane)** — temps d'une vraie requête DNS chiffrée, **DoT** — port 853 en TLS,
  **UDP 53** — chemin non chiffré, **Poignée TLS** — délai d'établissement TLS seul,
  **Résolveur système** — ce que répond votre carte aujourd'hui.
- **Meilleur** classe les services ; la barre comparative se met à jour à mesure que les mesures tombent.
- Réglez *Nombre d'échantillons par test* (5 par défaut) et *Domaine utilisé par les tests*
  (`example.com`) dans Paramètres.

Chaque mesure envoie une véritable requête DNS au format wire (RFC 8484 en POST, repli en GET ; RFC 7858
pour DoT) et chronomètre la réponse. Rien n'est simulé : quand un point d'entrée n'est pas joignable depuis
votre réseau, la colonne **Remarque** l'écrit noir sur blanc, par exemple
*« Port DoT 853 injoignable depuis ce réseau »* ou *« Le modèle DoH de la fiche ne répond pas ; le point
d'entrée de secours a fonctionné »*.

## 6. Sauvegardes et restauration

Onglet **Sauvegardes de configuration**.

- **Sauvegarder l'état actuel** crée une entrée avec la date, l'interface, les serveurs et la politique en
  cours. Avant chaque application, une sauvegarde automatique est créée (message
  *« Sauvegarde automatique créée. »*).
- **Restaurer** réécrit les serveurs d'origine — ou remet la carte en DHCP si c'était le cas — et remet la
  politique DoH précédente, y compris en la supprimant si elle n'existait pas.
- **Exporter** enregistre le JSON de la sauvegarde sélectionnée à l'endroit que vous choisissez.
- **Supprimer** efface la sauvegarde (fichier JSON et miroir dans le registre).

Deux copies sont conservées à chaque sauvegarde :

- fichier : `%LOCALAPPDATA%\SquadDns\backups\<id>.json`
- registre : `HKCU\Software\SquadDns\Backups\<id>`

C'est le miroir registre qui permet de retrouver une configuration même après avoir supprimé le dossier, et
il n'est effacé que par **Supprimer** ou par votre choix explicite à la désinstallation.

## 7. Mode avancé : un service à vous

Paramètres → activez **Mode avancé** (*Personnaliser un service : modèle DoH, adresses, port DoT*), puis
remplissez :

| Champ | Exemple |
| --- | --- |
| *Nom* | `Resolver labo` |
| *Modèle DoH* | `https://dns.exemple.fr/dns-query` |
| *Adresses DNS (séparées par des virgules)* | `192.0.2.10, 192.0.2.11` |
| *Serveur DoT* / *Port DoT* | `dns.exemple.fr` / `853` |

**Enregistrer le service** ajoute la carte en fin de liste, applicable et testable comme les autres. Une
erreur explicite — *« Modèle DoH invalide (URL https absolue attendue) »* — s'affiche si l'URL n'est pas
absolue et en `https://`.

L'option *Utiliser les adresses DoT comme serveurs DNS* force l'application à écrire les adresses DoT
(plutôt que les adresses de résolveurs) lorsque vous appliquez un service : utile si votre réseau route déjà
le port 853 vers un resolver intermédiaire.

## 8. Mises à jour

Par défaut, l'application ne contacte aucun serveur. Pour activer la vérification :

1. Paramètres → cochez **Mises à jour automatiques** ;
2. renseignez **URL du manifeste** (fichier JSON que vous publiez vous-même) ;
3. **Vérifier maintenant** force un contrôle ; sinon le contrôle a lieu à l'ouverture.

Si une version plus récente existe, l'application affiche le numéro, les notes de version dans votre langue
et ouvre le lien de téléchargement. Elle **n'installe rien** à votre place.

## 9. Auto-diagnostic et journaux

- Paramètres → **Lancer l'auto-diagnostic** : système détecté, cartes réseau, catalogue, état relu, plan de
  commandes et latences, écrits dans
  `%LOCALAPPDATA%\SquadDns\logs\selftest-<horodatage>.json`.
- **Ouvrir le dossier des journaux** : `squadns-YYYYMMDD.log` (actions, erreurs d'interface, lancements élevés).
- En ligne de commande, sans interface graphique :

```powershell
& "$env:ProgramFiles\SquadDns\SquadDns.exe" --selftest
& "$env:ProgramFiles\SquadDns\SquadDns.exe" --selftest --probe   # ajoute la latence réelle des 7 services
```

À joindre obligatoirement quand vous signalez un problème.

## 10. Problèmes courants

| Ce que vous voyez | Que faire |
| --- | --- |
| Bannière *« droits administrateur sont requis »* | Cliquez **Relancer en administrateur**, puis réappliquez |
| *« Élévation annulée. »* | Vous avez refusé la prompt UAC ; réessayez et acceptez |
| *« Aucune interface détectée »* | Carte réseau désactivée, ou Wi-Fi déconnecté : rebranchez puis **Actualiser** |
| Internet coupé après une application en *Chiffré uniquement* | Réappliquez en *Chiffré de préférence*, ou restaurez la sauvegarde automatique. Certains réseaux d'entreprise ou opérateurs bloquent le DoH |
| *Politique DoH* affiche *« Non configurée »* après une application réussie | Une stratégie de groupe impose la clé `DoHPolicy` : prévenez l'administrateur du domaine |
| Un service affiche *« injoignable depuis ce réseau »* | Ce n'est pas un bug du logiciel : le port ou le nom n'est pas atteignable chez vous. Choisissez un autre service, ou testez depuis un autre réseau |
| *NextDNS* note *« exige un ID de configuration »* | Renseignez votre ID dans le *Modèle DoH* via le Mode avancé |
| *dnsForge* bloque un site légitime | Le point d'entrée du catalogue applique le filtrage complet (pubs, traqueurs, malwares). Déclarez la variante « clean » (`https://clean.dnsforge.de/dns-query`) comme service additionnel via le Mode avancé |
| Le DNS affiché diffère de celui de `ipconfig /all` | `ipconfig` liste toutes les cartes ; Squad DNS lit la carte **ciblée**, indiquée dans la barre d'état |
| Un VPN reprend la main après application | Réappliquez après la connexion VPN, en choisissant l'interface du VPN dans la liste |
| Les tests sont longs | Réduisez *Nombre d'échantillons par test*, ou lancez un service isolé avec **Tester** |

## 11. Limites à connaître

- **DoT n'est pas chiffré par Windows.** Le resolver de Windows 10 et 11 ne sait pas envoyer de requêtes
  DoT : Squad DNS configure les adresses du service et vérifie réellement le port 853 en TLS, mais pour que
  tout le trafic DNS du système soit chiffré en DoT, il faut un resolver tiers sur le réseau
  (AdGuard Home, Pi-hole + dnscrypt-proxy, resolver d'entreprise). Le message est écrit sur la page
  **À propos**, dans l'onglet **Tests** et dans les notes de résultats — le logiciel ne prétend jamais
  avoir chiffré ce qu'il n'a pas chiffré.
- **DoH dépend du réseau.** Certains opérateurs et réseaux d'entreprise bloquent le port 443 DNS ou
  forcent un résolveur interne.
- **x64 uniquement**, Windows 10 1809 ou ultérieur.
- **Une interface réseau par application.** Les autres cartes ne sont pas touchées.
- **Aucune donnée de navigation n'est collectée.** Les tests envoient uniquement des requêtes DNS vers les
  serveurs que vous avez choisis.

## 12. Désinstaller

`Paramètres > Applications > Squad DNS > Désinstaller`, ou le raccourci de désinstallation du groupe.
Le désinstallateur demande si vous voulez aussi supprimer les sauvegardes (dossier
`%LOCALAPPDATA%\SquadDns` et miroir `HKCU\Software\SquadDns`) : **Non** les conserve, ce qui permet de
réinstaller plus tard ou de lire les JSON vous-même. **La configuration DNS déjà écrite sur la carte réseau
n'est pas annulée** : si vous voulez repartir de zéro, restaurez d'abord votre sauvegarde, puis désinstallez.

## 13. Soutenir le projet

Si vous appréciez ce logiciel, faites-nous une donation en crypto. Depuis la page **À propos**, l'adresse
s'affiche et le bouton **Copier l'adresse** la place dans le presse-papiers :

```text
xel:6mmj85x7504h3z9qwendxhahc4804xgrek59rec3zhhexcdywe8qqvnypnh
```

- **Trocador Swap** ouvre <https://trocador.app/?ref=BLbjXxTsoK> pour convertir d'autres cryptos en Xelis ;
- **Site Xelis** ouvre <https://www.xelis.io>.

---

*Documentation de Squad DNS 1.0.0 — [README](../README.md) côté technique et build.*
