// SPDX-License-Identifier: GPL-2.0-only
/* MT7925 PCIe monitor and raw 802.11 transport. Register and packet layouts
 * follow Linux mt76/mt7925 and mt76_connac3_mac.h. */
#include "mt7902.h"
#include "mt_regs.h"

#include <algorithm>

using namespace mt;

namespace {
constexpr uint16_t kBandConfig = 0x08, kDomainInfo = 0x15, kEfuseCtrl = 0x2d;
constexpr uint32_t kMdpDcr0 = 0x820cc800, kMdpDcr1 = kMdpDcr0 + 4;
constexpr uint32_t kWtblControl = 0x820d43b0, kWtblData0 = kWtblControl + 8, kWtblData1 = kWtblControl + 12;
constexpr uint32_t kWtblExecute = bit(31);
constexpr uint32_t kBasicOfdm6 = 18; /* Linux basic rates table base 14 plus four CCK entries (18..25 = OFDM 6..54) */
constexpr uint32_t kHtMcsOddBase = 27; /* mt7925_main.c: fixed-rate table entries must use odd indices <= 63 */
constexpr uint32_t kVht1ssOddBase = 43; /* odd indices 43..53 = VHT80 1SS MCS 2,4,6,7,8,9 */
constexpr uint32_t kVht2ssOddBase = 55; /* odd indices 55..63 = VHT80 2SS MCS 4,6,7,8,9 */
constexpr unsigned kVht1ssMcs[6] = {2, 4, 6, 7, 8, 9};
constexpr unsigned kVht2ssMcs[5] = {4, 6, 7, 8, 9};
constexpr uint32_t kNoToken = 0x7fff;

uint32_t mt7925_rate_table_idx(uint16_t rate) {
  const unsigned mode = (rate >> 6) & 0xf;
  const unsigned idx = rate & 0x3f;
  if (mode == kPhyTypeOfdm) {
    switch (idx) {
    case 11: return kBasicOfdm6 + 0; /* 6M */
    case 15: return kBasicOfdm6 + 1; /* 9M */
    case 10: return kBasicOfdm6 + 2; /* 12M */
    case 14: return kBasicOfdm6 + 3; /* 18M */
    case 9:  return kBasicOfdm6 + 4; /* 24M */
    case 13: return kBasicOfdm6 + 5; /* 36M */
    case 8:  return kBasicOfdm6 + 6; /* 48M */
    case 12: return kBasicOfdm6 + 7; /* 54M */
    default: return kBasicOfdm6;
    }
  }
  if (mode == kPhyTypeHt && idx < 8) return kHtMcsOddBase + 2 * idx;
  if (mode == kPhyTypeVht) {
    const unsigned nss = ((rate >> 10) & 0x7) + 1;
    const unsigned mcs = rate & 0xf;
    if (nss >= 2) {
      const unsigned slot = mcs >= 9 ? 4 : mcs == 8 ? 3 : mcs == 7 ? 2 : mcs >= 5 ? 1 : 0;
      return kVht2ssOddBase + 2 * slot;
    }
    const unsigned slot = mcs >= 9 ? 5 : mcs == 8 ? 4 : mcs == 7 ? 3 : mcs >= 5 ? 2 : mcs >= 3 ? 1 : 0;
    return kVht1ssOddBase + 2 * slot;
  }
  return kBasicOfdm6;
}
bool valid_mt7925_5g(uint8_t ch) {
  if (ch >= 36 && ch <= 64) return (ch - 36) % 4 == 0;
  if (ch >= 100 && ch <= 144) return (ch - 100) % 4 == 0;
  if (ch >= 149 && ch <= 165) return (ch - 149) % 4 == 0;
  return false;
}
} // namespace

