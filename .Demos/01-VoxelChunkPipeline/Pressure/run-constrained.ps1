[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path,
    [int]$Pairs = 30,
    [int]$ControlPairs = 30,
    [int]$CalibrationChunks = 2,
    [int]$CalibrationWorkers = 2,
    [int]$CalibrationIterations = 1,
    [int]$Chunks = 8,
    [int]$Workers = 4,
    [int]$Iterations = 2,
    [int]$Warmup = 0,
    [int]$CpuLimit = 2,
    [int]$TimeoutSeconds = 240,
    [string]$Image,
    [string]$OutputPath,
    [switch]$SkipImageBuild,
    [switch]$SkipControl,
    [switch]$Enforce)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-ProcessArguments {
    param([string[]]$Arguments)

    return [string]::Join(
        " ",
        ($Arguments | ForEach-Object {
            if ($_ -match '[\s"]') {
                '"' + $_.Replace('"', '\\"') + '"'
            } else {
                $_
            }
        }))
}

if ($Pairs -lt 30) {
    throw "Pairs must be at least 30 for the constrained statistical gate."
}

if ($ControlPairs -lt 0) {
    throw "ControlPairs cannot be negative."
}

if ($Warmup -lt 0) {
    throw "Warmup cannot be negative."
}

$RepoRoot = (Resolve-Path $RepoRoot).Path
$demoRoot = Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline"
$shortCommit = (& git -C $RepoRoot rev-parse --short=12 HEAD).Trim()
$fullCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
if ([string]::IsNullOrWhiteSpace($shortCommit) -or [string]::IsNullOrWhiteSpace($fullCommit)) {
    throw "The benchmark requires a readable Git commit identity."
}

if ([string]::IsNullOrWhiteSpace($Image)) {
    $Image = "nam-voxel-pressure:$shortCommit"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $RepoRoot "artifacts\voxel-pressure-$shortCommit.json"
}

$profiles = @(
    [pscustomobject]@{ Name = "1GiB"; Bytes = 1GB },
    [pscustomobject]@{ Name = "768MiB"; Bytes = 768MB },
    [pscustomobject]@{ Name = "640MiB"; Bytes = 640MB }
)

function Invoke-CheckedCommand {
    param([string]$FileName, [string[]]$Arguments, [int]$TimeoutMs = 120000)

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Arguments = ConvertTo-ProcessArguments $Arguments

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) {
        throw "Could not start $FileName."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutMs)) {
        try { $process.Kill($true) } catch { }
        throw "$FileName exceeded the $TimeoutMs ms bound."
    }

    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "$FileName failed with exit $($process.ExitCode): $stderr"
    }

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        StandardOutput = $stdout
        StandardError = $stderr
    }
}

try {
    Invoke-CheckedCommand "docker" @("info", "--format", "{{.ServerVersion}}") 30000 | Out-Null
} catch {
    throw "Docker Desktop's Linux engine is required for the constrained experiment. $($_.Exception.Message)"
}

$pressureDirectory = Join-Path $demoRoot "Pressure"
if (-not $SkipImageBuild) {
    Invoke-CheckedCommand "docker" @(
        "build",
        "--file", (Join-Path $pressureDirectory "Dockerfile"),
        "--tag", $Image,
        $pressureDirectory) 300000 | Out-Null
}

$safeAssembly = "/workspace/.Demos/01-VoxelChunkPipeline/SafeCSharp/bin/Release/net10.0/VoxelChunkPipeline.SafeCSharp.dll"
$namAssembly = "/workspace/.Demos/01-VoxelChunkPipeline/NAM/bin/Release/net10.0/VoxelChunkPipeline.NAM.dll"

