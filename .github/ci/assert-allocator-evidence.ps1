param(
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$Trx,
    [Parameter(Mandatory)][string]$Worker,
    [Parameter(Mandatory)][string]$Runtime
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$namRoot = [IO.Path]::GetFullPath($EvidenceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
[xml]$namTrx = [IO.File]::ReadAllText($Trx)
$namResults = @($namTrx.TestRun.Results.UnitTestResult)
$namSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$namExpected = @{
    Region = 'NativeRegionBeatsOptimizedTypedArrayPools'
    Arena = 'NativeArenaBeatsOptimizedTypedArrayPools'
    ArenaScoped = 'NativeArenaBeatsOptimizedTypedArrayPools'
}
foreach ($namKind in @('Region', 'Arena', 'ArenaScoped')) {
    $namTest = @($namResults | Where-Object { $_.testName.EndsWith($namExpected[$namKind], [StringComparison]::Ordinal) })
    if ($namTest.Count -ne 1 -or $namTest[0].outcome -ne 'Passed') { throw "Required allocator test did not pass: $namKind" }
    $namMatching = @()
    foreach ($namMatch in [regex]::Matches([string]$namTest[0].Output.StdOut, '(?m)^allocatorWorkerEvidence=([^\r\n]+)')) {
        $namDirectory = [IO.Path]::GetFullPath($namMatch.Groups[1].Value)
        if (-not $namDirectory.StartsWith($namRoot, [StringComparison]::Ordinal)) { throw 'Allocator evidence escaped its retained boundary.' }
        $namIdentity = [IO.File]::ReadAllText((Join-Path $namDirectory 'allocator-worker.json')) | ConvertFrom-Json
        if ($namIdentity.Kind -eq $namKind) { $namMatching += [pscustomobject]@{ Directory = $namDirectory; Identity = $namIdentity } }
    }
    if ($namMatching.Count -ne 1 -or -not $namSeen.Add($namMatching[0].Directory)) { throw "Missing or duplicate allocator worker: $namKind" }
    $namDirectory = $namMatching[0].Directory
    $namIdentity = $namMatching[0].Identity
    foreach ($namInput in @(@('Worker', $Worker), @('CandidateRuntime', $Runtime))) {
        $namRecorded = $namIdentity.($namInput[0])
        $namActual = Get-Item -LiteralPath $namInput[1]
        if ($namRecorded.Path -cne $namActual.FullName -or $namRecorded.Length -ne $namActual.Length -or
            $namRecorded.SHA256 -cne (Get-FileHash -LiteralPath $namActual.FullName -Algorithm SHA256).Hash) {
            throw "Actual allocator input identity differs: $namKind/$($namInput[0])"
        }
    }
    $namArguments = @($Worker, '--allocator-regression-worker', '--kind', $namKind)
    if (($namIdentity.Arguments | ConvertTo-Json -Compress) -cne ($namArguments | ConvertTo-Json -Compress)) { throw 'Allocator arguments differ.' }
    $namEnvironment = $namIdentity.CompilationEnvironment
    if ($namEnvironment.DOTNET_TieredCompilation -cne '0' -or $namEnvironment.DOTNET_TieredPGO -cne '0' -or
        @($namEnvironment.PSObject.Properties | Where-Object { $_.Name -match 'Jit' -and -not [string]::IsNullOrEmpty($_.Value) }).Count -ne 0) {
        throw 'Uninstrumented controlled allocator settings differ.'
    }
    $namCommand = [IO.File]::ReadAllText((Join-Path $namDirectory 'command.json')) | ConvertFrom-Json
    if ($null -eq $namCommand.ExitCode -or $namCommand.ExitCode -ne 0 -or $namCommand.TimedOut -or
        $namCommand.DeadlineSeconds -ne 10 -or [DateTimeOffset]$namCommand.EndedAt -lt [DateTimeOffset]$namCommand.StartedAt -or
        (($namCommand.Arguments | ConvertFrom-Json) | ConvertTo-Json -Compress) -cne ($namArguments | ConvertTo-Json -Compress) -or
        -not [string]::IsNullOrEmpty([IO.File]::ReadAllText((Join-Path $namDirectory 'stderr.log')))) {
        throw 'Actual allocator command did not pass its unchanged boundary.'
    }
    $namReport = [IO.File]::ReadAllText((Join-Path $namDirectory 'stdout.log')) | ConvertFrom-Json
    $namSampleCount = if ($namKind -eq 'ArenaScoped') { 10 } else { 8 }
    if (-not $namReport.Passed -or $namReport.MinimumSpeedup -ne 1.50 -or $namReport.MedianSpeedup -lt 1.50 -or
        $namReport.AggregateSpeedup -lt 1.50 -or $namReport.TieredCompilation -cne '0' -or $namReport.TieredPgo -cne '0' -or
        $namReport.SampleCount -ne $namSampleCount -or @($namReport.Pairs).Count -ne $namSampleCount) { throw 'Retained raw allocator report did not pass.' }
    $namNativeName = if ($namKind -eq 'Region') { 'Region' } else { 'Arena' }
    foreach ($namPair in $namReport.Pairs) {
        $namNative = $namPair.$namNativeName
        if ([long]$namPair.ArrayPool.Checksum -ne [long]$namNative.Checksum -or
            [long]$namPair.ArrayPool.LogicalBytes -ne [long]$namNative.LogicalBytes -or $namNative.FreshSegmentCount -ne 0 -or
            $namNative.ElapsedMilliseconds -lt 10 -or $namPair.ArrayPool.ElapsedMilliseconds -lt 10) { throw 'Retained sample output/work differs.' }
        foreach ($namSample in @($namPair.ArrayPool, $namNative)) {
            if ($namSample.Gen0Collections -ne 0 -or $namSample.Gen1Collections -ne 0 -or $namSample.Gen2Collections -ne 0 -or
                $namSample.Attempt -lt 1 -or $namSample.Attempt -gt 3) { throw 'Retained sample acceptance differs.' }
        }
    }
    Write-Output "Verified original uninstrumented $namKind report: $namDirectory"
}