bool Mt7902::start_mt7925_radio(const std::string &country, std::string &error) {
  if (!valid_mt7925_5g(_channel) || (_width != 20 && _width != 40 && _width != 80)) {
    error = "MT7925 uses 20, 40 or 80 MHz on 5 GHz channels 36..165";
    return false;
  }
  if (_channel == 165) _width = 20;
  uint8_t power[16 + 320] = {};
  constexpr char keep[] = "KeepFullPwr 1";
  power[4] = 2;  /* UNI_CHIP_CONFIG_CHIP_CFG */
  const uint16_t length = sizeof(power) - 4;
  std::memcpy(power + 6, &length, sizeof(length));
  power[12] = sizeof(keep); /* mt76_connac_config::data_size */
  std::memcpy(power + 16, keep, sizeof(keep));
  if (!mcu_send(uni_cmd(mt7925::kChipConfig), power, sizeof(power), false)) {
    error = "could not send KeepFullPwr";
    return false;
  }
  const uint16_t efuse[] = {0, 0, 2, 8, 0x0100, 0};
  if (!mcu_send(uni_cmd(kEfuseCtrl), efuse, sizeof(efuse), true)) {
    error = "MT7925 EFUSE_BUFFER_MODE did not respond";
    return false;
  }
  static constexpr uint8_t kCh5g[] = {36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116,
                                      120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165};
  constexpr uint16_t kN5g = sizeof(kCh5g);
  constexpr uint16_t kTlvLen = 8 + 8 * kN5g;
  std::vector<uint8_t> domain(8 + kTlvLen, 0);
  if (country.size() == 2) std::memcpy(domain.data(), country.data(), 2);
  domain[4] = 1; /* 2.4 GHz: 20 MHz */
  domain[5] = 2; /* 5 GHz: 20/40/80 MHz */
  domain[6] = 1;
  const uint16_t dom_tag = 2;
  std::memcpy(domain.data() + 8, &dom_tag, 2);
  std::memcpy(domain.data() + 10, &kTlvLen, 2);
  domain[13] = static_cast<uint8_t>(kN5g);
  for (size_t i = 0; i < kN5g; ++i) domain[16 + i * 8] = kCh5g[i];
  if (!mcu_send(uni_cmd(kDomainInfo), domain.data(), domain.size(), true)) {
    error = "MT7925 SET_DOMAIN_INFO did not respond";
    return false;
  }
  rmw(kMdpDcr1, 0x1fffu << 3, 0x1fffu << 3);
  clear(kMdpDcr0, bit(19) | bit(15)); /* native 802.11 frames, no A-MSDU deaggregation */
  const auto write_rate_entry = [&](uint32_t tbl_idx, uint16_t rate_val) -> bool {
    wr(kWtblData0, rate_val);
    wr(kWtblData1, bit(6)); /* MT_WTBL_SPE_IDX_SEL */
    wr(kWtblControl, kWtblExecute | bit(16) | tbl_idx);
    return poll(kWtblControl, kWtblExecute, 0, 100);
  };
  constexpr unsigned kOfdmMbps[8] = {6, 9, 12, 18, 24, 36, 48, 54};
  for (unsigned i = 0; i < 8; ++i) {
    if (!write_rate_entry(kBasicOfdm6 + i, tx_rate_ofdm(kOfdmMbps[i]))) {
      error = "MT7925 OFDM fixed-rate table did not accept the write";
      return false;
    }
  }
  for (unsigned mcs = 0; mcs < 8; ++mcs) {
    if (!write_rate_entry(kHtMcsOddBase + 2 * mcs, tx_rate_ht(mcs))) {
      error = "MT7925 HT fixed-rate table did not accept the write";
      return false;
    }
  }
  for (unsigned i = 0; i < 6; ++i) {
    if (!write_rate_entry(kVht1ssOddBase + 2 * i, tx_rate_vht(kVht1ssMcs[i], 1))) {
      error = "MT7925 VHT 1SS fixed-rate table did not accept the write";
      return false;
    }
  }
  for (unsigned i = 0; i < 5; ++i) {
    if (!write_rate_entry(kVht2ssOddBase + 2 * i, tx_rate_vht(kVht2ssMcs[i], 2))) {
      error = "MT7925 VHT 2SS fixed-rate table did not accept the write";
      return false;
    }
  }
  const uint8_t awdl_bssid[] = {0x00, 0x25, 0x00, 0xff, 0x94, 0x73};
  std::memcpy(_bssid, awdl_bssid, sizeof(_bssid));
  _iface_up = add_interface(true);
  if (!_iface_up) {
    error = "MT7925 monitor interface creation failed";
    return false;
  }
  if (!set_rx_filter_mt7925() || !sniffer(true)) {
    error = "MT7925 monitor filter or sniffer setup failed";
    return false;
  }
  /* Pre-warm DPD calibration on the opposite non-DFS 5 GHz band at boot so the
   * first runtime 149<->48 slot-follow hop takes ~15 ms instead of ~350 ms. */
  if (_width >= 40) {
    const uint8_t home = _channel;
    _channel = home >= 149 ? 48 : 149;
    (void)sniffer_config(true);
    _channel = home;
  }
  if (!sniffer_config(true)) {
    error = "MT7925 sniffer configuration failed";
    return false;
  }
  log("mt7925: 5 GHz monitor mode on channel " + std::to_string(_channel) +
      "/" + std::to_string(_width) + " MHz" + (he80 ? " (HE80)" : ""));
  return !_proxy.failed();
}

