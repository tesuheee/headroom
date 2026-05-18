param([string]$Version = "")

$ErrorActionPreference = "Stop"

$PackageRoot = Join-Path $PSScriptRoot "packages"
$Pkg = Join-Path $PackageRoot "Microsoft.Web.WebView2.1.0.3537.50"
$Out = Join-Path $PSScriptRoot "bin"
$Csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$WinForms = Join-Path $Pkg "lib\net462\Microsoft.Web.WebView2.WinForms.dll"
$Core = Join-Path $Pkg "lib\net462\Microsoft.Web.WebView2.Core.dll"
$Loader = Join-Path $Pkg "runtimes\win-x64\native\WebView2Loader.dll"
$Icon = Join-Path $PSScriptRoot "app.ico"

if (-not (Test-Path $WinForms) -or -not (Test-Path $Core) -or -not (Test-Path $Loader)) {
  New-Item -ItemType Directory -Force -Path $PackageRoot | Out-Null
  $Nupkg = Join-Path $PackageRoot "Microsoft.Web.WebView2.1.0.3537.50.nupkg"
  if (-not (Test-Path $Nupkg)) {
    Invoke-WebRequest -UseBasicParsing -Uri "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/1.0.3537.50" -OutFile $Nupkg
  }
  $Zip = Join-Path $PackageRoot "Microsoft.Web.WebView2.1.0.3537.50.zip"
  Copy-Item $Nupkg $Zip -Force
  Expand-Archive -Path $Zip -DestinationPath $Pkg -Force
}

New-Item -ItemType Directory -Force -Path $Out | Out-Null

$OutWinForms = Join-Path $Out (Split-Path $WinForms -Leaf)
$OutCore = Join-Path $Out (Split-Path $Core -Leaf)
$OutLoader = Join-Path $Out (Split-Path $Loader -Leaf)

Copy-Item $WinForms $OutWinForms -Force
Copy-Item $Core $OutCore -Force
Copy-Item $Loader $OutLoader -Force

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
  "/reference:$WinForms",
  "/reference:$Core",
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

$ZipName = if ($Version) { "Headroom-v$Version.zip" } else { "Headroom.zip" }
$ReleaseOut = Join-Path $PSScriptRoot "releases"
New-Item -ItemType Directory -Force -Path $ReleaseOut | Out-Null
$ZipPath = Join-Path $ReleaseOut $ZipName
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
$PackageFiles = @($Exe, $OutWinForms, $OutCore, $OutLoader)
Compress-Archive -Path $PackageFiles -DestinationPath $ZipPath
"Packaged: $ZipPath"
