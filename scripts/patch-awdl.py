# Applies nekodrop's changes to winawdl's awdld sources before they are built.
# Usage: python patch-awdl.py <winawdl folder>
#
# - awdl0 is retried when Wintun says it already exists (#30).  The service
#   restarts awdld whenever the radio restarts (a channel move, a Wi-Fi join),
#   and the old awdld is killed, so its Wintun adapter (same fixed GUID) can
#   still be on its way out when the new one asks for it.  CreateAdapter then
#   fails with ERROR_ALREADY_EXISTS (183) and awdld runs on without a host
#   interface: every AWDL data frame is dropped, so the iPhone sees the PC but
#   no AirDrop gets through until the next restart.  Waiting for the old one
#   to go fixes it; other errors fail at once, as before.
# - Experimental high-throughput flags (-H for Apple AWDL TLV 12 Wi-Fi 6 HE80
#   capability on 5 GHz 80 MHz; -M <mtu> for Wintun MTU up to 1500, inspired by
#   YiHoooong/owly feat/windows-mtkradio, #65). Both stay off/1450 unless passed.
# - OWL RX safety fixes: bounded 512-entry A-MSDU frame output vector, exact
#   Ethernet frame allocation + write check, unconditional cleanup on partial
#   A-MSDU decode failure, malloc NULL checks, and initialized rssi/flags.
#
# Idempotent; exits with an error when the source no longer matches.
import pathlib
import sys

root = pathlib.Path(sys.argv[1])


def patch_once(path: pathlib.Path, marker: str, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding='utf-8')
    if marker in text:
        print(f'{path.name}: {label} already patched')
        return
    if text.count(old) != 1:
        sys.exit(f'{path.name}: anchor for {label} moved; update patch-awdl.py')
    path.write_text(text.replace(old, new, 1), encoding='utf-8')
    print(f'{path.name}: {label}')


# 1. host_if.c: retry CreateAdapter while previous awdld adapter is closing (#30)
host_if_c = root / 'awdl' / 'win' / 'host_if.c'
patch_once(
    host_if_c,
    'nekodrop (#30)',
    '\thost->adapter = host->api.CreateAdapter(ADAPTER_NAME, TUNNEL_TYPE, &adapter_guid);\n',
    '\n'.join([
        '\t/* nekodrop (#30): the previous awdld\'s adapter may still be going away. */',
        '\tfor (int tries = 0;; tries++) {',
        '\t\thost->adapter = host->api.CreateAdapter(ADAPTER_NAME, TUNNEL_TYPE, &adapter_guid);',
        '\t\tif (host->adapter || GetLastError() != ERROR_ALREADY_EXISTS || tries >= 40)',
        '\t\t\tbreak;',
        '\t\tSleep(250);',
        '\t}',
        '',
    ]),
    'awdl0 CreateAdapter retry',
)

# 2. host_if.h & host_if.c: configurable Wintun IPv6 MTU (-M 1500, default 1450)
host_if_h = root / 'awdl' / 'win' / 'host_if.h'
patch_once(
    host_if_h,
    'host_if_set_mtu',
    'struct host_if *host_if_open(const uint8_t address[16], char *error, size_t error_len);\n',
    '/* Optional experimental MTU override (clamped 1280..1500; default 1450). */\n'
    'void host_if_set_mtu(unsigned long mtu);\n'
    'struct host_if *host_if_open(const uint8_t address[16], char *error, size_t error_len);\n',
    'host_if_set_mtu declaration',
)
patch_once(
    host_if_c,
    'host_if_set_mtu',
    '#define HOST_MTU 1450\n',
    '#define HOST_MTU 1450\n'
    'static unsigned long host_mtu = HOST_MTU;\n'
    'void host_if_set_mtu(unsigned long mtu) {\n'
    '\tif (mtu < 1280) mtu = 1280;\n'
    '\tif (mtu > 1500) mtu = 1500;\n'
    '\thost_mtu = mtu;\n'
    '}\n',
    'host_if_set_mtu definition',
)
patch_once(
    host_if_c,
    'row.NlMtu = host_mtu;',
    '\trow.NlMtu = HOST_MTU;\n',
    '\trow.NlMtu = host_mtu;\n',
    'configure_ip6 uses host_mtu',
)

# 3. frame_fixup.h & frame_fixup.c: optional AWDL TLV 12 Wi-Fi 6 HE80 capability (-H)
frame_fixup_h = root / 'awdl' / 'win' / 'frame_fixup.h'
frame_fixup_c = root / 'awdl' / 'win' / 'frame_fixup.c'
patch_once(
    frame_fixup_h,
    'frame_fixup_set_he',
    'void frame_fixup_announce(int two_streams, int short_guard_interval, int width_mhz);\n',
    'void frame_fixup_announce(int two_streams, int short_guard_interval, int width_mhz);\n'
    '/* Optional experimental Wi-Fi 6 HE80 flag in AWDL Data Path State TLV (12). */\n'
    'void frame_fixup_set_he(int enable);\n',
    'frame_fixup_set_he declaration',
)
patch_once(
    frame_fixup_c,
    'announce_he',
    'static int announce_width_mhz = 20;\n',
    'static int announce_width_mhz = 20;\n'
    'static int announce_he;\n'
    '#define TLV_DATA_PATH_STATE 12\n'
    'void frame_fixup_set_he(int enable) {\n'
    '\tannounce_he = enable ? 1 : 0;\n'
    '}\n',
    'frame_fixup_set_he definition',
)
patch_once(
    frame_fixup_c,
    'dp_val[14] |= 0x01u;',
    '\t/* Only a frame that ends on a TLV, as OWL\'s do, can take one more. */\n'
    '\tif (announce_width_mhz < 80 || fcs_len || end != length || length + VHT_TLV_LEN > capacity)\n'
    '\t\treturn length;\n'
    '\twrite_vht_tlv(frame + length);\n'
    '\treturn length + VHT_TLV_LEN;\n',
    '\t/* Experimental Wi-Fi 6 HE80 flag in Data Path State TLV (12): ext_flags |= 0x0100\n'
    '\t * (bit 0 of high byte at TLV value offset 14 when flags bit 0x8000 is set, #65). */\n'
    '\tif (announce_he && announce_width_mhz >= 80) {\n'
    '\t\tint dp = -1;\n'
    '\t\twalk_tlvs(frame, offset, length - fcs_len, TLV_DATA_PATH_STATE, &dp);\n'
    '\t\tif (dp >= 0) {\n'
    '\t\t\tint dp_len = (int) read_le16(frame + dp + 1);\n'
    '\t\t\tuint8_t *dp_val = frame + dp + TLV_HEADER_LEN;\n'
    '\t\t\tif (dp_len >= 15 && (read_le16(dp_val) & 0x8000u))\n'
    '\t\t\t\tdp_val[14] |= 0x01u;\n'
    '\t\t}\n'
    '\t}\n'
    '\t/* Only a frame that ends on a TLV, as OWL\'s do, can take one more. */\n'
    '\tif (announce_width_mhz < 80 || fcs_len || end != length || length + VHT_TLV_LEN > capacity)\n'
    '\t\treturn length;\n'
    '\twrite_vht_tlv(frame + length);\n'
    '\treturn length + VHT_TLV_LEN;\n',
    'TLV 12 HE80 ext_flags patch',
)

