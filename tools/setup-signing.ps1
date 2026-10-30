# One-time setup: creates the Android release signing key and stores it as GitHub Actions secrets.
# Every APK update must be signed with the same key, so keep the generated files backed up.
param(
  [string]$Repo = "hpixsel/ConnectSound",
  [string]$Dir = "$env:USERPROFILE\.connectsound"
)
$ErrorActionPreference = "Stop"

$keytool = Get-Command keytool -ErrorAction SilentlyContinue
if (-not $keytool) { $keytool = Get-ChildItem "$env:ProgramFiles\Eclipse Adoptium", "$env:ProgramFiles\Java", "$env:ProgramFiles\Microsoft" -Recurse -Filter keytool.exe -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $keytool) { throw "keytool not found. Install a JDK (e.g. Temurin 21)." }
$keytoolPath = if ($keytool.Source) { $keytool.Source } else { $keytool.FullName }

New-Item -ItemType Directory -Force $Dir | Out-Null
$keystore = Join-Path $Dir "release.keystore"
$info = Join-Path $Dir "release-keystore.txt"

if (Test-Path $keystore) {
  Write-Host "Using existing key: $keystore"
  $password = ((Get-Content $info) | Where-Object { $_ -like "password=*" }) -replace "password=", ""
} else {
  $password = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
  & $keytoolPath -genkeypair -keystore $keystore -alias connectsound -keyalg RSA -keysize 4096 -validity 10000 `
    -storepass $password -keypass $password -dname "CN=ConnectSound, O=hpixsel" | Out-Null
  @("alias=connectsound", "password=$password", "# Back this file and release.keystore up. Without them no APK update can be published.") | Set-Content $info
  Write-Host "Created $keystore"
}

[Convert]::ToBase64String([IO.File]::ReadAllBytes($keystore)) | gh secret set ANDROID_KEYSTORE_BASE64 -R $Repo
$password | gh secret set ANDROID_KEYSTORE_PASSWORD -R $Repo
"connectsound" | gh secret set ANDROID_KEY_ALIAS -R $Repo
Write-Host "Secrets set on $Repo. Back up $Dir somewhere safe."
