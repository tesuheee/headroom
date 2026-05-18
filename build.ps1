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

Copy-Item $WinForms $Out -Force
Copy-Item $Core $Out -Force
Copy-Item $Loader $Out -Force

$Exe = Join-Path $Out "AiUsageWebView2.exe"
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
        Write-Warning "Cert not found in store. Run setup-cert.ps1 first."
    }
}

$ZipName = if ($Version) { "AiUsageWebView2-v$Version.zip" } else { "AiUsageWebView2.zip" }
$ZipPath = Join-Path $PSScriptRoot $ZipName
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
$PackageFiles = Get-ChildItem $Out -File | Where-Object { $_.Name -ne "settings.json" }
Compress-Archive -Path $PackageFiles.FullName -DestinationPath $ZipPath
"Packaged: $ZipPath"