# 4. main.c: parse -H (HE80) and -M <mtu> CLI flags
main_c = root / 'awdl' / 'win' / 'main.c'
patch_once(
    main_c,
    'int enable_he = 0;',
    '\tint short_guard_interval = 1;\n'
    '\tint option;\n'
    '\twinawdl_join_segments(DEFAULT_JOIN_WAIT_US);\n'
    '\twhile ((option = getopt(argc, argv, "c:nrs:lj:v")) != -1) {\n',
    '\tint short_guard_interval = 1;\n'
    '\tint enable_he = 0;\n'
    '\tunsigned long host_mtu = 1450;\n'
    '\tint option;\n'
    '\twinawdl_join_segments(DEFAULT_JOIN_WAIT_US);\n'
    '\twhile ((option = getopt(argc, argv, "c:nrs:lj:HM:v")) != -1) {\n',
    'main.c getopt -H and -M',
)
patch_once(
    main_c,
    'option == \'H\'',
    '\t\t} else if (option == \'j\') {\n'
    '\t\t\twinawdl_join_segments(atoi(optarg));\n'
    '\t\t} else if (option == \'v\') {\n',
    '\t\t} else if (option == \'j\') {\n'
    '\t\t\twinawdl_join_segments(atoi(optarg));\n'
    '\t\t} else if (option == \'H\') {\n'
    '\t\t\tenable_he = 1;\n'
    '\t\t} else if (option == \'M\') {\n'
    '\t\t\thost_mtu = (unsigned long) atoi(optarg);\n'
    '\t\t\thost_if_set_mtu(host_mtu);\n'
    '\t\t} else if (option == \'v\') {\n',
    'main.c handle -H and -M',
)
patch_once(
    main_c,
    'frame_fixup_set_he(enable_he)',
    '\tframe_fixup_announce(streams == 2, short_guard_interval,\n'
    '\t\t\t     radio_client_width_mhz(winawdl_radio()));\n',
    '\tframe_fixup_announce(streams == 2, short_guard_interval,\n'
    '\t\t\t     radio_client_width_mhz(winawdl_radio()));\n'
    '\tframe_fixup_set_he(enable_he);\n'
    '\tif (enable_he || host_mtu != 1450)\n'
    '\t\tlog_info("experimental high-throughput: HE80=%d, MTU=%lu", enable_he, host_mtu);\n',
    'main.c apply HE80 and log high-throughput',
)

# 5. host_if.c: 8 MiB Wintun ring (2x default) to absorb bursty 500+ Mbit/s AWDL slots
patch_once(
    host_if_c,
    '#define RING_BYTES 0x800000',
    '#define RING_BYTES 0x400000\n',
    '#define RING_BYTES 0x800000 /* nekodrop: 8 MiB Wintun ring for bursty HE80 throughput */\n',
    'host_if.c 8 MiB Wintun ring',
)

# 5b. radio_client.h: raise RADIO_CLIENT_MAX_FRAME to 12800 so 8/12 KiB RX A-MSDUs pass through
radio_client_h = root / 'awdl' / 'win' / 'radio_client.h'
patch_once(
    radio_client_h,
    '#define RADIO_CLIENT_MAX_FRAME 12800',
    '#define RADIO_CLIENT_MAX_FRAME 4096\n',
    '#define RADIO_CLIENT_MAX_FRAME 12800 /* nekodrop: fit 8/12 KiB RX A-MSDUs + radiotap */\n',
    'radio_client.h 12800-byte RX A-MSDU frame buffer',
)

