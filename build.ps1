param(
  [string]$Version = "",
  [switch]$DebugFixture = $false
)

$ErrorActionPreference = "Stop"

$OutDirName = "bin"
if ($DebugFixture) { $OutDirName = "debug" }
$Out = Join-Path $PSScriptRoot $OutDirName

New-Item -ItemType Directory -Force -Path $Out | Out-Null

$ExeName = "Headroom.exe"
if ($DebugFixture) { $ExeName = "Headroom.fixture.exe" }
elseif ($Version) { $ExeName = "Headroom-v$Version.exe" }
$Exe = Join-Path $Out $ExeName
$AssemblyName = [System.IO.Path]::GetFileNameWithoutExtension($ExeName)
$Project = Join-Path $PSScriptRoot "Headroom.csproj"
$VersionInfoDir = Join-Path $PSScriptRoot "obj"
$VersionInfoFile = Join-Path $VersionInfoDir "Headroom.VersionInfo.cs"
$MsBuild = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if (!(Test-Path $MsBuild)) {
  throw "MSBuild not found: $MsBuild"
}

$InformationalVersion = "dev"
if ($DebugFixture) { $InformationalVersion = "dev-fixture" }
if ($Version) { $InformationalVersion = $Version.Trim() }

$FileVersion = "0.0.0.0"
if ($InformationalVersion -match '^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?') {
  $FileVersion = @(
    $Matches[1],
    $(if ($Matches[2]) { $Matches[2] } else { "0" }),
    $(if ($Matches[3]) { $Matches[3] } else { "0" }),
    $(if ($Matches[4]) { $Matches[4] } else { "0" })
  ) -join "."
}

$InformationalVersionLiteral = $InformationalVersion.Replace('\', '\\').Replace('"', '\"')
New-Item -ItemType Directory -Force -Path $VersionInfoDir | Out-Null
@(
  "using System.Reflection;",
  "[assembly: AssemblyTitle(""Headroom"")]",
  "[assembly: AssemblyProduct(""Headroom"")]",
  "[assembly: AssemblyVersion(""$FileVersion"")]",
  "[assembly: AssemblyFileVersion(""$FileVersion"")]",
  "[assembly: AssemblyInformationalVersion(""$InformationalVersionLiteral"")]"
) | Set-Content -Encoding UTF8 -Path $VersionInfoFile

& $MsBuild $Project `
  /nologo `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:OutDir="$Out\" `
  /p:AssemblyName="$AssemblyName" `
  /p:HeadroomVersionInfoFile="$VersionInfoFile"
if ($LASTEXITCODE -ne 0) {
  throw "MSBuild failed with exit code $LASTEXITCODE"
}

"Built: $Exe"

$thumbprintFile = Join-Path $PSScriptRoot ".cert-thumbprint"
if (Test-Path $thumbprintFile) {
    $thumbprint = (Get-Content $thumbprintFile -Raw).Trim()
    $signingCert = Get-Item "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
    if ($signingCert) {
        Set-AuthenticodeSignature -FilePath $Exe -Certificate $signingCert `
            -TimestampServer "http://timestamp.digicert.com" | Out-Null
        "Signed: $Exe"
    } else {
        Write-Warning "Cert not found in store. Remove .cert-thumbprint or create/import a code-signing certificate with that thumbprint."
    }
}

if ($Version) {
    $ReleaseOut = Join-Path $PSScriptRoot "releases"
    New-Item -ItemType Directory -Force -Path $ReleaseOut | Out-Null
    $ZipPath = Join-Path $ReleaseOut "Headroom-v$Version.zip"
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path $Exe -DestinationPath $ZipPath
    "Packaged: $ZipPath"
}
