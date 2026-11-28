// SPDX-License-Identifier: GPL-2.0-only
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NekoDrop;

/// <summary>Experimental IPv4 LAN peer; Quick Share runs out of process.</summary>
public sealed class QuickShareEngine : IDisposable
{
    readonly object _gate = new();
    Process? _worker;
    bool _look;
    string _address = "", _name = "", _folder = "";
    public const string Protocol = "quickshare";
    public static bool IsPeer(string id) => id.StartsWith("qs:", StringComparison.Ordinal);
    public static bool IsRequest(string id) => id.StartsWith("qr:", StringComparison.Ordinal);
    public event Action<JsonObject>? Event;
    public event Action<string>? Log;

    static string? Address()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                    (n.Name == "nekodrop" || n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet))
                .Select(n => (Name: n.Name, Props: n.GetIPProperties()))
                .Where(n => n.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                             !g.Address.Equals(System.Net.IPAddress.Any)))
                .OrderBy(n => n.Name == "nekodrop" ? 0 : 1)
                .SelectMany(n => n.Props.UnicastAddresses)
                .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .FirstOrDefault(a => { byte[] b = a.GetAddressBytes(); return b[0] > 0 && b[0] < 224 &&
                    b[0] != 127 && !(b[0] == 169 && b[1] == 254); })?.ToString();
        }
        catch (NetworkInformationException) { return null; }
    }

    public void Apply(Settings settings)
    {
        lock (_gate)
        {
            string? address = settings.QuickShareOn ? Address() : null;
            if (address is not null && _worker is { HasExited: false } && _address == address &&
                _name == settings.DeviceName && _folder == settings.SaveFolder) return;
            Stop();
            if (address is null) return;
            string root = AppContext.BaseDirectory;
            string python = Path.Combine(root, "python", "python.exe");
            string script = Path.Combine(root, "quickshare", "quickshare-bridge.py");
            if (!File.Exists(python) || !File.Exists(script))
            { Log?.Invoke("Quick Share is unavailable in this build."); return; }
            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-I", "-u", script, "--address", address, "--name", settings.DeviceName, "--folder", settings.SaveFolder })
                start.ArgumentList.Add(arg);
            try
            {
                var worker = Process.Start(start);
                if (worker is null) return;
                _worker = worker;
                (_address, _name, _folder) = (address, settings.DeviceName, settings.SaveFolder);
                worker.OutputDataReceived += (_, e) =>
                {
                    if (!ReferenceEquals(_worker, worker) || e.Data is null) return;
                    try { if (JsonNode.Parse(e.Data) is JsonObject o) Event?.Invoke(o); }
                    catch (JsonException) { }
                };
                worker.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke(e.Data); };
                worker.EnableRaisingEvents = true;
                worker.Exited += (_, _) =>
                {
                    if (ReferenceEquals(_worker, worker)) Event?.Invoke(new JsonObject { ["event"] = "qs-stopped" });
                };
                worker.BeginOutputReadLine();
                worker.BeginErrorReadLine();
                Write(new JsonObject { ["do"] = "look", ["on"] = _look });
            }
            catch (Exception e) { Log?.Invoke("Quick Share start: " + e.GetType().Name); Stop(); }
        }
    }

    bool Write(JsonObject command)
    {
        try
        {
            if (_worker is not { HasExited: false } worker) return false;
            worker.StandardInput.WriteLine(command.ToJsonString());
            worker.StandardInput.Flush();
            return true;
        }
        catch (IOException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void Look(bool on) { lock (_gate) { _look = on; Write(new() { ["do"] = "look", ["on"] = on }); } }
    public void Answer(string id, bool accept) { lock (_gate) Write(new() { ["do"] = "answer", ["id"] = id, ["accept"] = accept }); }
    public void Cancel(string id) { lock (_gate) Write(new() { ["do"] = "cancel", ["id"] = id }); }
    public void SendFiles(string id, string peer, IEnumerable<string> files)
    {
        lock (_gate)
            if (!Write(new() { ["do"] = "send", ["id"] = id, ["peer"] = peer,
                              ["files"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()) }))
                Event?.Invoke(new() { ["event"] = "send", ["id"] = id, ["state"] = "failed", ["reason"] = "Quick Share" });
    }

    void Stop()
    {
        if (_worker is not { } p) return;
        Write(new() { ["do"] = "quit" });
        _worker = null;
        try { if (!p.WaitForExit(1000)) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        finally { p.Dispose(); }
        Event?.Invoke(new() { ["event"] = "qs-stopped" });
    }

    public void Dispose() { lock (_gate) Stop(); }
}