bool Mt7902::set_rx_filter_mt7925() {
  uint8_t filter[72] = {};
  filter[4] = 0x0c; /* UNI_BAND_CONFIG_SET_MAC80211_RX_FILTER */
  filter[6] = sizeof(filter) - 4;
  const uint32_t flags = kFilterEnable | kFilterControl | kFilterOtherBss;
  std::memcpy(filter + 12, &flags, sizeof(flags));
  return mcu_send(uni_cmd(kBandConfig), filter, sizeof(filter), true);
}

int Mt7902::transmit_mt7925(const uint8_t *frame, size_t len, uint16_t rate, uint32_t queue, uint8_t omac,
                            unsigned attempts) {
  if (len < 24 || attempts < 1 || attempts > 15) return -1;
  const uint16_t fc = static_cast<uint16_t>(frame[0] | frame[1] << 8);
  const uint32_t type = (fc >> 2) & 3, subtype = (fc >> 4) & 15;
  const bool group = (frame[4] & 1u) != 0;
  const bool qos = type == 2 && (subtype & 8u);
  const unsigned qos_offset = (fc & 0x0300) == 0x0300 ? 30 : 24;
  const unsigned hdrlen = qos ? qos_offset + 2 + ((fc & 0x8000) ? 4u : 0u) : 24u;
  if (len < hdrlen || (type != 0 && type != 2)) return -1;
  const uint32_t tid = qos ? frame[qos_offset] & 15u : 0u;
  const uint32_t seq = (frame[22] >> 4) | (static_cast<uint32_t>(frame[23]) << 4);
  const bool no_ack = force_no_ack >= 0 ? force_no_ack != 0 : group;
  const uint32_t rate_idx = (group || type != 2) ? kBasicOfdm6 : mt7925_rate_table_idx(rate);
  const uint16_t wcid = omac ? mt7925::kStationMonitorWcid : mt7925::kMonitorWcid;
  uint32_t txd[8] = {};
  txd[0] = static_cast<uint32_t>(len + kTxdSize) | (kTxTypeCt << 23) | (queue << 25);
  txd[1] = bit(31) | (static_cast<uint32_t>(omac) << 25) | (tid << 21) | ((hdrlen / 2) << 16) |
           (kHdrFormat80211 << 14) | wcid;
  txd[2] = type << 4 | subtype;
  txd[3] = bit(31) | bit(28) | (seq << 16) | (attempts << 11) |
           (group ? bit(4) : 0) | (no_ack ? bit(0) : 0);
  const unsigned mode = (rate >> 6) & 0xf;
  uint32_t bw_bits = 0;
  if (force_bw >= 0) {
    bw_bits = bit(25) | ((static_cast<uint32_t>(force_bw) & 7u) << 22);
  } else if (!group && type == 2 && mode == kPhyTypeVht && _width == 80) {
    bw_bits = bit(25) | (2u << 22); /* MT_TXD6_FIXED_BW | CMD_CBW_80MHZ */
  }
  txd[6] = bw_bits | (rate_idx << 16) | (1u << 4) | bit(3) | bit(2);
  return queue_frame(frame, len, txd);
}

