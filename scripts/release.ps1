<#
.SYNOPSIS
  Builds a signed GemakTV TV app and publishes it, encrypted, so TVs can install it with "Check For Updates".

.EXAMPLE
  .\scripts\release.ps1 -VersionName 1.0.3
  Bumps the version code by one, sets the version name, builds for ARM (MyGica boxes) and publishes.

.EXAMPLE
  .\scripts\release.ps1 -VersionName 1.0.3 -Emulator -DryRun
  Also includes x86 (for the Android 6 emulator) and stops before publishing.
#>
param(
    [Parameter(Mandatory)] [string] $VersionName,
    [int] $VersionCode = 0,
    [switch] $Emulator,
    [switch] $DryRun
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$csproj = Join-Path $root 'src\GemakTV.Tv\GemakTV.Tv.csproj'
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = (Get-ChildItem 'C:\Program Files\Android\openjdk' -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName }

# 1. Version: the project file is the source of truth, so the repository records what was released.
$xml = [xml](Get-Content $csproj -Raw)
$props = $xml.Project.PropertyGroup | Where-Object { $_.ApplicationVersion } | Select-Object -First 1
$current = [int]$props.ApplicationVersion
if ($VersionCode -eq 0) { $VersionCode = $current + 1 }
if ($VersionCode -le $current) { throw "Version code $VersionCode must be higher than the current $current." }
$text = Get-Content $csproj -Raw
$text = $text -replace '<ApplicationVersion>\d+</ApplicationVersion>', "<ApplicationVersion>$VersionCode</ApplicationVersion>"
$text = $text -replace '<ApplicationDisplayVersion>[^<]+</ApplicationDisplayVersion>', "<ApplicationDisplayVersion>$VersionName</ApplicationDisplayVersion>"
[IO.File]::WriteAllText($csproj, $text)
Write-Host "Version $VersionName (code $VersionCode)"

# 2. Build a signed release APK (signing comes from build/Secrets.props).
$buildArgs = @('build', $csproj, '-c', 'Release', '-v', 'q', '-nologo')
if ($Emulator) { $buildArgs += '-p:GemakEmulator=true' }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$apk = Join-Path $root 'src\GemakTV.Tv\bin\Release\net10.0-android\io.github.ryusch.gemaktv-Signed.apk'
$out = Join-Path $root "artifacts\GemakTV-$VersionName-$VersionCode.apk"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
Copy-Item $apk $out -Force
Write-Host ("Built {0} ({1:N1} MB)" -f $out, ((Get-Item $out).Length / 1MB))

if ($DryRun) { Write-Host 'Dry run: not published.'; return }

# 3. Encrypt and publish (APK + config in one commit on the publish branch).
$publisher = Join-Path $root 'tools\GemakTV.Publisher\bin\Release\net10.0\GemakTV.Publisher.dll'
& dotnet build (Join-Path $root 'tools\GemakTV.Publisher') -c Release -v q -nologo | Out-Null
& dotnet $publisher release --apk $out --version-code $VersionCode --version-name $VersionName
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
Write-Host "Remember to commit the version bump in $csproj."
