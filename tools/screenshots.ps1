# Renders README screenshots from the desktop app's demo mode (simulated phones, no sound).
param([string]$Out = "$PSScriptRoot/../docs/screenshots")
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/.."
dotnet build "$root/src/ConnectSound.Desktop" -c Release -v q -nologo | Out-Null
$exe = "$root/src/ConnectSound.Desktop/bin/Release/net10.0-windows/win-x64/ConnectSound.exe"
New-Item -ItemType Directory -Force $Out | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) "connectsound-shots"
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue

$shots = @(
  @{ name = "console";      args = "--role host --size 1080x660" },
  @{ name = "console-call"; args = "--role host --call --size 1080x660" },
  @{ name = "chooser";      args = "--role chooser --size 1080x660" },
  @{ name = "phone-host";   args = "--role host --size 400x820" },
  @{ name = "phone-source"; args = "--role source --size 400x820" }
)
foreach ($s in $shots) {
  $file = Join-Path $Out "$($s.name).png"
  $cmd = "--demo --data `"$tmp/$($s.name)`" $($s.args) --screenshot `"$file`""
  Start-Process -FilePath $exe -ArgumentList $cmd -Wait
  Write-Host "  $file"
}
