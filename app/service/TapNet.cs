// SPDX-License-Identifier: GPL-2.0-only
using System.Diagnostics;

namespace NekoDrop.Service;

/// <summary>
/// When the card goes back to Windows, the TAP adapter is disconnected but
/// kept its IPv4 address and default route. Windows then joins Wi-Fi with
/// APIPA only, because the old TAP lease still owns the router-assigned
/// address and the only default route points at the disconnected TAP. Release
/// those before Windows reconnects; the TAP gets a fresh DHCP lease when the
/// next session takes the card.
/// </summary>
static class TapNet
{
    const string Adapter = "nekodrop";

    /// <summary>Removes the TAP adapter's IPv4 addresses and default routes.
    /// Returns "" or what went wrong.</summary>
    public static string Release()
    {
        const string command = """
            $ErrorActionPreference = 'Stop'
            $tap = 'nekodrop'
            Get-NetIPAddress -InterfaceAlias $tap -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
            Get-NetRoute -InterfaceAlias $tap -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
            $left = @(Get-NetIPAddress -InterfaceAlias $tap -AddressFamily IPv4 -ErrorAction SilentlyContinue)
            if ($left.Count -gt 0) {
                throw ('TAP still has IPv4: ' + (($left | ForEach-Object { $_.IPAddress }) -join ', '))
            }
            """;
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return "cannot start PowerShell";
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (Exception) { } return "PowerShell did not finish"; }
            if (p.ExitCode != 0)
                return ((errors.Wait(1000) ? errors.Result : "") + " " + (output.Wait(1000) ? output.Result : ""))
                    .Replace("\r", " ").Replace("\n", " ").Trim();
            return "";
        }
        catch (Exception e) { return e.Message; }
    }
}
