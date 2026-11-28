// SPDX-License-Identifier: GPL-2.0-only
/*
 * AWDL radio policy, ported from ForbiddenItlwm 0.6.0 (retropineaple,
 * GPL-2.0): include/AWDLBlockAck.hpp, include/AWDLProbePolicy.hpp,
 * include/AWDLChannels.hpp and the awdlCoex* helpers in ItlIwx.cpp. The logic
 * and constants are upstream's; the names are shortened and the macOS state
 * around them is replaced by plain structs.
 */
#pragma once
#include "awdl_fcs.h"
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

/* The peer's 16-slot channel sequence (TLV 18) from an AWDL action frame
 * without FCS: one channel number per slot. */
inline bool peer_sequence(const uint8_t *frame, size_t length, uint8_t out[16]) {
  if (!frame || length < 40) return false;
  for (size_t i = 40; i + 3 <= length;) {
    const size_t value = size_t(frame[i + 1]) | size_t(frame[i + 2]) << 8;
    if (i + 3 + value > length) return false;
    if (frame[i] == 18) {
      if (value < 6 + 32) return false;
      for (unsigned n = 0; n < 16; ++n) out[n] = frame[i + 3 + 6 + 2 * n];
      return true;
    }
    i += 3 + value;
  }
  return false;
}

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
  bool amsdu;
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
  out->amsdu = (params & 1U) != 0;
  out->immediate = (params & 2U) != 0;
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
constexpr uint64_t kBaTxIdleEndUS = 2500000;
inline bool ba_tx_session_idle(bool bulk, size_t waiting, unsigned queued, uint64_t last_agg_us, uint64_t agreed_us,
                               uint64_t now_us) {
  if (bulk || waiting || queued) return false;
  const uint64_t since = last_agg_us > agreed_us ? last_agg_us : agreed_us;
  return since && now_us - since >= kBaTxIdleEndUS;
}

/* Status 37 (REQUEST_DECLINED): the peer sends unaggregated and does not
 * keep asking, unlike a request left unanswered. */
constexpr uint16_t kAddbaDeclined = 37;
inline size_t build_addba_response(uint8_t *out, const AddbaRequest &r, const uint8_t *own, uint16_t seq,
                                   bool allow_amsdu = false, uint16_t status = 0) {
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
  out[27] = uint8_t(status);
  out[28] = uint8_t(status >> 8);
  const uint16_t amsdu_bit = (allow_amsdu && r.amsdu) ? 1U : 0U;
  const uint16_t params = uint16_t(amsdu_bit | 2U | unsigned(r.tid) << 2 | unsigned(r.window) << 6);
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

/* A failed frame to one station is sent again for 600 ms (>2 full 16-slot
 * AWDL cycles, so a 2/16-slot peer that skips one AW still gets TCP SYN /
 * mDNS QU replies, #30), with a growing gap. */
constexpr uint64_t kResendWindowUS = 600000;
constexpr unsigned kResendMaxAttempts = 120;
constexpr uint64_t kTxStatusTimeoutUS = 1000000;
constexpr uint64_t resend_backoff_us(unsigned attempts) {
  return attempts <= 1U ? 0U : attempts == 2U ? 2000U : 5000U;
}

/* A completion can arrive out of order. An already finished entry may still
 * sit behind an older frame until the queue front advances. */
struct UnicastRetry {
  int slot = -1;
  bool acked = false, abandon = false;
  uint64_t first_us = 0, sent_us = 0, next_us = 0;
  unsigned attempts = 0;

  constexpr bool finished() const { return acked || abandon; }
  constexpr bool ready(uint64_t now) const {
    return !finished() && slot < 0 && now >= next_us;
  }
  constexpr bool expired(uint64_t now) const {
    return attempts && (now - first_us >= kResendWindowUS || attempts >= kResendMaxAttempts);
  }
  constexpr bool status_missing(uint64_t now) const {
    return slot >= 0 && now - sent_us >= kTxStatusTimeoutUS;
  }
};

/* Unicast data rate ladder: legacy 24, HT MCS 4, 5, 7 (20 MHz, 1 stream),
 * then VHT 80 MHz 2 streams MCS 4, 7 and 8 (with LDPC + SGI) while on 80 MHz.
 * Down after a window of 16 with over 20% failures (or immediately after 4
 * consecutive failures / an abandoned frame, #91), up after one with under 5%. */
constexpr uint8_t kRateMcs[7] = {0, 4, 5, 7, 4, 7, 8};
struct RateLadder {
  unsigned step = 0;
  uint32_t ok = 0, failed = 0, consec_fail = 0, ups = 0, downs = 0;
  void note(bool acked, bool wide80) {
    const unsigned top = wide80 ? 7u : 4u;
    if (acked) {
      ++ok;
      consec_fail = 0;
    } else {
      ++failed;
      if (++consec_fail >= 4 && step > 0) {
        --step;
        ++downs;
        ok = failed = consec_fail = 0;
        return;
      }
    }
    const uint32_t window = ok + failed;
    if (window < 16) return;
    if (failed * 5 > window && step > 0) {
      --step;
      ++downs;
    } else if (failed * 20 < window && step + 1 < top) {
      ++step;
      ++ups;
    }
    if (step >= top) step = top - 1;
    ok = failed = consec_fail = 0;
  }
  /* A unicast data frame timed out across all retries: drop quickly so a
   * stalled link (e.g. TCP ACKs failing at VHT80 MCS 8, #91) recovers
   * immediately instead of waiting for 16 first-attempt frames. */
  void note_give_up() {
    if (step > 0) {
      step = step > 2 ? step - 2 : 0u;
      ++downs;
    }
    ok = failed = consec_fail = 0;
  }
};

/* On 80 MHz with strong close-range RSSI from the peer's AWDL sync frames,
 * seed the rate ladder directly into VHT80 2SS (step 4 = MCS 4 at >= -54 dBm,
 * step 5 = MCS 7 at >= -46 dBm) so the very first transfer starts fast;
 * weak signal or 20/40 MHz keeps the conservative base_step. */
inline unsigned rssi_initial_rate_step(int dbm, bool wide80, unsigned base_step) {
  if (!wide80 || dbm <= -120) return base_step;
  unsigned want = base_step;
  if (dbm >= -46) want = 5u;
  else if (dbm >= -54) want = 4u;
  return want > base_step ? want : base_step;
}

/* Per-retry rate fallback (#91): when a unicast data frame (such as a TCP ACK)
 * fails at a high MCS, step down on retry 1 and fall back to robust Legacy 24
 * (step 0) on retry 2+ so brief SNR dips never stall a transfer into HTTP 408. */
inline unsigned retry_rate_step(unsigned current_step, unsigned attempts) {
  if (attempts == 0) return current_step;
  if (attempts == 1) return current_step > 2 ? current_step - 2 : 0u;
  return 0u;
}


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
 * not, and those stalled through it upstream). Trigger immediately once 6
 * payload frames (~9 KB; 4 frames / ~6 KB if already agreed) arrive within
 * 100 ms so the very first file upload enters A-MPDU in a few milliseconds. */