# 6. radio_client.c: lock-free shared-memory SPSC data plane (matches iwxradio/src/radio_shm.h)
#    plus 64 KiB buffered Named Pipe fallback for radios without SHM (e.g. RTL8852CE / MT7902).
radio_client_c = root / 'awdl' / 'win' / 'radio_client.c'
SHM_DEFS = '\n'.join([
    '#define SLOT_COUNT 2048',
    '#define PIPE_READ_BUF 65536',
    '#define RADIO_SHM_MAGIC 0x4E4B5348u',
    '#define RADIO_SHM_VERSION 1u',
    '#define RADIO_SHM_RING_SIZE (4u * 1024u * 1024u)',
    '#define RADIO_SHM_RING_MASK (RADIO_SHM_RING_SIZE - 1u)',
    '#define RADIO_LINK_CAP_SHM_GLOBAL 1u',
    '#define RADIO_LINK_CAP_SHM_LOCAL 2u',
    '',
    'struct radio_shm_ring {',
    '\tvolatile LONG write_pos;',
    '\tvolatile LONG pad0[15];',
    '\tvolatile LONG read_pos;',
    '\tvolatile LONG waiting;',
    '\tvolatile LONG pad1[14];',
    '\tuint8_t data[RADIO_SHM_RING_SIZE];',
    '};',
    '',
    'struct radio_shm_layout {',
    '\tuint32_t magic;',
    '\tuint32_t version;',
    '\tvolatile LONG epoch;',
    '\tvolatile LONG client_ready;',
    '\tuint32_t reserved[12];',
    '\tstruct radio_shm_ring rx;',
    '\tstruct radio_shm_ring tx;',
    '};',
    '_Static_assert(sizeof(struct radio_shm_ring) == 128u + RADIO_SHM_RING_SIZE, "shm ring ABI");',
    '_Static_assert(sizeof(struct radio_shm_layout) == 64u + 2u * sizeof(struct radio_shm_ring), "shm layout ABI");',
    '',
    'static void shm_write_bytes(uint8_t *ring, uint32_t pos, const uint8_t *src, uint32_t n) {',
    '\tuint32_t idx = pos & RADIO_SHM_RING_MASK;',
    '\tuint32_t first = RADIO_SHM_RING_SIZE - idx;',
    '\tif (first > n) first = n;',
    '\tmemcpy(ring + idx, src, first);',
    '\tif (n > first) memcpy(ring, src + first, n - first);',
    '}',
    '',
    'static void shm_read_bytes(const uint8_t *ring, uint32_t pos, uint8_t *dst, uint32_t n) {',
    '\tuint32_t idx = pos & RADIO_SHM_RING_MASK;',
    '\tuint32_t first = RADIO_SHM_RING_SIZE - idx;',
    '\tif (first > n) first = n;',
    '\tmemcpy(dst, ring + idx, first);',
    '\tif (n > first) memcpy(dst + first, ring, n - first);',
    '}',
    '',
])
patch_once(
    radio_client_c,
    'RADIO_SHM_MAGIC',
    '#define SLOT_COUNT 2048\n',
    SHM_DEFS,
    'radio_client.c SHM ring definitions',
)
patch_once(
    radio_client_c,
    'struct radio_shm_layout *shm;',
    '\tvolatile LONG closing;\n\tvoid (*on_disconnect)(void);\n};\n',
    '\tvolatile LONG closing;\n'
    '\tvoid (*on_disconnect)(void);\n'
    '\tuint8_t *pipe_buf;\n'
    '\tDWORD pipe_pos, pipe_len;\n'
    '\tHANDLE shm_map;\n'
    '\tHANDLE shm_rx_event;\n'
    '\tHANDLE shm_tx_event;\n'
    '\tstruct radio_shm_layout *shm;\n'
    '};\n',
    'radio_client.c SHM & pipe buffer fields',
)
patch_once(
    radio_client_c,
    'client->pipe_buf',
    'static BOOL read_exact(struct radio_client *client, void *data, DWORD length) {\n'
    '\tuint8_t *bytes = data;\n'
    '\twhile (length > 0) {\n'
    '\t\tOVERLAPPED io = {.hEvent = client->read_done};\n'
    '\t\tDWORD got = 0;\n'
    '\t\tBOOL started = ReadFile(client->pipe, bytes, length, &got, &io);\n'
    '\t\tif (!finish(client, &io, started, &got) || got == 0)\n'
    '\t\t\treturn FALSE;\n'
    '\t\tbytes += got;\n'
    '\t\tlength -= got;\n'
    '\t}\n'
    '\treturn TRUE;\n'
    '}\n',
    'static BOOL read_exact(struct radio_client *client, void *data, DWORD length) {\n'
    '\tuint8_t *bytes = data;\n'
    '\twhile (length > 0) {\n'
    '\t\tif (client->pipe_pos == client->pipe_len) {\n'
    '\t\t\tOVERLAPPED io = {.hEvent = client->read_done};\n'
    '\t\t\tDWORD got = 0;\n'
    '\t\t\tBOOL started = ReadFile(client->pipe, client->pipe_buf, PIPE_READ_BUF, &got, &io);\n'
    '\t\t\tif (!finish(client, &io, started, &got) || got == 0)\n'
    '\t\t\t\treturn FALSE;\n'
    '\t\t\tclient->pipe_pos = 0;\n'
    '\t\t\tclient->pipe_len = got;\n'
    '\t\t}\n'
    '\t\tDWORD avail = client->pipe_len - client->pipe_pos;\n'
    '\t\tDWORD step = length < avail ? length : avail;\n'
    '\t\tmemcpy(bytes, client->pipe_buf + client->pipe_pos, step);\n'
    '\t\tclient->pipe_pos += step;\n'
    '\t\tbytes += step;\n'
    '\t\tlength -= step;\n'
    '\t}\n'
    '\treturn TRUE;\n'
    '}\n'
    '\n'
    'static void try_open_shm(struct radio_client *client, uint8_t cap) {\n'
    '\tconst char *map_name = (cap & RADIO_LINK_CAP_SHM_GLOBAL) ? "Global\\\\nekodrop-radio-shm-v1"\n'
    '\t                     : (cap & RADIO_LINK_CAP_SHM_LOCAL)  ? "Local\\\\nekodrop-radio-shm-v1" : NULL;\n'
    '\tconst char *rx_name  = (cap & RADIO_LINK_CAP_SHM_GLOBAL) ? "Global\\\\nekodrop-radio-shm-rx-v1"\n'
    '\t                     : "Local\\\\nekodrop-radio-shm-rx-v1";\n'
    '\tconst char *tx_name  = (cap & RADIO_LINK_CAP_SHM_GLOBAL) ? "Global\\\\nekodrop-radio-shm-tx-v1"\n'
    '\t                     : "Local\\\\nekodrop-radio-shm-tx-v1";\n'
    '\tif (!map_name)\n'
    '\t\treturn;\n'
    '\tHANDLE map = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, map_name);\n'
    '\tif (!map)\n'
    '\t\treturn;\n'
    '\tstruct radio_shm_layout *shm =\n'
    '\t    MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(struct radio_shm_layout));\n'
    '\tif (!shm || shm->magic != RADIO_SHM_MAGIC || shm->version != RADIO_SHM_VERSION) {\n'
    '\t\tif (shm) UnmapViewOfFile(shm);\n'
    '\t\tCloseHandle(map);\n'
    '\t\treturn;\n'
    '\t}\n'
    '\tHANDLE rx_ev = OpenEventA(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, rx_name);\n'
    '\tHANDLE tx_ev = OpenEventA(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, tx_name);\n'
    '\tif (!rx_ev || !tx_ev) {\n'
    '\t\tif (rx_ev) CloseHandle(rx_ev);\n'
    '\t\tif (tx_ev) CloseHandle(tx_ev);\n'
    '\t\tUnmapViewOfFile(shm);\n'
    '\t\tCloseHandle(map);\n'
    '\t\treturn;\n'
    '\t}\n'
    '\tclient->shm_map = map;\n'
    '\tclient->shm_rx_event = rx_ev;\n'
    '\tclient->shm_tx_event = tx_ev;\n'
    '\tclient->shm = shm;\n'
    '\tInterlockedExchange(&shm->rx.waiting, 1);\n'
    '\tMemoryBarrier();\n'
    '\tInterlockedExchange(&shm->client_ready, 1);\n'
    '\tlog_info("radio data plane: zero-pipe shared-memory rings active (4 MiB RX + 4 MiB TX)");\n'
    '}\n',
    'radio_client.c buffered pipe read + try_open_shm',
)
patch_once(
    radio_client_c,
    'try_open_shm(client, hello->reserved[0]);',
    '\t\t\tclient->version = hello->version;\n'
    '\t\t\tclient->channel = hello->channel;\n'
    '\t\t\tclient->width_mhz = hello->width_mhz;\n'
    '\t\t\tSetEvent(client->hello_event);\n',
    '\t\t\tclient->version = hello->version;\n'
    '\t\t\tclient->channel = hello->channel;\n'
    '\t\t\tclient->width_mhz = hello->width_mhz;\n'
    '\t\t\ttry_open_shm(client, hello->reserved[0]);\n'
    '\t\t\tSetEvent(client->hello_event);\n',
    'radio_client.c negotiate SHM on HELLO',
)
patch_once(
    radio_client_c,
    'client->pipe_buf = malloc(PIPE_READ_BUF);',
    '\tclient->slots = calloc(SLOT_COUNT, sizeof(*client->slots));\n',
    '\tclient->slots = calloc(SLOT_COUNT, sizeof(*client->slots));\n'
    '\tclient->pipe_buf = malloc(PIPE_READ_BUF);\n',
    'radio_client.c allocate pipe_buf',
)
patch_once(
    radio_client_c,
    'if (client->shm)',
    '\tfree(client->slots);\n\tfree(client);\n',
    '\tif (client->shm) {\n'
    '\t\tInterlockedExchange(&client->shm->client_ready, 0);\n'
    '\t\tUnmapViewOfFile(client->shm);\n'
    '\t}\n'
    '\tif (client->shm_map) CloseHandle(client->shm_map);\n'
    '\tif (client->shm_rx_event) CloseHandle(client->shm_rx_event);\n'
    '\tif (client->shm_tx_event) CloseHandle(client->shm_tx_event);\n'
    '\tfree(client->pipe_buf);\n'
    '\tfree(client->slots);\n'
    '\tfree(client);\n',
    'radio_client.c close SHM handles',
)
patch_once(
    radio_client_c,
    'client->shm_rx_event ? client->shm_rx_event',
    'void *radio_client_rx_event(const struct radio_client *client) {\n'
    '\treturn client->rx_event;\n'
    '}\n',
    'void *radio_client_rx_event(const struct radio_client *client) {\n'
    '\treturn client->shm_rx_event ? client->shm_rx_event : client->rx_event;\n'
    '}\n',
    'radio_client.c return SHM rx_event',
)
patch_once(
    radio_client_c,
    'shm_pop_rx',
    'int radio_client_pop(struct radio_client *client, uint8_t *frame) {\n'
    '\tint length = 0;\n'
    '\tEnterCriticalSection(&client->queue_lock);\n'
    '\tif (client->count > 0) {\n'
    '\t\tconst struct slot *slot = &client->slots[client->head];\n'
    '\t\tlength = slot->length;\n'
    '\t\tmemcpy(frame, slot->frame, (size_t) length);\n'
    '\t\tclient->head = (client->head + 1) % SLOT_COUNT;\n'
    '\t\tclient->count--;\n'
    '\t}\n'
    '\tLeaveCriticalSection(&client->queue_lock);\n'
    '\treturn length;\n'
    '}\n'
    '\n'
    'int radio_client_has_frame(struct radio_client *client) {\n'
    '\tEnterCriticalSection(&client->queue_lock);\n'
    '\tint waiting = client->count > 0;\n'
    '\tLeaveCriticalSection(&client->queue_lock);\n'
    '\treturn waiting;\n'
    '}\n'
    '\n'
    'int radio_client_send(struct radio_client *client, uint16_t type, const uint8_t *payload,\n'
    '\t\t      uint32_t length) {\n',
    'static int shm_pop_rx(struct radio_client *client, uint8_t *frame) {\n'
    '\tstruct radio_shm_ring *ring = &client->shm->rx;\n'
    '\tfor (int attempt = 0; attempt < 2; attempt++) {\n'
    '\t\tuint32_t r = (uint32_t) ring->read_pos;\n'
    '\t\tuint32_t w = (uint32_t) InterlockedCompareExchange(&ring->write_pos, 0, 0);\n'
    '\t\tif (w == r) {\n'
    '\t\t\tif (attempt == 0) {\n'
    '\t\t\t\tResetEvent(client->shm_rx_event);\n'
    '\t\t\t\tInterlockedExchange(&ring->waiting, 1);\n'
    '\t\t\t\tMemoryBarrier();\n'
    '\t\t\t\tcontinue;\n'
    '\t\t\t}\n'
    '\t\t\treturn 0;\n'
    '\t\t}\n'
    '\t\tif (attempt == 1) {\n'
    '\t\t\tInterlockedExchange(&ring->waiting, 0);\n'
    '\t\t\tSetEvent(client->shm_rx_event);\n'
    '\t\t}\n'
    '\t\tMemoryBarrier();\n'
    '\t\tuint32_t len = 0;\n'
    '\t\tshm_read_bytes(ring->data, r, (uint8_t *) &len, 4u);\n'
    '\t\tuint32_t total = (4u + len + 3u) & ~3u;\n'
    '\t\tif (len == 0 || len > RADIO_CLIENT_MAX_FRAME || (w - r) < total) {\n'
    '\t\t\tInterlockedExchange(&ring->read_pos, (LONG) w);\n'
    '\t\t\treturn 0;\n'
    '\t\t}\n'
    '\t\tshm_read_bytes(ring->data, r + 4u, frame, len);\n'
    '\t\tMemoryBarrier();\n'
    '\t\tInterlockedExchange(&ring->read_pos, (LONG) (r + total));\n'
    '\t\treturn (int) len;\n'
    '\t}\n'
    '\treturn 0;\n'
    '}\n'
    '\n'
    'int radio_client_pop(struct radio_client *client, uint8_t *frame) {\n'
    '\tif (client->shm)\n'
    '\t\treturn shm_pop_rx(client, frame);\n'
    '\tint length = 0;\n'
    '\tEnterCriticalSection(&client->queue_lock);\n'
    '\tif (client->count > 0) {\n'
    '\t\tconst struct slot *slot = &client->slots[client->head];\n'
    '\t\tlength = slot->length;\n'
    '\t\tmemcpy(frame, slot->frame, (size_t) length);\n'
    '\t\tclient->head = (client->head + 1) % SLOT_COUNT;\n'
    '\t\tclient->count--;\n'
    '\t}\n'
    '\tLeaveCriticalSection(&client->queue_lock);\n'
    '\treturn length;\n'
    '}\n'
    '\n'
    'int radio_client_has_frame(struct radio_client *client) {\n'
    '\tif (client->shm) {\n'
    '\t\tstruct radio_shm_ring *ring = &client->shm->rx;\n'
    '\t\treturn (uint32_t) InterlockedCompareExchange(&ring->write_pos, 0, 0) !=\n'
    '\t\t       (uint32_t) ring->read_pos;\n'
    '\t}\n'
    '\tEnterCriticalSection(&client->queue_lock);\n'
    '\tint waiting = client->count > 0;\n'
    '\tLeaveCriticalSection(&client->queue_lock);\n'
    '\treturn waiting;\n'
    '}\n'
    '\n'
    'int radio_client_send(struct radio_client *client, uint16_t type, const uint8_t *payload,\n'
    '\t\t      uint32_t length) {\n'
    '\tif (type == RADIO_LINK_TX && client->shm && length > 0 && length <= RADIO_LINK_MAX_PAYLOAD) {\n'
    '\t\tstruct radio_shm_ring *ring = &client->shm->tx;\n'
    '\t\tuint32_t total = (4u + length + 3u) & ~3u;\n'
    '\t\tfor (int spins = 0; spins < 2000; spins++) {\n'
    '\t\t\tuint32_t w = (uint32_t) ring->write_pos;\n'
    '\t\t\tuint32_t r = (uint32_t) InterlockedCompareExchange(&ring->read_pos, 0, 0);\n'
    '\t\t\tif (RADIO_SHM_RING_SIZE - (w - r) >= total) {\n'
    '\t\t\t\tshm_write_bytes(ring->data, w, (const uint8_t *) &length, 4u);\n'
    '\t\t\t\tshm_write_bytes(ring->data, w + 4u, payload, length);\n'
    '\t\t\t\tMemoryBarrier();\n'
    '\t\t\t\tInterlockedExchange(&ring->write_pos, (LONG) (w + total));\n'
    '\t\t\t\treturn 0;\n'
    '\t\t\t}\n'
    '\t\t\tYieldProcessor();\n'
    '\t\t}\n'
    '\t\treturn 0; /* ring full under stall: drop rather than block AWDL sync */\n'
    '\t}\n',
    'radio_client.c lock-free SHM RX pop + TX push',
)

