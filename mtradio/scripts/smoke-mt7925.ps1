#Requires -RunAsAdministrator
param([ValidateSet('id', 'dma', 'fw', 'rx', 'tx', 'broadcast', 'kinds', 'sta', 'switch', 'scan')] [string]$Step = 'fw',
      [ValidateSet(36, 40, 44, 48, 149, 153, 157, 161, 165)] [int]$Channel = 44,
      [switch]$He80)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$build = Join-Path $repo 'build\mtradio'
$radioTest = $Step -in 'rx', 'tx', 'broadcast', 'kinds', 'sta', 'switch', 'scan'
$probe = Join-Path $build $(if ($radioTest) { 'nekodrop-radio-mt.exe' } else { 'mtprobe7925.exe' })
$firmware = Join-Path $repo 'third_party\linux-firmware-mt7925'
$package = Join-Path $build 'package'
New-Item -ItemType Directory -Path $package -Force | Out-Null
$stageDriver = Join-Path $repo 'build\stage\driver'
if (Test-Path -LiteralPath (Join-Path $stageDriver 'winawdl_regproxy.cat')) {
    Copy-Item "$stageDriver\winawdl_regproxy.*" $package -Force
}
$inf = Join-Path $package 'winawdl_regproxy.inf'
$cat = Join-Path $package 'winawdl_regproxy.cat'
$dll = Join-Path $package 'winawdl_regproxy.dll'
$devcon = Get-ChildItem (Join-Path $repo 'third_party\wdk-nuget\wdk\c\Tools') -Recurse -Filter devcon.exe |
    Where-Object FullName -match '\\x64\\' | Select-Object -First 1 -ExpandProperty FullName
$logDir = Join-Path $build 'logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$run = [guid]::NewGuid().ToString('N')
$transcript = Join-Path $logDir "smoke-$run.log"
$probeLog = Join-Path $logDir "probe-$run.log"
$task = "nekodrop-mt7925-probe-$run"
$target = 'PCI\VEN_14C3&DEV_7925'
$stores = 'Root', 'TrustedPublisher'
$cert = $null
$public = Join-Path $package "probe-$run.cer"
$registered = $false
$publishedInf = $null
$attemptedBind = $false
$wifiProfile = $null
$profileBackup = Join-Path $logDir "wifi-$run"
$lease = [Threading.Mutex]::new($false, 'Global\NekodropMt7925Smoke')

function Get-ProxyPackages {
    $output = & pnputil.exe /enum-drivers /format xml
    if ($LASTEXITCODE) { throw "Cannot enumerate installed driver packages ($LASTEXITCODE)." }
    ([xml]($output | Out-String)).PnpUtil.Driver |
        Where-Object { $_.OriginalName -eq 'winawdl_regproxy.inf' -and $_.ProviderName -eq 'winairdrop project' }
}

function Read-Binding([string]$InstanceId) {
    (Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName DEVPKEY_Device_DriverInfPath).Data
}

