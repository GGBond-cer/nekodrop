// SPDX-License-Identifier: GPL-2.0-only
/*
 * AWDL radio policy, ported from ForbiddenItlwm 0.6.0 (retropineaple,
 * GPL-2.0): include/AWDLBlockAck.hpp, include/AWDLProbePolicy.hpp,
 * include/AWDLChannels.hpp and the awdlCoex* helpers in ItlIwx.cpp. The logic
 * and constants are upstream's; the names are shortened and the macOS state
 * around them is replaced by plain structs.
 */
#pragma once
#include "../../iwxradio/src/awdl_fcs.h"
#include <chrono>
#include <map>
#include <vector>

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace awdl {

/* ---------------------------------------------------------- channels */

/* Channels a fixed AWDL schedule may name (no radar duty). */
inline bool fixed_schedule_channel(unsigned ch) {
  if (ch >= 1 && ch <= 13) return true;
  if (ch >= 36 && ch <= 48) return ch % 4 == 0;
  if (ch >= 149 && ch <= 165) return (ch - 149) % 4 == 0;
  return false;
}

/* First channel of the 80 MHz block (149-161 or 36-48), 0 otherwise. */
inline unsigned block80_first(unsigned ch) {
  if (ch >= 149 && ch <= 161 && (ch - 149) % 4 == 0) return 149;
  if (ch >= 36 && ch <= 48 && ch % 4 == 0) return 36;
  return 0;
}

/* Parses a 16-slot channel sequence body (6-byte header + 16 entries) across
 * OWL's SIMPLE (0), LEGACY (1) and OPCLASS (3) encodings. */
inline bool parse_chanseq_body(const uint8_t *val, size_t size, uint8_t out[16]) {
  if (!val || size < 6 || val[0] != 15 || val[2] != 0) return false;
  const uint8_t encoding = val[1];
  if (encoding == 0) {
    if (size < 6 + 16) return false;
    for (unsigned n = 0; n < 16; ++n) out[n] = val[6 + n];
    return true;
  }
  if (encoding == 1) {
    if (size < 6 + 32) return false;
    for (unsigned n = 0; n < 16; ++n) out[n] = val[6 + 2 * n + 1];
    return true;
  }
  if (encoding == 3) {
    if (size < 6 + 32) return false;
    for (unsigned n = 0; n < 16; ++n) out[n] = val[6 + 2 * n];
    return true;
  }
  return false;
}

/* The peer's 16-slot channel sequence (TLV 18, or embedded in TLV 4) from an
 * AWDL action frame without FCS: one channel number per slot. */
inline bool peer_sequence(const uint8_t *frame, size_t length, uint8_t out[16]) {
  if (!frame || length < 40) return false;
  bool fallback = false;
  uint8_t embedded[16]{};
  for (size_t i = 40; i + 3 <= length;) {
    const size_t value = size_t(frame[i + 1]) | size_t(frame[i + 2]) << 8;
    if (i + 3 + value > length) return false;
    if (frame[i] == 18 && parse_chanseq_body(frame + i + 3, value, out)) return true;
    if (frame[i] == 4 && value >= 32 + 22 && !fallback &&
        parse_chanseq_body(frame + i + 3 + 32, value - 32, embedded)) {
      fallback = true;
    }
    i += 3 + value;
  }
  if (fallback) {
    std::memcpy(out, embedded, 16);
    return true;
  }
  return false;
}

/* Peers with <= 5 slots on our channel (Home-screen Idle = 5, Infra Priority = 2)
 * doze after the first ~4.5 ms of each 16 TU AW unless data arrived recently
 * (ported from iwxradio/src/awdl_schedule.h SendGate::Doze). */
constexpr unsigned kPeerDozeMaxEntries = 5;

struct SyncAnchor {
  using Clock = std::chrono::steady_clock;
  Clock::time_point sampled{};
  std::chrono::microseconds remaining{};
  std::chrono::microseconds eaw_period{64 * 1024};
  unsigned slot = 0;

  bool parse_tlv4(const uint8_t *frame, size_t length, Clock::time_point now) {
    if (!frame || length < 40) return false;
    for (size_t i = 40; i + 3 <= length;) {
      const size_t size = size_t(frame[i + 1]) | size_t(frame[i + 2]) << 8;
      if (i + 3 + size > length) break;
      if (frame[i] == 4 && size >= 31) {
        const auto *value = frame + i + 3;
        const unsigned down_tu = value[1] | (unsigned(value[2]) << 8);
        unsigned aw_period_tu = value[5] | (unsigned(value[6]) << 8);
        if (aw_period_tu == 0) aw_period_tu = 16;
        const unsigned presence = value[27] ? value[27] : 4u;
        const unsigned eaw_tu = aw_period_tu * presence;
        const unsigned counter = value[29] | (unsigned(value[30]) << 8);
        if (aw_period_tu >= 8 && aw_period_tu <= 64 && presence >= 1 && presence <= 8 &&
            down_tu <= eaw_tu + 2) {
          sampled = now;
          eaw_period = std::chrono::microseconds(uint64_t(eaw_tu) * 1024);
          remaining = std::chrono::microseconds(
              uint64_t(down_tu <= eaw_tu ? down_tu : eaw_tu) * 1024);
          slot = (counter / presence) % 16;
          return true;
        }
        break;
      }
      i += 3 + size;
    }
    return false;
  }
};

/* OWL's synchronization (TLV 4, in PSF/MIF) and channel-sequence (TLV 18, in
 * MIF) TLVs often arrive in separate frames: merge them per peer so every PSF
 * keeps the EAW phase fresh while the latest MIF sequence stays active. */
struct PeerTxWindow {
  using Clock = std::chrono::steady_clock;
  Clock::time_point sampled{};
  Clock::time_point seq_updated{};
  Clock::time_point last_data_rx{};
  std::chrono::microseconds remaining{};
  std::chrono::microseconds eaw_period{64 * 1024};
  uint8_t channels[16]{};
  unsigned slot = 0;
  bool has_seq = false;

  bool valid(Clock::time_point now) const {
    const auto period = eaw_period.count() > 0 ? eaw_period : std::chrono::microseconds(64 * 1024);
    return has_seq && sampled != Clock::time_point{} && now >= sampled &&
           now - sampled < 3 * 16 * period;
  }

  unsigned present_entries(unsigned channel) const {
    if (!has_seq) return 0;
    unsigned count = 0;
    for (uint8_t c : channels)
      if (c == channel) ++count;
    return count;
  }

  bool has_channel(unsigned channel) const { return present_entries(channel) > 0; }

  void note_data_rx(Clock::time_point now, bool power_save) {
    last_data_rx = power_save ? Clock::time_point{} : now;
  }

  bool is_dozing(Clock::time_point now, unsigned channel) const {
    const unsigned present = present_entries(channel);
    if (present == 0 || present > kPeerDozeMaxEntries) return false;
    return last_data_rx == Clock::time_point{} || now < last_data_rx ||
           now - last_data_rx > std::chrono::milliseconds(100);
  }

  void inherit_sync(const SyncAnchor &anchor) {
    if (anchor.sampled != Clock::time_point{} &&
        (sampled == Clock::time_point{} || anchor.sampled > sampled)) {
      sampled = anchor.sampled;
      remaining = anchor.remaining;
      eaw_period = anchor.eaw_period;
      slot = anchor.slot;
    }
  }

  bool can_send(Clock::time_point now, unsigned channel, bool respect_doze = false) const {
    const auto period = eaw_period.count() > 0 ? eaw_period : std::chrono::microseconds(64 * 1024);
    if (!valid(now)) return false;
    const auto elapsed = now - sampled + (period - remaining);
    const auto position = elapsed % period;
    const auto index = static_cast<unsigned>((slot + elapsed / period) % 16);
    if (channels[index] != channel) return false;
    const bool prev_same = channels[(index + 15) % 16] == channel;
    const bool next_same = channels[(index + 1) % 16] == channel;
    const bool dozing = respect_doze && is_dozing(now, channel);
    const auto start_guard = dozing ? std::chrono::milliseconds(1) : std::chrono::milliseconds(3);
    constexpr auto end_guard = std::chrono::milliseconds(4);
    if (!prev_same && position < start_guard) return false;
    if (!next_same && period - position < end_guard) return false;
    if (dozing) {
      const auto aw_period = period / 4;
      if (aw_period.count() > 0 && (position % aw_period) >= std::chrono::microseconds(4500))
        return false;
    }
    return true;
  }

  std::chrono::microseconds remaining_open(Clock::time_point now, unsigned channel,
                                           bool respect_doze = false) const {
    const auto period = eaw_period.count() > 0 ? eaw_period : std::chrono::microseconds(64 * 1024);
    if (!can_send(now, channel, respect_doze)) return std::chrono::microseconds(0);
    const auto elapsed = now - sampled + (period - remaining);
    const auto position = elapsed % period;
    const auto index = static_cast<unsigned>((slot + elapsed / period) % 16);
    if (respect_doze && is_dozing(now, channel)) {
      const auto aw_period = period / 4;
      const auto aw_pos = aw_period.count() > 0 ? (position % aw_period) : Clock::duration::zero();
      return aw_pos < std::chrono::microseconds(4500)
                 ? std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::microseconds(4500) - aw_pos)
                 : std::chrono::microseconds(0);
    }
    unsigned run = 1;
    while (run < 16 && channels[(index + run) % 16] == channel) ++run;
    if (run >= 16) return 16 * period;
    constexpr auto end_guard = std::chrono::milliseconds(4);
    const auto total = run * period - position;
    return total > end_guard
               ? std::chrono::duration_cast<std::chrono::microseconds>(total - end_guard)
               : std::chrono::microseconds(0);
  }

  unsigned recommended_inflight(Clock::time_point now, unsigned channel, unsigned max_slots = 32) const {
    if (!valid(now) || !has_channel(channel)) return max_slots;
    const auto rem = remaining_open(now, channel, false);
    if (rem <= std::chrono::microseconds(0)) return 0;
    if (rem >= std::chrono::milliseconds(18)) return max_slots;
    if (max_slots > 64u) {
      if (rem >= std::chrono::milliseconds(10)) return 48u;
      if (rem >= std::chrono::milliseconds(5)) return 16u;
      return 4u;
    }
    if (rem >= std::chrono::milliseconds(10)) return max_slots < 16u ? max_slots : 16u;
    if (rem >= std::chrono::milliseconds(5)) return max_slots < 6u ? max_slots : 6u;
    return max_slots < 2u ? max_slots : 2u;
  }

  bool note_frame(const uint8_t *frame, size_t length, Clock::time_point now) {
    if (!frame || length < 40) return false;
    bool updated = false;
    uint8_t seq[16]{};
    if (peer_sequence(frame, length, seq)) {
      std::memcpy(channels, seq, 16);
      has_seq = true;
      seq_updated = now;
      updated = true;
    }
    SyncAnchor anchor;
    if (anchor.parse_tlv4(frame, length, now)) {
      sampled = anchor.sampled;
      remaining = anchor.remaining;
      eaw_period = anchor.eaw_period;
      slot = anchor.slot;
      updated = true;
    }
    return updated && has_seq && sampled != Clock::time_point{};
  }

  static constexpr bool is_non_dfs_5g(uint8_t ch) {
    if (ch >= 36 && ch <= 48) return (ch - 36) % 4 == 0;
    if (ch >= 149 && ch <= 165) return (ch - 149) % 4 == 0;
    return false;
  }

  uint8_t secondary_5g_channel(uint8_t primary_ch, uint8_t away_mask[16]) const {
    std::memset(away_mask, 0, 16);
    if (!has_seq) return 0;
    unsigned primary_n = 0;
    uint8_t best_ch = 0;
    unsigned best_n = 0;
    unsigned counts[256] = {};
    for (uint8_t c : channels) {
      if (c == primary_ch) {
        ++primary_n;
      } else if (is_non_dfs_5g(c)) {
        if (++counts[c] > best_n) {
          best_n = counts[c];
          best_ch = c;
        }
      }
    }
    if (primary_n < 2 || best_n < 3 || !best_ch) return 0;
    for (unsigned k = 0; k < 16; ++k) away_mask[k] = (channels[k] == best_ch) ? 1u : 0u;
    return best_ch;
  }

  uint8_t desired_channel_at(Clock::time_point now, uint8_t primary_ch, uint8_t alt_ch,
                             std::chrono::microseconds lead = std::chrono::milliseconds(3)) const {
    if (!alt_ch || !valid(now)) return primary_ch;
    const auto period = eaw_period.count() > 0 ? eaw_period : std::chrono::microseconds(64 * 1024);
    const auto probe = now + lead;
    const auto elapsed = probe - sampled + (period - remaining);
    const auto index = static_cast<unsigned>((slot + elapsed / period) % 16);
    return channels[index] == alt_ch ? alt_ch : primary_ch;
  }
};