# 7. ev_win.c: fair poll all ready I/O handles after WaitForMultipleObjects so a
#    persistently signaled manual-reset shm_rx_event never starves host_fd (TCP ACKs!).
ev_win_c = root / 'awdl' / 'win' / 'ev_win.c'
patch_once(
    ev_win_c,
    'Fair poll: never starve later handles',
    '\tDWORD result = WaitForMultipleObjects(count, handles, FALSE, INFINITE);\n'
    '\tDWORD index = result - WAIT_OBJECT_0;\n'
    '\tif (index < count && watchers[index])\n'
    '\t\tev_feed_event(loop, watchers[index], EV_READ);\n',
    '\tDWORD result = WaitForMultipleObjects(count, handles, FALSE, INFINITE);\n'
    '\tDWORD index = result - WAIT_OBJECT_0;\n'
    '\tif (index < count && watchers[index])\n'
    '\t\tev_feed_event(loop, watchers[index], EV_READ);\n'
    '\t/* Fair poll: never starve later handles (e.g. host_fd TCP ACKs) behind\n'
    '\t * a persistently signaled manual-reset SHM RX event. */\n'
    '\tfor (DWORD i = index + 1; i < count; i++) {\n'
    '\t\tif (watchers[i] && WaitForSingleObject(handles[i], 0) == WAIT_OBJECT_0)\n'
    '\t\t\tev_feed_event(loop, watchers[i], EV_READ);\n'
    '\t}\n',
    'ev_win.c fair poll all ready handles',
)

