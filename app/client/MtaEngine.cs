// SPDX-License-Identifier: GPL-2.0-only
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace NekoDrop;

/// <summary>
/// Pure user-mode (zero kernel driver, zero ASP.NET Core) Mutual Transmission
/// Alliance (MTA v2 LAN) engine for Windows:
///   - Receives from OPPO / OnePlus / realme (BLE 8881 + GATT 9999 + same-LAN WSS/HTTPS).
///   - Sends to OPPO / OnePlus / realme (BLE 3333/3334 scan + GATT 9897/9896 blind LAN offer
///     bypassing Windows WinRT's missing-CCCD limitation + streaming zero-temp ZIP_STORED).
///   - Emits the exact same JSON event contract as Engine (AirDrop) with "mta:" prefixed IDs.
///   - Includes a privacy-redacted BLE diagnostic scanner to collect Xiaomi / vivo / Honor
///     cross-ecosystem discovery fingerprints from community reports.
/// </summary>
public sealed class MtaEngine : IDisposable
{
    public const string PeerPrefix = "mta2:";
    public const string ReqPrefix = "mta:";

    static readonly Guid UuidAdv8881 = new("00008881-0000-1000-8000-00805f9b34fb");
    static readonly Guid UuidSvc9999 = new("00009999-0000-1000-8000-00805f9b34fb");
    static readonly Guid UuidChr9898 = new("00009898-0000-1000-8000-00805f9b34fb");
    static readonly Guid UuidChr9897 = new("00009897-0000-1000-8000-00805f9b34fb");
    static readonly Guid UuidChr9896 = new("00009896-0000-1000-8000-00805f9b34fb");
    static readonly Guid UuidChr9895 = new("00009895-0000-1000-8000-00805f9b34fb");
    static readonly Guid UuidChr9984 = new("00009984-0000-1000-8000-00805f9b34fb");
    static readonly Guid UuidChr9998 = new("00009998-0000-1000-8000-00805f9b34fb");
    static readonly Guid UuidAdv3331 = new("00003331-0000-1000-8000-008123456789");

    static readonly byte[] FixedIv = Encoding.ASCII.GetBytes("0102030405060708");
    static readonly int[] PreferredPorts = [38771, 38772, 38773, 38774, 38775, 38776, 38777, 38778, 38779];
    static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(14);

    public event Action<JsonObject>? Event;
    public event Action<string>? Log;
    /// <summary>Fires as soon as an Android peer initiates a BLE receive handshake (used to pre-warm AirDrop in Relay mode).</summary>
    public event Action? IncomingHandshake;