inline bool peer_tx_window(const uint8_t *frame, size_t length,
                           PeerTxWindow::Clock::time_point now, PeerTxWindow &out) {
  PeerTxWindow window = out;
  if (!window.note_frame(frame, length, now)) return false;
  out = window;
  return true;
}

struct TcpSegment {
  uint16_t source = 0;
  uint16_t destination = 0;
  uint8_t flags = 0;
  uint32_t payload_bytes = 0;
  uint32_t sequence = 0;
  uint32_t acknowledgment = 0;
};

inline bool tcp_seq_leq(uint32_t a, uint32_t b) { return static_cast<int32_t>(a - b) <= 0; }
inline bool tcp_seq_gt(uint32_t a, uint32_t b) { return static_cast<int32_t>(a - b) > 0; }

inline bool unwrap_rfc1042_awdl(const uint8_t *payload, size_t payload_len, size_t &skip_out, size_t &keep_len_out) {
  constexpr uint8_t rfc1042[] = {0xaa, 0xaa, 0x03, 0x00, 0x00, 0x00};
  constexpr uint8_t snap[] = {0xaa, 0xaa, 0x03, 0x00, 0x17, 0xf2, 0x08, 0x00};
  if (!payload || payload_len < 16u || std::memcmp(payload, rfc1042, sizeof(rfc1042)) != 0) return false;
  const uint16_t inner_len = static_cast<uint16_t>((static_cast<uint16_t>(payload[6]) << 8) | payload[7]);
  if (inner_len < 16u || inner_len >= 0x0600u || 8u + inner_len > payload_len) return false;
  if (std::memcmp(payload + 8, snap, sizeof(snap)) == 0) {
    skip_out = 8u;
    keep_len_out = inner_len;
    return true;
  }
  if (inner_len >= 30u && std::memcmp(payload + 22, snap, sizeof(snap)) == 0) {
    skip_out = 22u;
    keep_len_out = inner_len - 14u;
    return true;
  }
  return false;
}