# 8. io_win.c: batch up to 32 RX frames per pcap_dispatch call (32x fewer kernel
#    WaitForMultipleObjects + SetWaitableTimer syscalls during high-rate RX bursts).
io_win_c = root / 'awdl' / 'win' / 'io_win.c'
patch_once(
    io_win_c,
    'int handled = 0;',
    'int pcap_dispatch(pcap_t *handle, int count, pcap_handler callback, unsigned char *user) {\n'
    '\t(void) count;\n'
    '\tstatic uint8_t frame[RADIO_CLIENT_MAX_FRAME];\n'
    '\tint length = radio_client_pop(handle->radio, frame);\n'
    '\tif (length == 0 && merge.length) {\n'
    '\t\t/* The queue is empty, but the rest of an aggregate may still be\n'
    '\t\t * on its way from the radio. */\n'
    '\t\tif (frame_before_delivery())\n'
    '\t\t\tlength = radio_client_pop(handle->radio, frame);\n'
    '\t\telse\n'
    '\t\t\tdeliver_joined();\n'
    '\t}\n'
    '\tif (length == 0)\n'
    '\t\treturn 0;\n'
    '\tjoining.last_frame_us = now_us();\n'
    '\tlength = frame_fixup_received(frame, length);\n'
    '\tstatic uint8_t announced[SERVICE_BRIDGE_MAX_PACKET];\n'
    '\tint announced_len = service_bridge_packet(frame, length, announced);\n'
    '\tif (announced_len && host)\n'
    '\t\thost_if_write(host, announced, announced_len);\n'
    '\tstruct pcap_pkthdr header = {.caplen = (uint32_t) length, .len = (uint32_t) length};\n'
    '\tcallback(user, &header, frame);\n'
    '\treturn 1;\n'
    '}\n',
    'int pcap_dispatch(pcap_t *handle, int count, pcap_handler callback, unsigned char *user) {\n'
    '\t(void) count;\n'
    '\tstatic uint8_t frame[RADIO_CLIENT_MAX_FRAME];\n'
    '\tstatic uint8_t announced[SERVICE_BRIDGE_MAX_PACKET];\n'
    '\tint handled = 0;\n'
    '\twhile (handled < 32) {\n'
    '\t\tint length = radio_client_pop(handle->radio, frame);\n'
    '\t\tif (length == 0 && merge.length) {\n'
    '\t\t\t/* The queue is empty, but the rest of an aggregate may still be\n'
    '\t\t\t * on its way from the radio. */\n'
    '\t\t\tif (frame_before_delivery())\n'
    '\t\t\t\tlength = radio_client_pop(handle->radio, frame);\n'
    '\t\t\telse\n'
    '\t\t\t\tdeliver_joined();\n'
    '\t\t}\n'
    '\t\tif (length == 0)\n'
    '\t\t\tbreak;\n'
    '\t\tjoining.last_frame_us = now_us();\n'
    '\t\tlength = frame_fixup_received(frame, length);\n'
    '\t\tint announced_len = service_bridge_packet(frame, length, announced);\n'
    '\t\tif (announced_len && host)\n'
    '\t\t\thost_if_write(host, announced, announced_len);\n'
    '\t\tstruct pcap_pkthdr header = {.caplen = (uint32_t) length, .len = (uint32_t) length};\n'
    '\t\tcallback(user, &header, frame);\n'
    '\t\thandled++;\n'
    '\t}\n'
    '\treturn handled;\n'
    '}\n',
    'io_win.c batch up to 32 RX frames per pcap_dispatch',
)