bool Mt7902::set_bss_qos_mt7925(uint8_t bss_idx) {
#pragma pack(push, 1)
  struct {
    uint8_t bss_idx, pad[3];
    uint16_t tag, len;
    uint8_t qos, pad2[3];
  } qos_req{};
  struct {
    uint8_t bss_idx, pad[3];
    uint16_t tag, len;
    uint8_t slot_valid, sifs_valid, rifs_valid, eifs_valid;
    uint16_t slot_time, sifs_time, rifs_time, eifs_time;
    uint8_t eifs_cck_valid, rsv;
    uint16_t eifs_cck_time;
  } ifs_req{};
  struct {
    uint8_t bss_idx, pad[3];
    uint16_t tag, len;
  } pm_req{};
#pragma pack(pop)
  static_assert(sizeof(qos_req) == 4 + 8, "mt7925 bss qos");
  static_assert(sizeof(ifs_req) == 4 + 20, "mt7925 bss ifs");
  static_assert(sizeof(pm_req) == 4 + 4, "mt7925 bss pm disable");

  qos_req.bss_idx = bss_idx;
  qos_req.tag = 15; /* UNI_BSS_INFO_QBSS */
  qos_req.len = 8;
  qos_req.qos = 1;

  ifs_req.bss_idx = bss_idx;
  ifs_req.tag = 23; /* UNI_BSS_INFO_IFS_TIME */
  ifs_req.len = 20;
  ifs_req.slot_valid = 1;
  ifs_req.slot_time = 9;

  pm_req.bss_idx = bss_idx;
  pm_req.tag = 27; /* UNI_BSS_INFO_PM_DISABLE */
  pm_req.len = 4;

  const bool q_ok = mcu_send(uni_cmd(kMcuUniBssInfoUpdate), &qos_req, sizeof(qos_req), true);
  const bool i_ok = mcu_send(uni_cmd(kMcuUniBssInfoUpdate), &ifs_req, sizeof(ifs_req), true);
  const bool p_ok = mcu_send(uni_cmd(kMcuUniBssInfoUpdate), &pm_req, sizeof(pm_req), true);
  /* Do NOT send UNI_BSS_INFO_HE_BASIC on the kConnectionInfraAp monitor BSS:
   * on Connac3 it activates the AP HE/MURU scheduler, which raises fw-event
   * eid 00 and wedges the TX queue on sniffer_config dual-channel switches.
   * Sniffer PHY natively receives incoming 802.11ax HE80 PPDUs & A-MSDUs. */
  return q_ok && i_ok && p_ok;
}

bool Mt7902::set_edca_mt7925(uint8_t bss_idx) {
#pragma pack(push, 1)
  struct EdcaTlv {
    uint16_t tag, len;
    uint8_t queue, set, cw_min, cw_max;
    uint16_t txop;
    uint8_t aifs, rsv;
  };
  struct {
    uint8_t bss_idx, rsv[3];
    EdcaTlv ac[4];
  } req{};
#pragma pack(pop)
  static_assert(sizeof(req) == 4 + 4 * 12, "mt7925 edca");
  req.bss_idx = bss_idx;
  for (uint8_t q = 0; q < 4; ++q) {
    req.ac[q].tag = 0; /* MCU_EDCA_AC_PARAM */
    req.ac[q].len = sizeof(EdcaTlv);
    req.ac[q].queue = q;
    req.ac[q].set = 0x0f; /* WMM_PARAM_SET */
    req.ac[q].cw_min = 3;
    req.ac[q].cw_max = 7;
    req.ac[q].txop = 94;
    req.ac[q].aifs = 2;
  }
  return mcu_send(uni_cmd(kMcuUniEdcaUpdate), &req, sizeof(req), true);
}

