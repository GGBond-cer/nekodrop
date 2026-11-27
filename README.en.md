<p align="center">
  <img src="app/assets/nekodrop.png" width="128" alt="nekodrop">
</p>

<h1 align="center">nekodrop</h1>

<p align="center">
  <b>Native AirDrop on Windows using your built-in Wi-Fi card while staying online —<br>plus Android Mutual Transmission Alliance, LocalSend, and rule-based cross-ecosystem relay.</b><br>
  No USB dongle, no disabling Secure Boot, no Linux VM, and no Mac required.
</p>

<p align="center">
  <a href="README.md">简体中文</a> · English · <a href="../../releases/latest">Download</a> · <a href="docs/ARCHITECTURE.md">Architecture</a> · <a href="docs/COMPATIBILITY.md">Card compatibility</a> · <a href="CHANGELOG.md">Changelog</a>
</p>

<p align="center">
  <a href="#community"><img alt="QQ Group 1137780084" src="https://img.shields.io/badge/QQ%20Group-1137780084-12b7f5?logo=qq&logoColor=white"></a>
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-0078d4">
  <img alt="speed" src="docs/badges/speed.svg">
  <img alt="protocols" src="docs/badges/protocols.svg">
  <img alt="card" src="docs/badges/card.svg">
  <img alt="license" src="https://img.shields.io/badge/license-GPL--2.0-blue">
</p>

<p align="center">
  <img src="docs/demo.gif" width="760" alt="Real hardware demo: iPhone and Windows 11 exchanging native AirDrop over the built-in Intel AX201 while Wi-Fi stays online">
  <br>
  <sub>Real hardware: stock iPhone (no app installed) ↔ Windows 11 with a built-in Intel AX201, Wi-Fi online throughout.</sub>
</p>

## One App, Three Ecosystems

| Ecosystem / Protocol | Peer Devices | Zero-App on Peer? | Notes |
|---|---|---|---|
| **Apple AirDrop** (native AWDL) | iPhone · iPad · Mac | ✅ Native | Appears directly in Apple's AirDrop sheet; **53 MB/s** both ways; supports **Contacts Only** (Apple ID sign-in or Mac cert import) and iOS / iPadOS 26.x |
| **Android Mutual Transmission Alliance** (MTA v2, experimental) | OnePlus · OPPO · Xiaomi · vivo · Honor · realme, etc. | ✅ Native | BLE radar discovery & fast bidirectional LAN transfers on the same Wi-Fi, plus a privacy-redacted BLE diagnostic scanner (opt-in) |
| **LocalSend** (v2, experimental) | Android · iOS · macOS · Windows · Linux | LocalSend app on peer | Universal LAN fallback with mutual TLS client certificates, sharing the same radar and send UI as AirDrop (opt-in) |
| **Rule-Based Cross-Ecosystem Relay** (with flow diagram) | Any pair of AirDrop ⇄ Android MTA ⇄ LocalSend | — | Turn your Windows PC into a local zero-config bridge: define one-way or two-way per-device rules; only matching sources are auto-accepted and relayed |

## Highlights