# 9. upstream/owl/src/rx.c: strip optional +HTC control field and unwrap Apple
#    RFC 1042-encapsulated 802.3 A-MSDU subframes (#30, #66, #87, #88).
owl_rx_c = root / 'upstream' / 'owl' / 'src' / 'rx.c'
patch_once(
    owl_rx_c,
    'nekodrop (#30, #66): +HTC control field',
    '\t\tcase IEEE80211_FTYPE_DATA | IEEE80211_STYPE_DATA | IEEE80211_STYPE_QOS_DATA:\n'
    '\t\t\tREAD_LE16(frame, 0, &qosc);\n'
    '\t\t\tBUF_STRIP(frame, IEEE80211_QOS_CTL_LEN);\n',
    '\t\tcase IEEE80211_FTYPE_DATA | IEEE80211_STYPE_DATA | IEEE80211_STYPE_QOS_DATA:\n'
    '\t\t\tREAD_LE16(frame, 0, &qosc);\n'
    '\t\t\tBUF_STRIP(frame, IEEE80211_QOS_CTL_LEN);\n'
    '\t\t\tif (fc & 0x8000u) /* nekodrop (#30, #66): +HTC control field */\n'
    '\t\t\t\tBUF_STRIP(frame, 4);\n',
    'owl rx.c strip +HTC control field when Order bit set',
)
patch_once(
    owl_rx_c,
    'nekodrop (#30, #66, v2): iOS/iPadOS/macOS TX A-MSDU',
    '\tif (!awdl_valid_llc_header(frame))\n'
    '\t\treturn RX_UNEXPECTED_FORMAT;\n',
    '\t/* nekodrop (#30, #66, v2): iOS/iPadOS/macOS TX A-MSDU bridge converter wraps\n'
    '\t * inner 802.3 AWDL length (< 0x0600) in an extra 8-byte RFC 1042 LLC/SNAP\n'
    '\t * header (aa aa 03 00 00 00 <be16 len>) ahead of AWDL\'s aa aa 03 00 17 f2 08 00\n'
    '\t * (PID 0x0800, big-endian). v1 compared the PID as 00 08 and never matched, so\n'
    '\t * these frames were still dropped. Unwrap any inner LLC/SNAP header; the\n'
    '\t * check below then logs what is really inside if it is not AWDL\'s. */\n'
    '\tif (buf_len(frame) >= 16 && buf_data(frame)[4] == 0x00 && buf_data(frame)[5] == 0x00 &&\n'
    '\t    buf_data(frame)[0] == 0xaa && buf_data(frame)[1] == 0xaa &&\n'
    '\t    buf_data(frame)[2] == 0x03 && buf_data(frame)[3] == 0x00) {\n'
    '\t\tuint16_t inner_len = ((uint16_t) buf_data(frame)[6] << 8) | buf_data(frame)[7];\n'
    '\t\tif (inner_len >= 16 && inner_len < 0x0600 && 8 + (int) inner_len <= buf_len(frame)) {\n'
    '\t\t\t/* The wrapped 802.3 frame is either the bare LLC part or a whole one\n'
    '\t\t\t * (DA, SA, length, then LLC); take whichever carries an LLC header. */\n'
    '\t\t\tint skip = !memcmp(buf_data(frame) + 8, "\\xaa\\xaa\\x03", 3) ? 8\n'
    '\t\t\t         : (inner_len >= 30 && !memcmp(buf_data(frame) + 22, "\\xaa\\xaa\\x03", 3)) ? 22 : 0;\n'
    '\t\t\tif (skip) {\n'
    '\t\t\t\tint keep = (int) inner_len - (skip - 8);\n'
    '\t\t\t\tbuf_strip(frame, skip);\n'
    '\t\t\t\tif (buf_len(frame) > keep)\n'
    '\t\t\t\t\tbuf_take(frame, buf_len(frame) - keep);\n'
    '\t\t\t}\n'
    '\t\t}\n'
    '\t}\n'
    '\tif (!awdl_valid_llc_header(frame)) {\n'
    '\t\t/* nekodrop (#30): the first bytes of a few frames we still cannot read,\n'
    '\t\t * so the next diagnostic report shows what the peer really sends. */\n'
    '\t\tstatic int dumped;\n'
    '\t\tif (dumped < 8) {\n'
    '\t\t\tchar hex[3 * 32 + 1] = "";\n'
    '\t\t\tint n = buf_len(frame) < 32 ? buf_len(frame) : 32;\n'
    '\t\t\tfor (int i = 0; i < n; i++) {\n'
    '\t\t\t\thex[3 * i] = "0123456789abcdef"[buf_data(frame)[i] >> 4];\n'
    '\t\t\t\thex[3 * i + 1] = "0123456789abcdef"[buf_data(frame)[i] & 15];\n'
    '\t\t\t\thex[3 * i + 2] = \' \';\n'
    '\t\t\t\thex[3 * i + 3] = 0;\n'
    '\t\t\t}\n'
    '\t\t\tlog_warn("llc: unreadable data frame %d/8 from %s, %d bytes: %s", ++dumped, ether_ntoa(src), buf_len(frame), hex);\n'
    '\t\t}\n'
    '\t\treturn RX_UNEXPECTED_FORMAT;\n'
    '\t}\n',
    'owl rx.c unwrap RFC 1042-encapsulated 802.3 AWDL A-MSDU subframes',
)