    readonly object _lock = new();
    string _deviceName = Environment.MachineName;
    string _saveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "nekodrop");
    bool _enabled;
    bool _discoverable = true;
    bool _looking;
    bool _radioYield;
    bool _disposed;

    // BLE GATT server (receive)
    GattServiceProvider? _g8881;
    GattServiceProvider? _g9999;
    GattLocalCharacteristic? _c9895;
    bool _rxStarting;
    ECDiffieHellman? _rxEcdh;
    byte[] _rxStateBytes = [];
    ReceiveSession? _rxSession;
    readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _rxDecisions = new();
    readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCancels = new();

    // BLE scanner (send)
    BluetoothLEAdvertisementWatcher? _watcher;
    Timer? _pruneTimer;
    readonly ConcurrentDictionary<ulong, ScanPartial> _partials = new();
    readonly ConcurrentDictionary<string, MtaPeer> _peers = new();

    sealed class ScanPartial
    {
        public string? Identity3333;
        public string? Name3334;
        public string? Brand;
        public string? Kind;
        public DateTime LastSeen;
    }

    sealed class MtaPeer
    {
        public required string Id;
        public ulong Address;
        public string Name = "Android";
        public string Brand = "";
        public string Kind = "phone";
        public string LastEmittedModel = "";
        public DateTime LastSeen;
    }

    sealed class ReceiveSession
    {
        public required string Id;
        public required byte[] AesKey;
        public string PeerName = "Android";
        public string Brand = "";
        public string Kind = "phone";
        public int Count;
        public string? NetworkHint;
        public bool StartedDownload;
        public DateTime CreatedUtc = DateTime.UtcNow;
        public CancellationTokenSource Cts = new();
    }

    public static bool IsMtaPeer(string id) => id.StartsWith(PeerPrefix, StringComparison.Ordinal);
    public static bool IsMtaRequest(string id) => id.StartsWith(ReqPrefix, StringComparison.Ordinal);

    public bool HasPeers => !_peers.IsEmpty;

    void Say(string line) => Log?.Invoke("mta: " + line);

    void Emit(JsonObject obj) => Event?.Invoke(obj);

    public void Apply(Settings s)
    {
        bool startAdv, stopAdv, startScan, stopScan, clearPeers;
        lock (_lock)
        {
            if (_disposed) return;
            _deviceName = string.IsNullOrWhiteSpace(s.DeviceName) ? Environment.MachineName : s.DeviceName.Trim();
            _saveFolder = s.SaveFolder;
            var wasAdv = _enabled && _discoverable && !_radioYield;
            var wasScan = _enabled && _looking && !_radioYield;
            _enabled = s.MtaOn;
            _discoverable = s.Discoverable;
            var wantAdv = _enabled && _discoverable && !_radioYield;
            var wantScan = _enabled && _looking && !_radioYield;
            startAdv = wantAdv && !wasAdv;
            stopAdv = !wantAdv && wasAdv;
            startScan = wantScan && !wasScan;
            stopScan = !wantScan && wasScan;
            clearPeers = !_enabled;
        }
        if (stopAdv) StopReceiver();
        if (startAdv) _ = StartReceiverAsync();
        if (stopScan || clearPeers) StopScanner(clearPeers: clearPeers);
        if (startScan) StartScanner();
    }

    public void Look(bool on)
    {
        bool startScan = false, stopScan = false;
        lock (_lock)
        {
            if (_disposed) return;
            if (_looking == on) return;
            _looking = on;
            if (!_enabled || _radioYield) return;
            startScan = on;
            stopScan = !on;
        }
        if (startScan) StartScanner();
        if (stopScan) StopScanner(clearPeers: false);
    }

    /// <summary>
    /// Temporarily pauses BLE active scanning and peripheral advertising while an AirDrop
    /// transfer is active so combo Wi-Fi+BT adapters (e.g. Intel AX201) do not lose AWDL
    /// airtime to BT-Coex arbitration. Keeps discovered MTA peers intact across the pause.
    /// </summary>
    public void SetRadioYield(bool yield)
    {
        bool stopScan = false, startScan = false, stopAdv = false, startAdv = false;
        lock (_lock)
        {
            if (_disposed || _radioYield == yield) return;
            _radioYield = yield;
            if (yield)
            {
                stopScan = _watcher is not null;
                stopAdv = (_g8881 is not null || _g9999 is not null) && _rxSession is null;
            }
            else
            {
                var now = DateTime.UtcNow;
                foreach (var p in _peers.Values) p.LastSeen = now;
                foreach (var p in _partials.Values) p.LastSeen = now;
                startScan = _enabled && _looking && _watcher is null;
                startAdv = _enabled && _discoverable && _g8881 is null && _g9999 is null;
            }
        }
        if (stopScan) StopScanner(clearPeers: false);
        if (stopAdv) StopReceiver();
        if (yield && (stopScan || stopAdv)) Say("BLE yielded during AirDrop transfer");
        if (!yield && (startScan || startAdv)) Say("BLE resumed after AirDrop transfer");
        if (startAdv) _ = StartReceiverAsync();
        if (startScan) StartScanner();
    }

    public void Answer(string id, bool accept)
    {
        if (_rxDecisions.TryRemove(id, out var tcs))
            tcs.TrySetResult(accept);
        if (!accept && _activeCancels.TryGetValue(id, out var cts))
            try { cts.Cancel(); } catch (Exception) { }
    }

    public void Cancel(string id)
    {
        if (_rxDecisions.TryRemove(id, out var tcs))
            tcs.TrySetResult(false);
        if (_activeCancels.TryRemove(id, out var cts))
            try { cts.Cancel(); } catch (Exception) { }
    }

    public void SendFiles(string id, string peerId, IReadOnlyList<string> files)
    {
        if (!_peers.TryGetValue(peerId, out var peer))
        {
            Say($"send {id} failed: peer {peerId} not found");
            Emit(new JsonObject { ["event"] = "send", ["id"] = id, ["state"] = "failed", ["reason"] = "peer_gone" });
            return;
        }
        Say($"send {id} -> {peer.Name} (ble={peer.Address:x12}, files={files.Count})");
        var cts = new CancellationTokenSource();
        _activeCancels[id] = cts;
        _ = Task.Run(async () =>
        {
            try { await RunSendV2Async(id, peer, files, cts.Token); }
            catch (OperationCanceledException)
            {
                Emit(new JsonObject { ["event"] = "send", ["id"] = id, ["state"] = "failed", ["reason"] = "cancelled" });
            }
            catch (Exception e)
            {
                Say($"send {id} failed: {e.GetType().Name}: {e.Message}");
                Emit(new JsonObject { ["event"] = "send", ["id"] = id, ["state"] = "failed", ["reason"] = e.Message });
            }
            finally
            {
                _activeCancels.TryRemove(id, out _);
                cts.Dispose();
            }
        });
    }

    // ============================================================= BLE Scan

    void StartScanner()
    {
        lock (_lock)
        {
            if (_watcher is not null || _disposed || !_enabled || _radioYield) return;
            try
            {
                var w = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active,
                    AllowExtendedAdvertisements = true,
                };
                w.Received += OnAdvertisementReceived;
                w.Stopped += (watcher, args) =>
                {
                    bool restart;
                    lock (_lock)
                    {
                        if (_watcher != watcher) return;
                        _watcher = null;
                        restart = _enabled && _looking && !_radioYield && !_disposed;
                    }
                    if (restart)
                    {
                        Say($"BLE scanner stopped ({args.Error}); retrying in 3s");
                        _ = Task.Delay(3000).ContinueWith(_ => StartScanner());
                    }
                };
                w.Start();
                _watcher = w;
                _pruneTimer ??= new Timer(_ => PrunePeers(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
                Say("BLE v2 active scanner started");
            }
            catch (Exception e)
            {
                Say($"BLE scanner start failed: {e.Message}");
            }
        }
    }

    void StopScanner(bool clearPeers)
    {
        BluetoothLEAdvertisementWatcher? w;
        lock (_lock)
        {
            w = _watcher;
            _watcher = null;
        }
        if (w is not null)
        {
            try { w.Stop(); } catch (Exception) { }
            Say("BLE v2 scanner stopped");
        }
        if (clearPeers)
        {
            _partials.Clear();
            foreach (var key in _peers.Keys.ToList())
                if (_peers.TryRemove(key, out _))
                    Emit(new JsonObject { ["event"] = "peer_gone", ["id"] = key });
        }
    }

    void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        try
        {
            bool touched = false;
            var partial = _partials.GetOrAdd(e.BluetoothAddress, _ => new ScanPartial());
            var now = DateTime.UtcNow;

            foreach (var sec in e.Advertisement.GetSectionsByType(0x16))
            {
                var raw = FromBuf(sec.Data);
                if (raw.Length < 2) continue;
                ushort u16 = (ushort)(raw[0] | (raw[1] << 8));
                var payload = raw[2..];

                if (u16 == 0x3333 && payload.Length >= 14)
                {
                    var identity = Encoding.ASCII.GetString(payload).TrimEnd('\0');
                    var parsed = Parse3333(identity);
                    if (parsed.Valid)
                    {
                        partial.Identity3333 = parsed.PeerKey;
                        if (!string.IsNullOrEmpty(parsed.Brand)) partial.Brand = parsed.Brand;
                        if (!string.IsNullOrEmpty(parsed.Kind)) partial.Kind = parsed.Kind;
                        partial.LastSeen = now;
                        touched = true;
                    }
                }
                else if (u16 == 0x3334 && payload.Length > 0)
                {
                    var name = Encoding.UTF8.GetString(payload).TrimEnd('\0').Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        partial.Name3334 = name;
                        partial.LastSeen = now;
                        touched = true;
                    }
                }
            }

            if (!touched || string.IsNullOrEmpty(partial.Name3334)) return;

            // ColorOS masks parts[0] of 0x3333 as "000000000000" and rotates BLE RPA every 5-15s.
            // Derive a stable ID from name+kind when Identity3333 is unavailable so the same phone
            // never splits into multiple radar icons, while refreshing peer.Address to the freshest RPA.
            var normName = partial.Name3334!.Trim();
            var normKind = partial.Kind ?? "phone";
            string stableKey;
            if (!string.IsNullOrEmpty(partial.Identity3333))
            {
                stableKey = partial.Identity3333!;
            }
            else
            {
                var h = SHA256.HashData(Encoding.UTF8.GetBytes($"{normName.ToLowerInvariant()}|{normKind}"));
                stableKey = "n:" + Convert.ToHexString(h.AsSpan(0, 6)).ToLowerInvariant();
            }

            var peerId = PeerPrefix + stableKey;
            bool isNew = false;
            bool changed = false;
            var peer = _peers.AddOrUpdate(
                peerId,
                _ =>
                {
                    isNew = true;
                    return new MtaPeer
                    {
                        Id = peerId,
                        Address = e.BluetoothAddress,
                        Name = normName,
                        Brand = partial.Brand ?? "OPPO/OnePlus",
                        Kind = normKind,
                        LastSeen = now,
                    };
                },
                (_, existing) =>
                {
                    existing.Address = e.BluetoothAddress;
                    if (existing.Name != normName) { existing.Name = normName; changed = true; }
                    if (!string.IsNullOrEmpty(partial.Brand) && existing.Brand != partial.Brand) { existing.Brand = partial.Brand!; changed = true; }
                    if (!string.IsNullOrEmpty(partial.Kind) && existing.Kind != partial.Kind) { existing.Kind = partial.Kind!; changed = true; }
                    existing.LastSeen = now;
                    return existing;
                });

            var modelTag = BuildModelLabel(peer.Brand, peer.Kind);
            if (isNew || changed || peer.LastEmittedModel != modelTag)
            {
                peer.LastEmittedModel = modelTag;
                Emit(new JsonObject
                {
                    ["event"] = "peer",
                    ["id"] = peer.Id,
                    ["name"] = peer.Name,
                    ["model"] = modelTag,
                    ["protocol"] = "mta2",
                });
            }
        }
        catch (Exception) { }
    }

    void PrunePeers()
    {
        lock (_lock)
        {
            if (_radioYield) return;
        }
        var cutoff = DateTime.UtcNow - PeerTtl;
        foreach (var (addr, p) in _partials)
            if (p.LastSeen < cutoff) _partials.TryRemove(addr, out _);

        foreach (var (id, peer) in _peers)
        {
            if (peer.LastSeen < cutoff && _peers.TryRemove(id, out _))
                Emit(new JsonObject { ["event"] = "peer_gone", ["id"] = id });
        }
    }

    static (bool Valid, string? PeerKey, string Brand, string Kind) Parse3333(string identity)
    {
        var parts = identity.Split('-');
        if (parts.Length < 2 || parts[1] is not ("r" or "sr")) return (false, null, "", "phone");
        string? key = null;
        if (parts[0].Length == 12 && parts[0].All(Uri.IsHexDigit) && parts[0] != "000000000000")
            key = "mta11:" + parts[0].ToLowerInvariant();
        string brand = "", kind = "phone";
        if (parts.Length == 6 && parts[4] == "11" && int.TryParse(parts[5], out var flags) && flags is >= 0 and <= 255)
        {
            brand = (flags & 0x18) switch { 0x08 => "OPPO", 0x10 => "realme", _ => "" };
            kind = (flags & 0x02) != 0 ? "tablet" : "phone";
        }
        return (true, key, brand, kind);
    }

    static string BuildModelLabel(string brand, string kind)
    {
        var b = string.IsNullOrEmpty(brand) ? "Android" : brand;
        var k = kind == "tablet" ? "Tablet" : "";
        return string.IsNullOrEmpty(k) ? $"{b} · {L.T("mta.tag")}" : $"{b} {k} · {L.T("mta.tag")}";
    }

    // ========================================================= BLE Receive

    async Task StartReceiverAsync()
    {
        lock (_lock)
        {
            if (!_enabled || !_discoverable || _radioYield || _disposed || _rxStarting || _g8881 is not null) return;
            _rxStarting = true;
        }
        bool cancelledMidFlight = false;
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported)
            {
                Say("BLE peripheral role unavailable on this adapter");
                return;
            }

            RefreshRxKey(busy: false);

            var r8881 = await GattServiceProvider.CreateAsync(UuidAdv8881);
            var r9999 = await GattServiceProvider.CreateAsync(UuidSvc9999);
            if (r8881.Error != BluetoothError.Success || r9999.Error != BluetoothError.Success)
            {
                Say($"GattServiceProvider failed: 8881={r8881.Error}, 9999={r9999.Error}");
                return;
            }

            var g8881 = r8881.ServiceProvider;
            var g9999 = r9999.ServiceProvider;

            var c9898 = (await g9999.Service.CreateCharacteristicAsync(UuidChr9898, new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                ReadProtectionLevel = GattProtectionLevel.Plain,
            })).Characteristic;

            var c9896 = (await g9999.Service.CreateCharacteristicAsync(UuidChr9896, new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            })).Characteristic;

            var c9895 = (await g9999.Service.CreateCharacteristicAsync(UuidChr9895, new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.Plain,
            })).Characteristic;

            await g9999.Service.CreateCharacteristicAsync(UuidChr9984, new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.Plain,
            });

            var c9998 = (await g9999.Service.CreateCharacteristicAsync(UuidChr9998, new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                ReadProtectionLevel = GattProtectionLevel.Plain,
            })).Characteristic;

            c9898.ReadRequested += OnRxRead9898;
            c9998.ReadRequested += OnRxReadEmpty;
            c9895.SubscribedClientsChanged += OnRxSubscribed9895;
            c9896.WriteRequested += OnRxWrite9896;

            bool stillWanted;
            lock (_lock)
            {
                stillWanted = _enabled && _discoverable && !_radioYield && !_disposed;
                if (stillWanted)
                {
                    _g8881 = g8881;
                    _g9999 = g9999;
                    _c9895 = c9895;
                }
            }
            if (!stillWanted)
            {
                cancelledMidFlight = true;
                return;
            }

            g8881.AdvertisementStatusChanged += (prov, args) =>
            {
                if (args.Status == GattServiceProviderAdvertisementStatus.Aborted && args.Error != BluetoothError.Success)
                {
                    bool restart;
                    lock (_lock) restart = _g8881 == prov && _enabled && _discoverable && !_radioYield && !_disposed;
                    if (restart)
                    {
                        Say($"BLE 8881 aborted ({args.Error}); retrying in 3s");
                        _ = Task.Delay(3000).ContinueWith(_ =>
                        {
                            bool stillAborted;
                            lock (_lock)
                                stillAborted = _g8881 == prov && _enabled && _discoverable && !_radioYield && !_disposed &&
                                               prov.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Aborted;
                            if (!stillAborted) return;
                            StopReceiver();
                            _ = StartReceiverAsync();
                        });
                    }
                }
            };
            g9999.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = false });
            g8881.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });
            await Task.Delay(300);
            Say($"BLE v2 receiver advertising (8881={g8881.AdvertisementStatus}, 9999={g9999.AdvertisementStatus})");
        }
        catch (Exception e)
        {
            Say($"StartReceiver failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            bool retry;
            lock (_lock)
            {
                _rxStarting = false;
                retry = cancelledMidFlight && _enabled && _discoverable && !_radioYield && !_disposed && _g8881 is null;
            }
            if (retry) _ = StartReceiverAsync();
        }
    }

    void StopReceiver()
    {
        GattServiceProvider? g8, g9;
        lock (_lock)
        {
            g8 = _g8881; _g8881 = null;
            g9 = _g9999; _g9999 = null;
            _c9895 = null;
            _rxSession?.Cts.Cancel();
            _rxSession = null;
        }
        try { g8?.StopAdvertising(); } catch (Exception) { }
        try { g9?.StopAdvertising(); } catch (Exception) { }
        Say("BLE v2 receiver stopped");
    }

    void RefreshRxKey(bool busy)
    {
        lock (_lock)
        {
            if (!busy || _rxEcdh is null)
            {
                _rxEcdh?.Dispose();
                _rxEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            }
            var json = JsonSerializer.Serialize(new
            {
                state = busy ? 1 : 0,
                key = Convert.ToBase64String(_rxEcdh.ExportSubjectPublicKeyInfo()),
                version = "10014",
                isFast = 0,
                pv = 4,
            });
            _rxStateBytes = Encoding.UTF8.GetBytes(json);
        }
    }

    async void OnRxRead9898(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            if (req is null) return;
            byte[] snap;
            lock (_lock) snap = _rxStateBytes;
            int off = (int)req.Offset;
            if (off <= snap.Length) req.RespondWithValue(ToBuf(snap[off..]));
            else req.RespondWithProtocolError(GattProtocolError.InvalidOffset);
        }
        catch (Exception) { }
        finally { deferral.Complete(); }
    }

    static async void OnRxReadEmpty(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            req?.RespondWithValue(ToBuf([]));
        }
        catch (Exception) { }
        finally { deferral.Complete(); }
    }

    async void OnRxSubscribed9895(GattLocalCharacteristic sender, object args)
    {
        try
        {
            if (sender.SubscribedClients.Count > 0)
                await sender.NotifyValueAsync(ToBuf(Encoding.UTF8.GetBytes("ready")));
        }
        catch (Exception) { }
    }

    async void OnRxWrite9896(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        string text;
        try
        {
            var req = await args.GetRequestAsync();
            if (req is null) return;
            text = Encoding.UTF8.GetString(FromBuf(req.Value));
            if (req.Option == GattWriteOption.WriteWithResponse) req.Respond();
        }
        catch (Exception) { deferral.Complete(); return; }
        finally { deferral.Complete(); }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("key", out var keyProp) && keyProp.GetString() is string peerPubB64)
            {
                ReceiveSession? session;
                GattLocalCharacteristic? notifyChar;
                lock (_lock)
                {
                    notifyChar = _c9895;
                    if (_rxSession is not null && !_rxSession.StartedDownload && (DateTime.UtcNow - _rxSession.CreatedUtc).TotalSeconds > 3)
                    {
                        _rxSession.Cts.Cancel();
                        _rxSession = null;
                    }
                    if (_rxSession is not null || _rxEcdh is null)
                    {
                        _ = Notify9895Async(notifyChar, "3");
                        return;
                    }
                    using var peer = ECDiffieHellman.Create();
                    peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerPubB64), out _);
                    var shared = _rxEcdh.DeriveRawSecretAgreement(peer.PublicKey);
                    var aesKey = Encoding.ASCII.GetBytes(Convert.ToBase64String(shared))[..16];

                    var peerName = root.TryGetProperty("device_name", out var dn) ? TryDec(aesKey, dn.GetString()) ?? "Android" : "Android";
                    var brand = root.TryGetProperty("vender", out var vd) ? TryDec(aesKey, vd.GetString()) ?? "" : "";
                    var devType = root.TryGetProperty("device_type", out var dt) ? TryDec(aesKey, dt.GetString()) : "0";
                    var numStr = root.TryGetProperty("number", out var nb) ? TryDec(aesKey, nb.GetString()) : "1";
                    int.TryParse(numStr, out var count);
                    var ipHint = root.TryGetProperty("ip", out var ipP) ? TryDec(aesKey, ipP.GetString()) : null;

                    session = new ReceiveSession
                    {
                        Id = ReqPrefix + Guid.NewGuid().ToString("N")[..12],
                        AesKey = aesKey,
                        PeerName = peerName,
                        Brand = brand,
                        Kind = devType == "1" ? "tablet" : "phone",
                        Count = Math.Clamp(count, 1, 10000),
                        NetworkHint = IsPrivateIpv4(ipHint) ? ipHint : null,
                    };
                    _rxSession = session;
                }

                RefreshRxKey(busy: true);
                Say($"rx handshake from {session.PeerName} ({session.Brand}, {session.Count} files)");
                IncomingHandshake?.Invoke();
                await Notify9895Async(notifyChar, "6");

                // Safety expiry if phone cancels before sending route offer
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(8));
                    lock (_lock)
                    {
                        if (_rxSession == session && !session.StartedDownload)
                        {
                            _rxSession = null;
                            RefreshRxKey(busy: false);
                        }
                    }
                });
            }
            else
            {
                ReceiveSession? session;
                GattLocalCharacteristic? notifyChar;
                lock (_lock)
                {
                    session = _rxSession;
                    notifyChar = _c9895;
                }
                if (session is null || session.StartedDownload) return;

                string? host = root.TryGetProperty("ip", out var ipProp) ? TryDec(session.AesKey, ipProp.GetString()) : session.NetworkHint;
                string? portStr = root.TryGetProperty("port", out var portProp) ? TryDec(session.AesKey, portProp.GetString()) : null;
                if (string.IsNullOrEmpty(host) || !int.TryParse(portStr, out var port) || port is < 1 or > 65535) return;
                if (!IsPrivateIpv4(host)) return;

                bool isLan = root.TryGetProperty("wlan", out _);
                var localIp = PickLocalIpv4(host);
                if (localIp is null)
                {
                    Say($"rx route {host}:{port} unreachable from local subnets");
                    await Notify9895Async(notifyChar, "cancel");
                    FinishRxSession(session);
                    return;
                }

                session.StartedDownload = true;
                _activeCancels[session.Id] = session.Cts;

                UdpClient? udp = null;
                Task<double> bwTask = Task.FromResult(0.0);
                try
                {
                    udp = new UdpClient(new IPEndPoint(IPAddress.Parse(localIp), port + 1));
                    udp.Client.ReceiveBufferSize = 1 << 20;
                    bwTask = MeasureUdpBandwidthAsync(udp, host, session.Cts.Token);
                }
                catch (Exception ex) { Say($"rx udp bind warn: {ex.Message}"); }

                if (isLan)
                {
                    var acc = JsonSerializer.Serialize(new
                    {
                        wlan = "wlan",
                        wlan_accept = true,
                        ip = EncryptAesCbc(session.AesKey, localIp),
                    });
                    await Notify9895Async(notifyChar, acc);
                }

                _ = Task.Run(() => RunReceiveTransferAsync(session, host, port, udp, bwTask, notifyChar));
            }
        }
        catch (Exception e)
        {
            Say($"rx write9896 error: {e.Message}");
        }
    }

    async Task RunReceiveTransferAsync(ReceiveSession session, string host, int port, UdpClient? udp, Task<double> bwTask, GattLocalCharacteristic? notifyChar)
    {
        var ct = session.Cts.Token;
        string? tmpZip = null;
        try
        {
            using var ws = new ClientWebSocket();
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            await ConnectWsWithRetryAsync(ws, new Uri($"wss://{host}:{port}/websocket"), ct);

            async Task SendWs(string msg) =>
                await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);

            async Task<double> QuickBandwidthAsync()
            {
                try
                {
                    var finished = await Task.WhenAny(bwTask, Task.Delay(250, ct));
                    double spd = finished == bwTask ? await bwTask : 0.0;
                    return spd > 0.05 ? spd : 10.0;
                }
                catch (Exception) { return 10.0; }
            }

            // ColorOS expects the receiver to proactively send ack:0:wlanBandwidth right after /websocket opens
            // (it closes the WebSocket after ~1s if not received).
            double speed = await QuickBandwidthAsync();
            udp?.Dispose();
            udp = null;
            await SendWs($"ack:0:wlanBandwidth?{{\"speed\":\"{speed:F2}\"}}");

            var buf = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                string taskId = "";
                string fileName = "files";
                long totalSize = 0;
                int fileCount = session.Count;
                string mimeType = "*/*";
                string? thumbPath = null;
                string sendReqIdx = "0";

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var msg = await ReceiveWsMessageAsync(ws, buf, ct);
                    if (msg is null) break;
                    var (kind, idxStr, act, body) = ParseWire(msg);
                    if (kind != "action") continue;

                    if (act == "wlanBandwidth")
                    {
                        await SendWs($"ack:{idxStr}:{act}?{{\"speed\":\"{speed:F2}\"}}");
                    }
                    else if (act == "versionNegotiation")
                    {
                        await SendWs($"ack:{idxStr}:{act}?{{\"version\":1,\"threadLimit\":1}}");
                    }
                    else if (act == "sendRequest")
                    {
                        sendReqIdx = idxStr;
                        using var sdoc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                        var r = sdoc.RootElement;
                        taskId = r.TryGetProperty("id", out var idP) ? idP.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(taskId) && r.TryGetProperty("taskId", out var tidP)) taskId = tidP.GetString() ?? "";
                        fileName = r.TryGetProperty("fileName", out var fnP) ? fnP.GetString() ?? "files" : "files";
                        totalSize = r.TryGetProperty("totalSize", out var tsP) ? tsP.GetInt64() : 0;
                        fileCount = r.TryGetProperty("fileCount", out var fcP) ? fcP.GetInt32() : session.Count;
                        mimeType = r.TryGetProperty("mimeType", out var mtP) ? mtP.GetString() ?? "*/*" : "*/*";
                        if (r.TryGetProperty("thumbnail", out var thP) && thP.GetString() is { Length: > 0 } tp) thumbPath = tp;
                        break;
                    }
                    else if (act == "status")
                    {
                        await SendWs($"ack:{idxStr}:{act}");
                        Emit(new JsonObject { ["event"] = "lost", ["id"] = session.Id, ["reason"] = "cancelled" });
                        return;
                    }
                    else
                    {
                        await SendWs($"ack:{idxStr}:{act}");
                    }
                }

                if (string.IsNullOrEmpty(taskId))
                    throw new IOException("Sender closed before sendRequest");

                // Fetch optional preview thumbnail for Toast hero image
                string? previewB64 = null;
                using var handler = new SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
                    ConnectCallback = FastConnectCallback,
                };
                using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                if (!string.IsNullOrEmpty(thumbPath))
                {
                    try
                    {
                        using var tCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        tCts.CancelAfter(TimeSpan.FromSeconds(3));
                        var tb = await http.GetByteArrayAsync($"https://{host}:{port}{thumbPath}", tCts.Token);
                        if (tb.Length is > 0 and <= 2 * 1024 * 1024)
                            previewB64 = Convert.ToBase64String(tb);
                    }
                    catch (Exception) { }
                }

                // Emit standard "asked" event -> NekoDrop decides auto-accept or prompts user!
                var filesArr = new JsonArray();
                for (int i = 0; i < Math.Max(1, fileCount); i++)
                    filesArr.Add(new JsonObject { ["name"] = i == 0 ? fileName : $"file_{i}", ["type"] = mimeType });

                var decisionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _rxDecisions[session.Id] = decisionTcs;
                var askEv = new JsonObject
                {
                    ["event"] = "asked",
                    ["id"] = session.Id,
                    ["sender"] = session.PeerName,
                    ["model"] = BuildModelLabel(session.Brand, session.Kind),
                    ["files"] = filesArr,
                    ["waits"] = true,
                    ["contact"] = false,
                    ["protocol"] = "mta2",
                };
                if (previewB64 is not null) askEv["icon"] = previewB64;
                Emit(askEv);

                bool accepted;
                try { accepted = await decisionTcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct); }
                catch (TimeoutException) { accepted = false; }
                finally { _rxDecisions.TryRemove(session.Id, out _); }

                if (!accepted)
                {
                    try { await SendWs($"action:101:status?{{\"taskId\":\"{EscapeJson(taskId)}\",\"id\":\"{EscapeJson(taskId)}\",\"type\":3,\"reason\":\"user refuse\"}}"); }
                    catch (Exception) { }
                    await Notify9895Async(notifyChar, "3");
                    return;
                }

                await SendWs($"ack:{sendReqIdx}:sendRequest");

                // Download & unpack: stream-extract ZIP_STORED directly to saveDir (zero temp file, 50% less disk IO),
                // falling back to a temporary .zip only if entries are compressed or use data descriptors.
                string saveDir;
                lock (_lock) saveDir = _saveFolder;
                Directory.CreateDirectory(saveDir);

                var dlUrl = $"https://{host}:{port}/download?taskId={Uri.EscapeDataString(taskId)}";
                using var resp = await http.GetAsync(dlUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var expectedTotal = resp.Content.Headers.ContentLength ?? totalSize;

                long doneBytes = 0;
                var lastReport = Environment.TickCount64;
                void ReportRx(bool force = false)
                {
                    var now = Environment.TickCount64;
                    if (!force && now - lastReport < 120) return;
                    lastReport = now;
                    Emit(new JsonObject
                    {
                        ["event"] = "incoming",
                        ["id"] = session.Id,
                        ["done"] = (double)doneBytes,
                        ["total"] = (double)Math.Max(expectedTotal, doneBytes),
                    });
                }

                var savedNames = new JsonArray();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                var (streamSaved, prefixBytes) = await TryStreamUnpackStoredZipAsync(src, saveDir, n => { doneBytes += n; ReportRx(); }, ct);
                if (streamSaved is not null)
                {
                    foreach (var name in streamSaved) savedNames.Add(name);
                    ReportRx(force: true);
                }
                else
                {
                    tmpZip = Path.Combine(saveDir, $".nekodrop_mta_{Guid.NewGuid():N}.zip");
                    var dlBuf = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                    try
                    {
                        await using var fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (prefixBytes.Length > 0)
                        {
                            await fs.WriteAsync(prefixBytes, ct);
                            doneBytes += prefixBytes.Length;
                            ReportRx();
                        }
                        int n;
                        while ((n = await src.ReadAsync(dlBuf.AsMemory(0, dlBuf.Length), ct)) > 0)
                        {
                            await fs.WriteAsync(dlBuf.AsMemory(0, n), ct);
                            doneBytes += n;
                            ReportRx();
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(dlBuf); }
                    ReportRx(force: true);

                    using (var za = ZipFile.OpenRead(tmpZip))
                    {
                        foreach (var entry in za.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;
                            var safe = SanitizeFileName(entry.Name);
                            var dst = UniqueFilePath(saveDir, safe);
                            await using var es = entry.Open();
                            await using var outFs = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
                            await es.CopyToAsync(outFs, 1024 * 1024, ct);
                            savedNames.Add(Path.GetFileName(dst));
                        }
                    }
                }

                await SendWs($"action:100:status?{{\"taskId\":\"{EscapeJson(taskId)}\",\"id\":\"{EscapeJson(taskId)}\",\"type\":1,\"reason\":\"ok\"}}");
                try
                {
                    using var ackCts = new CancellationTokenSource(1500);
                    await ReceiveWsMessageAsync(ws, buf, ackCts.Token);
                }
                catch (Exception) { }

                Emit(new JsonObject
                {
                    ["event"] = "stored",
                    ["id"] = session.Id,
                    ["folder"] = saveDir,
                    ["names"] = savedNames,
                    ["protocol"] = "mta2",
                });
                Say($"rx complete from {session.PeerName}: {savedNames.Count} file(s), {doneBytes} bytes");
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
        catch (OperationCanceledException)
        {
            await Notify9895Async(notifyChar, "cancel");
            Emit(new JsonObject { ["event"] = "lost", ["id"] = session.Id, ["reason"] = "cancelled" });
        }
        catch (Exception e)
        {
            Say($"rx failed: {e.GetType().Name}: {e.Message}");
            await Notify9895Async(notifyChar, "cancel");
            Emit(new JsonObject { ["event"] = "lost", ["id"] = session.Id, ["reason"] = e.Message });
        }
        finally
        {
            udp?.Dispose();
            if (tmpZip is not null) try { File.Delete(tmpZip); } catch (Exception) { }
            FinishRxSession(session);
        }
    }

    void FinishRxSession(ReceiveSession session)
    {
        _activeCancels.TryRemove(session.Id, out _);
        _rxDecisions.TryRemove(session.Id, out _);
        lock (_lock)
        {
            if (_rxSession == session)
            {
                _rxSession = null;
                RefreshRxKey(busy: false);
            }
        }
        session.Cts.Dispose();
    }

    static async Task Notify9895Async(GattLocalCharacteristic? ch, string value)
    {
        if (ch is null) return;
        try { await ch.NotifyValueAsync(ToBuf(Encoding.UTF8.GetBytes(value))); }
        catch (Exception) { }
    }

    // ========================================================= BLE Send (v2)

    async Task RunSendV2Async(string sendId, MtaPeer peer, IReadOnlyList<string> filePaths, CancellationToken ct)
    {
        var localIp = PickLocalIpv4(null) ?? throw new IOException(L.T("mta.err.noLan"));
        var fileInfos = filePaths.Select(p => new FileInfo(p)).ToList();
        if (fileInfos.Count == 0 || fileInfos.Any(f => !f.Exists))
            throw new FileNotFoundException("not_a_file");

        // Precompute Slicing-by-8 CRC32 & ZIP_STORED layout in parallel with BLE connect & user Accept!
        var zipPlanTask = Task.Run(() => ZipStoredPlan.Build(fileInfos, ct), ct);
        long totalPayloadBytes = fileInfos.Sum(f => f.Length);
        var mimeType = ComputeMimeType(fileInfos);
        var taskId = Guid.NewGuid().ToString("N");
        string ourName;
        lock (_lock) ourName = _deviceName;

        using var bleDev = await BluetoothLEDevice.FromBluetoothAddressAsync(peer.Address)
            ?? throw new IOException(L.T("mta.err.bleConnect"));
        using var gattSession = await GattSession.FromDeviceIdAsync(bleDev.BluetoothDeviceId);
        gattSession.MaintainConnection = true;

        var svcsRes = await bleDev.GetGattServicesForUuidAsync(UuidSvc9999, BluetoothCacheMode.Uncached);
        var svc9999 = svcsRes.Services.FirstOrDefault() ?? throw new IOException(L.T("mta.err.gattMissing"));
        var chRes = await svc9999.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        var c9897 = chRes.Characteristics.FirstOrDefault(c => c.Uuid == UuidChr9897);
        var c9896 = chRes.Characteristics.FirstOrDefault(c => c.Uuid == UuidChr9896);
        if (c9897 is null || c9896 is null) throw new IOException(L.T("mta.err.gattMissing"));

        var readRes = await c9897.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (readRes.Status != GattCommunicationStatus.Success)
            throw new IOException($"GATT 9897 read failed ({readRes.Status})");
        var replyText = Encoding.UTF8.GetString(FromBuf(readRes.Value));
        using var replyDoc = JsonDocument.Parse(replyText);
        if (replyDoc.RootElement.TryGetProperty("state", out var st) && st.GetInt32() != 0)
            throw new IOException(L.T("mta.err.peerBusy"));
        var peerPubB64 = replyDoc.RootElement.GetProperty("key").GetString()!;

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var peerKey = ECDiffieHellman.Create();
        peerKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerPubB64), out _);
        var shared = ecdh.DeriveRawSecretAgreement(peerKey.PublicKey);
        var aesKey = Encoding.ASCII.GetBytes(Convert.ToBase64String(shared))[..16];
        var ourPubB64 = Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());

        // Start pure-BCL TLSv1.2/1.3 HTTPS + WSS server BEFORE writing 9896
        using var cert = CreateSelfSignedCert();
        using var server = BindLanListener(localIp, out int boundPort);

        var wsConnectedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var doneTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool downloaded = false, acked = false;
        using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var acceptLoop = Task.Run(async () =>
        {
            while (!serverCts.Token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await server.AcceptTcpClientAsync(serverCts.Token); }
                catch (OperationCanceledException) { break; }
                catch (Exception) { break; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        client.NoDelay = true;
                        client.SendBufferSize = 1 << 20;
                        using var c = client;
                        await using var ssl = new SslStream(c.GetStream(), false);
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = cert,
                            ClientCertificateRequired = false,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        }, serverCts.Token);

                        var req = await ReadHttpRequestAsync(ssl, serverCts.Token);
                        if (req is null) return;
                        var remoteIp = ((IPEndPoint?)c.Client.RemoteEndPoint)?.Address.MapToIPv4().ToString() ?? "";

                        if (req.Path == "/websocket" && req.Headers.TryGetValue("sec-websocket-key", out var wsKey))
                        {
                            wsConnectedTcs.TrySetResult(remoteIp);
                            await WriteWsUpgradeResponseAsync(ssl, wsKey, serverCts.Token);
                            using var ws = WebSocket.CreateFromStream(ssl, new WebSocketCreationOptions { IsServer = true });

                            // Unfragmented 1400B UDP bandwidth burst to remoteIp:(boundPort + 1)
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(120, serverCts.Token);
                                    using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(localIp), 0));
                                    var ep = new IPEndPoint(IPAddress.Parse(remoteIp), boundPort + 1);
                                    var small = new byte[1400];
                                    for (int i = 0; i < 360 && !serverCts.Token.IsCancellationRequested; i++)
                                    {
                                        await udp.SendAsync(small, ep, serverCts.Token);
                                        if ((i & 7) == 7) await Task.Delay(1, serverCts.Token);
                                    }
                                }
                                catch (Exception) { }
                            });

                            async Task SendWs(string text) =>
                                await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, serverCts.Token);

                            await SendWs("action:0:wlanBandwidth?{}");

                            bool sentVersion = false;
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(3500, serverCts.Token);
                                if (!sentVersion && ws.State == WebSocketState.Open)
                                {
                                    sentVersion = true;
                                    try { await SendWs("action:1:versionNegotiation?{\"versions\":[1],\"version\":1}"); } catch (Exception) { }
                                }
                            });

                            var wbuf = ArrayPool<byte>.Shared.Rent(65536);
                            try
                            {
                                while (ws.State == WebSocketState.Open && !serverCts.Token.IsCancellationRequested)
                                {
                                    var msg = await ReceiveWsMessageAsync(ws, wbuf, serverCts.Token);
                                    if (msg is null) break;
                                    var (kind, idxStr, act, body) = ParseWire(msg);
                                    if (kind == "ack" && act == "wlanBandwidth" && !sentVersion)
                                    {
                                        sentVersion = true;
                                        await SendWs("action:1:versionNegotiation?{\"versions\":[1],\"version\":1}");
                                    }
                                    else if (kind == "ack" && act == "versionNegotiation")
                                    {
                                        var reqJson = JsonSerializer.Serialize(new
                                        {
                                            id = taskId,
                                            senderId = taskId[..12],
                                            senderName = ourName,
                                            fileName = fileInfos[0].Name,
                                            fileCount = fileInfos.Count,
                                            number = fileInfos.Count.ToString(),
                                            totalSize = totalPayloadBytes,
                                            mimeType,
                                            wlan = true,
                                        });
                                        await SendWs($"action:2:sendRequest?{reqJson}");
                                    }
                                    else if (kind == "action")
                                    {
                                        await SendWs(string.IsNullOrEmpty(body) ? $"ack:{idxStr}:{act}" : $"ack:{idxStr}:{act}?{body}");
                                        if (act == "status" && !string.IsNullOrEmpty(body))
                                        {
                                            using var sdoc = JsonDocument.Parse(body);
                                            var type = sdoc.RootElement.TryGetProperty("type", out var tp) ? tp.GetInt32() : 0;
                                            if (type == 1) { acked = true; if (downloaded) doneTcs.TrySetResult("sent"); }
                                            else if (type == 3) { doneTcs.TrySetResult("declined"); }
                                            else if (type == 2) { doneTcs.TrySetResult("failed"); }
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(wbuf);
                                if (downloaded && !doneTcs.Task.IsCompleted) doneTcs.TrySetResult("sent");
                            }
                        }
                        else if (req.Path == "/download")
                        {
                            if (!req.Query.TryGetValue("taskId", out var qTask) || qTask != taskId)
                            {
                                await WriteSimpleHttpAsync(ssl, "403 Forbidden", "text/plain", "Forbidden", serverCts.Token);
                                return;
                            }
                            var zipPlan = await zipPlanTask;
                            var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {zipPlan.TotalZipSize}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                            await ssl.WriteAsync(Encoding.ASCII.GetBytes(header), serverCts.Token);

                            var lastTick = Environment.TickCount64;
                            await zipPlan.StreamToAsync(ssl, (done, total) =>
                            {
                                var now = Environment.TickCount64;
                                if (now - lastTick >= 120 || done >= total)
                                {
                                    lastTick = now;
                                    Emit(new JsonObject
                                    {
                                        ["event"] = "send",
                                        ["id"] = sendId,
                                        ["state"] = "sending",
                                        ["done"] = (double)done,
                                        ["total"] = (double)total,
                                    });
                                }
                            }, serverCts.Token);
                            await ssl.FlushAsync(serverCts.Token);
                            downloaded = true;
                            if (acked) doneTcs.TrySetResult("sent");
                        }
                        else
                        {
                            await WriteSimpleHttpAsync(ssl, "404 Not Found", "text/plain", "Not Found", serverCts.Token);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Say($"send https conn: {ex.Message}");
                    }
                });
            }
        });

        try
        {
            // Trigger native accept card on Android peer
            var initPayload = JsonSerializer.Serialize(new
            {
                key = ourPubB64,
                version = EncryptAesCbc(aesKey, "10014"),
                pv = 4,
                isFast = 0,
                number = EncryptAesCbc(aesKey, fileInfos.Count.ToString()),
                type = EncryptAesCbc(aesKey, mimeType),
                device_name = EncryptAesCbc(aesKey, ourName),
                random_code = EncryptAesCbc(aesKey, ""),
            });
            var initRes = await c9896.WriteValueAsync(ToBuf(Encoding.UTF8.GetBytes(initPayload)), GattWriteOption.WriteWithResponse);
            if (initRes != GattCommunicationStatus.Success)
                throw new IOException($"GATT 9896 initial write failed ({initRes})");

            Emit(new JsonObject { ["event"] = "send", ["id"] = sendId, ["state"] = "asking" });

            // Blind periodic LAN offer on 9896 until peer connects to /websocket (bypasses missing 9898 CCCD on WinRT!)
            var offerBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                wlan = "wlan",
                ip = EncryptAesCbc(aesKey, localIp),
                port = EncryptAesCbc(aesKey, boundPort.ToString()),
            }));
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 30 && !wsConnectedTcs.Task.IsCompleted && !doneTcs.Task.IsCompleted && !serverCts.Token.IsCancellationRequested; i++)
                {
                    await Task.Delay(1500, serverCts.Token);
                    if (wsConnectedTcs.Task.IsCompleted || doneTcs.Task.IsCompleted) break;
                    try
                    {
                        var r = await c9896.WriteValueAsync(ToBuf(offerBytes), GattWriteOption.WriteWithResponse);
                        if (r != GattCommunicationStatus.Success) break;
                    }
                    catch (Exception) { break; }
                }
            });

            string outcome;
            try
            {
                outcome = await doneTcs.Task.WaitAsync(TimeSpan.FromMinutes(10), ct);
            }
            catch (TimeoutException)
            {
                outcome = "failed";
            }

            if (outcome == "sent")
                Emit(new JsonObject { ["event"] = "send", ["id"] = sendId, ["state"] = "sent", ["done"] = (double)totalPayloadBytes, ["total"] = (double)totalPayloadBytes });
            else if (outcome == "declined")
                Emit(new JsonObject { ["event"] = "send", ["id"] = sendId, ["state"] = "declined" });
            else
                Emit(new JsonObject { ["event"] = "send", ["id"] = sendId, ["state"] = "failed", ["reason"] = L.T("mta.err.timeout") });
        }
        finally
        {
            serverCts.Cancel();
            try { server.Stop(); } catch (Exception) { }
            try { await acceptLoop.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        }
    }

    // ===================================================== BLE Diagnostics

    /// <summary>
    /// Scans nearby BLE advertisements for 8 seconds and writes a privacy-redacted
    /// protocol report (no personal device names, no real MACs, no IPs) so users
    /// with Xiaomi / vivo / Honor / Huawei phones can submit discovery fingerprints.
    /// </summary>
    public static async Task<string> ScanDiagnosticsAsync(int seconds = 8, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"nekodrop MTA BLE Diagnostic Report ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine("Privacy: device names and Bluetooth MAC addresses are redacted.");
        sb.AppendLine();

        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            sb.AppendLine("Adapter: NONE");
            return sb.ToString();
        }
        sb.AppendLine($"Adapter: LE={adapter.IsLowEnergySupported}, Peripheral={adapter.IsPeripheralRoleSupported}, ExtAdv={adapter.IsExtendedAdvertisingSupported}, MaxAdvLen={adapter.MaxAdvertisementDataLength}");
        sb.AppendLine();

        var addrMap = new Dictionary<ulong, int>();
        var records = new ConcurrentDictionary<string, string>();
        var mtaAddresses = new ConcurrentDictionary<ulong, string>();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
            AllowExtendedAdvertisements = true,
        };
        watcher.Received += (_, e) =>
        {
            int alias;
            lock (addrMap)
            {
                if (!addrMap.TryGetValue(e.BluetoothAddress, out alias))
                    addrMap[e.BluetoothAddress] = alias = addrMap.Count + 1;
            }
            var adv = e.Advertisement;
            var parts = new List<string>();
            bool interesting = false;

            foreach (var u in adv.ServiceUuids)
            {
                var us = u.ToString();
                if (u == UuidAdv3331) { parts.Add("SvcUUID=3331(MTA-v1)"); interesting = true; mtaAddresses.TryAdd(e.BluetoothAddress, "v1-3331"); }
                else parts.Add("SvcUUID=" + (us.EndsWith("-0000-1000-8000-00805f9b34fb") ? us[4..8] : us));
            }
            foreach (var sec in adv.GetSectionsByType(0x16))
            {
                var raw = FromBuf(sec.Data);
                if (raw.Length < 2) continue;
                ushort u16 = (ushort)(raw[0] | (raw[1] << 8));
                var p = raw[2..];
                interesting = true;
                if (u16 == 0x3333)
                {
                    var id = Encoding.ASCII.GetString(p).TrimEnd('\0');
                    var fields = id.Split('-');
                    var safe3333 = fields.Length >= 2 ? $"<id:{fields[0].Length}ch>-{string.Join('-', fields.Skip(1))}" : $"len={p.Length}";
                    parts.Add($"SD[3333]({p.Length}B)={safe3333}");
                    mtaAddresses.TryAdd(e.BluetoothAddress, "v2-3333");
                }
                else if (u16 == 0x3334)
                {
                    parts.Add($"SD[3334]({p.Length}B)=<redacted-name>");
                    mtaAddresses.TryAdd(e.BluetoothAddress, "v2-3334");
                }
                else if (u16 == 0xFFFF && p.Length == 27)
                {
                    parts.Add($"SD[FFFF](27B,tail=0x{p[26]:X2})=<redacted-v1-name>");
                    mtaAddresses.TryAdd(e.BluetoothAddress, "v1-FFFF");
                }
                else if (p.Length == 6 && (u16 >> 8) <= 1)
                {
                    parts.Add($"SD[0x{u16:X4}](6B,5G={u16 >> 8},brand={u16 & 0xFF})");
                    mtaAddresses.TryAdd(e.BluetoothAddress, "v1-01XX");
                }
                else
                {
                    parts.Add($"SD[0x{u16:X4}]({p.Length}B,head={Convert.ToHexString(p.Take(Math.Min(4, p.Length)).ToArray())})");
                }
            }
            foreach (var m in adv.ManufacturerData)
            {
                var d = FromBuf(m.Data);
                // Record known phone vendor company IDs (Xiaomi 0x038F, Huawei 0x027D, OPPO 0x079A, Samsung 0x0075, Google 0x00E0, Apple 0x004C, vivo/Honor)
                if (m.CompanyId is 0x038F or 0x027D or 0x079A or 0x0075 or 0x00E0 or 0x004C or 0x0528 or 0x0664)
                {
                    interesting = true;
                    parts.Add($"Mfg[0x{m.CompanyId:X4}]({d.Length}B,head={Convert.ToHexString(d.Take(Math.Min(4, d.Length)).ToArray())})");
                }
            }
            if (!interesting) return;
            var key = $"dev#{alias}:{e.AdvertisementType}:{string.Join("|", parts)}";
            records.TryAdd(key, $"dev#{alias,-3} RSSI={e.RawSignalStrengthInDBm,4} {e.AdvertisementType,-22} | {string.Join(" ; ", parts)}");
        };

        watcher.Start();
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); }
        finally { watcher.Stop(); }

        sb.AppendLine($"--- Captured Advertisements ({records.Count}) ---");
        foreach (var line in records.Values.OrderBy(x => x)) sb.AppendLine(line);

        // Read-only GATT service enumeration on MTA-like peers (at most 3 devices, 4s each)
        sb.AppendLine();
        sb.AppendLine("--- Read-only GATT Service UUIDs (MTA candidates) ---");
        foreach (var (addr, tag) in mtaAddresses.Take(3))
        {
            var alias = addrMap.GetValueOrDefault(addr);
            try
            {
                using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr).AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct);
                if (dev is null) continue;
                var svcs = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct);
                sb.AppendLine($"dev#{alias} ({tag}): status={svcs.Status}, services={svcs.Services.Count}");
                foreach (var svc in svcs.Services)
                {
                    var su = svc.Uuid.ToString();
                    var shortSvc = su.EndsWith("-0000-1000-8000-00805f9b34fb") ? su[4..8] : su;
                    var chs = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct);
                    var chList = string.Join(", ", chs.Characteristics.Select(c =>
                    {
                        var cu = c.Uuid.ToString();
                        var sc = cu.EndsWith("-0000-1000-8000-00805f9b34fb") ? cu[4..8] : cu[..8];
                        return $"{sc}({c.CharacteristicProperties})";
                    }));
                    sb.AppendLine($"  svc {shortSvc}: {chList}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"dev#{alias} ({tag}): gatt probe skipped ({ex.GetType().Name})");
            }
        }

        return sb.ToString();
    }

    // ===================================================== Helpers & Crypto

    static IBuffer ToBuf(byte[] bytes)
    {
        var w = new DataWriter();
        w.WriteBytes(bytes);
        return w.DetachBuffer();
    }

    static byte[] FromBuf(IBuffer buf)
    {
        var bytes = new byte[buf.Length];
        DataReader.FromBuffer(buf).ReadBytes(bytes);
        return bytes;
    }

    static string EncryptAesCbc(byte[] key, string plain)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = FixedIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        var data = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(enc.TransformFinalBlock(data, 0, data.Length));
    }

    static string? TryDec(byte[] key, string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = FixedIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            var cipher = Convert.FromBase64String(b64);
            using var dec = aes.CreateDecryptor();
            return Encoding.UTF8.GetString(dec.TransformFinalBlock(cipher, 0, cipher.Length));
        }
        catch (Exception) { return null; }
    }

    static X509Certificate2 CreateSelfSignedCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=NekoDrop", ecdsa, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable);
    }

    static TcpListener BindLanListener(string localIp, out int port)
    {
        var ip = IPAddress.Parse(localIp);
        foreach (var p in PreferredPorts)
        {
            var l = new TcpListener(ip, p);
            try { l.Start(8); port = p; return l; }
            catch (SocketException) { try { l.Stop(); } catch (Exception) { } }
        }
        var ep = new TcpListener(ip, 0);
        ep.Start(8);
        port = ((IPEndPoint)ep.LocalEndpoint).Port;
        return ep;
    }

    static bool IsPrivateIpv4(string? ipStr)
    {
        if (string.IsNullOrEmpty(ipStr) || !IPAddress.TryParse(ipStr, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    static string? PickLocalIpv4(string? peerHint)
    {
        var candidates = new List<(IPAddress Addr, IPAddress Mask, bool Prefer)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var desc = nic.Description;
            if (desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("WSL", StringComparison.OrdinalIgnoreCase)) continue;
            var hasGw = nic.GetIPProperties().GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                if (!IsPrivateIpv4(ua.Address.ToString())) continue;
                candidates.Add((ua.Address, ua.IPv4Mask, hasGw || nic.Name == "nekodrop"));
            }
        }
        if (peerHint is not null && IPAddress.TryParse(peerHint, out var peerIp))
        {
            var pb = peerIp.GetAddressBytes();
            foreach (var (addr, mask, _) in candidates.OrderByDescending(x => x.Prefer))
            {
                var ab = addr.GetAddressBytes();
                var mb = mask.GetAddressBytes();
                if ((ab[0] & mb[0]) == (pb[0] & mb[0]) && (ab[1] & mb[1]) == (pb[1] & mb[1]) &&
                    (ab[2] & mb[2]) == (pb[2] & mb[2]) && (ab[3] & mb[3]) == (pb[3] & mb[3]))
                    return addr.ToString();
            }
        }
        return candidates.OrderByDescending(x => x.Prefer).FirstOrDefault().Addr?.ToString();
    }

    static async ValueTask<Stream> FastConnectCallback(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var sock = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            SendBufferSize = 1 << 20,
        };
        try
        {
            await sock.ConnectAsync(ctx.DnsEndPoint, ct);
            return new NetworkStream(sock, ownsSocket: true);
        }
        catch
        {
            sock.Dispose();
            throw;
        }
    }

    static async Task ConnectWsWithRetryAsync(ClientWebSocket ws, Uri uri, CancellationToken ct)
    {
        int[] delays = [150, 350, 700];
        for (int attempt = 0; ; attempt++)
        {
            try { await ws.ConnectAsync(uri, ct); return; }
            catch (Exception) when (attempt < delays.Length && !ct.IsCancellationRequested)
            {
                await Task.Delay(delays[attempt], ct);
            }
        }
    }

    static async Task<double> MeasureUdpBandwidthAsync(UdpClient udp, string expectedHost, CancellationToken ct)
    {
        long total = 0;
        DateTime? t0 = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(2200);
        try
        {
            while (total < 262144)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                if (r.RemoteEndPoint.Address.MapToIPv4().ToString() != expectedHost) continue;
                t0 ??= DateTime.UtcNow;
                total += r.Buffer.Length;
            }
        }
        catch (Exception) { }
        if (!t0.HasValue || total == 0) return 0.0;
        var dt = Math.Max((DateTime.UtcNow - t0.Value).TotalSeconds, 0.001);
        return total / 1048576.0 / dt;
    }

    static async Task<string?> ReceiveWsMessageAsync(WebSocket ws, byte[] buf, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > 256 * 1024) throw new IOException("WebSocket frame too large");
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    static (string Kind, string Index, string Action, string Body) ParseWire(string raw)
    {
        var qIdx = raw.IndexOf('?');
        var head = qIdx >= 0 ? raw[..qIdx] : raw;
        var body = qIdx >= 0 ? raw[(qIdx + 1)..] : "";
        var parts = head.Split(':', 3);
        return parts.Length < 3 ? ("", "", "", "") : (parts[0], parts[1], parts[2], body);
    }

    static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static string ComputeMimeType(List<FileInfo> files)
    {
        if (files.All(f => MediaHelper.IsImage(f.FullName)))
            return files.Count == 1 && files[0].Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        if (files.All(f => MediaHelper.IsMotion(f.FullName))) return "video/mp4";
        return "*/*";
    }

    static string SanitizeFileName(string name)
    {
        var leaf = Path.GetFileName(name.Replace('/', Path.DirectorySeparatorChar));
        foreach (var bad in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(bad, '_');
        return string.IsNullOrWhiteSpace(leaf) || leaf is "." or ".." ? "file" : leaf;
    }

    static string UniqueFilePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    // Minimal pure-BCL HTTP/1.1 request parser (zero ASP.NET Core dependency)
    sealed record HttpReq(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers);

    static async Task<HttpReq?> ReadHttpRequestAsync(Stream stream, CancellationToken ct)
    {
        var headerBuf = new MemoryStream();
        var one = new byte[1];
        int match = 0;
        while (headerBuf.Length < 32768)
        {
            int n = await stream.ReadAsync(one, ct);
            if (n == 0) return null;
            byte b = one[0];
            headerBuf.WriteByte(b);
            match = (match, b) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0,
            };
            if (match == 4) break;
        }
        if (match != 4) return null;
        var text = Encoding.ASCII.GetString(headerBuf.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        var reqParts = lines[0].Split(' ');
        if (reqParts.Length < 2) return null;

        var rawTarget = reqParts[1];
        var qIdx = rawTarget.IndexOf('?');
        var path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (qIdx >= 0)
        {
            foreach (var pair in rawTarget[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var col = lines[i].IndexOf(':');
            if (col > 0) headers[lines[i][..col].Trim()] = lines[i][(col + 1)..].Trim();
        }
        return new HttpReq(reqParts[0], path, query, headers);
    }

    static async Task WriteWsUpgradeResponseAsync(Stream stream, string secWebSocketKey, CancellationToken ct)
    {
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(secWebSocketKey.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var resp = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), ct);
        await stream.FlushAsync(ct);
    }

    static async Task WriteSimpleHttpAsync(Stream stream, string status, string contentType, string body, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var resp = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), ct);
        await stream.WriteAsync(bodyBytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Fast-path zero-temp-file extractor for standard ZIP_STORED streams (method 0, no data descriptor).
    /// Returns saved file names on success, or null (with any already-read bytes in prefix) if the stream
    /// uses compression/data-descriptors so the caller falls back cleanly to temp-file ZipFile extraction.
    /// </summary>
    static async Task<(List<string>? Saved, byte[] Prefix)> TryStreamUnpackStoredZipAsync(
        Stream src, string saveDir, Action<long> onBytes, CancellationToken ct)
    {
        var hdr = new byte[30];
        int got = await ReadExactOrPartialAsync(src, hdr, ct);
        if (got < 30) return (null, hdr[..got]);

        uint sig = BitConverter.ToUInt32(hdr, 0);
        ushort flags = BitConverter.ToUInt16(hdr, 6);
        ushort method = BitConverter.ToUInt16(hdr, 8);
        if (sig != 0x04034b50u || method != 0 || (flags & 0x08) != 0)
            return (null, hdr);

        // First entry is STORED with known size! Stream-unpack entries directly.
        var saved = new List<string>();
        var created = new List<string>();
        var buf = ArrayPool<byte>.Shared.Rent(512 * 1024);
        try
        {
            onBytes(30);
            while (true)
            {
                uint comp32 = BitConverter.ToUInt32(hdr, 18);
                uint uncomp32 = BitConverter.ToUInt32(hdr, 22);
                ushort nameLen = BitConverter.ToUInt16(hdr, 26);
                ushort extraLen = BitConverter.ToUInt16(hdr, 28);

                var nameBytes = new byte[nameLen];
                if (nameLen > 0)
                {
                    if (await ReadExactOrPartialAsync(src, nameBytes, ct) != nameLen)
                        throw new EndOfStreamException("Truncated ZIP filename");
                    onBytes(nameLen);
                }

                long entrySize = uncomp32;
                if (extraLen > 0)
                {
                    var extra = new byte[extraLen];
                    if (await ReadExactOrPartialAsync(src, extra, ct) != extraLen)
                        throw new EndOfStreamException("Truncated ZIP extra field");
                    onBytes(extraLen);
                    if (uncomp32 == 0xFFFFFFFFu || comp32 == 0xFFFFFFFFu)
                        entrySize = ParseZip64UncompressedSize(extra);
                }

                var rawName = Encoding.UTF8.GetString(nameBytes);
                bool isDir = rawName.EndsWith('/') || rawName.EndsWith('\\');
                if (!isDir && !string.IsNullOrEmpty(rawName))
                {
                    var safe = SanitizeFileName(rawName);
                    var dst = UniqueFilePath(saveDir, safe);
                    created.Add(dst);
                    await using var outFs = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 2 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    long rem = entrySize;
                    while (rem > 0)
                    {
                        int want = (int)Math.Min(buf.Length, rem);
                        int n = await src.ReadAsync(buf.AsMemory(0, want), ct);
                        if (n == 0) throw new EndOfStreamException("Truncated ZIP entry body");
                        await outFs.WriteAsync(buf.AsMemory(0, n), ct);
                        rem -= n;
                        onBytes(n);
                    }
                    saved.Add(Path.GetFileName(dst));
                }
                else
                {
                    long rem = entrySize;
                    while (rem > 0)
                    {
                        int want = (int)Math.Min(buf.Length, rem);
                        int n = await src.ReadAsync(buf.AsMemory(0, want), ct);
                        if (n == 0) throw new EndOfStreamException("Truncated ZIP directory entry");
                        rem -= n;
                        onBytes(n);
                    }
                }

                // Read next signature (4 bytes)
                int sigGot = await ReadExactOrPartialAsync(src, hdr.AsMemory(0, 4), ct);
                if (sigGot < 4) break;
                onBytes(sigGot);
                uint nextSig = BitConverter.ToUInt32(hdr, 0);
                if (nextSig == 0x02014b50u || nextSig == 0x06054b50u)
                {
                    // Drain central directory + EOCD so server finishes cleanly
                    int drain;
                    while ((drain = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0) onBytes(drain);
                    break;
                }
                if (nextSig != 0x04034b50u)
                    throw new InvalidDataException($"Unexpected ZIP signature 0x{nextSig:X8}");

                if (await ReadExactOrPartialAsync(src, hdr.AsMemory(4, 26), ct) != 26)
                    throw new EndOfStreamException("Truncated next ZIP header");
                onBytes(26);
                ushort nFlags = BitConverter.ToUInt16(hdr, 6);
                ushort nMethod = BitConverter.ToUInt16(hdr, 8);
                if (nMethod != 0 || (nFlags & 0x08) != 0)
                    throw new NotSupportedException("Mixed compressed entry in STORED stream");
            }
            return (saved, []);
        }
        catch (Exception) when (saved.Count == 0 && created.Count == 0)
        {
            return (null, hdr);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    static async Task<int> ReadExactOrPartialAsync(Stream s, Memory<byte> dst, CancellationToken ct)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int n = await s.ReadAsync(dst[total..], ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    static long ParseZip64UncompressedSize(byte[] extra)
    {
        int i = 0;
        while (i + 4 <= extra.Length)
        {
            ushort id = BitConverter.ToUInt16(extra, i);
            ushort sz = BitConverter.ToUInt16(extra, i + 2);
            i += 4;
            if (id == 0x0001 && sz >= 8 && i + 8 <= extra.Length)
                return (long)BitConverter.ToUInt64(extra, i);
            i += sz;
        }
        throw new InvalidDataException("Missing ZIP64 size");
    }

    // Zero-temp-file streaming ZIP_STORED writer (100% compatible with Java ZipInputStream & ZIP64)
    sealed class ZipStoredPlan
    {
        sealed record Entry(FileInfo File, byte[] NameUtf8, uint Crc32, long Length, long LocalOffset, bool NeedZip64);

        readonly List<Entry> _entries;
        readonly long _cdOffset;
        readonly long _cdSize;
        readonly bool _needZip64Eocd;
        public long TotalZipSize { get; }

        ZipStoredPlan(List<Entry> entries, long cdOffset, long cdSize, bool needZip64Eocd, long totalZipSize)
        {
            _entries = entries;
            _cdOffset = cdOffset;
            _cdSize = cdSize;
            _needZip64Eocd = needZip64Eocd;
            TotalZipSize = totalZipSize;
        }

        public static ZipStoredPlan Build(List<FileInfo> files, CancellationToken ct)
        {
            var entries = new List<Entry>(files.Count);
            long offset = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var nameBytes = Encoding.UTF8.GetBytes(f.Name);
                var len = f.Length;
                var crc = ComputeCrc32(f.FullName, ct);
                bool need64 = len >= 0xFFFFFFFFL || offset >= 0xFFFFFFFFL;
                entries.Add(new Entry(f, nameBytes, crc, len, offset, need64));
                offset += 30 + nameBytes.Length + (need64 ? 20 : 0) + len;
            }
            long cdOffset = offset;
            long cdSize = 0;
            foreach (var e in entries)
                cdSize += 46 + e.NameUtf8.Length + (e.NeedZip64 ? 28 : 0);
            bool needZip64Eocd = cdOffset >= 0xFFFFFFFFL || cdSize >= 0xFFFFFFFFL || entries.Count >= 0xFFFF;
            long total = cdOffset + cdSize + (needZip64Eocd ? 56 + 20 : 0) + 22;
            return new ZipStoredPlan(entries, cdOffset, cdSize, needZip64Eocd, total);
        }

        public async Task StreamToAsync(Stream dst, Action<long, long> onProgress, CancellationToken ct)
        {
            long sentPayload = 0;
            long totalPayload = _entries.Sum(e => e.Length);
            const int ChunkSize = 256 * 1024;
            var bufA = ArrayPool<byte>.Shared.Rent(ChunkSize);
            var bufB = ArrayPool<byte>.Shared.Rent(ChunkSize);
            try
            {
                foreach (var e in _entries)
                {
                    await WriteLocalHeaderAsync(dst, e, ct);
                    await using var fs = new FileStream(e.File.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    long rem = e.Length;
                    if (rem <= 0) continue;

                    // Double-buffered async read-ahead overlapped with TLS/TCP write
                    int firstRead = await fs.ReadAsync(bufA.AsMemory(0, (int)Math.Min(ChunkSize, rem)), ct);
                    if (firstRead == 0) throw new IOException("File shrank during transfer");
                    rem -= firstRead;
                    var curBuf = bufA;
                    var nxtBuf = bufB;
                    int curLen = firstRead;

                    while (curLen > 0)
                    {
                        Task<int>? readTask = rem > 0
                            ? fs.ReadAsync(nxtBuf.AsMemory(0, (int)Math.Min(ChunkSize, rem)), ct).AsTask()
                            : null;

                        await dst.WriteAsync(curBuf.AsMemory(0, curLen), ct);
                        sentPayload += curLen;
                        onProgress(sentPayload, totalPayload);

                        if (readTask is null) break;
                        int nextLen = await readTask;
                        if (nextLen == 0) throw new IOException("File shrank during transfer");
                        rem -= nextLen;
                        (curBuf, nxtBuf) = (nxtBuf, curBuf);
                        curLen = nextLen;
                    }
                }
                foreach (var e in _entries) await WriteCentralHeaderAsync(dst, e, ct);
                await WriteEocdAsync(dst, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bufA);
                ArrayPool<byte>.Shared.Return(bufB);
            }
        }

        static async Task WriteLocalHeaderAsync(Stream dst, Entry e, CancellationToken ct)
        {
            using var ms = new MemoryStream(64 + e.NameUtf8.Length);
            using var bw = new BinaryWriter(ms);
            bw.Write(0x04034b50u);
            bw.Write((ushort)(e.NeedZip64 ? 45 : 20));
            bw.Write((ushort)0x0800); // UTF-8
            bw.Write((ushort)0);      // STORED
            bw.Write((ushort)0);      // time
            bw.Write((ushort)0x0021); // date
            bw.Write(e.Crc32);
            bw.Write(e.NeedZip64 ? 0xFFFFFFFFu : (uint)e.Length);
            bw.Write(e.NeedZip64 ? 0xFFFFFFFFu : (uint)e.Length);
            bw.Write((ushort)e.NameUtf8.Length);
            bw.Write((ushort)(e.NeedZip64 ? 20 : 0));
            bw.Write(e.NameUtf8);
            if (e.NeedZip64)
            {
                bw.Write((ushort)0x0001);
                bw.Write((ushort)16);
                bw.Write((ulong)e.Length);
                bw.Write((ulong)e.Length);
            }
            bw.Flush();
            await dst.WriteAsync(ms.ToArray(), ct);
        }

        static async Task WriteCentralHeaderAsync(Stream dst, Entry e, CancellationToken ct)
        {
            using var ms = new MemoryStream(80 + e.NameUtf8.Length);
            using var bw = new BinaryWriter(ms);
            bw.Write(0x02014b50u);
            bw.Write((ushort)(e.NeedZip64 ? 45 : 20));
            bw.Write((ushort)(e.NeedZip64 ? 45 : 20));
            bw.Write((ushort)0x0800);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((ushort)0x0021);
            bw.Write(e.Crc32);
            bw.Write(e.NeedZip64 ? 0xFFFFFFFFu : (uint)e.Length);
            bw.Write(e.NeedZip64 ? 0xFFFFFFFFu : (uint)e.Length);
            bw.Write((ushort)e.NameUtf8.Length);
            bw.Write((ushort)(e.NeedZip64 ? 28 : 0));
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write(0u);
            bw.Write(e.NeedZip64 ? 0xFFFFFFFFu : (uint)e.LocalOffset);
            bw.Write(e.NameUtf8);
            if (e.NeedZip64)
            {
                bw.Write((ushort)0x0001);
                bw.Write((ushort)24);
                bw.Write((ulong)e.Length);
                bw.Write((ulong)e.Length);
                bw.Write((ulong)e.LocalOffset);
            }
            bw.Flush();
            await dst.WriteAsync(ms.ToArray(), ct);
        }

        async Task WriteEocdAsync(Stream dst, CancellationToken ct)
        {
            using var ms = new MemoryStream(128);
            using var bw = new BinaryWriter(ms);
            if (_needZip64Eocd)
            {
                long z64Offset = _cdOffset + _cdSize;
                bw.Write(0x06064b50u);
                bw.Write(44ul);
                bw.Write((ushort)45);
                bw.Write((ushort)45);
                bw.Write(0u);
                bw.Write(0u);
                bw.Write((ulong)_entries.Count);
                bw.Write((ulong)_entries.Count);
                bw.Write((ulong)_cdSize);
                bw.Write((ulong)_cdOffset);
                // ZIP64 EOCD Locator
                bw.Write(0x07064b50u);
                bw.Write(0u);
                bw.Write((ulong)z64Offset);
                bw.Write(1u);
            }
            bw.Write(0x06054b50u);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((ushort)Math.Min(_entries.Count, 0xFFFF));
            bw.Write((ushort)Math.Min(_entries.Count, 0xFFFF));
            bw.Write(_needZip64Eocd ? 0xFFFFFFFFu : (uint)_cdSize);
            bw.Write(_needZip64Eocd ? 0xFFFFFFFFu : (uint)_cdOffset);
            bw.Write((ushort)0);
            bw.Flush();
            await dst.WriteAsync(ms.ToArray(), ct);
        }

        // Slicing-by-8 CRC32 tables (~6-8x faster than byte-by-byte table lookup, zero external dependencies)
        static readonly uint[][] CrcTables = BuildSlicingTables();
        static uint[][] BuildSlicingTables()
        {
            var t = new uint[8][];
            for (int k = 0; k < 8; k++) t[k] = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int b = 0; b < 8; b++) c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[0][i] = c;
            }
            for (int k = 1; k < 8; k++)
                for (int i = 0; i < 256; i++)
                    t[k][i] = (t[k - 1][i] >> 8) ^ t[0][t[k - 1][i] & 0xFF];
            return t;
        }

        static uint ComputeCrc32(string path, CancellationToken ct)
        {
            uint crc = 0xFFFFFFFFu;
            var t0 = CrcTables[0]; var t1 = CrcTables[1]; var t2 = CrcTables[2]; var t3 = CrcTables[3];
            var t4 = CrcTables[4]; var t5 = CrcTables[5]; var t6 = CrcTables[6]; var t7 = CrcTables[7];
            var buf = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int i = 0;
                    int limit = n - 7;
                    while (i < limit)
                    {
                        uint one = crc ^ (uint)(buf[i] | (buf[i + 1] << 8) | (buf[i + 2] << 16) | (buf[i + 3] << 24));
                        uint two = (uint)(buf[i + 4] | (buf[i + 5] << 8) | (buf[i + 6] << 16) | (buf[i + 7] << 24));
                        crc = t7[one & 0xFF] ^ t6[(one >> 8) & 0xFF] ^ t5[(one >> 16) & 0xFF] ^ t4[one >> 24]
                            ^ t3[two & 0xFF] ^ t2[(two >> 8) & 0xFF] ^ t1[(two >> 16) & 0xFF] ^ t0[two >> 24];
                        i += 8;
                    }
                    while (i < n)
                    {
                        crc = t0[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
                        i++;
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
            return ~crc;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
        }
        StopReceiver();
        StopScanner(clearPeers: false);
        _pruneTimer?.Dispose();
        _rxEcdh?.Dispose();
        foreach (var cts in _activeCancels.Values) try { cts.Cancel(); } catch (Exception) { }
    }
}