inline bool parse_awdl_tcp(const uint8_t *frame, size_t length, TcpSegment &out) {
  if (!frame || length < 24) return false;
  const uint16_t fc = static_cast<uint16_t>(frame[0] | (static_cast<uint16_t>(frame[1]) << 8));
  if (((fc >> 2) & 3u) != 2u || (frame[4] & 1u) || (fc & 0x4300u)) return false;
  size_t header = (fc & 0x80u) ? ((fc & 0x8000u) ? 30u : 26u) : 24u;
  if (header >= length) return false;
  size_t skip = 0, wrapped_len = 0;
  if (unwrap_rfc1042_awdl(frame + header, length - header, skip, wrapped_len)) {
    header += skip;
    length = header + wrapped_len;
  }
  constexpr uint8_t snap[] = {0xaa, 0xaa, 3, 0, 0x17, 0xf2, 8, 0};
  if (length < header + 16u + 60u ||
      std::memcmp(frame + header, snap, sizeof(snap)) != 0 ||
      frame[header + 14] != 0x86 || frame[header + 15] != 0xdd) return false;
  const auto *ip = frame + header + 16;
  if ((ip[0] >> 4) != 6 || ip[6] != 6) return false;
  const auto be16 = [](const uint8_t *p) -> uint16_t {
    return static_cast<uint16_t>((static_cast<uint16_t>(p[0]) << 8) | p[1]);
  };
  const auto be32 = [&](const uint8_t *p) -> uint32_t {
    return (static_cast<uint32_t>(be16(p)) << 16) | be16(p + 2);
  };
  const auto *tcp = ip + 40;
  const uint32_t tcp_length = be16(ip + 4);
  const uint32_t tcp_header = static_cast<uint32_t>((tcp[12] >> 4) & 0xfu) * 4u;
  if (tcp_header < 20u || tcp_header > tcp_length || tcp_length > length - header - 56u) return false;
  out = TcpSegment{be16(tcp), be16(tcp + 2), tcp[13], tcp_length - tcp_header,
                   be32(tcp + 4), be32(tcp + 8)};
  return true;
}

