// SPDX-License-Identifier: GPL-2.0-only
#include "awdl_policy.h"

#undef NDEBUG
#include <cassert>
#include <cstdio>
#include <cstring>
#include <vector>

namespace {

std::vector<uint8_t> make_awdl_tcp_frame(uint16_t src_port, uint16_t dst_port, uint8_t flags,
                                         uint32_t seq, uint32_t ack, size_t payload_len) {
  const size_t tcp_len = 20 + payload_len;
  std::vector<uint8_t> f(24 + 16 + 40 + tcp_len, 0);
  f[0] = 0x08; /* Data, unicast */
  f[4] = 0x02; /* unicast dst */
  f[10] = 0x06;
  const uint8_t bssid[6] = {0x00, 0x25, 0x00, 0xff, 0x94, 0x73};
  std::memcpy(f.data() + 16, bssid, 6);
  const uint8_t snap[8] = {0xaa, 0xaa, 3, 0, 0x17, 0xf2, 8, 0};
  std::memcpy(f.data() + 24, snap, 8);
  f[24 + 14] = 0x86;
  f[24 + 15] = 0xdd;
  uint8_t *ip = f.data() + 40;
  ip[0] = 0x60;
  ip[4] = static_cast<uint8_t>(tcp_len >> 8);
  ip[5] = static_cast<uint8_t>(tcp_len & 0xff);
  ip[6] = 6; /* TCP */
  uint8_t *tcp = ip + 40;
  tcp[0] = static_cast<uint8_t>(src_port >> 8);
  tcp[1] = static_cast<uint8_t>(src_port & 0xff);
  tcp[2] = static_cast<uint8_t>(dst_port >> 8);
  tcp[3] = static_cast<uint8_t>(dst_port & 0xff);
  tcp[4] = static_cast<uint8_t>(seq >> 24);
  tcp[5] = static_cast<uint8_t>(seq >> 16);
  tcp[6] = static_cast<uint8_t>(seq >> 8);
  tcp[7] = static_cast<uint8_t>(seq);
  tcp[8] = static_cast<uint8_t>(ack >> 24);
  tcp[9] = static_cast<uint8_t>(ack >> 16);
  tcp[10] = static_cast<uint8_t>(ack >> 8);
  tcp[11] = static_cast<uint8_t>(ack);
  tcp[12] = 5u << 4;
  tcp[13] = flags;
  return f;
}

void test_chanseq_encodings() {
  const uint8_t expected[16] = {48, 0, 149, 149, 149, 6, 48, 48, 6, 149, 149, 0, 149, 0, 48, 48};

  // SIMPLE encoding (0): 1 byte per slot
  {
    std::vector<uint8_t> frame(40 + 3 + 22, 0);
    frame[0] = 0xd0;
    frame[40] = 18;
    frame[41] = 22;
    frame[43] = 15; /* 16 slots */
    frame[44] = 0;  /* SIMPLE */
    std::memcpy(frame.data() + 49, expected, 16);
    uint8_t out[16]{};
    assert(awdl::peer_sequence(frame.data(), frame.size(), out));
    assert(std::memcmp(out, expected, 16) == 0);
  }

  // LEGACY encoding (1): [flags, chan] per slot
  {
    std::vector<uint8_t> frame(40 + 3 + 38, 0);
    frame[0] = 0xd0;
    frame[40] = 18;
    frame[41] = 38;
    frame[43] = 15;
    frame[44] = 1; /* LEGACY */
    for (unsigned i = 0; i < 16; ++i) {
      frame[49 + 2 * i] = 0xaa;
      frame[49 + 2 * i + 1] = expected[i];
    }
    uint8_t out[16]{};
    assert(awdl::peer_sequence(frame.data(), frame.size(), out));
    assert(std::memcmp(out, expected, 16) == 0);
  }

  // OPCLASS encoding (3): [chan, opclass] per slot
  {
    std::vector<uint8_t> frame(40 + 3 + 38, 0);
    frame[0] = 0xd0;
    frame[40] = 18;
    frame[41] = 38;
    frame[43] = 15;
    frame[44] = 3; /* OPCLASS */
    for (unsigned i = 0; i < 16; ++i) {
      frame[49 + 2 * i] = expected[i];
      frame[49 + 2 * i + 1] = 128;
    }
    uint8_t out[16]{};
    assert(awdl::peer_sequence(frame.data(), frame.size(), out));
    assert(std::memcmp(out, expected, 16) == 0);
  }
}

void test_adjacent_slot_window() {
  using Clock = awdl::PeerTxWindow::Clock;
  constexpr auto period = std::chrono::microseconds(64 * 1024);
  const Clock::time_point t0 = Clock::now();

  awdl::PeerTxWindow w;
  w.sampled = t0;
  w.remaining = period; /* right at start of slot 2 */
  w.slot = 2;
  w.has_seq = true;
  w.seq_updated = t0;
  const uint8_t seq[16] = {48, 0, 149, 149, 149, 6, 48, 48, 6, 149, 0, 0, 0, 0, 48, 48};
  std::memcpy(w.channels, seq, 16);

  // Start of slot 2 (prev slot 1 = 0 != 149): blocked in first 2 ms, allowed at 4 ms
  assert(!w.can_send(t0 + std::chrono::milliseconds(2), 149));
  assert(w.can_send(t0 + std::chrono::milliseconds(4), 149));

  // Boundary between slot 2 (149) and slot 3 (149): NO dark gap!
  assert(w.can_send(t0 + period - std::chrono::milliseconds(1), 149));
  assert(w.can_send(t0 + period + std::chrono::milliseconds(1), 149));

  // End of slot 4 (next slot 5 = 6 != 149): blocked in last 3 ms!
  assert(!w.can_send(t0 + 3 * period - std::chrono::milliseconds(3), 149));
  // Off-channel slot 5 (ch 6): blocked for 149!
  assert(!w.can_send(t0 + 3 * period + std::chrono::milliseconds(20), 149));

  // Separate MIF (TLV 18 only) followed >30s later by PSF (TLV 4 only) stays valid!
  std::vector<uint8_t> mif(40 + 3 + 22, 0);
  mif[0] = 0xd0; mif[40] = 18; mif[41] = 22; mif[43] = 15; mif[44] = 0;
  std::memcpy(mif.data() + 49, seq, 16);
  std::vector<uint8_t> psf(40 + 3 + 36, 0);
  psf[0] = 0xd0; psf[40] = 4; psf[41] = 36;
  psf[43 + 1] = 64; /* remaining = 64 TU */
  psf[43 + 5] = 16; /* aw_period = 16 */
  psf[43 + 27] = 4; /* presence_mode = 4 */
  psf[43 + 29] = 8; /* counter = 8 -> slot 2 */
  awdl::PeerTxWindow split;
  assert(!split.note_frame(mif.data(), mif.size(), t0));
  const auto t1 = t0 + std::chrono::seconds(45);
  assert(split.note_frame(psf.data(), psf.size(), t1));
  assert(split.valid(t1 + std::chrono::milliseconds(10)));
  assert(split.can_send(t1 + std::chrono::milliseconds(10), 149));
}

void test_home_screen_doze_gate() {
  using Clock = awdl::PeerTxWindow::Clock;
  constexpr auto period = std::chrono::microseconds(64 * 1024);
  constexpr auto aw = period / 4; /* 16.384 ms */
  const Clock::time_point t0 = Clock::now();

  // Exact Home-screen iPad Infra-Priority sequence from air-fed8d67a/awdl.log:
  // only 2 slots (slots 2 and 10) on channel 149 -> present_entries == 2 <= kPeerDozeMaxEntries (5).
  const uint8_t infra_seq[16] = {48, 0, 149, 0, 0, 0, 0, 0, 6, 0, 149, 0, 0, 0, 0, 0};
  awdl::PeerTxWindow w;
  w.sampled = t0;
  w.remaining = period;
  w.slot = 2;
  w.has_seq = true;
  std::memcpy(w.channels, infra_seq, 16);

  assert(w.present_entries(149) == 2);
  assert(w.is_dozing(t0 + std::chrono::milliseconds(2), 149));

  // While dozing: open at start of AW 0 (2 ms) and AW 1 (aw + 1 ms), closed in Doze tail (8 ms)!
  assert(w.can_send(t0 + std::chrono::milliseconds(2), 149, true));
  assert(!w.can_send(t0 + std::chrono::milliseconds(8), 149, true));
  assert(w.can_send(t0 + aw + std::chrono::milliseconds(1), 149, true));
  assert(!w.can_send(t0 + aw + std::chrono::milliseconds(9), 149, true));

  // Once peer sends us unicast data without PM bit, full EAW opens for 100 ms!
  w.note_data_rx(t0 + std::chrono::milliseconds(2), false);
  assert(!w.is_dozing(t0 + std::chrono::milliseconds(8), 149));
  assert(w.can_send(t0 + std::chrono::milliseconds(8), 149, true));
  assert(w.can_send(t0 + aw + std::chrono::milliseconds(9), 149, true));

  // If peer sets 802.11 Power-Save bit, Doze gate re-engages immediately.
  w.note_data_rx(t0 + std::chrono::milliseconds(10), true);
  assert(w.is_dozing(t0 + std::chrono::milliseconds(11), 149));
  assert(!w.can_send(t0 + aw + std::chrono::milliseconds(9), 149, true));
}

void test_zombie_tcp_ack_purge() {
  awdl::TcpAckTracker tracker;
  // Replay exact zombie sequence range from air-5d4b48d8/radio.log:
  // Outbound PC:51422 -> iPad:8770 segments 4122037700 .. 4122054380 (1390 B each),
  // followed by iPad cumulative ACK = 4122057160.
  const auto seg_old = make_awdl_tcp_frame(51422, 8770, 0x10, 4122037700u, 1000u, 1390);
  const auto seg_mid = make_awdl_tcp_frame(51422, 8770, 0x18, 4122054380u, 1000u, 1390);
  const auto seg_unacked = make_awdl_tcp_frame(51422, 8770, 0x10, 4122057160u, 1000u, 1390);
  const auto pure_ack = make_awdl_tcp_frame(51422, 8770, 0x10, 4122057160u, 1000u, 0);
  const auto fin_seg = make_awdl_tcp_frame(51422, 8770, 0x11, 4122054380u, 1000u, 100);

  assert(!tracker.is_outbound_acked(seg_old.data(), seg_old.size()));

  const auto peer_ack = make_awdl_tcp_frame(8770, 51422, 0x10, 1000u, 4122057160u, 0);
  assert(tracker.note_peer_rx(peer_ack.data(), peer_ack.size()));

  assert(tracker.is_outbound_acked(seg_old.data(), seg_old.size()));
  assert(tracker.is_outbound_acked(seg_mid.data(), seg_mid.size()));
  assert(!tracker.is_outbound_acked(seg_unacked.data(), seg_unacked.size()));
  assert(!tracker.is_outbound_acked(pure_ack.data(), pure_ack.size()));
  assert(!tracker.is_outbound_acked(fin_seg.data(), fin_seg.size()));

  // Wraparound across 2^32 boundary
  const auto wrap_seg = make_awdl_tcp_frame(60000, 8770, 0x10, 0xfffffc00u, 500u, 1390);
  const uint32_t wrap_end = 0xfffffc00u + 1390u; /* wraps to 0x16e */
  const auto wrap_ack = make_awdl_tcp_frame(8770, 60000, 0x10, 500u, wrap_end + 1390u, 0);
  assert(tracker.note_peer_rx(wrap_ack.data(), wrap_ack.size()));
  assert(tracker.is_outbound_acked(wrap_seg.data(), wrap_seg.size()));
}

void test_window_inflight_tapering() {
  using Clock = awdl::PeerTxWindow::Clock;
  constexpr auto period = std::chrono::microseconds(64 * 1024);
  const Clock::time_point t0 = Clock::now();

  awdl::PeerTxWindow uninit;
  assert(uninit.recommended_inflight(t0, 149, 32) == 32);

  awdl::PeerTxWindow w;
  w.sampled = t0;
  w.remaining = period;
  w.slot = 2;
  w.has_seq = true;
  const uint8_t seq[16] = {48, 0, 149, 149, 6, 6, 48, 48, 6, 149, 0, 0, 0, 0, 48, 48};
  std::memcpy(w.channels, seq, 16);

  // Start of 2-slot run (slots 2..3 = ~131 ms total): full 32 slots allowed once past 3 ms guard
  assert(w.recommended_inflight(t0 + std::chrono::milliseconds(4), 149, 32) == 32);
  // Across slot 2 -> slot 3 boundary: still > 60 ms left -> full 32 slots
  assert(w.recommended_inflight(t0 + period, 149, 32) == 32);
  // 16 ms before end of slot 3 -> 12 ms remaining before 4 ms end_guard -> tapers to 16 slots
  assert(w.recommended_inflight(t0 + 2 * period - std::chrono::milliseconds(16), 149, 32) == 16);
  // 11 ms before end of slot 3 -> 7 ms remaining -> tapers to 6 slots
  assert(w.recommended_inflight(t0 + 2 * period - std::chrono::milliseconds(11), 149, 32) == 6);
  // 6 ms before end of slot 3 -> 2 ms remaining -> tapers to 2 slots
  assert(w.recommended_inflight(t0 + 2 * period - std::chrono::milliseconds(6), 149, 32) == 2);
  // Inside 4 ms end_guard -> 0 slots
  assert(w.recommended_inflight(t0 + 2 * period - std::chrono::milliseconds(2), 149, 32) == 0);
}

void test_dual_channel_slot_following() {
  using Clock = awdl::PeerTxWindow::Clock;
  constexpr auto period = std::chrono::microseconds(64 * 1024);
  const Clock::time_point t0 = Clock::now();

  awdl::PeerTxWindow w;
  w.sampled = t0;
  w.remaining = period;
  w.slot = 0;
  w.has_seq = true;
  // Exact live iPad sequence when associated to ch 48 AP while AWDL primary is ch 149:
  const uint8_t seq[16] = {149, 149, 149, 149, 48, 48, 48, 48, 6, 149, 149, 149, 48, 48, 48, 48};
  std::memcpy(w.channels, seq, 16);

  uint8_t away[16] = {};
  assert(w.secondary_5g_channel(149, away) == 48);
  assert(away[0] == 0 && away[3] == 0 && away[4] == 1 && away[7] == 1 && away[8] == 0 && away[12] == 1);

  // Slot 0..3 stay on 149 until 3 ms before slot 4
  assert(w.desired_channel_at(t0 + std::chrono::milliseconds(10), 149, 48) == 149);
  assert(w.desired_channel_at(t0 + 4 * period - std::chrono::milliseconds(2), 149, 48) == 48);
  // Slot 4..7 follow onto 48
  assert(w.desired_channel_at(t0 + 5 * period, 149, 48) == 48);
  // Slot 8 is 2.4 GHz social ch 6 -> immediately returns to primary 149 ahead of slot 9
  assert(w.desired_channel_at(t0 + 8 * period + std::chrono::milliseconds(5), 149, 48) == 149);

  // DFS channels (52..144) must NEVER be selected for autonomous slot following
  const uint8_t dfs_seq[16] = {149, 149, 149, 149, 100, 100, 100, 100, 6, 149, 149, 149, 100, 100, 100, 100};
  std::memcpy(w.channels, dfs_seq, 16);
  assert(w.secondary_5g_channel(149, away) == 0);
}

void test_rfc1042_wrapped_awdl_tcp() {
  std::vector<uint8_t> f(26 + 8 + 16 + 60, 0);
  f[0] = 0x88; // QoS Data
  f[4] = 0x02; // unicast RA
  // 8-byte iOS TX A-MSDU RFC1042 bridge wrapper around 76-byte inner AWDL LLC/SNAP+Data+IPv6+TCP
  const uint8_t rfc1042[8] = {0xaa, 0xaa, 0x03, 0x00, 0x00, 0x00, 0x00, 76};
  const uint8_t awdl_snap[8] = {0xaa, 0xaa, 0x03, 0x00, 0x17, 0xf2, 0x08, 0x00};
  std::memcpy(f.data() + 26, rfc1042, 8);
  std::memcpy(f.data() + 34, awdl_snap, 8);
  f[34 + 14] = 0x86;
  f[34 + 15] = 0xdd;
  uint8_t *ip = f.data() + 34 + 16;
  ip[0] = 0x60;
  ip[4] = 0x00;
  ip[5] = 20; // TCP length 20
  ip[6] = 6;  // Next Header = TCP
  uint8_t *tcp = ip + 40;
  tcp[0] = 0x22; tcp[1] = 0x42; // src 8770
  tcp[2] = 0xc0; tcp[3] = 0x01; // dst 49153
  tcp[8] = 0x00; tcp[9] = 0x02; tcp[10] = 0x00; tcp[11] = 0x00; // ack
  tcp[12] = 0x50; // data offset 20B
  tcp[13] = 0x10; // ACK flag
  awdl::TcpSegment seg{};
  assert(awdl::parse_awdl_tcp(f.data(), f.size(), seg));
  assert(seg.source == 0x2242 && seg.destination == 0xc001 && seg.acknowledgment == 0x00020000u);

  // Also test upstream v2 full-802.3 wrapped variant (skip = 22: RFC1042 + DA + SA + len + AWDL SNAP)
  std::vector<uint8_t> f2(26 + 22 + 16 + 60, 0);
  f2[0] = 0x88;
  f2[4] = 0x02;
  const uint8_t rfc1042_v2[8] = {0xaa, 0xaa, 0x03, 0x00, 0x00, 0x00, 0x00, 14 + 76};
  std::memcpy(f2.data() + 26, rfc1042_v2, 8);
  f2[26 + 20] = 0x00; f2[26 + 21] = 76;
  std::memcpy(f2.data() + 48, awdl_snap, 8);
  std::memcpy(f2.data() + 48 + 8, f.data() + 34 + 8, 8 + 60);
  awdl::TcpSegment seg2{};
  assert(awdl::parse_awdl_tcp(f2.data(), f2.size(), seg2));
  assert(seg2.source == 0x2242 && seg2.destination == 0xc001 && seg2.acknowledgment == 0x00020000u);
}

} // namespace

int main() {
  test_chanseq_encodings();
  test_adjacent_slot_window();
  test_home_screen_doze_gate();
  test_zombie_tcp_ack_purge();
  test_window_inflight_tapering();
  test_dual_channel_slot_following();
  test_rfc1042_wrapped_awdl_tcp();
  std::printf("PASS: all AWDL schedule, Doze-gate, inflight-tapering, slot-following & TCP zombie-purge tests succeeded\n");
  return 0;
}
