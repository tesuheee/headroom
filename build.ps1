param([string]$Version = "")

$ErrorActionPreference = "Stop"

$Out = Join-Path $PSScriptRoot "bin"
$Csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$Icon = Join-Path $PSScriptRoot "app.ico"

New-Item -ItemType Directory -Force -Path $Out | Out-Null

$Exe = Join-Path $Out "Headroom.exe"
$Source = Join-Path $PSScriptRoot "Program.cs"
$CscArgs = @(
  "/nologo",
  "/target:winexe",
  "/platform:x64",
  "/out:$Exe",
  "/win32icon:$Icon",
  "/reference:System.dll",
  "/reference:System.Core.dll",
  "/reference:System.Drawing.dll",
  "/reference:System.Windows.Forms.dll",
  "/reference:System.Net.Http.dll",
  $Source
)

& $Csc @CscArgs
if ($LASTEXITCODE -ne 0) {
  throw "C# compile failed with exit code $LASTEXITCODE"
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