/* Tracks cumulative TCP ACKs received from the peer per (local_port, peer_port)
 * flow so already-acknowledged outbound data frames are never retransmitted
 * from MAC software retry queues. */
class TcpAckTracker {
public:
  static uint32_t flow_key(uint16_t local_port, uint16_t peer_port) {
    return (static_cast<uint32_t>(local_port) << 16) | peer_port;
  }

  void clear() { _acked.clear(); }

  bool note_peer_rx(const uint8_t *frame, size_t len) {
    TcpSegment rx;
    if (!parse_awdl_tcp(frame, len, rx)) return false;
    const uint32_t key = flow_key(rx.destination, rx.source);
    if (rx.flags & 0x04u) {
      _acked.erase(key);
      return false;
    }
    if (!(rx.flags & 0x10u)) return false;
    auto it = _acked.find(key);
    if (it == _acked.end() || (rx.flags & 0x02u) != 0) {
      _acked[key] = rx.acknowledgment;
      return true;
    }
    if (tcp_seq_gt(rx.acknowledgment, it->second)) {
      it->second = rx.acknowledgment;
      return true;
    }
    return false;
  }

  void note_local_tx(const TcpSegment &tx) {
    const uint32_t key = flow_key(tx.source, tx.destination);
    if ((tx.flags & 0x04u) != 0 || ((tx.flags & 0x02u) != 0 && (tx.flags & 0x10u) == 0))
      _acked.erase(key);
  }

