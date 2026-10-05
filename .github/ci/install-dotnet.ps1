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
    'linux-x64' = '10069bec8783596484a610332f090d562802a41b9b40e3327a5a5688b572e10c296ae300f940d40461f23c157ed1b0843c2f8e6b3f20d8d8d9d83432d8143bac'
    'linux-arm64' = '9e409c14e00686d661c78fa4dd9ad0e4dcf695c328bd5ff777d05b4a9c34b42cf89b12573b92e9fb2f565dbe12016b4835f77c7d9a42b55a7494df21634cd5d6'
    'win-x64' = '7d170ed75fa9af34c00646621d92011dbd71943952e2787cd15df9be78e6452b55dadef34d7eff77b802e6af4959e071a55855ac649afeac70901c3a2a258716'
    'win-arm64' = '241abb2b345cff1b32d87a9e29da5e9d52f899f691e7b34661274477564c4717054c489814a9fd7a5526fc9e0d8174a0d951a4a845556eee53add526f71917e7'
}
$namExtension = if ($namWindows) { 'zip' } else { 'tar.gz' }
$namName = "dotnet-sdk-10.0.302-$ExpectedRid.$namExtension"
$namUri = "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.302/$namName"
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
if ($LASTEXITCODE -ne 0 -or $namVersion.Trim() -ne '10.0.302') { throw 'The exact SDK did not execute.' }
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
