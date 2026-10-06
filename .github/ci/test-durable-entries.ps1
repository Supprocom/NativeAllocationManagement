param([Parameter(Mandatory)][string]$EvidenceRoot, [Parameter(Mandatory)][string]$ScratchRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'durable-entries.ps1')
$namRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$namScratch = [IO.Path]::GetFullPath($ScratchRoot)
if ($namRoot -ceq $namScratch -or $namScratch.StartsWith($namRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $namRoot) -or (Test-Path -LiteralPath $namScratch)) { throw 'Fixtures require new separate evidence/scratch roots.' }
[IO.Directory]::CreateDirectory($namRoot) | Out-Null
[IO.Directory]::CreateDirectory($namScratch) | Out-Null
$namResults = [Collections.Generic.List[object]]::new()
$namRejections = [Collections.Generic.List[object]]::new()
function Assert-NamEntryRejection {
    param([string]$Path, [string]$Message)
    $namRejected = $false
    try { Get-NamDurableEntries -EvidenceRoot $Path -ObservationName 'observation.json' | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Message) { throw }
        $namRejections.Add([pscustomobject]@{ Path = $Path; ActualException = $_.Exception.Message; ExpectedPattern = $Message })
        $namRejected = $true
    }
    if (-not $namRejected) { throw 'A required retention rejection did not occur.' }
}
function New-NamEntryFixture {
    param([string]$Name)
    $namPath = Join-Path $namScratch $Name
    [IO.Directory]::CreateDirectory((Join-Path $namPath 'tmp')) | Out-Null
    $namPath
}
$namOrdinary = New-NamEntryFixture 'ordinary-hidden'
[IO.File]::WriteAllText((Join-Path $namOrdinary 'ordinary.txt'), 'actual ordinary bytes')
[IO.File]::WriteAllText((Join-Path $namOrdinary '.hidden.txt'), 'actual hidden bytes')
$namEntries = @(Get-NamDurableEntries -EvidenceRoot $namOrdinary -ObservationName 'observation.json')
if (@($namEntries | Where-Object { -not $_.PSIsContainer }).Count -ne 3 -or
    @($namEntries | Where-Object Name -ceq '.hidden.txt').Count -ne 1) { throw 'Complete ordinary/hidden file graph differs.' }
foreach ($namFile in $namEntries | Where-Object { -not $_.PSIsContainer }) {
    if ((Get-FileHash -LiteralPath $namFile.FullName -Algorithm SHA256).Hash.Length -ne 64) { throw 'A real retained digest is unavailable.' }
}
[IO.Directory]::CreateDirectory((Join-Path $namRoot 'ordinary-hidden')) | Out-Null
foreach ($namFile in $namEntries | Where-Object { -not $_.PSIsContainer }) {
    [IO.File]::Copy($namFile.FullName, (Join-Path $namRoot "ordinary-hidden/$($namFile.Name)"), $false)
}
$namObservation = [IO.File]::ReadAllText((Join-Path $namOrdinary 'observation.json')) | ConvertFrom-Json
if (@($namObservation.ExcludedEndpoints).Count -ne 0) { throw 'Ordinary files were misreported as excluded.' }
$namResults.Add([pscustomobject]@{ Case = 'ordinary-hidden'; Passed = $true })
Assert-NamEntryRejection $namOrdinary 'must not be overwritten'
$namResults.Add([pscustomobject]@{ Case = 'overwrite-refused'; Passed = $true })
$namLinkRoot = New-NamEntryFixture 'link-rejected'
$namLinkType = if ([OperatingSystem]::IsWindows()) { 'Junction' } else { 'SymbolicLink' }
New-Item -ItemType $namLinkType -Path (Join-Path $namLinkRoot 'linked') -Target $namOrdinary | Out-Null
Assert-NamEntryRejection $namLinkRoot 'must not contain links'
$namResults.Add([pscustomobject]@{ Case = 'link-rejected'; Passed = $true })
if (-not [OperatingSystem]::IsWindows()) {
    $namDead = New-NamEntryFixture 'dead-runtime-endpoints'
    foreach ($namDirection in @('in', 'out')) {
        & mkfifo -- (Join-Path $namDead "tmp/clr-debug-pipe-2147483647-1-$namDirection")
        if ($LASTEXITCODE -ne 0) { throw 'Actual named-pipe fixture creation failed.' }
    }
    $namEntries = @(Get-NamDurableEntries -EvidenceRoot $namDead -ObservationName 'observation.json')
    $namObservation = [IO.File]::ReadAllText((Join-Path $namDead 'observation.json')) | ConvertFrom-Json
    if (@($namObservation.ExcludedEndpoints).Count -ne 2 -or
        @($namEntries | Where-Object { -not $_.PSIsContainer }).Count -ne 1 -or
        @($namObservation.ExcludedEndpoints | Where-Object { $_.ProcessPresent -or $_.ItemType -cne 'NamedPipe' -or $_.ProcessId -ne 2147483647 }).Count -ne 0 -or
        -not (Test-Path -LiteralPath (Join-Path $namDead 'tmp/clr-debug-pipe-2147483647-1-in'))) { throw 'Real abandoned endpoints were not recorded without deletion.' }
    $namResults.Add([pscustomobject]@{ Case = 'dead-runtime-endpoints'; Passed = $true })
    [IO.File]::Copy((Join-Path $namDead 'observation.json'), (Join-Path $namRoot 'dead-runtime-endpoints.json'), $false)
    $namActive = New-NamEntryFixture 'active-runtime-endpoint'
    & mkfifo -- (Join-Path $namActive "tmp/clr-debug-pipe-$PID-1-in")
    if ($LASTEXITCODE -ne 0) { throw 'Actual active endpoint fixture creation failed.' }
    Assert-NamEntryRejection $namActive 'still has an active process'
    $namResults.Add([pscustomobject]@{ Case = 'active-runtime-endpoint'; Passed = $true })
    $namUnknown = New-NamEntryFixture 'unknown-special'
    & mkfifo -- (Join-Path $namUnknown 'tmp/unrecognized.pipe')
    if ($LASTEXITCODE -ne 0) { throw 'Actual unknown special-file fixture creation failed.' }
    Assert-NamEntryRejection $namUnknown 'Unexpected special evidence entry'
    $namResults.Add([pscustomobject]@{ Case = 'unknown-special'; Passed = $true })
}
[pscustomobject]@{ Passed = $true; Architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString();
    Rid = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier; Cases = $namResults.ToArray();
    ScratchRoot = $namScratch; ActualRejections = $namRejections.ToArray();
    UnixNamedPipeCasesExecuted = -not [OperatingSystem]::IsWindows(); WindowsLinkType = $namLinkType;
    Scope = 'Special/link fixture objects remain in the separate scratch root; results, actual rejection messages and successful observations are durable ordinary evidence files.' } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $namRoot 'results.json') -Encoding utf8
Write-Output ([IO.File]::ReadAllText((Join-Path $namRoot 'results.json')))