  bool is_outbound_acked(const uint8_t *frame, size_t len) const {
    TcpSegment tx;
    if (!parse_awdl_tcp(frame, len, tx)) return false;
    if (tx.payload_bytes == 0 || (tx.flags & 0x07u) != 0) return false;
    const auto it = _acked.find(flow_key(tx.source, tx.destination));
    if (it == _acked.end()) return false;
    const uint32_t seq_end = tx.sequence + tx.payload_bytes;
    const uint32_t ahead = it->second - seq_end;
    return tcp_seq_leq(seq_end, it->second) && ahead < (64u << 20);
  }

private:
  std::map<uint32_t, uint32_t> _acked;
};

/* Whether the sequence names `ch` as an AWDL channel. Slots 6, 7, 14, 15 are
 * the phone's own Wi-Fi; a channel there counts only with wifi_too and when
 * it also appears in another slot (upstream PR #21's min rule). */
inline bool peer_names(const uint8_t seq[16], unsigned ch, bool wifi_too = false) {
  if (!wifi_too && (seq[6] == ch || seq[7] == ch || seq[14] == ch || seq[15] == ch)) return false;
  for (unsigned n = 0; n < 16; ++n)
    if ((n & 7) < 6 && seq[n] == ch) return true;
  return false;
}

/* The phone's 5 GHz AWDL channel: 44 or 149, 0 if neither. 44 wins. */
inline unsigned peer_channel(const uint8_t seq[16]) {
  const bool names149 = peer_names(seq, 149);
  if (peer_names(seq, 44, !names149)) return 44;
  return names149 ? 149 : 0;
}

/* The country a peer states in its AWDL frames: TLV 12, value bytes 2-3. */
inline bool peer_country(const uint8_t *frame, size_t length, char out[3]) {
  if (!frame || length < 40) return false;
  for (size_t i = 40; i + 3 <= length;) {
    const size_t value = size_t(frame[i + 1]) | size_t(frame[i + 2]) << 8;
    if (i + 3 + value > length) return false;
    if (frame[i] == 12) {
      if (value < 4) return false;
      const char a = char(frame[i + 5]), b = char(frame[i + 6]);
      if (a < 'A' || a > 'Z' || b < 'A' || b > 'Z' || (a == 'Z' && b == 'Z')) return false;
      out[0] = a;
      out[1] = b;
      out[2] = 0;
      return true;
    }
    i += 3 + value;
  }
  return false;
}

/* The same country this many times in a row before it is believed. */
constexpr unsigned kPeerCountryFrames = 5;
struct PeerCountry {
  char candidate[3] = {}, confirmed[3] = {};
  unsigned run = 0;
  /* True when `confirmed` just changed. */
  bool note(const char c[3]) {
    if (std::strcmp(c, candidate) != 0) {
      std::memcpy(candidate, c, 3);
      run = 0;
    }
    if (++run >= kPeerCountryFrames && std::strcmp(candidate, confirmed) != 0) {
      std::memcpy(confirmed, candidate, 3);
      return true;
    }
    return false;
  }
};

/* The AWDL action frame header: management action, category 127, Apple OUI,
 * type 8. */
inline bool is_awdl_action(const uint8_t *f, size_t len) {
  return len >= 30 && f[0] == 0xd0 && f[24] == 0x7f && f[25] == 0x00 && f[26] == 0x17 &&
         f[27] == 0xf2 && f[28] == 0x08;
}

/* How an AWDL action frame's TLVs (after the 24-byte header and 16 fixed
 * bytes) fit the frame: the bytes left after the last whole TLV, whether
 * all of them are zero (padding awdld trims), and whether a version TLV
 * (21), which awdld needs before it takes the peer as valid, is there. */
struct AwdlTlvFit {
  size_t left = 0;
  bool left_zero = true;
  bool has_version = false;
};
inline AwdlTlvFit awdl_tlv_fit(const uint8_t *f, size_t len) {
  AwdlTlvFit r;
  size_t off = 40;
  if (len < off) return r;
  while (len - off >= 3) {
    const size_t n = size_t(f[off + 1]) | size_t(f[off + 2]) << 8;
    if (n > len - off - 3) break;
    if (f[off] == 21 && n >= 2) r.has_version = true;
    off += 3 + n;
  }
  r.left = len - off;
  for (size_t i = off; i < len; i++)
    if (f[i]) r.left_zero = false;
  return r;
}