function Get-LastJsonObject {
    param([string]$Text)

    $lines = @($Text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    for ($index = $lines.Count - 1; $index -ge 0; $index--) {
        try {
            return $lines[$index] | ConvertFrom-Json -Depth 100
        } catch {
        }
    }

    return $null
}

function Invoke-DockerChild {
    param(
        [string]$ProfileName,
        [long]$MemoryBytes,
        [string]$Implementation,
        [int]$Pair,
        [string]$Mode,
        [int]$WorkChunks,
        [int]$WorkWorkers,
        [int]$WorkIterations,
        [int]$WorkWarmup)

    $assembly = if ($Implementation -eq "SafeCSharp") { $safeAssembly } else { $namAssembly }
    $containerName = "nam-voxel-$shortCommit-$($ProfileName.ToLowerInvariant())-$($Implementation.ToLowerInvariant())-$Pair-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $arguments = @(
        "run",
        "--name", $containerName,
        "--memory", "$MemoryBytes`b",
        "--memory-swap", "$MemoryBytes`b",
        "--memory-swappiness", "0",
        "--cpus", $CpuLimit.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--pids-limit", "256",
        "--volume", "$RepoRoot`:/workspace:ro",
        "--workdir", "/workspace",
        $Image,
        $assembly,
        "--pressure",
        "--seed", "1706251",
        "--chunks", $WorkChunks.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--workers", $WorkWorkers.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--iterations", $WorkIterations.ToString([Globalization.CultureInfo]::InvariantCulture))
    $arguments += @("--warmup", $WorkWarmup.ToString([Globalization.CultureInfo]::InvariantCulture))

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = "docker"
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Arguments = ConvertTo-ProcessArguments $arguments

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $startedAt = [DateTime]::UtcNow
    if (-not $process.Start()) {
        throw "Could not start Docker sample $containerName."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        try { & docker kill $containerName 2>$null | Out-Null } catch { }
        try { $process.Kill($true) } catch { }
        $process.WaitForExit()
    } else {
        $process.WaitForExit()
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = if ($timedOut) { 124 } else { $process.ExitCode }
    $state = $null
    try {
        $stateText = (& docker inspect --format '{{json .State}}' $containerName 2>$null | Out-String).Trim()
        if ($stateText) {
            $state = $stateText | ConvertFrom-Json
        }
    } catch {
    }
    try { & docker rm -f $containerName 2>$null | Out-Null } catch { }

    $child = Get-LastJsonObject $stdout
    $status = if ($timedOut) {
        "Timeout"
    } elseif ($state -and $state.OOMKilled) {
        "OomKilled"
    } elseif ($exitCode -in @(137, 143)) {
        "OomKilled"
    } elseif ($exitCode -eq 0 -and $child) {
        "Completed"
    } else {
        "Failed"
    }

    [pscustomobject]@{
        Profile = $ProfileName
        MemoryLimitBytes = $MemoryBytes
        Implementation = $Implementation
        Pair = $Pair
        Mode = $Mode
        Status = $status
        ExitCode = $exitCode
        OomKilled = [bool]($state -and $state.OOMKilled)
        StartedUtc = $startedAt
        FinishedUtc = [DateTime]::UtcNow
        Child = $child
        StandardOutput = if ($stdout.Length -gt 12000) { $stdout.Substring($stdout.Length - 12000) } else { $stdout }
        StandardError = if ($stderr.Length -gt 12000) { $stderr.Substring($stderr.Length - 12000) } else { $stderr }
    }
}

function Get-WorkSignature {
    param($Child)

    $names = @(
        "digest", "chunks", "visibleFaces", "vertices", "indices", "stagedBytes",
        "managedPayloadObjectBytes", "emptySections", "uniformSections", "expandedSections",
        "packedSections", "multiPackedSections", "transparentMaskCount", "transparentMaskWords",
        "dominantTransparentSections", "residualTransparentSections", "opaqueVisibleFaces",
        "transparentVisibleFaces", "opaqueVertices", "transparentVertices", "opaqueIndices",
        "transparentIndices", "opaqueStagedBytes", "transparentStagedBytes", "enabledStageBytes",
        "input", "output", "chunkOutputs")
    $signature = [ordered]@{}
    foreach ($name in $names) {
        $value = $Child.result.$name
        $signature[$name] = if ($name -in @("input", "output", "chunkOutputs")) {
            $value | ConvertTo-Json -Depth 100 -Compress
        } else {
            $value
        }
    }
    return $signature
}

function Test-WorkParity {
    param($SafeChild, $NamChild)

    if (-not $SafeChild -or -not $NamChild) {
        return $false
    }

    $left = Get-WorkSignature $SafeChild
    $right = Get-WorkSignature $NamChild
    foreach ($name in $left.Keys) {
        if ([string]$left[$name] -ne [string]$right[$name]) {
            return $false
        }
    }

    return $true
}

function Get-Mean {
    param([double[]]$Values)
    if ($Values.Count -eq 0) { return 0.0 }
    return ($Values | Measure-Object -Average).Average
}

function Get-StdDev {
    param([double[]]$Values)
    if ($Values.Count -lt 2) { return 0.0 }
    $mean = Get-Mean $Values
    $sum = 0.0
    foreach ($value in $Values) { $sum += [math]::Pow($value - $mean, 2) }
    return [math]::Sqrt($sum / ($Values.Count - 1))
}

function Get-Percentile {
    param([double[]]$Values, [double]$Percentile)
    if ($Values.Count -eq 0) { return 0.0 }
    $sorted = @($Values | Sort-Object)
    $position = ($sorted.Count - 1) * $Percentile
    $lower = [math]::Floor($position)
    $upper = [math]::Ceiling($position)
    if ($lower -eq $upper) { return [double]$sorted[$lower] }
    return [double]$sorted[$lower] + ([double]$sorted[$upper] - [double]$sorted[$lower]) * ($position - $lower)
}

function Get-TCritical95 {
    param([int]$DegreesOfFreedom)
    $table = @(12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306,
        2.262, 2.228, 2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110,
        2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060, 2.056,
        2.052, 2.048, 2.04523, 2.04227)
    if ($DegreesOfFreedom -le 0) { throw "Student-t degrees of freedom must be positive." }
    if ($DegreesOfFreedom -le $table.Count) { return $table[$DegreesOfFreedom - 1] }
    if ($DegreesOfFreedom -le 40) { return 2.02108 }
    if ($DegreesOfFreedom -le 60) { return 2.00030 }
    if ($DegreesOfFreedom -le 80) { return 1.99006 }
    if ($DegreesOfFreedom -le 100) { return 1.98397 }
    if ($DegreesOfFreedom -le 120) { return 1.97993 }
    return 1.96
}

function Summarize-Runs {
    param(
        [string]$ProfileName,
        [long]$MemoryBytes,
        [object[]]$Runs,
        [int]$ExpectedPairs,
        [bool]$IsControl)

    $completed = @($Runs | Where-Object { $_.Status -eq "Completed" })
    $safe = @($completed | Where-Object { $_.Implementation -eq "SafeCSharp" })
    $nam = @($completed | Where-Object { $_.Implementation -eq "NAM" })
    $pairs = @()
    for ($pair = 0; $pair -lt $ExpectedPairs; $pair++) {
        $safeRun = $safe | Where-Object { $_.Pair -eq $pair } | Select-Object -First 1
        $namRun = $nam | Where-Object { $_.Pair -eq $pair } | Select-Object -First 1
        if ($safeRun -and $namRun) {
            $parity = Test-WorkParity $safeRun.Child $namRun.Child
            $pairs += [pscustomobject]@{
                Pair = $pair
                SafeMilliseconds = [double]$safeRun.Child.elapsedMilliseconds
                NamMilliseconds = [double]$namRun.Child.elapsedMilliseconds
                Speedup = [double]$safeRun.Child.elapsedMilliseconds / [double]$namRun.Child.elapsedMilliseconds
                Parity = $parity
                SafeGen0 = [int]$safeRun.Child.result.measuredGen0Collections
                SafeGen1 = [int]$safeRun.Child.result.measuredGen1Collections
                SafeGen2 = [int]$safeRun.Child.result.measuredGen2Collections
                NamGen0 = [int]$namRun.Child.result.measuredGen0Collections
                NamGen1 = [int]$namRun.Child.result.measuredGen1Collections
                NamGen2 = [int]$namRun.Child.result.measuredGen2Collections
                SafePressure = $safeRun.Child.pressure
                NamPressure = $namRun.Child.pressure
                SafeResult = $safeRun.Child.result
                NamResult = $namRun.Child.result
            }
        }
    }

    $speedups = @($pairs | ForEach-Object { [double]$_.Speedup })
    $safeLatency = @($pairs | ForEach-Object { [double]$_.SafeMilliseconds })
    $namLatency = @($pairs | ForEach-Object { [double]$_.NamMilliseconds })
    $meanSpeedup = Get-Mean $speedups
    $speedupStdDev = Get-StdDev $speedups
    $critical = if ($speedups.Count -gt 0) { Get-TCritical95 ($speedups.Count - 1) } else { 0 }
    $half = if ($speedups.Count -gt 1) { $critical * $speedupStdDev / [math]::Sqrt($speedups.Count) } else { 0 }
    $safeMean = Get-Mean $safeLatency
    $namMean = Get-Mean $namLatency
    $safeThroughput = if ($safeMean -gt 0) { ($Chunks * $Iterations) / ($safeMean / 1000.0) } else { 0 }
    $namThroughput = if ($namMean -gt 0) { ($Chunks * $Iterations) / ($namMean / 1000.0) } else { 0 }
    $safePressure = @($pairs | ForEach-Object { $_.SafePressure } | Where-Object { $_ })
    $namPressure = @($pairs | ForEach-Object { $_.NamPressure } | Where-Object { $_ })
    $safeGc = @($pairs | ForEach-Object { [int]$_.SafeGen0 + [int]$_.SafeGen1 + [int]$_.SafeGen2 })
    $namGc = @($pairs | ForEach-Object { [int]$_.NamGen0 + [int]$_.NamGen1 + [int]$_.NamGen2 })
    $safeResult = @($pairs | ForEach-Object { $_.SafeResult })
    $namResult = @($pairs | ForEach-Object { $_.NamResult })
    $safeManaged = @($safe | ForEach-Object { [double]$_.Child.managedAllocatedBytes })
    $namManaged = @($nam | ForEach-Object { [double]$_.Child.managedAllocatedBytes })
    $meanSafeManaged = Get-Mean $safeManaged
    $meanNamManaged = Get-Mean $namManaged
    $safePeak = @($safePressure | ForEach-Object { [double]$_.cgroupPeakBytes })
    $namPeak = @($namPressure | ForEach-Object { [double]$_.cgroupPeakBytes })
    $safePause = @($safePressure | ForEach-Object { [double]$_.totalPauseMilliseconds })
    $namPause = @($namPressure | ForEach-Object { [double]$_.totalPauseMilliseconds })
    $safeLoh = @($safePressure | ForEach-Object { [double]$_.largeObjectHeapBytes })
    $namLoh = @($namPressure | ForEach-Object { [double]$_.largeObjectHeapBytes })
    $safeFrag = @($safePressure | ForEach-Object { [double]$_.fragmentedHeapBytes })
    $namFrag = @($namPressure | ForEach-Object { [double]$_.fragmentedHeapBytes })
    $safeWorkingSet = @($safe | ForEach-Object { [double]$_.Child.peakWorkingSetBytes })
    $namWorkingSet = @($nam | ForEach-Object { [double]$_.Child.peakWorkingSetBytes })
    $safeAvailable = @($safePressure | ForEach-Object { [double]$_.totalAvailableMemoryBytes })
    $namAvailable = @($namPressure | ForEach-Object { [double]$_.totalAvailableMemoryBytes })
    $safeCommitted = @($safePressure | ForEach-Object { [double]$_.committedHeapBytes })
    $namCommitted = @($namPressure | ForEach-Object { [double]$_.committedHeapBytes })
    $safeHeap = @($safePressure | ForEach-Object { [double]$_.heapBytes })
    $namHeap = @($namPressure | ForEach-Object { [double]$_.heapBytes })
    $namNative = @($namResult | ForEach-Object { [double]$_.peakNativeBackingBytes })
    $namFinal = @($namResult | ForEach-Object { [double]$_.finalNativeBackingBytes })
    $observedLimits = @($safePressure + $namPressure | ForEach-Object { [long]$_.cgroupLimitBytes } | Sort-Object -Unique)
    $parityCount = @($pairs | Where-Object { $_.Parity }).Count
    $safePressureObserved = @($safeGc | Where-Object { $_ -gt 0 }).Count -gt 0 -or (Get-Mean $safePause) -gt 0
    $allNativeZero = $namFinal.Count -gt 0 -and (@($namFinal | Where-Object { $_ -ne 0 }).Count -eq 0)
    $allCgroupLimitsMatch = $MemoryBytes -eq 0 -or ($observedLimits.Count -eq 1 -and $observedLimits[0] -eq $MemoryBytes)
    $completion = $pairs.Count -eq $ExpectedPairs
    $capacityFailures = @($Runs | Where-Object { $_.Status -eq "OomKilled" }).Count
    $otherFailures = @($Runs | Where-Object { $_.Status -notin @("Completed", "OomKilled") }).Count
    $throughputGate = $pairs.Count -gt 0 -and $namThroughput -ge ($safeThroughput * 1.05)
    $confidenceGate = $pairs.Count -gt 1 -and ($meanSpeedup - $half) -gt 1.0
    $managedAllocationReductionGate = $pairs.Count -gt 0 -and $meanSafeManaged -gt 0 -and $meanNamManaged -le ($meanSafeManaged * 0.95)
    $validThroughput = $completion -and $parityCount -eq $pairs.Count -and $safePressureObserved -and $allNativeZero -and $allCgroupLimitsMatch -and $managedAllocationReductionGate
    $gate = $validThroughput -and $throughputGate -and $confidenceGate

    [pscustomobject]@{
        Name = $ProfileName
        MemoryLimitBytes = $MemoryBytes
        IsUnconstrainedControl = $IsControl
        ExpectedPairs = $ExpectedPairs
        CompletedPairs = $pairs.Count
        SafeCompletedRuns = $safe.Count
        NamCompletedRuns = $nam.Count
        CapacityFailureCount = $capacityFailures
        OtherFailureCount = $otherFailures
        ParityPairs = $parityCount
        ObservedCgroupLimitsBytes = $observedLimits
        SafeCollectionPressureObserved = $safePressureObserved
        SafeCollectionDeltas = $safeGc
        NamCollectionDeltas = $namGc
        MeanSafeMilliseconds = $safeMean
        MeanNamMilliseconds = $namMean
        SafeLatencyStandardDeviationMilliseconds = Get-StdDev $safeLatency
        NamLatencyStandardDeviationMilliseconds = Get-StdDev $namLatency
        SafeP50Milliseconds = Get-Percentile $safeLatency 0.50
        SafeP95Milliseconds = Get-Percentile $safeLatency 0.95
        SafeP99Milliseconds = Get-Percentile $safeLatency 0.99
        NamP50Milliseconds = Get-Percentile $namLatency 0.50
        NamP95Milliseconds = Get-Percentile $namLatency 0.95
        NamP99Milliseconds = Get-Percentile $namLatency 0.99
        SafeThroughput = $safeThroughput
        NamThroughput = $namThroughput
        MeanPairedSpeedup = $meanSpeedup
        PairedSpeedupStandardDeviation = $speedupStdDev
        StudentTCritical95 = $critical
        PairedSpeedupLower95 = $meanSpeedup - $half
        PairedSpeedupUpper95 = $meanSpeedup + $half
        MeanSafeManagedAllocatedBytes = $meanSafeManaged
        MeanNamManagedAllocatedBytes = $meanNamManaged
        MeanSafeCgroupPeakBytes = Get-Mean $safePeak
        MeanNamCgroupPeakBytes = Get-Mean $namPeak
        MeanSafePauseMilliseconds = Get-Mean $safePause
        MeanNamPauseMilliseconds = Get-Mean $namPause
        MeanSafeLargeObjectHeapBytes = Get-Mean $safeLoh
        MeanNamLargeObjectHeapBytes = Get-Mean $namLoh
        MeanSafeFragmentedHeapBytes = Get-Mean $safeFrag
        MeanNamFragmentedHeapBytes = Get-Mean $namFrag
        MeanSafePeakWorkingSetBytes = Get-Mean $safeWorkingSet
        MeanNamPeakWorkingSetBytes = Get-Mean $namWorkingSet
        MeanSafeTotalAvailableMemoryBytes = Get-Mean $safeAvailable
        MeanNamTotalAvailableMemoryBytes = Get-Mean $namAvailable
        MeanSafeCommittedHeapBytes = Get-Mean $safeCommitted
        MeanNamCommittedHeapBytes = Get-Mean $namCommitted
        MeanSafeHeapBytes = Get-Mean $safeHeap
        MeanNamHeapBytes = Get-Mean $namHeap
        MeanNamPeakNativeBackingBytes = Get-Mean $namNative
        MeanNamFinalNativeBackingBytes = Get-Mean $namFinal
        CompletionGate = $completion
        ParityGate = $parityCount -eq $pairs.Count -and $pairs.Count -gt 0
        CollectionPressureGate = $safePressureObserved
        NativeFinalZeroGate = $allNativeZero
        CgroupLimitGate = $allCgroupLimitsMatch
        ManagedAllocationReductionGate = $managedAllocationReductionGate
        ThroughputGate = $throughputGate
        ConfidenceGate = $confidenceGate
        ValidThroughputResult = $validThroughput
        GatePassed = $gate
        Verdict = if ($capacityFailures -gt 0 -and -not $completion) { "capacity-limited" } elseif (-not $safePressureObserved) { "invalid-no-safe-gc-pressure" } elseif (-not $validThroughput) { "completed-but-invalid" } elseif ($gate) { "throughput-pass" } else { "throughput-fail" }
        Pairs = $pairs
    }
}

function Invoke-Profile {
    param(
        [string]$Name,
        [long]$Bytes,
        [int]$PairCount,
        [int]$WorkChunks,
        [int]$WorkWorkers,
        [int]$WorkIterations,
        [int]$WorkWarmup,
        [bool]$Control)

    $runs = [System.Collections.Generic.List[object]]::new()
    for ($pair = 0; $pair -lt $PairCount; $pair++) {
        $order = if (($pair % 2) -eq 0) { @("SafeCSharp", "NAM") } else { @("NAM", "SafeCSharp") }
        foreach ($implementation in $order) {
            $runs.Add((Invoke-DockerChild $Name $Bytes $implementation $pair "pressure" $WorkChunks $WorkWorkers $WorkIterations $WorkWarmup))
        }
        Write-Host "$Name pair $($pair + 1)/$PairCount complete"
    }

    [pscustomobject]@{
        Name = $Name
        MemoryLimitBytes = $Bytes
        Runs = @($runs)
        Summary = Summarize-Runs $Name $Bytes @($runs) $PairCount $Control
    }
}

$calibrationRuns = @(
    (Invoke-DockerChild "calibration-1GiB" 1GB "SafeCSharp" 0 "calibration" $CalibrationChunks $CalibrationWorkers $CalibrationIterations $Warmup),
    (Invoke-DockerChild "calibration-1GiB" 1GB "NAM" 0 "calibration" $CalibrationChunks $CalibrationWorkers $CalibrationIterations $Warmup))
$calibrationSummary = Summarize-Runs "calibration-1GiB" 1GB $calibrationRuns 1 $false

$control = $null
if (-not $SkipControl -and $ControlPairs -gt 0) {
    $control = Invoke-Profile "unconstrained-control" 0 $ControlPairs $Chunks $Workers $Iterations $Warmup $true
}

$profileReports = @()
foreach ($profile in $profiles) {
    $profileReports += Invoke-Profile $profile.Name $profile.Bytes $Pairs $Chunks $Workers $Iterations $Warmup $false
}

$report = [ordered]@{
    Schema = "voxel-pressure-v1"
    UtcStarted = [DateTime]::UtcNow
    RepositoryCommit = $fullCommit
    RepositoryShortCommit = $shortCommit
    RepositoryRoot = $RepoRoot
    Image = $Image
    ImageBuild = if ($SkipImageBuild) { "prebuilt" } else { "built-before-measurement" }
    DockerMemoryFlags = @("--memory=profile", "--memory-swap=profile", "--memory-swappiness=0")
    CpuLimit = $CpuLimit
    NoManagedHeapHardLimit = $true
    Workload = [ordered]@{
        Seed = 1706251
        Chunks = $Chunks
        Workers = $Workers
        Iterations = $Iterations
        WarmupChunksPerWorker = $Warmup
        InFlightWorkers = $Workers
        Description = "The existing worker-local generation, prerender, mesh-packing, and upload-staging pipeline runs variable chunk indices concurrently; no synthetic garbage or explicit GC is used in pressure mode."
    }
    Calibration = [ordered]@{
        Workload = [ordered]@{ Chunks = $CalibrationChunks; Workers = $CalibrationWorkers; Iterations = $CalibrationIterations }
        Runs = $calibrationRuns
        Summary = $calibrationSummary
    }
    PredeclaredProfiles = $profiles
    UnconstrainedControl = $control
    Profiles = $profileReports
    UtcFinished = [DateTime]::UtcNow
}

New-Item -ItemType Directory -Force (Split-Path -Parent $OutputPath) | Out-Null
$report | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Wrote $OutputPath"

$failed = @($profileReports | Where-Object { $_.Summary.GatePassed -ne $true })
if ($Enforce -and $failed.Count -gt 0) {
    Write-Error "One or more constrained profiles did not pass the predeclared throughput gate."
    exit 3
}

exit 0
