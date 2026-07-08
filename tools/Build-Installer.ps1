# Builds a signed, self-contained MSIX test installer into artifacts\.
# Creates (or reuses) a self-signed dev certificate whose subject matches the
# manifest Publisher (CN=MegaPDF Project). Release builds should replace this
# with Azure Trusted Signing (SDD 5.6).
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq "CN=MegaPDF Project" } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=MegaPDF Project" `
        -KeyUsage DigitalSignature -FriendlyName "MegaPDF Dev Signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -NotAfter (Get-Date).AddYears(2)
    Write-Host "Created dev signing certificate $($cert.Thumbprint)"
}

New-Item -ItemType Directory -Force (Join-Path $repoRoot "artifacts") | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $repoRoot "artifacts\MegaPDF-dev.cer") | Out-Null

dotnet build (Join-Path $repoRoot "src\MegaPDF.App\MegaPDF.App.csproj") `
    -c $Configuration `
    -p:Platform=$Platform `
    -p:WindowsPackageType=MSIX `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($cert.Thumbprint) `
    -p:SelfContained=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:AppxPackageDir="$repoRoot\artifacts\\"

if ($LASTEXITCODE -ne 0) { throw "Package build failed." }

Get-ChildItem (Join-Path $repoRoot "artifacts") -Recurse -Filter *.msix | ForEach-Object {
    # Ship our installer next to the package (the generated Add-AppDevPackage.ps1
    # trips over legacy developer-license acquisition on Windows 11).
    Copy-Item (Join-Path $PSScriptRoot "Install-MegaPDF.ps1") $_.DirectoryName -Force
    Write-Host "Installer: $($_.FullName)"
    Write-Host "Install with: powershell -ExecutionPolicy Bypass -File `"$($_.DirectoryName)\Install-MegaPDF.ps1`""
}