/* --------------------------------------------------------- Block Ack */

constexpr uint16_t kBaWindowMax = 64;
constexpr size_t kAddbaResponseLength = 33;

struct AddbaRequest {
  uint8_t peer[6], bssid[6];
  uint8_t token, tid;
  bool immediate;
  uint16_t window, ssn;
};

/* A management action frame to `own`: category 3 (Block Ack), action 0. */
inline bool parse_addba_request(const uint8_t *f, size_t length, const uint8_t *own, AddbaRequest *out) {
  if (!f || !own || !out || length < 33 || f[0] != 0xd0 || std::memcmp(f + 4, own, 6) || f[24] != 3 ||
      f[25] != 0)
    return false;
  const uint16_t params = uint16_t(f[27] | f[28] << 8);
  const uint16_t seqctl = uint16_t(f[31] | f[32] << 8);
  const unsigned tid = (params >> 2) & 15U;
  if (tid >= 8) return false;
  std::memcpy(out->peer, f + 10, 6);
  std::memcpy(out->bssid, f + 16, 6);
  out->token = f[26];
  out->tid = uint8_t(tid);
  out->immediate = params & 2U;
  const uint16_t asked = params >> 6;
  out->window = !asked || asked > kBaWindowMax ? kBaWindowMax : asked;
  out->ssn = seqctl >> 4;
  return true;
}

/* DELBA from the peer (category 3, action 2) to `own`. */
inline bool parse_delba(const uint8_t *f, size_t length, const uint8_t *own, bool *peer_was_sender,
                        uint8_t *tid) {
  if (!f || !own || length < 28 || f[0] != 0xd0 || std::memcmp(f + 4, own, 6) || f[24] != 3 || f[25] != 2)
    return false;
  const uint16_t params = uint16_t(f[26] | f[27] << 8);
  *peer_was_sender = (params >> 11) & 1U; /* initiator bit */
  *tid = uint8_t((params >> 12) & 15U);
  return true;
}

/* DELBA (category 3, action 2) to `peer`: `initiator` when we are the
 * session's sender. Reason 39 (QSTA_TIMEOUT), as Linux sends for an idle
 * session. */
constexpr size_t kDelbaLength = 30;
inline size_t build_delba(uint8_t *out, const uint8_t *peer, const uint8_t *own, const uint8_t *bssid, uint8_t tid,
                          bool initiator, uint16_t seq) {
  std::memset(out, 0, kDelbaLength);
  out[0] = 0xd0;
  std::memcpy(out + 4, peer, 6);
  std::memcpy(out + 10, own, 6);
  std::memcpy(out + 16, bssid, 6);
  const uint16_t sc = uint16_t(seq << 4);
  out[22] = uint8_t(sc);
  out[23] = uint8_t(sc >> 8);
  out[24] = 3;
  out[25] = 2;
  const uint16_t params = uint16_t((initiator ? 1U : 0U) << 11 | unsigned(tid & 15U) << 12);
  out[26] = uint8_t(params);
  out[27] = uint8_t(params >> 8);
  out[28] = 39;
  return kDelbaLength;
}

/* A TX Block Ack session is ended once a bulk send is over: nothing waits or
 * is queued for the peer and no aggregate frame went out for this long.
 * While agreed the listener takes group frames only, so the peer's own
 * DELBA (sent when its side times the session out) never arrives, and the
 * next send's frames went out as A-MPDUs the peer no longer accepted:
 * connection refused, then the peer gone until the radio restarted (#4). */
constexpr uint64_t kBaTxIdleEndUS = 1500000;
inline bool ba_tx_session_idle(bool bulk, size_t waiting, unsigned queued, uint64_t last_agg_us, uint64_t agreed_us,
                               uint64_t now_us) {
  if (bulk || waiting || queued) return false;
  const uint64_t since = last_agg_us > agreed_us ? last_agg_us : agreed_us;
  return since && now_us - since >= kBaTxIdleEndUS;
}

inline size_t build_addba_response(uint8_t *out, const AddbaRequest &r, const uint8_t *own, uint16_t seq) {
  std::memset(out, 0, kAddbaResponseLength);
  out[0] = 0xd0;
  std::memcpy(out + 4, r.peer, 6);
  std::memcpy(out + 10, own, 6);
  std::memcpy(out + 16, r.bssid, 6);
  const uint16_t sc = uint16_t(seq << 4);
  out[22] = uint8_t(sc);
  out[23] = uint8_t(sc >> 8);
  out[24] = 3;
  out[25] = 1;
  out[26] = r.token;
  /* 27-28: status 0, success. */
  const uint16_t params = uint16_t(2U | unsigned(r.tid) << 2 | unsigned(r.window) << 6);
  out[29] = uint8_t(params);
  out[30] = uint8_t(params >> 8);
  /* 31-32: timeout 0. */
  return kAddbaResponseLength;
}

