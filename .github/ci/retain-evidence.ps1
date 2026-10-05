param(
    [Parameter(Mandatory)][ValidateSet('linux-x64', 'linux-arm64', 'win-x64', 'win-arm64')][string]$ExpectedRid,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$UploadRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$namRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$namUpload = [IO.Path]::GetFullPath($UploadRoot)
$namSource = [IO.Path]::GetFullPath((Get-Location).Path)
foreach ($namPath in @($namRoot, $namUpload)) {
    if ($namPath -eq $namSource -or $namPath.StartsWith($namSource + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Generated retention payloads must stay outside the source checkout.'
    }
}
if ($namUpload -eq $namRoot -or $namUpload.StartsWith($namRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $namUpload)) { throw 'Upload must use a new exact directory outside the evidence payload.' }
[IO.Directory]::CreateDirectory($namRoot) | Out-Null
$namHead = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $namHead -ne $ExpectedCommit) { throw 'Retention source does not equal the requested commit.' }
$namDirty = @(& git status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Actual source status could not be retained.' }
$namArchiveSource = Join-Path $namRoot 'retention-source.tar.gz'
if (Test-Path -LiteralPath $namArchiveSource) { throw 'An existing retained source archive must not be overwritten.' }
& git archive --format=tar.gz "--output=$namArchiveSource" $namHead
if ($LASTEXITCODE -ne 0) { throw 'Exact committed source archive could not be retained.' }
$namRetention = Join-Path $namRoot 'retention.json'
if (Test-Path -LiteralPath $namRetention) { throw 'An existing retention boundary must not be overwritten.' }
[pscustomobject]@{ ExactCommit = $namHead; RequestedRid = $ExpectedRid; SourceStatus = $namDirty;
    SourceClean = $namDirty.Count -eq 0; NativeManifestPresent = Test-Path -LiteralPath (Join-Path $namRoot 'MANIFEST.tsv');
    NativeDispositionPresent = Test-Path -LiteralPath (Join-Path $namRoot 'disposition.json');
    RetainedAt = [DateTimeOffset]::UtcNow; Format = 'pax-tar-gzip' } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $namRetention -Encoding utf8
$namManifest = Join-Path $namRoot 'RETENTION-MANIFEST.tsv'
if (Test-Path -LiteralPath $namManifest) { throw 'An existing retention manifest must not be overwritten.' }
$namEntries = [Collections.Generic.List[string]]::new()
foreach ($namFile in Get-ChildItem -LiteralPath $namRoot -Force -Recurse | Sort-Object FullName) {
    if (($namFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Retained payloads must not contain links.' }
    if ($namFile.PSIsContainer) { continue }
    $namRelative = [IO.Path]::GetRelativePath($namRoot, $namFile.FullName).Replace('\', '/')
    if ($namRelative -match '[\r\n\t]') { throw 'Unsafe retention-manifest path.' }
    $namEntries.Add("$namRelative`t$($namFile.Length)`t$((Get-FileHash -LiteralPath $namFile.FullName -Algorithm SHA256).Hash)")
}
[IO.File]::WriteAllLines($namManifest, $namEntries)
[IO.Directory]::CreateDirectory($namUpload) | Out-Null
$namBundle = Join-Path $namUpload "nam-$ExpectedRid.tar.gz"
$namStream = [IO.File]::Create($namBundle)
try {
    $namGzip = [IO.Compression.GZipStream]::new($namStream, [IO.Compression.CompressionLevel]::Fastest, $true)
    try { [Formats.Tar.TarFile]::CreateFromDirectory($namRoot, $namGzip, $false) }
    finally { $namGzip.Dispose() }
} finally { $namStream.Dispose() }
[pscustomobject]@{ Bundle = [IO.Path]::GetFileName($namBundle); Length = (Get-Item -LiteralPath $namBundle).Length;
    SHA256 = (Get-FileHash -LiteralPath $namBundle -Algorithm SHA256).Hash;
    ManifestSHA256 = (Get-FileHash -LiteralPath $namManifest -Algorithm SHA256).Hash;
    PayloadFiles = $namEntries.Count; ExactCommit = $namHead; RequestedRid = $ExpectedRid } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $namUpload 'bundle-identity.json') -Encoding utf8
Write-Output (Get-Content -LiteralPath (Join-Path $namUpload 'bundle-identity.json') -Raw)
