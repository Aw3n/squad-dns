# Squad DNS — User guide

Windows 10 / 11 application to configure an encrypted DNS resolver (DoH or DoT) in one click, measure its
latency, save your previous configuration and roll it back. Interface available in French and English.

---

## 1. Install and launch

**Where to get the installer:** the repository releases at
<https://github.com/Aw3n/squad-dns/releases>, file `SquadDNS-Setup-<version>.exe`.

**On your PC:**

1. double-click `SquadDNS-Setup-1.0.0.exe`;
2. accept the User Account Control prompt (installation writes into *Program Files*);
3. at the last page, keep or clear "Launch Squad DNS" as you prefer.

**If the application refuses to start** with a framework error: install the
*.NET 8 Desktop Runtime (x64)* from <https://dotnet.microsoft.com/download/dotnet/8.0>.

**Portable version (no installation):** unzip the published folder and run `SquadDns.exe`. Behaviour is
identical; only the log location differs.

**On first launch the application runs as a standard user.** It reads and displays your current
configuration, can measure latency and generate a preview of the commands. It **cannot** write the DNS
configuration until you accept elevation: the banner on the left
*"Administrator rights are required to write the network configuration"* offers
**Relaunch as administrator**. The report of the elevated operation then comes back into the window.

## 2. Read the status bar

Always visible at the bottom of the window:

| Field | What it shows |
| --- | --- |
| *Network adapter* | the interface being targeted (name and index) |
| *Active DNS* | the resolver addresses actually read back from that interface |
| *DoH policy* | `DoH required` (3), `DoH automatic` (2), `DoH disabled` (1) or `Not configured` |
| centre | the last message: success (green), warning (amber), error (red) |
| right | the application version |

The header also shows the current privilege level: *Administrator* or *Standard*.

## 3. Language and theme

- **Switch language**: the **FR** / **EN** buttons in the header, or *Language* in Settings. The change is
  immediate — menus, service cards, descriptions, backups, status messages — without restarting.
- **Switch theme**: **Dark matrix** or **Light** (header or Settings). The *Matrix rain background* option
  turns the background animation on or off.

## 4. Choose a service and apply it

**DNS library** tab.

1. optionally filter with *Search a service*;
2. click a card: **Cloudflare**, **Quad9**, **DNS.SB**, **NextDNS**, **AdGuard DNS**, **dnsForge**,
   **Verisign Public DNS**. The detail shows the DoH template, the DoT target, the resolver addresses, the
   official website and the service description, with tags (*Blocks ads*, *Blocks malware*,
   *Non-commercial*, *Configuration ID required*);
3. pick the **Encryption mode**:
   - **Encrypted only (DoH required)** — policy `3`: Windows must encrypt, no UDP fallback;
   - **Encrypted preferred, fallback allowed** — policy `2`: DoH is tried, UDP is still possible when the
     network blocks it (default, and the safest option for not losing Internet);
   - **Unencrypted only** — policy `1`: applies the addresses without enabling DoH;
4. check the **Target interface** (by default the adapter holding the default route in *Connected* state;
   if you run a VPN or have several cards, select the right one);
5. leave *Save before applying* checked;
6. to see exactly what would be written without writing anything, tick **Preview without writing (dry run)**
   and click **Apply**: the planned command list appears (*Planned commands*,
   *No modification was applied*);
7. untick the preview and click **Apply**. Accept elevation if asked.

Expected result: *"Configuration applied and verified."* If you instead get
*"Commands applied but verification is incomplete"*, the writes happened but the read-back does not confirm
everything: open the step details, then restore if needed.

## 5. Test latency

**Latency tests** tab.

- **Run tests** measures all 7 services in sequence; **Stop** interrupts the campaign.
- A single card can also be measured with **Test** from the library.
- Columns: **DoH (median)** — a real encrypted DNS query, **DoT** — port 853 over TLS,
  **UDP 53** — the unencrypted path, **TLS handshake** — TLS setup time alone,
  **System resolver** — what your adapter answers today.
- **Best** ranks the services; the comparison bars update as measurements arrive.
- Set *Samples per test* (5 by default) and *Domain used by the tests* (`example.com`) in Settings.

Every measurement sends a genuine wire-format DNS query (RFC 8484 over POST, GET fallback; RFC 7858 for DoT)
and times the answer. Nothing is simulated: when an endpoint is unreachable from your network, the
**Note** column says so, for example *"DoT port 853 unreachable from this network"* or
*"The DoH template in the card does not answer; the fallback endpoint worked"*.

## 6. Backups and restore

**Configuration backups** tab.

- **Save current state** creates an entry with the date, the interface, the servers and the policy in force.
  Before every apply, an automatic backup is created (*"Automatic backup created."*).
- **Restore** writes the original servers back — or returns the interface to DHCP if it was on DHCP — and
  restores the previous DoH policy, including deleting it if it did not exist before.
- **Export** saves the selected backup as JSON wherever you choose.
- **Delete** removes the backup (JSON file and registry mirror).

Every backup is kept in two places:

- file: `%LOCALAPPDATA%\SquadDns\backups\<id>.json`
- registry: `HKCU\Software\SquadDns\Backups\<id>`