constexpr uint64_t kBaTxCheckUS = 250000;
constexpr uint64_t kBaTxEntryCheckUS = 100000;
constexpr uint32_t kBaTxBulkFrames = 6, kBaTxQuietFrames = 8, kBaTxQuietChecks = 4;
struct BaTxBulk {
  uint64_t last_us = 0;
  uint32_t last_frames = 0, quiet_checks = 0;
  bool bulk = false;
  /* `frames` counts payload-sized unicast data frames to the peer. True on a change. */
  bool note(uint64_t now_us, uint32_t frames, bool fast_entry) {
    const uint64_t sample = !bulk ? kBaTxEntryCheckUS : kBaTxCheckUS;
    const uint32_t entry = fast_entry ? 4u : kBaTxBulkFrames;
    if (!last_us) {
      last_us = now_us;
      last_frames = frames;
      return false;
    }
    const uint32_t delta = frames - last_frames;
    if (!bulk && delta >= entry) {
      last_us = now_us;
      last_frames = frames;
      quiet_checks = 0;
      bulk = true;
      return true;
    }
    if (now_us - last_us < sample) return false;
    last_us = now_us;
    last_frames = frames;
    if (!bulk) {
      quiet_checks = 0;
      return false;
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

/* A card whose firmware never reports Block Acks (#39, AX210 family) used to
 * stop asking until the radio restarted. That also punished the case where the
 * first stall was transient: a channel change, a peer that rejoined or a
 * different peer can make aggregation work later in the same session. After the
 * second stall, back off for five minutes instead of forever, and let a session
 * that really produced Block Acks clear the count (radio_main.cpp resets it when
 * it ends its own session after a finished bulk send). */
constexpr uint32_t kBaTxStallLimit = 2;
constexpr uint64_t kBaTxLongBackoffUS = 300000000;
inline uint64_t ba_tx_retry_us(uint32_t stalls, uint64_t now_us) {
  return now_us + (stalls >= kBaTxStallLimit ? kBaTxLongBackoffUS : kBaTxFailBackoffUS);
}

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