bool Mt7902::sta_add_mt7925(uint8_t wcid, const uint8_t peer[6], const PeerCaps &caps, uint8_t omac) {
  std::vector<uint8_t> b = {omac, wcid, 0, 0, 1, omac, 0, 0};
  auto add_tlv = [&](uint16_t tag, uint16_t len) -> uint8_t * {
    const size_t at = b.size();
    b.resize(at + len, 0);
    std::memcpy(&b[at], &tag, 2);
    std::memcpy(&b[at + 2], &len, 2);
    uint16_t n = 0;
    std::memcpy(&n, &b[2], 2);
    ++n;
    std::memcpy(&b[2], &n, 2);
    return b.data() + at;
  };
  auto put16 = [](uint8_t *p, size_t off, uint16_t v) { std::memcpy(p + off, &v, 2); };
  auto put32 = [](uint8_t *p, size_t off, uint32_t v) { std::memcpy(p + off, &v, 4); };
  const uint8_t radio_bw = _width >= 80 ? 2 : (_width >= 40 ? 1 : 0);
  const uint8_t eff_bw = std::min<uint8_t>(caps.bw, radio_bw);
  const bool use_vht = caps.vht && eff_bw >= 1;
  /* Keep peer TX STA_REC on VHT80 2SS MCS 4-9 SGI (like iwxradio iwx.cpp):
   * STA_REC_HE_V2 on Connac3 raises fw-event eid 00 after ~48 sniffer_config
   * dual-channel hops, whereas VHT80 2SS TX BA never raises eid 00 while --he80
   * enables AWDL HE80 RX, ADDBA RX A-MSDU (amsdu=1), and awdl0 MTU 1500. */
  const bool use_he = false;
  { /* STA_REC_BASIC (tag 0, 20B) */
    uint8_t *t = add_tlv(0, 20);
    put32(t, 4, kConnectionInfraSta);
    t[8] = 2; /* CONN_STATE_PORT_SECURE */
    t[9] = 1; /* qos */
    put16(t, 10, wcid);
    std::memcpy(t + 12, peer, 6);
    put16(t, 18, 0x3); /* EXTRA_INFO_VER | EXTRA_INFO_NEW */
  }
  { /* STA_REC_PHY (tag 0x15, 12B) */
    uint8_t *phy = add_tlv(0x15, 12);
    put16(phy, 4, 0x0150);
    phy[6] = static_cast<uint8_t>(bit(3) | bit(4) | (use_vht ? bit(5) : 0) | (use_he ? bit(6) : 0));
    uint8_t af = caps.ampdu_params & 0x3;
    if (use_vht) af = std::max<uint8_t>(af, static_cast<uint8_t>((caps.vht_cap >> 23) & 0x7));
    const uint8_t mm = static_cast<uint8_t>((caps.ampdu_params >> 2) & 0x7);
    phy[7] = static_cast<uint8_t>((af & 0x3) | (mm << 2));
    phy[9] = 180;
    phy[10] = af;
  }
  { /* STA_REC_HT (tag 9, 8B) */
    uint8_t *ht = add_tlv(9, 8);
    put16(ht, 4, eff_bw >= 1 ? caps.ht_cap : static_cast<uint16_t>(caps.ht_cap & ~0x0002u));
  }
  if (use_vht) { /* STA_REC_VHT (tag 10, 16B) */
    uint8_t *vht = add_tlv(10, 16);
    put32(vht, 4, caps.vht_cap);
    put16(vht, 8, caps.vht_mcs);
    put16(vht, 10, caps.vht_mcs);
  }
  if (use_he) { /* STA_REC_HE_V2 (tag 0x19, 28B) */
    uint8_t *he = add_tlv(0x19, 28);
    he[4] = 0x01;  /* +HTC HE Support (required by Connac3 HE SU TX BA) */
    he[7] = 0x18;  /* MAX_AMPDU_LEN_EXP_EXT */
    he[8] = 0x40;  /* A-MSDU in A-MPDU */
    he[10] = 0x04; /* 5 GHz 40/80 MHz */
    he[11] = 0x22; /* Class A | LDPC */
    he[19] = 0x02; /* 16 us nominal packet padding */
    he[21] = 2;    /* pkt_ext */
    put16(he, 22, caps.nss >= 2 ? 0xfffa : 0xfffe); /* BW80 MCS 0-11 */
    put16(he, 24, 0xffff);
    put16(he, 26, 0xffff);
  }
  { /* STA_REC_RA (tag 1, 16B) */
    uint8_t *ra = add_tlv(1, 16);
    /* Skip 6-18 Mbps OFDM and HT MCS 0-3 on 80 MHz AWDL peers (as iwx.cpp's
     * 0x00f0 mask): peers hop away every 65 ms and RA would otherwise spend
     * the first ~30 MB climbing out of low MCS or drop back on slot edges. */
    const bool fast_awdl = omac == 0 && eff_bw >= 2;
    put16(ra, 4, static_cast<uint16_t>((fast_awdl ? 0xf0u : 0xffu) << 6));
    ra[6] = fast_awdl ? static_cast<uint8_t>(caps.rx_mcs[0] & 0xf0u) : caps.rx_mcs[0];
    ra[7] = fast_awdl ? static_cast<uint8_t>(caps.rx_mcs[1] & 0xf0u) : caps.rx_mcs[1];
  }
  { /* STA_REC_STATE v2 (tag 7, 16B) */
    uint8_t *st = add_tlv(7, 16);
    st[4] = 2; /* MT76_STA_INFO_STATE_ASSOC */
    const uint8_t nss = caps.nss >= 2 ? 2 : 1;
    st[12] = static_cast<uint8_t>(eff_bw | ((nss - 1u) << 4));
  }
  { /* STA_REC_MLD (tag 0x20, 28B) */
    uint8_t *mld = add_tlv(0x20, 28);
    std::memcpy(mld + 4, peer, 6);
    put16(mld, 10, wcid);
    put16(mld, 12, wcid);
    put16(mld, 14, wcid);
    mld[16] = 1;
    put16(mld, 20, wcid);
    mld[22] = omac;
  }
  { /* STA_REC_HDR_TRANS (tag 0x2b, 8B) */
    uint8_t *htr = add_tlv(0x2b, 8);
    htr[6] = 1; /* dis_rx_hdr_tran: deliver native 802.11 */
  }
  const bool ok = mcu_send(uni_cmd(kMcuUniStaRecUpdate), b.data(), b.size(), true, nullptr, sta_cmd_timeout_ms);
  log(std::string("mt7925: station ") + std::to_string(wcid) +
      (ok ? ((he80 && omac == 0) ? " added (HE80-RX / VHT80-TX)" : " added") : " not added"));
  return ok;
}