/* Promiscuous receive off while a lot of data arrives: with a session open
 * and the filter on, the firmware does not acknowledge the peer's frames. */
constexpr uint64_t kBaFilterCheckUS = 1000000;
constexpr uint32_t kBaFilterBusyFrames = 16, kBaFilterQuietFrames = 8, kBaFilterQuietChecks = 3;
struct BaFilter {
  uint64_t last_check_us = 0;
  uint32_t last_frames = 0, quiet_checks = 0;
  bool off = false;
  /* Returns true when the filter should change; `off` holds the new state. */
  bool note(uint64_t now_us, uint32_t frames) {
    if (!last_check_us) {
      last_check_us = now_us;
      last_frames = frames;
      return false;
    }
    if (now_us - last_check_us < kBaFilterCheckUS) return false;
    const uint32_t delta = frames - last_frames;
    last_check_us = now_us;
    last_frames = frames;
    if (!off) {
      quiet_checks = 0;
      if (delta < kBaFilterBusyFrames) return false;
      off = true;
      return true;
    }
    if (delta >= kBaFilterQuietFrames) {
      quiet_checks = 0;
      return false;
    }
    if (++quiet_checks < kBaFilterQuietChecks) return false;
    quiet_checks = 0;
    off = false;
    return true;
  }
};

/* ------------------------------------------------------- send policy */

/* A failed frame to one station is sent again for 400 ms (the peer may be on
 * another channel for a few slots), with a growing gap. */
constexpr uint64_t kResendWindowUS = 400000;
constexpr unsigned kResendMaxAttempts = 100;
constexpr uint64_t resend_backoff_us(unsigned attempts) {
  return attempts <= 1U ? 0U : attempts == 2U ? 2000U : 5000U;
}

/* Unicast data rate ladder: legacy 24, HT MCS 4, 5, 7 (20 MHz, 1 stream),
 * then VHT 80 MHz 2 streams MCS 4, 7 and 8 (with LDPC + SGI) while on 80 MHz.
 * Down after a window of 32 with over 20% failures, up after one with under 5%. */
constexpr uint8_t kRateMcs[7] = {0, 4, 5, 7, 4, 7, 8};
struct RateLadder {
  unsigned step = 0;
  uint32_t ok = 0, failed = 0, ups = 0, downs = 0;
  void note(bool acked, bool wide80) {
    ++(acked ? ok : failed);
    const uint32_t window = ok + failed;
    if (window < 32) return;
    const unsigned top = wide80 ? 7u : 4u;
    if (failed * 5 > window && step > 0) {
      --step;
      ++downs;
    } else if (failed * 20 < window && step + 1 < top) {
      ++step;
      ++ups;
    }
    if (step >= top) step = top - 1;
    ok = failed = 0;
  }
};


/* ---- Sending with Block Ack (experimental --aggregate), from ForbiddenItlwm's
 * include/AWDLBlockAck.hpp. */

/* Asks `peer` for a session to send to it: immediate, no A-MSDU, no timeout. */
inline size_t build_addba_request(uint8_t *out, const uint8_t *peer, const uint8_t *own, const uint8_t *bssid,
                                  uint8_t token, uint8_t tid, uint16_t window, uint16_t start_seq, uint16_t seq) {
  std::memset(out, 0, kAddbaResponseLength);
  out[0] = 0xd0;
  std::memcpy(out + 4, peer, 6);
  std::memcpy(out + 10, own, 6);
  std::memcpy(out + 16, bssid, 6);
  const uint16_t sc = static_cast<uint16_t>(seq << 4);
  out[22] = static_cast<uint8_t>(sc);
  out[23] = static_cast<uint8_t>(sc >> 8);
  out[24] = 3; /* Block Ack */
  out[25] = 0; /* ADDBA request */
  out[26] = token;
  const uint16_t params = static_cast<uint16_t>(2u | unsigned(tid & 7) << 2 | unsigned(window) << 6);
  out[27] = static_cast<uint8_t>(params);
  out[28] = static_cast<uint8_t>(params >> 8);
  const uint16_t start = static_cast<uint16_t>(start_seq << 4);
  out[31] = static_cast<uint8_t>(start);
  out[32] = static_cast<uint8_t>(start >> 8);
  return kAddbaResponseLength;
}