The registry mirror is what lets you recover a configuration even if the folder was deleted. It is only
cleared by **Delete**, or by your explicit choice during uninstallation.

## 7. Advanced mode: your own service

Settings → enable **Advanced mode** (*Customise a service: DoH template, addresses, DoT port*), then fill in:

| Field | Example |
| --- | --- |
| *Name* | `Lab resolver` |
| *DoH template* | `https://dns.example.com/dns-query` |
| *DNS addresses (comma separated)* | `192.0.2.10, 192.0.2.11` |
| *DoT server* / *DoT port* | `dns.example.com` / `853` |

**Save service** appends the card to the list, applicable and testable like the built-in ones. An explicit
error — *"Invalid DoH template (absolute https URL expected)"* — appears when the URL is not absolute or not
`https://`.

The *Use DoT addresses as DNS servers* option makes the app write the DoT addresses (instead of the resolver
addresses) when applying a service: useful when your network already routes port 853 to an intermediary
resolver.

## 8. Updates

By default the application contacts no server at all. To enable the check:

1. Settings → tick **Automatic updates**;
2. set the **Manifest URL** (a JSON file you publish yourself);
3. **Check now** forces a check; otherwise the check runs at startup.

When a newer version exists, the application shows the number, the release notes in your language, and opens
the download link. It **installs nothing** on its own.

## 9. Self-test and logs

- Settings → **Run self-test**: detected OS, network adapters, catalog, read-back state, planned command
  list and latencies, written to
  `%LOCALAPPDATA%\SquadDns\logs\selftest-<timestamp>.json`.
- **Open log folder**: `squadns-YYYYMMDD.log` (actions, UI errors, elevated launches).
- From the command line, without a GUI:

```powershell
& "$env:ProgramFiles\SquadDns\SquadDns.exe" --selftest
& "$env:ProgramFiles\SquadDns\SquadDns.exe" --selftest --probe   # adds the real latency of all 7 services
```

Always attach these files when reporting a problem.

## 10. Common problems

| What you see | What to do |
| --- | --- |
| Banner *"Administrator rights are required"* | Click **Relaunch as administrator**, then apply again |
| *"Elevation cancelled."* | You dismissed the UAC prompt; retry and accept |
| *"No network adapter detected"* | The card is disabled, or Wi-Fi is disconnected: reconnect then **Refresh** |
| Internet cut after applying **Encrypted only** | Apply again with **Encrypted preferred**, or restore the automatic backup. Some corporate networks and ISPs block DoH |
| *DoH policy* shows *Not configured* after a successful apply | A Group Policy enforces the `DoHPolicy` key: tell your domain administrator |
| A service reports *"unreachable from this network"* | That is not a bug in the software: the port or name is not reachable where you are. Pick another service, or test from another network |
| *NextDNS* note *"requires a configuration ID"* | Put your ID into the *DoH template* using advanced mode |
| *dnsForge* blocks a legitimate site | The catalogued entry point applies full filtering (ads, trackers, malware). Declare the *"clean"* variant (`https://clean.dnsforge.de/dns-query`) as an extra service using advanced mode |
| The displayed DNS differs from `ipconfig /all` | `ipconfig` lists every adapter; Squad DNS reads the **targeted** one, shown in the status bar |
| A VPN takes control back after applying | Apply again after connecting the VPN, selecting the VPN interface in the list |
| Tests feel slow | Lower *Samples per test*, or measure a single service with **Test** |

## 11. Limitations to know about

- **DoT is not encrypted by Windows.** The Windows 10 and 11 resolver cannot send DoT queries: Squad DNS
  configures the service addresses and genuinely verifies port 853 over TLS, but to encrypt all system DNS
  traffic through DoT you need a third-party resolver on the network (AdGuard Home, Pi-hole +
  dnscrypt-proxy, an enterprise resolver). This is written on the **About** page, in the **Tests** tab and in
  the result notes — the software never claims to have encrypted what it did not encrypt.
- **DoH depends on your network.** Some ISPs and corporate networks block DNS over 443 or force an internal
  resolver.
- **x64 only**, Windows 10 version 1809 or later.
- **One network adapter per apply.** Other cards are untouched.
- **No browsing data is collected.** The tests only send DNS queries to the servers you chose.

## 12. Uninstall

`Settings > Apps > Squad DNS > Uninstall`, or the uninstall shortcut in the program group.
The uninstaller asks whether the backups should be removed too (the `%LOCALAPPDATA%\SquadDns` folder and the
`HKCU\Software\SquadDns` mirror): **No** keeps them, so you can reinstall later or read the JSON yourself.
**The DNS configuration already written on the network adapter is not reverted**: if you want a clean slate,
restore your backup first, then uninstall.

## 13. Support the project

If you appreciate this software, send us a crypto donation. On the **About** page the address is displayed and
the **Copy address** button puts it on the clipboard:

```text
xel:6mmj85x7504h3z9qwendxhahc4804xgrek59rec3zhhexcdywe8qqvnypnh
```

Swap through [Trocador Swap](https://trocador.app/?ref=BLbjXxTsoK) — site of the [Xelis](https://www.xelis.io) project.