bool Mt7902::sta_remove_mt7925(uint8_t wcid, const uint8_t peer[6], uint8_t omac) {
  std::vector<uint8_t> b = {omac, wcid, 3, 0, 1, omac, 0, 0};
  const size_t basic_at = b.size();
  b.resize(basic_at + 20 + 8 + 4, 0);
  const uint16_t t_basic = 0, l_basic = 20, extra = 1;
  std::memcpy(&b[basic_at], &t_basic, 2);
  std::memcpy(&b[basic_at + 2], &l_basic, 2);
  std::memcpy(&b[basic_at + 4], &kConnectionInfraSta, 4);
  std::memcpy(&b[basic_at + 12], peer, 6);
  std::memcpy(&b[basic_at + 18], &extra, 2);
  const uint16_t t_rem = 0x25, l_rem = 8;
  std::memcpy(&b[basic_at + 20], &t_rem, 2);
  std::memcpy(&b[basic_at + 22], &l_rem, 2);
  const uint16_t t_mld_off = 0x23, l_mld_off = 4;
  std::memcpy(&b[basic_at + 28], &t_mld_off, 2);
  std::memcpy(&b[basic_at + 30], &l_mld_off, 2);
  return mcu_send(uni_cmd(kMcuUniStaRecUpdate), b.data(), b.size(), true, nullptr, sta_cmd_timeout_ms);
}

bool Mt7902::sta_ba_mt7925(uint8_t wcid, uint8_t tid, uint16_t ssn, uint16_t win, bool tx, bool enable,
                           uint8_t omac, bool amsdu) {
  uint8_t req[8 + 16] = {omac, wcid, 1, 0, 1, omac, 0, 0};
  const uint16_t tag = 6, len = 16; /* sta_rec_ba_uni */
  std::memcpy(req + 8, &tag, 2);
  std::memcpy(req + 10, &len, 2);
  req[12] = tid;
  req[13] = tx ? 1 : 2; /* ORIGINATOR : RECIPIENT */
  req[14] = amsdu ? 1 : 0;
  req[15] = enable ? static_cast<uint8_t>(1u << tid) : 0;
  std::memcpy(req + 16, &ssn, 2);
  std::memcpy(req + 18, &win, 2);
  return mcu_send(uni_cmd(kMcuUniStaRecUpdate), req, sizeof(req), true, nullptr, sta_cmd_timeout_ms);
}

int Mt7902::transmit_sta_mt7925(const uint8_t *frame, size_t len, uint8_t wcid, uint8_t omac, bool allow_ba,
                                unsigned attempts) {
  if (len < 26 || attempts < 1 || attempts > 31) return -1;
  const uint16_t fc = static_cast<uint16_t>(frame[0] | frame[1] << 8);
  const uint32_t type = (fc >> 2) & 3, subtype = (fc >> 4) & 15;
  if (type != 2 || !(subtype & 8u)) return -1;
  const unsigned qos_offset = (fc & 0x0300) == 0x0300 ? 30 : 24;
  const unsigned hdrlen = qos_offset + 2 + ((fc & 0x8000) ? 4u : 0u);
  if (len < hdrlen) return -1;
  const uint32_t tid = frame[qos_offset] & 7u;
  const uint32_t seq = (frame[22] >> 4) | (static_cast<uint32_t>(frame[23]) << 4);
  const uint32_t q = force_altx >= 0 ? (force_altx ? kLmacAltx0 : kLmacAc00) : kLmacAc00;
  uint32_t txd[8] = {};
  txd[0] = static_cast<uint32_t>(len + kTxdSize) | (kTxTypeCt << 23) | (q << 25);
  txd[1] = (static_cast<uint32_t>(omac) << 25) | (tid << 21) | ((hdrlen / 2) << 16) |
           (kHdrFormat80211 << 14) | wcid;
  txd[2] = (type << 4) | subtype;
  txd[3] = bit(31) | (allow_ba ? 0u : bit(28)) | (seq << 16) | (attempts << 11);
  txd[6] = (1u << 4) | bit(3) | bit(2);
  return queue_frame(frame, len, txd);
}