function Read-Wifi([string]$InstanceId) {
    $driverKey = (Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName DEVPKEY_Device_Driver -ErrorAction SilentlyContinue).Data
    if ($driverKey -notmatch '^\{4d36e972-e325-11ce-bfc1-08002be10318\}\\\d{4}$') { return }
    $classPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$driverKey"
    $adapterId = (Get-ItemProperty -LiteralPath $classPath -Name NetCfgInstanceId -ErrorAction SilentlyContinue).NetCfgInstanceId
    if ($adapterId) {
        [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
            Where-Object {
                $_.Id -eq $adapterId -and $_.NetworkInterfaceType -eq 'Wireless80211' -and
                $_.OperationalStatus -ne 'NotPresent'
            } | Select-Object -First 1
    }
}

function Read-LastWifiProfile([guid]$InterfaceId) {
    foreach ($event in (Get-WinEvent -FilterHashtable @{
        LogName='Microsoft-Windows-WLAN-AutoConfig/Operational'; Id=8001
    } -MaxEvents 64 -ErrorAction Stop)) {
        $fields = @{}
        foreach ($node in ([xml]$event.ToXml()).Event.EventData.Data) {
            $fields[[string]$node.Name] = [string]$node.'#text'
        }
        if ($fields.InterfaceGuid -and [guid]$fields.InterfaceGuid -eq $InterfaceId) {
            return $fields.ProfileName
        }
    }
}

if (-not $lease.WaitOne(0)) {
    $lease.Dispose()
    throw 'Another MT7925 hardware test holds the lease.'
}
Start-Transcript -Path $transcript | Out-Null
try {
    foreach ($path in $probe, $inf, $cat, $dll, $devcon) {
        if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing probe dependency: $path"
        }
    }
    if ($Step -eq 'fw' -or $radioTest) {
        $firmwareHashes = @{
            'WIFI_RAM_CODE_MT7925_1_1.bin' = '329C0E27F780550789E4EBA47452731B1723F69F9E67F525D0E50BF3CD2978AC'
            'WIFI_MT7925_PATCH_MCU_1_1_hdr.bin' = '67FF85F1A39D1A100A51ABD8E2A26EDD2F198D7052514B6B8981948B231E5016'
        }
        foreach ($name in $firmwareHashes.Keys) {
            $actual = Get-FileHash -LiteralPath (Join-Path $firmware $name) -Algorithm SHA256
            if ($actual.Hash -ne $firmwareHashes[$name]) { throw "Incorrect firmware hash: $name" }
        }
    }
    $cards = @(Get-PnpDevice -PresentOnly -Class Net |
        Where-Object InstanceId -like ('{0}&*' -f $target))
    if ($cards.Count -ne 1) { throw "Expected exactly one present MT7925 in class Net; found $($cards.Count)." }
    $card = $cards[0]
    $vendorInf = Read-Binding $card.InstanceId
    $vendorPath = Join-Path "$env:windir\INF" $vendorInf
    $hardwareId = (Get-PnpDeviceProperty -InstanceId $card.InstanceId -KeyName DEVPKEY_Device_HardwareIds).Data[0]
    if (-not $hardwareId -or $vendorInf -notmatch '^oem\d+\.inf$' -or -not (Test-Path -LiteralPath $vendorPath)) {
        throw 'Cannot verify the existing driver and its rollback INF.'
    }
    $wired = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        Where-Object {
            $_.NetworkInterfaceType -eq 'Ethernet' -and $_.OperationalStatus -eq 'Up' -and
            $_.GetIPProperties().GatewayAddresses.Count -gt 0
        }
    if (-not $wired) { throw 'No connected Ethernet adapter with a gateway.' }
    $wifiBefore = Read-Wifi $card.InstanceId
    if (-not $wifiBefore) { throw 'Cannot identify the original Windows WLAN interface.' }
    if ($wifiBefore.OperationalStatus -eq 'Up') {
        $wifiProfile = Read-LastWifiProfile ([guid]$wifiBefore.Id)
        if (-not $wifiProfile) { throw 'The active Windows WLAN profile could not be identified.' }
        New-Item -ItemType Directory -Path $profileBackup | Out-Null
        & netsh.exe wlan export profile "name=$wifiProfile" "interface=$($wifiBefore.Name)" "folder=$profileBackup" | Out-Null
        $exportResult = $LASTEXITCODE
        $saved = @(Get-ChildItem -LiteralPath $profileBackup -Filter '*.xml' -File)
        if ($exportResult -or $saved.Count -ne 1) { throw 'Could not back up the active WLAN profile.' }
    }
    $before = @(Get-ProxyPackages | ForEach-Object DriverName)
    "Original driver: $vendorInf"

    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=nekodrop MT7925 probe $run" `
        -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddDays(2)
    Export-Certificate -Cert $cert -FilePath $public | Out-Null
    foreach ($store in $stores) {
        Import-Certificate -FilePath $public -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
    }
    foreach ($file in $cat, $dll) {
        $signature = Set-AuthenticodeSignature -LiteralPath $file -Certificate $cert -HashAlgorithm SHA256
        if ($signature.Status -ne 'Valid') { throw "Signing failed for $file ($($signature.Status))." }
    }
    & pnputil.exe /add-driver $inf
    $addResult = $LASTEXITCODE
    $added = @(Get-ProxyPackages | Where-Object DriverName -notin $before)
    if ($added.Count -eq 1) { $publishedInf = $added[0].DriverName }
    if ($addResult -or -not $publishedInf) { throw "Cannot identify newly staged proxy package ($addResult)." }

    $attemptedBind = $true
    & $devcon update $inf $target
    if ($LASTEXITCODE -ne 0 -or (Read-Binding $card.InstanceId) -ne $publishedInf) {
        throw 'The proxy did not bind without a reboot.'
    }
    "Proxy driver: $publishedInf"
    $heFlag = if ($He80) { ' --he80' } else { '' }
    if ($radioTest) {
        $cmd = "& '$probe' $Channel --mt7925 --country TW --fw-dir '$firmware'$heFlag --test-$Step 8 *> '$probeLog'; " +
               "`$rc=`$LASTEXITCODE; Add-Content -LiteralPath '$probeLog' -Value `"RESULT `$rc`"; exit `$rc"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
        $hostExe = "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe"
        $action = New-ScheduledTaskAction -Execute $hostExe -Argument "-NoProfile -EncodedCommand $encoded"
        $resultPattern = '^RESULT \d+$'
    } else {
        $action = New-ScheduledTaskAction -Execute $probe -Argument "$Step --fw-dir `"$firmware`" --log `"$probeLog`""
        $resultPattern = '^\[\s*[\d.]+\]\s+RESULT '
    }
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 120) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings | Out-Null
    $registered = $true
    Start-ScheduledTask -TaskName $task
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 500
        $finished = (Test-Path -LiteralPath $probeLog) -and
                    [bool](Select-String -LiteralPath $probeLog -Pattern $resultPattern -Quiet) -and
                    (Get-ScheduledTask -TaskName $task).State -ne 'Running'
    } while (-not $finished -and (Get-Date) -lt $deadline)
    if (Test-Path -LiteralPath $probeLog) { Get-Content -LiteralPath $probeLog }
    $result = (Get-ScheduledTaskInfo -TaskName $task).LastTaskResult
    if (-not $finished -or $result -ne 0) { throw "Probe did not complete successfully ($result)." }
} finally {
    try {
        if ($registered) {
            Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction Continue
        }
        if ($attemptedBind) {
            & $devcon update $vendorPath $hardwareId
            & pnputil.exe /scan-devices
        }
        if ($publishedInf) {
            & pnputil.exe /delete-driver $publishedInf /uninstall
            if ($LASTEXITCODE) { Write-Warning "Could not remove probe package $publishedInf." }
        }
        if ($attemptedBind) {
            $after = Read-Binding $card.InstanceId
            if ($after -ne $vendorInf) { throw "Driver restore mismatch: expected $vendorInf, found $after." }
            "RESTORE OK: $after"
            if ($wifiProfile) {
                $deadline = (Get-Date).AddSeconds(30)
                do {
                    $wifiAfter = Read-Wifi $card.InstanceId
                    if (-not $wifiAfter) { Start-Sleep -Seconds 1 }
                } while (-not $wifiAfter -and (Get-Date) -lt $deadline)
                if (-not $wifiAfter) { throw 'The restored Windows WLAN interface did not appear.' }
                if ($wifiAfter.Id -ne $wifiBefore.Id) {
                    & netsh.exe wlan add profile "filename=$($saved[0].FullName)" "interface=$($wifiAfter.Name)" user=all | Out-Null
                    if ($LASTEXITCODE) { throw 'The saved WLAN profile could not be restored onto the new interface.' }
                }
                if ($wifiAfter.OperationalStatus -ne 'Up') {
                    & netsh.exe wlan connect "name=$wifiProfile" "interface=$($wifiAfter.Name)" | Out-Null
                }
                $deadline = (Get-Date).AddSeconds(30)
                do {
                    $wifiAfter = Read-Wifi $card.InstanceId
                    if ($wifiAfter -and $wifiAfter.OperationalStatus -eq 'Up') { break }
                    Start-Sleep -Seconds 1
                } while ((Get-Date) -lt $deadline)
                if (-not $wifiAfter -or $wifiAfter.OperationalStatus -ne 'Up') {
                    throw 'The original driver is back, but Wi-Fi did not reconnect.'
                }
                'WLAN RECONNECTED'
                $wifiProfile = $null
            }
        }
    } finally {
        if ($cert) {
            foreach ($store in $stores) {
                Remove-Item -LiteralPath "Cert:\LocalMachine\$store\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
            }
            Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $public) { Remove-Item -LiteralPath $public }
        if ((-not $attemptedBind -or -not $wifiProfile) -and (Test-Path -LiteralPath $profileBackup)) {
            Get-ChildItem -LiteralPath $profileBackup -Filter '*.xml' -File | Remove-Item
            Remove-Item -LiteralPath $profileBackup
        }
        Stop-Transcript | Out-Null
        $lease.ReleaseMutex()
        $lease.Dispose()
    }
}
