// SPDX-License-Identifier: GPL-2.0-only
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace NekoDrop.Service;

/// <summary>
/// The network Windows is connected to, read through the WLAN API before the
/// card is taken, so the radio can join the same network itself. The key
/// comes from the saved profile; only SYSTEM or an administrator may read it
/// in clear (WLAN_PROFILE_GET_PLAINTEXT_KEY).
/// </summary>
public static class Wlan
{
    public sealed record Network(string Ssid, string Bssid, int Channel, string Security, string? Passphrase,
                                 string? PskHex, bool Open);

    /// <summary>Null when not connected; Security says what kept it from being usable.</summary>
    public static Network? Current(out string why, bool readKey = true)
    {
        why = "";
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) { why = "WLAN service unavailable"; return null; }
        try
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var listPtr) != 0) { why = "no WLAN interface"; return null; }
            try
            {
                int count = Marshal.ReadInt32(listPtr);
                var anyConnected = false;
                for (int i = 0; i < count; i++)
                {
                    // WLAN_INTERFACE_INFO: GUID (16), WCHAR[256], state (4) = 532 bytes, after 8 header bytes.
                    var item = listPtr + 8 + i * 532;
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    var state = Marshal.ReadInt32(item + 16 + 512);
                    if (state != 1 /* wlan_interface_state_connected */) continue;
                    anyConnected = true;
                    // A connected virtual/other WLAN interface may not answer the
                    // current-connection query; keep looking at the rest instead
                    // of returning null for all of them.
                    if (Query(h, guid, out why, readKey) is { } network) return network;
                }
                if (!anyConnected) why = "Wi-Fi is not connected";
                return null;
            }
            finally { WlanFreeMemory(listPtr); }
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// <summary>
    /// True while Windows is already associating, discovering or authenticating
    /// on a WLAN interface: worth waiting a moment instead of cutting it off,
    /// without calling WlanConnect on saved profiles (#4).
    /// </summary>
    public static bool IsConnecting()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return false;
        try
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var listPtr) != 0) return false;
            try
            {
                int count = Marshal.ReadInt32(listPtr);
                for (int i = 0; i < count; i++)
                {
                    var state = Marshal.ReadInt32(listPtr + 8 + i * 532 + 16 + 512);
                    // 5 associating, 6 discovering, 7 authenticating.
                    if (state is 5 or 6 or 7) return true;
                }
                return false;
            }
            finally { WlanFreeMemory(listPtr); }
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// <summary>
    /// True when a Windows WLAN interface has a non-APIPA IPv4 address and a
    /// default gateway. Wlan.Current only proves association; this proves the
    /// DHCP/default-route handoff actually completed after the card returns.
    /// </summary>
    public static bool Ipv4Ready()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 &&
                n.Name != "nekodrop" &&
                n.GetIPProperties().UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IsApipa(a.Address)) &&
                n.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !g.Address.Equals(System.Net.IPAddress.Any)));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return false; }
    }

    /// <summary>Waits for DHCP/NLA to finish after Windows joins the network.</summary>
    public static bool WaitForIpv4(TimeSpan timeout, CancellationToken token = default)
    {
        var end = DateTime.UtcNow + timeout;
        while (true)
        {
            if (token.IsCancellationRequested) return false;
            if (Ipv4Ready()) return true;
            if (DateTime.UtcNow >= end) return false;
            if (token.WaitHandle.WaitOne(250)) return false;
        }
    }

    static bool IsApipa(System.Net.IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    /// <summary>
    /// A wired adapter is up with a default gateway (USB tethering and VPN
    /// adapters do not count).
    /// </summary>
    public static bool WiredUp()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                    or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet
                    or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetT
                    or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetFx &&
                n.Name != "nekodrop" &&
                !NotWired.Any(d => n.Description.Contains(d, StringComparison.OrdinalIgnoreCase)) &&
                n.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !g.Address.Equals(System.Net.IPAddress.Any)));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return false; }
    }

    static readonly string[] NotWired = ["TAP-Windows", "Wintun", "WireGuard", "Remote NDIS", "Apple Mobile Device", "Bluetooth"];

    public sealed record SavedProfile(string Ssid, string? Passphrase, string? PskHex, bool Open,
                                      bool AutoConnect = true, bool Hidden = false, string Name = "");

    /// <summary>
    /// Every saved Wi-Fi profile Windows has that nekodrop's station can use
    /// (open or personal WPA/WPA2/WPA3 with a readable key), as SYSTEM.
    /// </summary>
    public static List<SavedProfile> SavedProfiles()
    {
        var result = new List<SavedProfile>();
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return result;
        try
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var listPtr) != 0) return result;
            var guids = new List<Guid>();
            try
            {
                int count = Marshal.ReadInt32(listPtr);
                for (int i = 0; i < count; i++) guids.Add(Marshal.PtrToStructure<Guid>(listPtr + 8 + i * 532));
            }
            finally { WlanFreeMemory(listPtr); }
            foreach (var g in guids)
            {
                var guid = g;
                if (WlanGetProfileList(h, ref guid, IntPtr.Zero, out var profiles) != 0) continue;
                var names = new List<string>();
                try
                {
                    int n = Marshal.ReadInt32(profiles);
                    for (int i = 0; i < n; i++) names.Add(Marshal.PtrToStringUni(profiles + 8 + i * 516, 256).TrimEnd('\0'));
                }
                finally { WlanFreeMemory(profiles); }
                foreach (var name in names)
                {
                    uint flags = 4; // WLAN_PROFILE_GET_PLAINTEXT_KEY
                    if (WlanGetProfile(h, ref guid, name, IntPtr.Zero, out var xmlPtr, ref flags, out _) != 0) continue;
                    string xml;
                    try { xml = Marshal.PtrToStringUni(xmlPtr) ?? ""; }
                    finally { WlanFreeMemory(xmlPtr); }
                    if (ParseProfile(xml, name) is { } p && result.All(r => r.Ssid != p.Ssid)) result.Add(p);
                }
            }
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
        return result;
    }

    static SavedProfile? ParseProfile(string xml, string name)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = doc.Root?.Name.Namespace ?? "";
            var ssid = doc.Descendants(ns + "SSID").FirstOrDefault()?.Element(ns + "name")?.Value;
            var auth = doc.Descendants(ns + "authentication").FirstOrDefault()?.Value ?? "";
            if (string.IsNullOrEmpty(ssid)) return null;
            var autoConnect = doc.Descendants(ns + "connectionMode").FirstOrDefault()?.Value != "manual";
            var hidden = doc.Descendants(ns + "nonBroadcast").FirstOrDefault()?.Value == "true";
            if ((auth == "open" && doc.Descendants(ns + "encryption").FirstOrDefault()?.Value == "none") || auth == "OWE")
                return new SavedProfile(ssid, null, null, true, autoConnect, hidden, name);
            if (auth is not ("WPA2PSK" or "WPAPSK" or "WPA3SAE")) return null; // enterprise: not yet
            var shared = doc.Descendants(ns + "sharedKey").FirstOrDefault();
            var key = shared?.Element(ns + "keyMaterial")?.Value;
            if (key is null || shared?.Element(ns + "protected")?.Value == "true") return null;
            return shared?.Element(ns + "keyType")?.Value == "networkKey" && key.Length == 64
                ? new SavedProfile(ssid, null, key, false, autoConnect, hidden, name)
                : new SavedProfile(ssid, key, null, false, autoConnect, hidden, name);
        }
        catch (Exception) { return null; }
    }

    [DllImport("wlanapi.dll")] static extern int WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);

    static Network? Query(IntPtr h, Guid guid, out string why, bool readKey)
    {
        why = "";
        if (WlanQueryInterface(h, ref guid, 7 /* current_connection */, IntPtr.Zero, out _, out var p, out _) != 0)
        {
            why = "cannot read the current connection";
            return null;
        }
        string profile, ssid, bssid;
        int auth, cipher;
        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: state 4, mode 4, profile WCHAR[256] (512),
            // then WLAN_ASSOCIATION_ATTRIBUTES: DOT11_SSID (4 + 32), bss type 4, bssid 6 (+2 pad),
            // phy type 4, phy index 4, quality 4, rx 4, tx 4 (= 68), then security: enabled 4,
            // 1X 4, auth 4, cipher 4.
            // Read while Windows is still joining, everything below is zero
            // ("auth 0 is not supported", issue #1): not connected yet.
            if (Marshal.ReadInt32(p) != 1 /* wlan_interface_state_connected */)
            {
                why = "Wi-Fi is not connected yet";
                return null;
            }
            profile = Marshal.PtrToStringUni(p + 8, 256).TrimEnd('\0');
            var ssidLen = Marshal.ReadInt32(p + 520);
            var ssidBytes = new byte[Math.Clamp(ssidLen, 0, 32)];
            Marshal.Copy(p + 524, ssidBytes, 0, ssidBytes.Length);
            ssid = Encoding.UTF8.GetString(ssidBytes);
            var mac = new byte[6];
            Marshal.Copy(p + 560, mac, 0, 6);
            bssid = string.Join(":", mac.Select(b => b.ToString("x2")));
            const int sec = 520 + 68;
            auth = Marshal.ReadInt32(p + sec + 8);
            cipher = Marshal.ReadInt32(p + sec + 12);
        }
        finally { WlanFreeMemory(p); }

        int channel = 0;
        if (WlanQueryInterface(h, ref guid, 8 /* channel_number */, IntPtr.Zero, out _, out var c, out _) == 0)
        {
            channel = Marshal.ReadInt32(c);
            WlanFreeMemory(c);
        }

        // DOT11_AUTH_ALGORITHM: 1 open, 6 RSNA (802.1X), 7 RSNA-PSK, 8 WPA3, 9 WPA3-SAE, 10 OWE.
        // DOT11_CIPHER_ALGORITHM: 0 none, 4 CCMP.
        string security = auth switch { 1 => "open", 7 => "WPA2-Personal", 6 => "WPA2-Enterprise", 8 or 9 => "WPA3", 10 => "OWE", _ => $"auth {auth}" };
        // Open, and Enhanced Open (OWE): no key; the radio finds OWE in the beacon.
        if ((auth == 1 && cipher == 0) || (auth == 10 && cipher == 4))
            return new Network(ssid, bssid, channel, security, null, null, true);
        // WPA3 (SAE): the radio uses WPA2-PSK where the network also offers it
        // (mixed, issue #12) and SAE with the same password where it does not.
        var sae = (auth == 8 || auth == 9) && cipher == 4;
        if ((auth != 7 && !sae) || cipher != 4)
        {
            why = $"{security} is not supported beside AirDrop (WPA2/WPA3-Personal or Enhanced Open with AES)";
            return new Network(ssid, bssid, channel, security, null, null, false);
        }
        if (!readKey) return new Network(ssid, bssid, channel, security, null, null, false);
        uint flags = 4; // WLAN_PROFILE_GET_PLAINTEXT_KEY
        if (WlanGetProfile(h, ref guid, profile, IntPtr.Zero, out var xmlPtr, ref flags, out _) != 0)
        {
            why = "cannot read the saved Wi-Fi profile";
            return null;
        }
        string xml;
        try { xml = Marshal.PtrToStringUni(xmlPtr) ?? ""; }
        finally { WlanFreeMemory(xmlPtr); }
        var doc = XDocument.Parse(xml);
        XNamespace ns = doc.Root?.Name.Namespace ?? "";
        var shared = doc.Descendants(ns + "sharedKey").FirstOrDefault();
        var type = shared?.Element(ns + "keyType")?.Value;
        var key = shared?.Element(ns + "keyMaterial")?.Value;
        var isProtected = shared?.Element(ns + "protected")?.Value == "true";
        if (key is null || isProtected)
        {
            why = "the saved Wi-Fi key could not be read in clear";
            return null;
        }
        return type == "networkKey" && key.Length == 64
            ? new Network(ssid, bssid, channel, security, null, key, false)
            : new Network(ssid, bssid, channel, security, key, null, false);
    }

    /// <summary>Writes the radio's --sta-config file, readable by SYSTEM and Administrators only.</summary>
    public static string WriteStaConfig(Network n, bool dataPlaneAccel = false, bool wifi6 = false, string? staMac = null)
    {
        var dir = Path.Combine(ServiceConfig.Folder, "run");
        ServiceConfig.EnsureSecureDirectory();
        ServiceConfig.EnsureSecureDirectory(dir);
        var path = Path.Combine(dir, "sta.conf");
        ServiceConfig.RejectReparsePoint(path);
        var lines = new List<string> { $"ssid={n.Ssid}", $"bssid={n.Bssid}", $"channel={n.Channel}" };
        // nekodrop's own TAP adapter (installer: DHCP on), never another VPN's.
        lines.Add("tap=nekodrop");
        // One switch controls both parts: RX Block Ack reordering and the two TAP writers.
        lines.Add(dataPlaneAccel ? "ba=1" : "ba=0");
        lines.Add(dataPlaneAccel ? "tap_writers=2" : "tap_writers=0");
        // Experimental Wi-Fi 6 (HE) toward an access point that offers it.
        lines.Add(wifi6 ? "he=1" : "he=0");
        // Air MAC address (#96): translated at the TAP boundary so factory OUI MACs work
        // without tap-windows6 rejecting non-locally-administered NetworkAddress values.
        if (!string.IsNullOrEmpty(staMac)) lines.Add($"mac={staMac}");
        if (n.Open) lines.Add("open=1");
        else if (n.PskHex is not null) lines.Add($"psk={n.PskHex}");
        else lines.Add($"passphrase={n.Passphrase}");
        if (File.Exists(path)) File.Delete(path);
        File.WriteAllLines(path, lines);
        var acl = new System.Security.AccessControl.FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid,
                                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(sid, null),
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
        return path;
    }

    /// <summary>
    /// Asks Windows to join the saved network it would normally choose: the
    /// preferred SSID if it is a saved auto-connect profile, otherwise the
    /// first non-hidden auto-connect profile. Used after AirDrop gives the card
    /// back, when Windows often sits disconnected until the user clicks Wi-Fi.
    /// </summary>
    public static bool ConnectDefault(Action<string> say, string? preferredSsid = null, CancellationToken token = default)
    {
        var profiles = SavedProfiles();
        SavedProfile? pick = null;
        if (!string.IsNullOrEmpty(preferredSsid))
            pick = profiles.FirstOrDefault(p => p.AutoConnect && p.Ssid == preferredSsid)
                ?? profiles.FirstOrDefault(p => p.Ssid == preferredSsid);
        pick ??= profiles.FirstOrDefault(p => p.AutoConnect && !p.Hidden)
              ?? profiles.FirstOrDefault(p => p.AutoConnect)
              ?? profiles.FirstOrDefault(p => !p.Hidden)
              ?? profiles.FirstOrDefault();

        string? profileName = pick?.Name;
        if (string.IsNullOrEmpty(profileName))
        {
            // A profile whose key Windows keeps protected is not in SavedProfiles;
            // its name is still enough to ask Windows to connect to it.
            if (Open(out var listHandle))
            {
                try
                {
                    if (FirstInterface(listHandle) is { } g)
                    {
                        var names = ProfileNames(listHandle, g);
                        profileName = names.FirstOrDefault(n => string.Equals(n, preferredSsid, StringComparison.Ordinal))
                            ?? names.FirstOrDefault();
                    }
                }
                finally { WlanCloseHandle(listHandle, IntPtr.Zero); }
            }
        }
        var wanted = pick?.Ssid ?? profileName;
        if (string.IsNullOrEmpty(wanted) || string.IsNullOrEmpty(profileName))
        {
            say("Wi-Fi: no saved network to reconnect Windows to");
            return false;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var nextAsk = DateTime.MinValue;
        var attempts = 0;
        var said = false;
        var lastWhy = "";
        while (true)
        {
            if (token.IsCancellationRequested) return false;
            // Do not read the key here: a protected WPA3 profile still counts as
            // connected, but Wlan.Current(readKey: true) would report it as null.
            if (Current(out lastWhy, readKey: false) is { } current)
            {
                say($"Wi-Fi: Windows is connected to \"{current.Ssid}\"" +
                    (current.Ssid == wanted ? "" : $" (not \"{wanted}\"; leaving it)"));
                return true;
            }
            if (DateTime.UtcNow >= deadline) break;
            // WlanConnect while Windows is associating cancels that association
            // ("the user wants to establish a new connection"), so wait for it.
            if (IsConnecting())
            {
                if (token.WaitHandle.WaitOne(1000)) return false;
                continue;
            }
            // Two asks at most: a third WlanConnect can tear down the connection
            // Windows has just completed while this loop still cannot see it.
            if (DateTime.UtcNow >= nextAsk && attempts < 2)
            {
                if (Open(out var h))
                {
                    try
                    {
                        if (FirstInterface(h) is { } g)
                        {
                            var parameters = new WlanConnectionParameters
                            {
                                connectionMode = 0, // wlan_connection_mode_profile
                                strProfile = profileName,
                                pDot11Ssid = IntPtr.Zero,
                                pDesiredBssidList = IntPtr.Zero,
                                dot11BssType = 1, // infrastructure
                            };
                            if (!said)
                            {
                                say($"Wi-Fi: asking Windows to reconnect to \"{wanted}\"");
                                said = true;
                            }
                            WlanConnect(h, ref g, ref parameters, IntPtr.Zero);
                        }
                    }
                    finally { WlanCloseHandle(h, IntPtr.Zero); }
                }
                attempts++;
                nextAsk = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            }
            if (token.WaitHandle.WaitOne(1000)) return false;
        }
        say($"Wi-Fi: Windows did not reconnect to \"{wanted}\" on its own" +
            (lastWhy.Length > 0 ? $" (last state: {lastWhy})" : ""));
        return false;
    }

    static List<string> ProfileNames(IntPtr h, Guid guid)
    {
        var result = new List<string>();
        if (WlanGetProfileList(h, ref guid, IntPtr.Zero, out var list) != 0) return result;
        try
        {
            int n = Marshal.ReadInt32(list);
            for (int i = 0; i < n; i++)
                result.Add(Marshal.PtrToStringUni(list + 8 + i * 516, 256).TrimEnd('\0'));
        }
        finally { WlanFreeMemory(list); }
        return result;
    }

    static bool Open(out IntPtr h) => WlanOpenHandle(2, IntPtr.Zero, out _, out h) == 0;

    static Guid? FirstInterface(IntPtr h)
    {
        if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) return null;
        try { return Marshal.ReadInt32(list) == 0 ? null : Marshal.PtrToStructure<Guid>(list + 8); }
        finally { WlanFreeMemory(list); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WlanConnectionParameters
    {
        public int connectionMode;
        public string? strProfile;
        public IntPtr pDot11Ssid;
        public IntPtr pDesiredBssidList;
        public int dot11BssType;
        public int dot11AuthAlgo;
        public int dot11CipherAlgo;
    }

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanConnect(IntPtr handle, ref Guid iface,
        ref WlanConnectionParameters parameters, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved,
        out int size, out IntPtr data, out int type);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanGetProfile(IntPtr handle, ref Guid iface,
        string profile, IntPtr reserved, out IntPtr xml, ref uint flags, out uint access);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr memory);
}
