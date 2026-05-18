#Requires -RunAsAdministrator

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=Headroom" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(5)

$rootStore = [System.Security.Cryptography.X509Certificates.X509Store]::new("Root", "LocalMachine")
$rootStore.Open("ReadWrite")
$rootStore.Add($cert)
$rootStore.Close()

$cert.Thumbprint | Out-File (Join-Path $PSScriptRoot ".cert-thumbprint") -NoNewline -Encoding ascii
"Certificate created: $($cert.Thumbprint)"
