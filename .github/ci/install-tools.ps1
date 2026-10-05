param(
    [Parameter(Mandatory)][ValidateSet('linux-x64', 'linux-arm64', 'win-x64', 'win-arm64')][string]$ExpectedRid,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$ToolRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$namEvidence = [IO.Path]::GetFullPath($EvidenceRoot)
$namTools = [IO.Path]::GetFullPath($ToolRoot)
$namWindows = $ExpectedRid.StartsWith('win-', [StringComparison]::Ordinal)
$namArch = $ExpectedRid.Split('-')[1]
if ($namWindows -ne [OperatingSystem]::IsWindows()) { throw 'Native OS does not match the requested validation cell.' }
if (-not [string]::Equals([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString(), $namArch, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native OS architecture does not match the requested validation cell.'
}
if (Test-Path -LiteralPath $namTools) { throw 'Tool extraction must start in a new, exact directory.' }
[IO.Directory]::CreateDirectory($namTools) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $namEvidence 'tools')) | Out-Null
$namAssets = @{
    'linux-x64' = @('powershell-7.6.6-linux-x64.tar.gz', 'ddbc4a2d113bbd46d283cfedcbcd117a70caefd7673f41f2b4e0000badf103bc')
    'linux-arm64' = @('powershell-7.6.6-linux-arm64.tar.gz', '924829e54c983648f6f1419a2dc7f9433c861b2fb5bd57736ff096c24f133729')
    'win-x64' = @('PowerShell-7.6.6-win-x64.zip', '02fe458be20493fbdf43f61ea20610b811ee6c738ab1676c61b9cfcd1a33c860')
    'win-arm64' = @('PowerShell-7.6.6-win-arm64.zip', 'bbde9dda31d148415eccb5fbe1638e6400a144187b006e5b3fd8ec2f39d781be')
}
$namAsset, $namHash = $namAssets[$ExpectedRid]
$namArchive = Join-Path $namEvidence "tools/$namAsset"
$namUri = "https://github.com/PowerShell/PowerShell/releases/download/v7.6.6/$namAsset"
$namStarted = [DateTimeOffset]::UtcNow
Invoke-WebRequest -Uri $namUri -OutFile $namArchive
$namObserved = (Get-FileHash -LiteralPath $namArchive -Algorithm SHA256).Hash
if (-not [string]::Equals($namObserved, $namHash, [StringComparison]::OrdinalIgnoreCase)) { throw 'Pinned PowerShell archive hash mismatch.' }
if ($namWindows) {
    [IO.Compression.ZipFile]::ExtractToDirectory($namArchive, $namTools)
} else {
    & tar -xzf $namArchive -C $namTools
    if ($LASTEXITCODE -ne 0) { throw "PowerShell archive extraction failed with exit $LASTEXITCODE." }
    & chmod u+x (Join-Path $namTools 'pwsh')
    if ($LASTEXITCODE -ne 0) { throw "PowerShell executable-mode setup failed with exit $LASTEXITCODE." }
}
$namExecutable = Join-Path $namTools $(if ($namWindows) { 'pwsh.exe' } else { 'pwsh' })
$namIdentity = & $namExecutable -NoProfile -Command '[pscustomobject]@{ Version = $PSVersionTable.PSVersion.ToString(); ProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString(); OSArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() } | ConvertTo-Json -Compress'
if ($LASTEXITCODE -ne 0) { throw 'The pinned native PowerShell did not execute.' }
$namParsed = $namIdentity | ConvertFrom-Json
if ($namParsed.Version -ne '7.6.6' -or -not [string]::Equals($namParsed.ProcessArchitecture, $namArch, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Pinned PowerShell version or execution architecture does not match.'
}
[pscustomobject]@{ RequestedRid = $ExpectedRid; URI = $namUri; Archive = $namArchive; Length = (Get-Item -LiteralPath $namArchive).Length;
    SHA256 = $namObserved; Executable = $namExecutable; Identity = $namParsed; StartedAt = $namStarted; EndedAt = [DateTimeOffset]::UtcNow } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $namEvidence 'tools/powershell-install.json') -Encoding utf8
if ([string]::IsNullOrWhiteSpace($env:GITHUB_ENV) -or [string]::IsNullOrWhiteSpace($env:GITHUB_PATH)) { throw 'GitHub runner environment files are required for this installer.' }
"NAM_PWSH_PATH=$namExecutable" | Add-Content -LiteralPath $env:GITHUB_ENV -Encoding utf8
$namTools | Add-Content -LiteralPath $env:GITHUB_PATH -Encoding utf8
Write-Output $namIdentity