# 10. upstream/owl: RX A-MSDU bounds checking, exact-sized Ethernet allocation,
#     unconditional cleanup on partial A-MSDU errors, malloc NULL checks, and
#     initialized rssi/flags in awdl_rx().
owl_wire_c = root / 'upstream' / 'owl' / 'src' / 'wire.c'
owl_rx_h = root / 'upstream' / 'owl' / 'src' / 'rx.h'
owl_core_c = root / 'upstream' / 'owl' / 'daemon' / 'core.c'

patch_once(
    owl_wire_c,
    'if (!buf->data && len)',
    'struct buf *buf_new_owned(int len) {\n'
    '\tif (len < 0)\n'
    '\t\treturn NULL;\n'
    '\tstruct buf *buf = (struct buf *) malloc(sizeof(struct buf));\n'
    '\tbuf->data = (uint8_t *) malloc(len);\n'
    '\tbuf->orig = buf->data;\n'
    '\tbuf->len = len;\n'
    '\tbuf->owned = 1;\n'
    '\treturn buf;\n'
    '}\n'
    '\n'
    'const struct buf *buf_new_const(const uint8_t *data, int len) {\n'
    '\tif (len < 0)\n'
    '\t\treturn NULL;\n'
    '\tstruct buf *buf = (struct buf *) malloc(sizeof(struct buf));\n',
    'struct buf *buf_new_owned(int len) {\n'
    '\tif (len < 0)\n'
    '\t\treturn NULL;\n'
    '\tstruct buf *buf = (struct buf *) malloc(sizeof(struct buf));\n'
    '\tif (!buf)\n'
    '\t\treturn NULL;\n'
    '\tbuf->data = (uint8_t *) malloc(len);\n'
    '\tif (!buf->data && len) {\n'
    '\t\tfree(buf);\n'
    '\t\treturn NULL;\n'
    '\t}\n'
    '\tbuf->orig = buf->data;\n'
    '\tbuf->len = len;\n'
    '\tbuf->owned = 1;\n'
    '\treturn buf;\n'
    '}\n'
    '\n'
    'const struct buf *buf_new_const(const uint8_t *data, int len) {\n'
    '\tif (len < 0)\n'
    '\t\treturn NULL;\n'
    '\tstruct buf *buf = (struct buf *) malloc(sizeof(struct buf));\n'
    '\tif (!buf)\n'
    '\t\treturn NULL;\n',
    'owl wire.c NULL checks in buf_new_owned/buf_new_const',
)

patch_once(
    owl_rx_h,
    'struct awdl_rx_frames',
    'int awdl_rx_data(const struct buf *frame, struct buf ***out,\n'
    '                 const struct ether_addr *src, const struct ether_addr *dst, struct awdl_state *state);\n'
    '\n'
    'int awdl_rx_data_amsdu(const struct buf *frame, struct buf ***out,\n'
    '                       const struct ether_addr *src, const struct ether_addr *dst, struct awdl_state *state);\n'
    '\n'
    '/** @brief Receive and process AWDL action and data frame\n'
    ' *\n'
    ' * @param frame input frame\n'
    ' * @param data if {@code frame} points to a valid AWDL data frame, will point to converted Ethernet frame after function returns\n'
    ' * @param state\n'
    ' * @return RX_OK\n'
    ' */\n'
    'int awdl_rx(const struct buf *frame, struct buf ***data_frames, struct awdl_state *state);\n',
    'struct awdl_rx_frames {\n'
    '\tstruct buf **next;\n'
    '\tstruct buf **end;\n'
    '};\n'
    '\n'
    'int awdl_rx_data(const struct buf *frame, struct awdl_rx_frames *out,\n'
    '                 const struct ether_addr *src, const struct ether_addr *dst, struct awdl_state *state);\n'
    '\n'
    'int awdl_rx_data_amsdu(const struct buf *frame, struct awdl_rx_frames *out,\n'
    '                       const struct ether_addr *src, const struct ether_addr *dst, struct awdl_state *state);\n'
    '\n'
    '/** @brief Receive and process AWDL action and data frame\n'
    ' *\n'
    ' * @param frame input frame\n'
    ' * @param data_frames output Ethernet frames in [next, end). The caller frees each\n'
    ' *        appended frame, including when a later A-MSDU subframe fails to decode.\n'
    ' * @param state\n'
    ' * @return RX_OK\n'
    ' */\n'
    'int awdl_rx(const struct buf *frame, struct awdl_rx_frames *data_frames, struct awdl_state *state);\n',
    'owl rx.h bounded struct awdl_rx_frames output',
)

patch_once(
    owl_rx_c,
    'if (!tlv_buf)\n\t\t\treturn RX_UNEXPECTED_VALUE;',
    '\twhile ((len = read_tlv(frame, 0, &tlv_type, &tlv_len, &tlv_value)) > 0) {\n'
    '\t\tconst struct buf *tlv_buf = buf_new_const(tlv_value, tlv_len);\n',
    '\twhile ((len = read_tlv(frame, 0, &tlv_type, &tlv_len, &tlv_value)) > 0) {\n'
    '\t\tconst struct buf *tlv_buf = buf_new_const(tlv_value, tlv_len);\n'
    '\t\tif (!tlv_buf)\n'
    '\t\t\treturn RX_UNEXPECTED_VALUE;\n',
    'owl rx.c NULL check on tlv_buf',
)

patch_once(
    owl_rx_c,
    'int awdl_rx_data(const struct buf *frame, struct awdl_rx_frames *out,',
    'int awdl_rx_data(const struct buf *frame, struct buf ***out, const struct ether_addr *src,\n',
    'int awdl_rx_data(const struct buf *frame, struct awdl_rx_frames *out, const struct ether_addr *src,\n',
    'owl rx.c awdl_rx_data bounded output signature',
)

