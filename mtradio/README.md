# mtradio — MediaTek MT7902 / MT7925 radio

`nekodrop-radio-mt.exe` drives MediaTek MT7902 (`14C3:7902`, Connac2) and
MT7925 (`14C3:7925`, Connac3 via `--mt7925`) from user mode through the
register proxy (`driver/regproxy`), like the Intel and Realtek radios. It is a
port of the parts of Linux [mt76](https://github.com/openwrt/mt76) (`mt7921/`,
`mt7925/`, `mt76_connac*`, mt76 commit `41d5d1d`) that AirDrop needs:

- firmware boot (ROM patch + RAM code) and WFDMA rings in AWE pages below 4 GiB
  (1 MiB BAR0 on MT7902; 2 MiB BAR0 + Connac3 fixed-map remap on MT7925)
- the firmware's sniffer on one channel for receiving, and cut-through TX with
  TX-free reports for sending (`mt7902_radio.cpp` for Connac2, `mt7925_radio.cpp`
  for Connac3 TXD/RXD, `UNI_CMD` TLV framing, and `STA_REC_BA` / `RA`)
- one interface whose address the MAC acknowledges, plus dual-channel 5 GHz
  AWDL slot-following (`149 <-> 44` or peer's home channel) on MT7925
- a STA record per AWDL peer: firmware rate control and Block Ack sessions,
  with VHT80 2SS TX A-MPDU and optional `--he80` RX A-MSDU aggregation on MT7925
- the radio pipe protocol (`radio_link.h`) and the AWDL policy ported from
  ForbiddenItlwm (`tests/test_awdl_policy.cpp`), so the service and awdld treat
  it like the other radios

- a Wi-Fi station beside AWDL (Concurrent mode), see below

## Concurrent Wi-Fi

The station is iwxradio's `WifiStation` (join, WPA2/WPA3, the TAP bridge)
over `MtStation` (`mt_station.*`, the `StaDevice` interface):

- the card stays in sniffer mode; a second own address (own MAC 1) makes the
  MAC acknowledge the AP's frames to the station
- CCMP is sealed and opened on the host (`ccmp.*`, Windows CNG CCM;
  `tests/ccmp_test.cpp` checks IEEE 802.11 J.6.4)
- the firmware has one channel (`num_different_channels = 1`), so the radio
  time-shares it: AWDL's 16-entry channel sequence (clocked from awdld's own
  frames) names the router's channel for the entries we spend there, a
  power-save null frame tells the AP before each move, and frames for the AP
  wait while the card is away. Wi-Fi gets 12 of 16 entries, 6 while AirDrop
  moves data
- a channel move is the RF switch only (re-configuring the sniffer took up to
  0.36 s), about 11 ms on average
- once associated the AP gets a STA record of its own (own MAC 1): data goes
  out at the firmware's rate, and the AP's Block Ack sessions are taken. The
  card hands A-MPDU subframes over as heard, so `MtStation` reorders them
  (as mac80211's reorder buffer) and drops the AP's frames to its other
  clients, which the sniffer hears too
- the join waits for the AP's capabilities (a beacon, or the answer to a
  probe sent to it): without them the association asks for no HT/VHT

Measured beside AWDL: about 3.5 MB/s download and 1.3 MB/s upload (1.6-2 MB/s
with fixed rates and no Block Ack). We do not ask the AP for a Block Ack
session of our own: with A-MPDUs both ways the test router dropped us within
seconds. Limits: 5 GHz networks only (see below).
`mtradio\scripts\test-station.ps1` joins the saved, visible 5 GHz networks
and checks ping and a download over TAP.

## Firmware

It needs linux-firmware's `WIFI_RAM_CODE_MT7902_1.bin` +
`WIFI_MT7902_patch_mcu_1_1_hdr.bin` for MT7902, and
`WIFI_RAM_CODE_MT7925_1_1.bin` + `WIFI_MT7925_PATCH_MCU_1_1_hdr.bin` for MT7925
(`scripts/fetch-deps.ps1` downloads them, pinned and hash-checked). The copy in
MediaTek's Windows driver boots, but ignores the unified (UNI) commands this
port sends.

## Build and test

    scripts\fetch-deps.ps1
    scripts\build-radio-mt.ps1      # -> build\mtradio (runs ccmp_test + test-awdl-policy)

Hardware tests take the card from Windows for a minute (the wired network
stays up) and always give it back to MediaTek's driver. Run them elevated:

- `mtradio\scripts\smoke-test.ps1` — binds the register proxy from a local
  `driver\regproxy` build, runs `mtprobe.exe` on MT7902 (chip ID, DMA, firmware boot)
- `mtradio\scripts\smoke-mt7925.ps1` — supervised MT7925 (`14C3:7925`) bring-up
  (`mtprobe7925.exe` / `nekodrop-radio-mt.exe --mt7925`) with automatic rollback
- `mtradio\scripts\test-radio.ps1` — receive and send rounds on 2.4 and 5 GHz
- `mtradio\scripts\test-installed.ps1` — the same with nekodrop installed, using
  its register proxy; e.g. `-Runs '44 --test-sta 60'`

`mtprobe` / `mtprobe7925` and the daemon run as SYSTEM through a one-shot
scheduled task: AWE pages need SeLockMemoryPrivilege.

## Known limits

- On 2.4 GHz, frames that need an ACK never complete on MT7902 (cause unknown;
  maybe Bluetooth coexistence), and MT7925 runs AWDL on 5 GHz 80 MHz (`44` / `149`),
  so the Wi-Fi station joins 5 GHz networks only on both cards.
- On MT7902, sending to an iPhone is slower than receiving (about 5 vs 15 MB/s
  measured). On MT7925 (`SUBSYS_60001A3B`), VHT80 2SS TX + `--he80` RX A-MSDU
  reaches ~37.5 MB/s average (52.5 MB/s peak) sending on same-channel 5 GHz,
  ~20.8 MB/s across dual-channel `149 <-> 48` hops, and ~27.5 MB/s receiving.

## Licence

GPL-2.0-only like nekodrop. The parts taken from mt76 are BSD-3-Clause-Clear /
ISC (MediaTek, Lorenzo Bianconi and others): see `LICENSE.mt76`.
