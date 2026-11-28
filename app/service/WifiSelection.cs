// SPDX-License-Identifier: GPL-2.0-only

namespace NekoDrop.Service;

/// <summary>Selection for the disconnected Concurrent station, not a roaming policy.</summary>
static class WifiSelection
{
    // Initial conservative thresholds for choosing among candidates, not for
    // disconnecting or rejecting the only available access point. dBm, not %.
    const int Adequate24 = -75, Adequate5 = -70;
    internal static readonly TimeSpan FailedApCooldown = TimeSpan.FromMinutes(2);

    internal sealed record AccessPoint(string Ssid, string Bssid, int Channel, int Dbm, string Security)
    {
        // The current station scanner reports 2.4/5 GHz channel numbers.
        public bool Is5 => Channel > 14;
        public bool Adequate => Dbm >= (Is5 ? Adequate5 : Adequate24);
        public (string Ssid, string Bssid) Key => (Ssid, Bssid.ToUpperInvariant());
    }

    internal sealed record Profile(bool AutoConnect, bool Open, bool HasPassphrase, bool HasPsk,
                                   DateTime LastSuccess, DateTime LastManualSuccess);

    public static bool IsOpen(string security) => security is "open" or "owe";
    public static bool IsJoinable(string security) =>
        IsOpen(security) || security is "wpa2-psk" or "wpa2/3-psk" or "wpa3-sae";

    public static bool Supports(AccessPoint ap, bool supports24) => ap.Channel > 0 && (supports24 || ap.Is5);

    static bool CanUse(AccessPoint ap, Profile p) => ap.Security switch
    {
        "open" or "owe" => p.Open && !p.HasPassphrase && !p.HasPsk,
        "wpa2-psk" or "wpa2/3-psk" => p.HasPassphrase || p.HasPsk,
        "wpa3-sae" => p.HasPassphrase,
        _ => false,
    };

    // Keep the displayed/explicit choice in one credential family. A strong
    // open AP with a saved protected SSID must not displace its protected APs.
    // Only prefer a band within an SSID; never use it as a cross-SSID bonus.
    public static AccessPoint? ForName(IEnumerable<AccessPoint> options, Profile? profile, string? password = null)
    {
        var seen = options.OrderByDescending(ap => ap.Dbm).ToArray();
        if (seen.Length == 0) return null;
        var joinable = seen.Where(ap => IsJoinable(ap.Security)).ToArray();
        if (joinable.Length == 0) return seen[0]; // preserve the useful "unsupported" error
        bool hasNewPassword = !string.IsNullOrEmpty(password), rawPsk = password is { Length: 64 } && password.All(Uri.IsHexDigit);
        var compatible = hasNewPassword ? joinable.Where(ap => !IsOpen(ap.Security) && (!rawPsk || ap.Security != "wpa3-sae")).ToArray()
                       : profile is null ? [] : joinable.Where(ap => CanUse(ap, profile)).ToArray();
        if (compatible.Length > 0) return BestBand(compatible);
        if (profile is { HasPassphrase: true } or { HasPsk: true } || hasNewPassword)
            return BestBand(joinable.Where(ap => !IsOpen(ap.Security)));
        bool open = IsOpen(joinable[0].Security);
        return BestBand(joinable.Where(ap => IsOpen(ap.Security) == open));
    }

    static AccessPoint? BestBand(IEnumerable<AccessPoint> options) =>
        options.OrderByDescending(ap => ap.Is5 && ap.Adequate).ThenByDescending(ap => ap.Dbm).FirstOrDefault();

    public static AccessPoint? Automatic(IEnumerable<AccessPoint> options, Func<string, Profile?> profileFor,
                                         Func<AccessPoint, bool> isCooling)
    {
        var candidates = new List<(AccessPoint Ap, Profile Profile)>();
        foreach (var group in options.GroupBy(ap => ap.Ssid, StringComparer.Ordinal))
        {
            if (profileFor(group.Key) is not { AutoConnect: true } p) continue;
            var available = group.Where(ap => !isCooling(ap) && CanUse(ap, p)).ToArray();
            var usable = available.Where(ap => ap.Adequate).ToArray();
            if (BestBand(usable.Length == 0 ? available : usable) is { } ap)
                candidates.Add((ap, p));
        }
        var viable = candidates.Where(c => c.Ap.Adequate).ToArray();
        if (viable.Length == 0) // Don't strand a machine when only weak networks are visible.
            return candidates.OrderByDescending(c => c.Ap.Dbm).Select(c => c.Ap).FirstOrDefault();
        return viable.OrderByDescending(c => c.Profile.LastManualSuccess)
                     .ThenByDescending(c => c.Profile.LastSuccess)
                     .ThenByDescending(c => c.Ap.Dbm).Select(c => c.Ap).FirstOrDefault();
    }

