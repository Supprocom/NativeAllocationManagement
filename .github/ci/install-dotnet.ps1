param(
    [Parameter(Mandatory)][ValidateSet('linux-x64', 'linux-arm64', 'win-x64', 'win-arm64')][string]$ExpectedRid,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$ToolRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'validation-contract.ps1')
$namWindows = $ExpectedRid.StartsWith('win-', [StringComparison]::Ordinal)
$namArch = $ExpectedRid.Split('-')[1]
if ($namWindows -ne [OperatingSystem]::IsWindows() -or
    -not [string]::Equals([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString(), $namArch, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The requested SDK must execute on its actual native OS/architecture.'
}
if ([string]::IsNullOrWhiteSpace($env:GITHUB_ENV) -or [string]::IsNullOrWhiteSpace($env:GITHUB_PATH)) {
    throw 'GitHub runner environment files are required for this installer.'
}
$namTools = [IO.Path]::GetFullPath($ToolRoot)
if (Test-Path -LiteralPath $namTools) { throw 'SDK extraction requires a new exact directory.' }
$namEvidence = Join-Path ([IO.Path]::GetFullPath($EvidenceRoot)) 'tools'
[IO.Directory]::CreateDirectory($namEvidence) | Out-Null
$namHashes = @{
    'linux-x64' = 'b7b3d8461cf98b07d2552ce9199702fa3ad010ac2671a5f8cd5136f7534e50f6855ba7bbb02b11494563f1d04156276a31cda1103b900e5eecd0d9473dc7b9c5'
    'linux-arm64' = '0966aa2b70f4677b8319be9df447a78a0dcd6164836425b55a68fd714f4023feb87b4cac21b9a996b79472ad54487cd0adcd2a9dfd570e2b2a1d26423a2baa9b'
    'win-x64' = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48'
    'win-arm64' = 'f78ecd8b48e24fd72ee604687b75f1bbb81505e8343f7d52e4baf9ea32372557eb30e88facaaad195bf2646fcce117fd941e49da0d5bdee1d60d63d836968429'
}
$namExtension = if ($namWindows) { 'zip' } else { 'tar.gz' }
$namName = "dotnet-sdk-10.0.403-$ExpectedRid.$namExtension"
$namUri = "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/$namName"
$namArchive = Join-Path $namEvidence $namName
if (Test-Path -LiteralPath $namArchive) { throw 'An existing SDK archive must not be overwritten.' }
$namStarted = [DateTimeOffset]::UtcNow
Invoke-WebRequest -Uri $namUri -OutFile $namArchive
$namObserved = (Get-FileHash -LiteralPath $namArchive -Algorithm SHA512).Hash
if ($namObserved -ne $namHashes[$ExpectedRid]) { throw 'SDK archive differs from its pinned official SHA-512 identity.' }
[IO.Directory]::CreateDirectory($namTools) | Out-Null
if ($namWindows) {
    [IO.Compression.ZipFile]::ExtractToDirectory($namArchive, $namTools)
} else {
    & tar -xzf $namArchive -C $namTools
    if ($LASTEXITCODE -ne 0) { throw "SDK extraction failed with exit $LASTEXITCODE." }
}
$namExecutable = Join-Path $namTools $(if ($namWindows) { 'dotnet.exe' } else { 'dotnet' })
$env:DOTNET_ROOT = $namTools
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$namVersion = & $namExecutable --version
if ($LASTEXITCODE -ne 0 -or $namVersion.Trim() -ne '10.0.403') { throw 'The exact SDK did not execute.' }
$namRuntimes = @(& $namExecutable --list-runtimes)
if ($LASTEXITCODE -ne 0) { throw 'The isolated SDK did not enumerate its actual runtimes.' }
Write-Output ($namRuntimes -join "`n")
Assert-NamRuntimeInventory -ExpectedRid $ExpectedRid -Runtimes $namRuntimes
$namIdentity = @(& $namExecutable --info)
if ($LASTEXITCODE -ne 0 -or ($namIdentity -join "`n") -notmatch "(?m)^\s*RID:\s+$ExpectedRid\s*$") {
    throw 'The SDK did not execute with the requested native RID.'
}
[pscustomobject]@{ RequestedRid = $ExpectedRid; URI = $namUri; Archive = $namArchive;
    Length = (Get-Item -LiteralPath $namArchive).Length; SHA512 = $namObserved;
    SHA256 = (Get-FileHash -LiteralPath $namArchive -Algorithm SHA256).Hash;
    Executable = $namExecutable; SDK = $namVersion.Trim(); Runtimes = $namRuntimes;
    Identity = $namIdentity; StartedAt = $namStarted; EndedAt = [DateTimeOffset]::UtcNow } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $namEvidence 'dotnet-install.json') -Encoding utf8
"DOTNET_ROOT=$namTools" | Add-Content -LiteralPath $env:GITHUB_ENV -Encoding utf8
'DOTNET_MULTILEVEL_LOOKUP=0' | Add-Content -LiteralPath $env:GITHUB_ENV -Encoding utf8
'DOTNET_CLI_UI_LANGUAGE=en-US' | Add-Content -LiteralPath $env:GITHUB_ENV -Encoding utf8
$namTools | Add-Content -LiteralPath $env:GITHUB_PATH -Encoding utf8
Write-Output ($namIdentity -join "`n")
