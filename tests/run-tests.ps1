$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path ([System.IO.Path]::GetTempPath()) "HeadroomTests"
$Exe = Join-Path $Out "HeadroomTests.exe"
$Csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

New-Item -ItemType Directory -Force -Path $Out | Out-Null

$Sources = @(
  (Join-Path $Root "src\Models.cs"),
  (Join-Path $Root "src\DebugLog.cs"),
  (Join-Path $Root "src\FileWrites.cs"),
  (Join-Path $Root "src\Json.cs"),
  (Join-Path $Root "src\CredentialStores.cs"),
  (Join-Path $Root "src\UsageParsers.cs"),
  (Join-Path $Root "src\RefreshPolicy.cs"),
  (Join-Path $Root "tests\ParserTests.cs"),
  (Join-Path $Root "tests\CredentialStoreTests.cs"),
  (Join-Path $Root "tests\RefreshPolicyTests.cs"),
  (Join-Path $Root "tests\TestRunner.cs")
)

$CscArgs = @(
  "/nologo",
  "/nowarn:0649",
  "/target:exe",
  "/out:$Exe",
  "/reference:System.dll",
  "/reference:System.Core.dll",
  "/reference:System.Drawing.dll",
  "/reference:System.Windows.Forms.dll",
  "/reference:System.Net.Http.dll",
  "/reference:System.Web.Extensions.dll"
) + $Sources

& $Csc @CscArgs
if ($LASTEXITCODE -ne 0) {
  throw "Parser test compile failed with exit code $LASTEXITCODE"
}

& $Exe $Root
if ($LASTEXITCODE -ne 0) {
  throw "Parser tests failed with exit code $LASTEXITCODE"
}
