# Pinned, separate Python worker for the optional LAN Quick Share transport.
param(
  [Parameter(Mandatory)][string]$Stage,
  [Parameter(Mandatory)][string]$Downloads,
  [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$upstream = Join-Path $repo 'third_party\quickshare-cli'
$pin = '891c73b88720587470cec602bfe5a9cc321ef25a'
$commit = git -C $upstream rev-parse HEAD
if ($LASTEXITCODE -or "$commit" -ne $pin) { throw 'Run scripts\fetch-deps.ps1 to get the pinned Quick Share backend.' }
$changes = git -C $upstream status --porcelain
if ($LASTEXITCODE -or $changes) { throw 'Quick Share backend checkout is not clean.' }
$stagePath = [IO.Path]::GetFullPath($Stage)
$dest = Join-Path $stagePath 'quickshare'
$wheels = Join-Path ([IO.Path]::GetFullPath($Downloads)) 'quickshare-wheels'
$lock = Join-Path $PSScriptRoot 'quickshare-wheels.txt'
New-Item -ItemType Directory -Force $dest, $wheels | Out-Null
$target = @('--platform','win_amd64','--implementation','cp','--python-version','3.12',
            '--only-binary=:all:','--require-hashes','--no-deps','-r',$lock)
if (-not $Offline) {
  python -m pip download --disable-pip-version-check --dest $wheels @target
  if ($LASTEXITCODE) { throw 'Quick Share wheel download failed.' }
}
python -m pip install --disable-pip-version-check --no-user --no-compile --no-index --find-links $wheels --target $dest @target
if ($LASTEXITCODE) { throw 'Quick Share dependency staging failed.' }
Copy-Item -Recurse -LiteralPath "$upstream\src\quickshare" -Destination $dest
Copy-Item -LiteralPath "$repo\installer\quickshare-bridge.py" -Destination $dest
Copy-Item -LiteralPath "$upstream\LICENSE" -Destination "$dest\LICENSE.quickshare-cli.txt"
Copy-Item -LiteralPath "$upstream\README.md" -Destination "$dest\UPSTREAM.quickshare-cli.md"
python -I "$PSScriptRoot\patch-quickshare.py" "$dest\quickshare\send.py"
if ($LASTEXITCODE) { throw 'Quick Share socket adaptation failed.' }
$py = Join-Path $stagePath 'python\python.exe'
& $py -I "$dest\quickshare-bridge.py" --selftest
if ($LASTEXITCODE) { throw 'Quick Share staged worker self-test failed.' }