struct AddbaAnswer {
  uint8_t token, tid;
  uint16_t status, window;
  bool immediate;
};
/* The peer's answer to our request: category 3, action 1, to `own`. */
inline bool parse_addba_answer(const uint8_t *f, size_t length, const uint8_t *own, AddbaAnswer *out) {
  if (length < 33 || f[0] != 0xd0 || std::memcmp(f + 4, own, 6) != 0 || f[24] != 3 || f[25] != 1) return false;
  out->token = f[26];
  out->status = static_cast<uint16_t>(f[27] | f[28] << 8);
  const uint16_t params = static_cast<uint16_t>(f[29] | f[30] << 8);
  out->tid = static_cast<uint8_t>((params >> 2) & 15u);
  out->window = params >> 6;
  out->immediate = params & 2u;
  return true;
}

/* A plain data frame as QoS data on `tid` with our sequence number: the new
 * length, or 0 if `in` is not plain data. */
inline size_t build_qos_data(const uint8_t *in, size_t length, uint8_t tid, uint16_t seq, std::vector<uint8_t> &out) {
  if (length < 24 || in[0] != 0x08) return 0;
  out.assign(in, in + 24);
  out[0] = 0x88;
  const uint16_t sc = static_cast<uint16_t>(seq << 4);
  out[22] = static_cast<uint8_t>(sc);
  out[23] = static_cast<uint8_t>(sc >> 8);
  out.push_back(tid & 7u);
  out.push_back(0);
  out.insert(out.end(), in + 24, in + length);
  return out.size();
}

/* The A-MPDU path only while a bulk send runs: short exchanges such as a
 * connection's setup stay on the monitor queue, where the host resends a
 * frame lost while the peer is away (the firmware's aggregate queue does
 * not, and those stalled through it upstream). Trigger aggressively (24
 * payload frames in 250 ms, ~35 KB) so even medium files enter A-MPDU fast. */
constexpr uint64_t kBaTxCheckUS = 250000;
constexpr uint32_t kBaTxBulkFrames = 24, kBaTxQuietFrames = 8, kBaTxQuietChecks = 4;
struct BaTxBulk {
  uint64_t last_us = 0;
  uint32_t last_frames = 0, quiet_checks = 0;
  bool bulk = false;
  /* `frames` counts payload-sized unicast data frames to the peer. True on a change. */
  bool note(uint64_t now_us, uint32_t frames, bool fast_entry) {
    const bool short_sample = fast_entry && !bulk;
    const uint64_t sample = short_sample ? 100000ULL : kBaTxCheckUS;
    const uint32_t entry = short_sample ? 8u : kBaTxBulkFrames;
    if (!last_us) {
      last_us = now_us;
      last_frames = frames;
      return false;
    }
    if (now_us - last_us < sample) return false;
    const uint32_t delta = frames - last_frames;
    last_us = now_us;
    last_frames = frames;
    if (!bulk) {
      quiet_checks = 0;
      if (delta < entry) return false;
      bulk = true;
      return true;
    }
    if (delta >= kBaTxQuietFrames) {
      quiet_checks = 0;
      return false;
    }
    if (++quiet_checks < kBaTxQuietChecks) return false;
    quiet_checks = 0;
    bulk = false;
    return true;
  }
};

/* Experimental A-MPDU negotiation bounds (#4): ask every 100 ms up to 20 asks
 * (or 5 s wall clock), then fall back cleanly to the single-frame queue and
 * retry after 60 s instead of looping in `asking` indefinitely. */
constexpr uint64_t kBaTxAskIntervalUS = 100000;
constexpr unsigned kBaTxAskMax = 20;
constexpr uint64_t kBaTxAskTimeoutUS = 5000000;
constexpr uint64_t kBaTxFailBackoffUS = 60000000;

/* Experimental A-MPDU send fallback (#4): if the peer agreed and we have fed at
 * least two full 64-frame Block Ack windows with zero Block Ack notifications
 * and >= 64 single-frame completions, the firmware is not aggregating on this
 * queue; fall back to the single-frame queue instead of crawling at ~40 KB/s. */
constexpr uint64_t kBaTxNoBaFallbackFrames = 128;
constexpr uint64_t kBaTxNoBaFallbackSingles = 64;
inline bool ba_tx_stalled_as_singles(uint64_t frames, uint64_t notifs, uint64_t singles) {
  return notifs == 0 && frames >= kBaTxNoBaFallbackFrames && singles >= kBaTxNoBaFallbackSingles;
}

} // namespace awdl