    public static List<string> SelfTest()
    {
        var errors = new List<string>();
        void Check(bool ok, string why) { if (!ok) errors.Add("Wi-Fi selection: " + why); }
        var earlier = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        var later = earlier.AddDays(1);
        var saved = new Profile(true, false, true, false, default, default);
        var home = new AccessPoint("home", "aa", 44, -63, "wpa2-psk");
        var wide = new AccessPoint("wide", "bb", 6, -43, "wpa2-psk");
        var same24 = wide with { Ssid = home.Ssid };
        var weak = home with { Dbm = -82 };
        var profiles = new Dictionary<string, Profile>
        {
            [home.Ssid] = saved with { LastSuccess = later },
            [wide.Ssid] = saved with { LastSuccess = earlier },
        };
        AccessPoint? Pick(params AccessPoint[] aps) => Automatic(aps, profiles.GetValueOrDefault, _ => false);
        Check(Pick(home, wide) == home, "recent proven SSID beats stronger unrelated network");
        Check(Pick(home, same24) == home, "usable 5 GHz wins inside a dual-band SSID");
        Check(Pick(weak, same24) == same24, "same SSID falls back to 2.4 GHz when 5 GHz is weak");
        Check(Pick(weak, wide) == wide, "weak remembered SSID does not displace a usable choice");
        Check(Pick(weak) == weak, "only weak choice is not rejected");
        Check(!Supports(wide, false) && Supports(home, false), "5 GHz-only station keeps its band restriction");
        Check(Automatic([home, same24], profiles.GetValueOrDefault, ap => ap.Key == home.Key) == same24,
              "failed BSSID cools down without blocking the network's other BSSID");
        Check(Automatic([home], profiles.GetValueOrDefault, _ => true) is null,
              "only cooling BSSID does not loop");
        profiles[wide.Ssid] = saved;
        Check(Pick(home, wide) == home, "imported/unproven profile does not count as a prior connection");
        profiles[home.Ssid] = saved;
        Check(Pick(home, wide) == wide, "without history, do not prefer 5 GHz across unrelated SSIDs");
        profiles[home.Ssid] = saved with { LastSuccess = later };
        profiles[wide.Ssid] = saved with { LastManualSuccess = earlier };
        Check(Pick(home, wide) == wide, "successful manual preference beats newer automatic success");
        profiles[wide.Ssid] = profiles[wide.Ssid] with { AutoConnect = false };
        Check(Pick(home, wide) == home && Pick(wide) is null, "respect auto-connect disabled");
        var openTwin = home with { Bssid = "cc", Dbm = -40, Security = "open" };
        Check(Pick(openTwin, same24) == same24 && Pick(openTwin) is null,
              "a saved protected profile must not auto-join an open lookalike");
        Check(ForName([same24, home], saved) == home && ForName([same24, weak], saved) == same24,
              "explicit choice by name uses the same safe band preference");
        Check(ForName([openTwin], saved) is null, "explicit protected profile avoids an open lookalike");
        var sae = home with { Security = "wpa3-sae" };
        profiles[home.Ssid] = saved with { HasPassphrase = false, HasPsk = true };
        Check(Pick(sae, same24) == same24, "raw PSK is not a WPA3-SAE passphrase");
        Check(ForName([sae, same24], null, new string('a', 64)) == same24, "manual raw PSK selects a WPA2 access point");
        Check(ForName([home with { Security = "wep" }], saved)?.Security == "wep",
              "all unsupported scan rows still have a diagnostic");
        var old = System.Text.Json.JsonSerializer.Deserialize<KnownNetworks.Entry>(
            """{"Ssid":"home","LastUsed":"2026-11-20T00:00:00Z"}""");
        Check(old is { LastSuccess.Ticks: 0, LastManualSuccess.Ticks: 0 } && old.LastUsed == earlier,
              "older LastUsed records are not migrated to confirmed success");
        return errors;
    }
}