void Mt7902::handle_mt7925_rx(const std::vector<uint8_t> &buf) {
  const auto *rxd = reinterpret_cast<const uint32_t *>(buf.data());
  const unsigned type = rxd[0] >> kRxd0PktTypeShift;
  const size_t total = std::min<size_t>(rxd[0] & 0xffff, buf.size());
  if (type == kPktTypeTxrxNotify) {
    if (total < 8 || ((rxd[1] >> 16) & 15u) < 4u) return;
    unsigned remaining = (rxd[0] >> 16) & 0x3ff;
    unsigned tries = 0, status = 1;
    for (size_t i = 2; i < total / 4 && remaining; ++i) {
      const uint32_t word = rxd[i];
      if (word & bit(31)) continue;
      if (word & bit(30)) {
        tries = (word >> 24) & 15u;
        status = (word >> 28) & 3u;
        continue;
      }
      for (unsigned shift = 0; shift < 30 && remaining; shift += 15) {
        const uint16_t token = static_cast<uint16_t>((word >> shift) & kNoToken);
        if (token == kNoToken) continue;
        --remaining;
        if (token >= _tx_slots.size() ||
            std::find(_tx_free_slots.begin(), _tx_free_slots.end(), token) != _tx_free_slots.end()) continue;
        _tx_free_slots.push_back(token);
        if (_on_tx_status) _on_tx_status(token, status == 0, tries);
      }
    }
    return;
  }
  const bool firmware_frame = type == kPktTypeRxEvent && ((rxd[0] >> 16) & 15u) == 1;
  if (type == kPktTypeRxEvent && !firmware_frame) {
    if (_on_event) _on_event(buf.data(), buf.size());
    return;
  }
  if (total < 32 || (type != kPktTypeNormal && type != kPktTypeNormalMcu && !firmware_frame)) return;
  if ((rxd[2] & (bit(7) | bit(23) | bit(24))) || (rxd[3] & bit(24))) return;
  const uint32_t groups = rxd[1];
  size_t word = 8;
  if (groups & bit(19)) word += 4;
  if (groups & bit(16)) word += 4;
  if (groups & bit(17)) word += 4;
  int rcpi = 0;
  if (groups & bit(18)) {
    if ((word + 4) * 4 > total) return;
    rcpi = rxd[word + 3] & 0xff;
    word += 4;
    if (groups & bit(20)) word += 24;
  }
  const size_t gap = word * 4 + 2 * ((rxd[2] >> 13) & 7u);
  if (total < gap + 10) return;
  const auto channel = static_cast<uint8_t>((rxd[3] >> 8) & 0xff);
  if (channel != _channel) return;
  const auto dbm = static_cast<int8_t>(rcpi ? (rcpi - 220) / 2 : -127);
  const uint8_t *fptr = buf.data() + gap;
  size_t flen = total - gap;
  if ((rxd[4] & 3u) != 0 && flen >= 26) {
    /* Connac3 raw A-MSDU frames without header translation insert 2 bytes of
     * padding immediately after the 802.11 MAC header (mt7925_mac_fill_rx). */
    const uint16_t fc = static_cast<uint16_t>(fptr[0] | (fptr[1] << 8));
    const unsigned ftype = (fc >> 2) & 3u, fstype = (fc >> 4) & 0xfu;
    size_t hlen = 24;
    if (ftype == 2) {
      if ((fc & 0x0300u) == 0x0300u) hlen += 6;
      if (fstype & 8u) {
        hlen += 2;
        if (fc & 0x8000u) hlen += 4;
      }
    }
    if (hlen + 2 <= flen) {
      _rx_unpad.resize(flen - 2);
      std::memcpy(_rx_unpad.data(), fptr, hlen);
      std::memcpy(_rx_unpad.data() + hlen, fptr + hlen + 2, flen - hlen - 2);
      fptr = _rx_unpad.data();
      flen = _rx_unpad.size();
    }
  }
  if (_on_rx) _on_rx({fptr, flen, dbm, channel});
}