- **53 MB/s both ways:** 846 MB transferred in ~16 s with 0 drops on an Intel AX201, reliable back-to-back sends, and no idle BLE interference knocking unlocked iPhones off Wi-Fi ([benchmarks](#speed)).
- **Stay online while using AirDrop:** Intel and MediaTek (MT7902 / MT7922) run **Concurrent mode** (Wi-Fi + AirDrop on one card); keeps your original Windows Wi-Fi MAC address by default (preserving DHCP leases and MAC filters) and gracefully returns the card to Windows on unsupported router encryption (such as WPA/TKIP mixed mode).
- **Pure user-mode, zero kernel drivers:** Powered by a UMDF register-proxy driver and 4 user-mode radio backends (Intel / MediaTek / Realtek). **No disabling Secure Boot, no Windows Test Signing mode**, and one-click rollback to stock Windows Wi-Fi.
- **Native Windows UX:** AirDrop-style radar view, Windows 11 first-level Explorer right-click (**Send with nekodrop**), Windows Share sheet integration, rich toast preview with one-click clipboard copy, clear unsupported file/folder warning cards ([supported file types](docs/FILE-TYPES.md)), and one-click clear staged files.
- **Experimental high-throughput switches (opt-in):** Intel Concurrent mode offers optional **Wi-Fi 6 (HE)** station connectivity and **Data-plane acceleration** (64-slot host STA RX Block Ack reordering + dual async TAP writers, measured 156–200 Mbps multi-connection).
- **Install and forget:** Single installer, built-in auto-updater (Stable / Preview channels), automatic service recovery, and all experimental features kept strictly opt-in.

> See the **[Architecture Overview](docs/ARCHITECTURE.md)** and [How it works](docs/HOW-IT-WORKS.md) for how the client, three protocol engines, rule relay, virtual interfaces, and four user-mode radio backends fit together.

## Speed

Measured on Intel AX201, Windows 11, `v2.2.9+` with High-throughput mode (HE80 + MTU 1500):

| Direction | Speed | Test |
|---|---|---|
| PC → iPhone | **53.00 MB/s** (50.55 MiB/s) | 846 MB in 15.97 s, 99.85% Block Ack, 0 drops |
| iPhone → PC | **53.28 MB/s** net (50.81 MiB/s) · 49.66 MB/s end-to-end | 846 MB in 15.84 s net / 17.00 s total, 0 drops |

Other verified numbers: Realtek RTL8852CE ~**24.5 MB/s** (on demand); MediaTek MT7902 ~**15 MB/s** RX with bidirectional A-MPDU; Intel Concurrent Wi-Fi multi-connection download **156–200 Mbps** with Data-plane acceleration enabled. How we measure: [docs/TESTING.md](docs/TESTING.md).

## Supported Wi-Fi Cards

Uses your laptop or desktop's built-in PCIe / CNVi Wi-Fi card — no external USB adapter needed:

| Vendor | Backend | Cards | Modes | Status |
|---|---|---|---|---|
| **Intel** | `iwxradio` | AX201 · AX200 · AX210 · AX211 · AX203 (Arrow Lake) · AX411 / Killer AX1690 · 9560 · 9462 | Concurrent Wi-Fi / On demand | ✅ **Tested** (AX201 by the author; AX200 / AX210 / AX211 / AX203, etc. by community) |
| **Intel** | `iwxradio` | BE200 (Wi-Fi 7, MLD) | Concurrent Wi-Fi / On demand | 🧪 Experimental |
| **MediaTek / AMD** | `mtradio` | MT7902 · MT7925 | 5 GHz Concurrent Wi-Fi / On demand | ✅ Tested both ways |
| **MediaTek / AMD** | `mtkradio` | MT7922 / AMD RZ616 | 5 GHz Concurrent Wi-Fi (exp.) / On demand | ✅ Tested both ways |
| **MediaTek / AMD** | `mtkradio` | MT7921 / AMD RZ608 | On demand | 🧪 Experimental |
| **Realtek** | `devourer` | RTL8852CE | On demand | ✅ Community-tested (~24.5 MB/s) |

> Unlisted cards are not supported for AWDL (Qualcomm, Broadcom, Intel BE201/BE211, legacy Intel 9260, etc.). Full PCI IDs, firmware, and per-card notes: **[Card compatibility](docs/COMPATIBILITY.md)** · **[Card status & porting a new card](docs/CARDS.md)**.

## Install & Restore

1. Requirements: **Windows 10 2004+ / Windows 11 x64**, a supported Wi-Fi card, Bluetooth enabled.
2. Download `nekodrop-<version>-setup.exe` from **[Releases](../../releases/latest)** and run it. *(Release installers are currently unsigned; if Windows SmartScreen prompts, click **More info → Run anyway**. See the [privacy notice](docs/PRIVACY.md) for full network transparency.)*
3. Click the purple cat in the tray and turn on **Allow receiving**. On your iPhone, set AirDrop to **Everyone for 10 Minutes** (or sign in with Apple ID for **Contacts Only**).
4. To enable **Android Share (MTA)**, **LocalSend**, or **Relay rules**, open **Settings → Sharing & Relay**.

**Something broke?** Start menu → **nekodrop - restore Windows Wi-Fi** returns your Wi-Fi card to Windows immediately. More: [Troubleshooting](docs/SUPPORT.md).

## Screenshots

<table>
  <tr>
    <td align="center"><img src="docs/screenshots/main-radar.png" width="260" alt="Main window: radar"><br><sub>Nearby devices on a radar</sub></td>
    <td align="center"><img src="docs/screenshots/send.png" width="230" alt="Send window"><br><sub>Right-click → Send with nekodrop</sub></td>
    <td align="center"><img src="docs/screenshots/wifi.png" width="240" alt="Wi-Fi window"><br><sub>nekodrop's own Wi-Fi</sub></td>
  </tr>
</table>

More screenshots and every feature: [Using nekodrop](docs/USAGE.md).

## Documentation

| Document | Contents |
|---|---|
| [Using nekodrop](docs/USAGE.md) | Features, install details, modes, Wi-Fi security, Contacts Only, Android Share / LocalSend / Relay, updates, uninstall |
| [Troubleshooting](docs/SUPPORT.md) | Common issues, one-click restore, generating a redacted diagnostic report |
| [Card compatibility](docs/COMPATIBILITY.md) · [Card status](docs/CARDS.md) | Full PCI IDs, firmware, per-card notes, and how to port a new card |
| [Supported file types](docs/FILE-TYPES.md) | File/folder send rules and limitations |
| [Architecture overview](docs/ARCHITECTURE.md) · [How it works](docs/HOW-IT-WORKS.md) | Four-layer architecture diagram, internals, project status, building from source |
| [Credits](docs/CREDITS.md) | Contributors, upstream code sources, licenses |
| [Privacy](docs/PRIVACY.md) | Where nekodrop connects and what it sends |
| [Changelog](CHANGELOG.md) | Full Stable & Preview release history |

## Community

Join the nekodrop QQ group to chat and share hardware test logs: **1137780084**

<p>
  <img src="docs/community/qq-group.jpg" width="240" alt="nekodrop QQ group QR code, group number 1137780084">
</p>

Community link: [LINUX DO](https://linux.do/).

## Contributors

Thanks to [@BaiBaiAi](https://github.com/BaiBaiAi), [@XXD051030](https://github.com/XXD051030), [@GGBond-cer](https://github.com/GGBond-cer), [@DaiJiequn](https://github.com/DaiJiequn), [@sh17632](https://github.com/sh17632), [@YiHoooong](https://github.com/YiHoooong), [@Yanlyn](https://github.com/Yanlyn), [@lincoo2019](https://github.com/lincoo2019), [@CarzyBilly](https://github.com/CarzyBilly), [@Aobing-code](https://github.com/Aobing-code), and everyone who sent logs and hardware feedback. See [Credits](docs/CREDITS.md#contributors).

Bug reports with a [diagnostic report](docs/SUPPORT.md#reporting-a-problem-the-diagnostic-report) are welcome in [Issues](../../issues).

## License

**GPL-2.0** ([LICENSE](LICENSE)); bundled third-party components keep their own licenses ([details](docs/CREDITS.md#license)).

nekodrop is an independent open-source project, not affiliated with or endorsed by Apple, Intel, MediaTek, Realtek, or any smartphone vendor. AirDrop, iPhone, iPad, and Mac are trademarks of Apple Inc.; Intel is a trademark of Intel Corporation. nekodrop temporarily takes over your Wi-Fi adapter via a user-mode proxy while running. It is experimental software, provided as is, without warranty.