patch_once(
    owl_rx_c,
    'buf_new_owned(2 * ETHER_ADDR_LEN + 2 + buf_len(frame))',
    '\t**out = buf_new_owned(ETHER_MAX_LEN);\n'
    '\n'
    '\t/* TODO use checked write methods */\n'
    '\toffset += write_ether_addr(**out, offset, dst);\n'
    '\toffset += write_ether_addr(**out, offset, src);\n'
    '\toffset += write_be16(**out, offset, ether_type);\n'
    '\t/* TODO avoid copying bytes */\n'
    '\toffset += write_bytes(**out, offset, buf_data(frame), buf_len(frame));\n'
    '\n'
    '\t(*out)++;\n',
    '\tif (out->next == out->end)\n'
    '\t\treturn RX_UNEXPECTED_VALUE;\n'
    '\tstruct buf *ethernet = buf_new_owned(2 * ETHER_ADDR_LEN + 2 + buf_len(frame));\n'
    '\tif (!ethernet)\n'
    '\t\treturn RX_UNEXPECTED_VALUE;\n'
    '\toffset += write_ether_addr(ethernet, offset, dst);\n'
    '\toffset += write_ether_addr(ethernet, offset, src);\n'
    '\toffset += write_be16(ethernet, offset, ether_type);\n'
    '\tif (write_bytes(ethernet, offset, buf_data(frame), buf_len(frame)) < 0) {\n'
    '\t\tbuf_free(ethernet);\n'
    '\t\treturn RX_UNEXPECTED_FORMAT;\n'
    '\t}\n'
    '\t*out->next++ = ethernet;\n',
    'owl rx.c bounds check and exact-sized Ethernet allocation',
)

patch_once(
    owl_rx_c,
    'int awdl_rx_data_amsdu(const struct buf *frame, struct awdl_rx_frames *out,',
    'int awdl_rx_data_amsdu(const struct buf *frame, struct buf ***out, const struct ether_addr *src __attribute__((unused)),\n',
    'int awdl_rx_data_amsdu(const struct buf *frame, struct awdl_rx_frames *out, const struct ether_addr *src __attribute__((unused)),\n',
    'owl rx.c awdl_rx_data_amsdu bounded output signature',
)

patch_once(
    owl_rx_c,
    'if (!subframe)\n\t\t\treturn RX_UNEXPECTED_VALUE;',
    '\t\t/* create subview of buf */\n'
    '\t\tsubframe = buf_new_const(buf_data(frame), len_a);\n',
    '\t\t/* create subview of buf */\n'
    '\t\tsubframe = buf_new_const(buf_data(frame), len_a);\n'
    '\t\tif (!subframe)\n'
    '\t\t\treturn RX_UNEXPECTED_VALUE;\n',
    'owl rx.c NULL check on A-MSDU subframe',
)

patch_once(
    owl_rx_c,
    'int awdl_rx(const struct buf *frame, struct awdl_rx_frames *data_frame,',
    'int awdl_rx(const struct buf *frame, struct buf ***data_frame, struct awdl_state *state) {\n'
    '\tconst struct ieee80211_hdr *ieee80211;\n'
    '\tconst struct ether_addr *from, *to;\n'
    '\tuint16_t fc, qosc; /* frame and QoS control */\n'
    '\tsigned char rssi;\n'
    '\tuint64_t tsft;\n'
    '\tuint8_t flags;\n',
    'int awdl_rx(const struct buf *frame, struct awdl_rx_frames *data_frame, struct awdl_state *state) {\n'
    '\tconst struct ieee80211_hdr *ieee80211;\n'
    '\tconst struct ether_addr *from, *to;\n'
    '\tuint16_t fc, qosc; /* frame and QoS control */\n'
    '\tsigned char rssi = 0;\n'
    '\tuint64_t tsft;\n'
    '\tuint8_t flags = 0;\n',
    'owl rx.c bounded awdl_rx signature and initialized rssi/flags',
)

patch_once(
    owl_core_c,
    'struct awdl_rx_frames data = {packets, packets + 512};',
    'void awdl_receive_frame(uint8_t *user, const struct pcap_pkthdr *hdr, const uint8_t *buf) {\n'
    '#define MAX_NUM_AMPDU 16 /* TODO lookup this constant from the standard */\n'
    '\tstruct daemon_state *state = (void *) user;\n'
    '\tint result;\n'
    '\tconst struct buf *frame = buf_new_const(buf, hdr->caplen);\n'
    '\tstruct buf *data_arr[MAX_NUM_AMPDU];\n'
    '\tstruct buf **data = &data_arr[0];\n'
    '\tresult = awdl_rx(frame, &data, &state->awdl_state);\n'
    '\tif (result == RX_OK) {\n'
    '\t\tfor (struct buf **data_start = &data_arr[0]; data_start < data; data_start++) {\n'
    '\t\t\thost_send(&state->io, buf_data(*data_start), buf_len(*data_start));\n'
    '\t\t\tbuf_free(*data_start);\n'
    '\t\t}\n'
    '\t} else if (result < RX_OK) {\n',
    'void awdl_receive_frame(uint8_t *user, const struct pcap_pkthdr *hdr, const uint8_t *buf) {\n'
    '\tstruct daemon_state *state = (void *) user;\n'
    '\tconst struct buf *frame = buf_new_const(buf, hdr->caplen);\n'
    '\tif (!frame)\n'
    '\t\treturn;\n'
    '\t/* Every nonfinal A-MSDU subframe occupies at least 32 bytes (Ethernet\n'
    '\t * subheader, LLC, AWDL, alignment). 512 pointers cover a 16 KiB MPDU. */\n'
    '\tstruct buf *packets[512];\n'
    '\tstruct awdl_rx_frames data = {packets, packets + 512};\n'
    '\tint result = awdl_rx(frame, &data, &state->awdl_state);\n'
    '\tfor (struct buf **packet = packets; packet < data.next; packet++) {\n'
    '\t\tif (result == RX_OK)\n'
    '\t\t\thost_send(&state->io, buf_data(*packet), buf_len(*packet));\n'
    '\t\tbuf_free(*packet);\n'
    '\t}\n'
    '\tif (result < RX_OK) {\n',
    'owl core.c 512-entry bounded RX vector and unconditional cleanup',
)
