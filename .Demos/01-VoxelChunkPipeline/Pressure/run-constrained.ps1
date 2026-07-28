[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$Image = "",
    [string]$OutputPath = "",
    [string]$CompilationOutputPath = "",
    [string]$Profiles = "50,100,200,500,1000,10000",
    [long]$CapBytes = 268435456,
    [int]$DeadlineMilliseconds = 6000,
    [int]$RetentionDepth = 20,
    [int]$InactivityTimeoutSeconds = 120,
    [long]$AbsoluteFailSafeTimeoutSeconds = 0,
    [int]$CompilationTimeoutSeconds = 120,
    [int]$CompilationPairs = 5,
    [int]$SamplesPerProfile = 6,
    [int]$GcHeapHardLimitPercent = 90,
    [switch]$ValidateTimeoutsOnly,
    [switch]$SetupOnly,
    [switch]$SkipBuild,
    [switch]$SkipImageBuild,
    [switch]$Enforce)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$longestHarnessOperationSeconds = 60
$warmupPassCount = 4
$preparationPassCount = 6
$stressProfilePercent = 10000
$stressProfileSamples = 6

function Get-ProfileValues {
    param([string]$Value)

    $result = @()
    foreach ($item in $Value.Split(",")) {
        $trimmedItem = $item.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmedItem)) {
            continue
        }

        $result += [int]::Parse(
            $trimmedItem,
            [Globalization.CultureInfo]::InvariantCulture)
    }

    if ($result.Count -eq 0) {
        throw "At least one pressure profile is required."
    }

    return $result
}

function Get-MinimumHarnessFailSafeSeconds {
    param(
        [int[]]$ProfileValues,
        [int]$ConfiguredSamples)

    if ($ConfiguredSamples -le 0 -or ($ConfiguredSamples -band 1) -ne 0) {
        throw "The sample count must be positive and even."
    }

    [long]$pairCount = 0
    foreach ($profile in $ProfileValues) {
        $pairCount += if ($profile -eq $stressProfilePercent) {
            $stressProfileSamples
        } else {
            $ConfiguredSamples
        }
    }

    [long]$operationsPerPair =
        2 * ($warmupPassCount + $preparationPassCount + 1)
    [long]$verificationOperations = 2 * ($warmupPassCount + 2)
    [long]$operationCount =
        $pairCount * $operationsPerPair + $verificationOperations
    return [long](
        $operationCount * $longestHarnessOperationSeconds + 300)
}

$profileValues = @(Get-ProfileValues $Profiles)
$minimumAbsoluteFailSafeSeconds =
    Get-MinimumHarnessFailSafeSeconds $profileValues $SamplesPerProfile
if ($InactivityTimeoutSeconds -le $longestHarnessOperationSeconds) {
    throw "The inactivity timeout must exceed the 60-second internal operation bound."
}

if ($AbsoluteFailSafeTimeoutSeconds -eq 0) {
    $AbsoluteFailSafeTimeoutSeconds = $minimumAbsoluteFailSafeSeconds
} elseif (
    $AbsoluteFailSafeTimeoutSeconds -lt $minimumAbsoluteFailSafeSeconds
) {
    throw "The absolute fail-safe is below the derived minimum of $minimumAbsoluteFailSafeSeconds seconds."
}

if ($ValidateTimeoutsOnly) {
    [pscustomobject]@{
        InactivityTimeoutSeconds = $InactivityTimeoutSeconds
        LongestHarnessOperationSeconds = $longestHarnessOperationSeconds
        AbsoluteFailSafeTimeoutSeconds = $AbsoluteFailSafeTimeoutSeconds
        MinimumAbsoluteFailSafeTimeoutSeconds =
            $minimumAbsoluteFailSafeSeconds
        Valid = $true
    } | ConvertTo-Json
    exit 0
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
} else {
    $RepoRoot = (Resolve-Path $RepoRoot).Path
}

$commit = (& git -C $RepoRoot rev-parse HEAD).Trim()
if ([string]::IsNullOrWhiteSpace($commit)) {
    throw "The pressure matrix requires an exact Git commit."
}

if ([string]::IsNullOrWhiteSpace($Image)) {
    $Image = "nam-voxel-pressure:$($commit.Substring(0, 12))"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $RepoRoot "artifacts\voxel-pressure-$($commit.Substring(0, 12)).json"
}

if ([string]::IsNullOrWhiteSpace($CompilationOutputPath)) {
    $CompilationOutputPath = Join-Path $RepoRoot "artifacts\voxel-compilation-$($commit.Substring(0, 12)).json"
}

function Invoke-Bounded {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [int]$BoundMilliseconds)

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.Arguments = [string]::Join(
        " ",
        ($Arguments | ForEach-Object {
            if ($_ -match '[\s"]') {
                '"' + $_.Replace('"', '\"') + '"'
            } else {
                $_
            }
        }))

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) {
        throw "Could not start $FileName."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($BoundMilliseconds)) {
        try {
            $process.Kill($true)
        } catch {
            try {
                $process.Kill()
            } catch {
            }
        }

        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        throw "$FileName exceeded its $BoundMilliseconds millisecond hard timeout.`n$stdout`n$stderr"
    }

    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "$FileName exited $($process.ExitCode).`n$stdout`n$stderr"
    }

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        StandardOutput = $stdout
        StandardError = $stderr
    }
}

function Write-AtomicJson {
    param(
        [string]$Path,
        [object]$Value)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $directory = [IO.Path]::GetDirectoryName($fullPath)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporaryPath = [IO.Path]::Combine(
        $directory,
        ".$([IO.Path]::GetFileName($fullPath)).$([Guid]::NewGuid().ToString('N')).tmp")
    $utf8 = [Text.UTF8Encoding]::new($false)
    $stream = $null
    $writer = $null
    try {
        $stream = [IO.FileStream]::new(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            65536,
            [IO.FileOptions]::WriteThrough)
        $writer = [IO.StreamWriter]::new($stream, $utf8)
        $writer.Write(($Value | ConvertTo-Json -Depth 100))
        $writer.Flush()
        $stream.Flush($true)
        $writer.Dispose()
        $writer = $null
        $stream.Dispose()
        $stream = $null
        if ([IO.File]::Exists($fullPath)) {
            [IO.File]::Replace($temporaryPath, $fullPath, $null)
        } else {
            [IO.File]::Move($temporaryPath, $fullPath)
        }
    } finally {
        if ($null -ne $writer) {
            $writer.Dispose()
        }

        if ($null -ne $stream) {
            $stream.Dispose()
        }

        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

function Invoke-HarnessWatchdog {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$CheckpointPath,
        [string]$ActivityPath,
        [int]$InactivitySeconds,
        [long]$AbsoluteSeconds)

    $checkpointFullPath = [IO.Path]::GetFullPath($CheckpointPath)
    $activityFullPath = [IO.Path]::GetFullPath($ActivityPath)
    $timeoutEvidencePath = "$checkpointFullPath.timeout.json"
    $activityDirectory = [IO.Path]::GetDirectoryName($activityFullPath)
    [IO.Directory]::CreateDirectory($activityDirectory) | Out-Null
    if ([IO.File]::Exists($activityFullPath)) {
        [IO.File]::Delete($activityFullPath)
    }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.Arguments = [string]::Join(
        " ",
        ($Arguments | ForEach-Object {
            if ($_ -match '[\s"]') {
                '"' + $_.Replace('"', '\"') + '"'
            } else {
                $_
            }
        }))

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $startedUtc = [DateTime]::UtcNow
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $latestObservedWriteUtc = $startedUtc
    [double]$lastActivityElapsedMilliseconds = 0
    if (-not $process.Start()) {
        throw "Could not start $FileName."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timeoutReason = $null
    while (-not $process.WaitForExit(1000)) {
        foreach ($candidatePath in @(
            $checkpointFullPath,
            $activityFullPath
        )) {
            if ([IO.File]::Exists($candidatePath)) {
                $writeUtc = [IO.File]::GetLastWriteTimeUtc($candidatePath)
                if (
                    $writeUtc -ge $startedUtc -and
                    $writeUtc -gt $latestObservedWriteUtc
                ) {
                    $latestObservedWriteUtc = $writeUtc
                    $lastActivityElapsedMilliseconds =
                        $clock.Elapsed.TotalMilliseconds
                }
            }
        }

        if (
            $clock.Elapsed.TotalMilliseconds -
                $lastActivityElapsedMilliseconds -gt
                $InactivitySeconds * 1000
        ) {
            $timeoutReason = "InactivityTimeout"
            break
        }

        if ($clock.Elapsed.TotalSeconds -gt $AbsoluteSeconds) {
            $timeoutReason = "AbsoluteFailSafeTimeout"
            break
        }
    }

    if ($null -ne $timeoutReason) {
        try {
            $process.Kill($true)
        } catch {
            try {
                $process.Kill()
            } catch {
            }
        }

        $process.WaitForExit()
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($null -ne $timeoutReason) {
        $checkpointPresent = [IO.File]::Exists($checkpointFullPath) -and
            [IO.File]::GetLastWriteTimeUtc($checkpointFullPath) -ge
                $startedUtc
        $checkpointHash = if ($checkpointPresent) {
            (Get-FileHash -LiteralPath $checkpointFullPath -Algorithm SHA256).Hash
        } else {
            $null
        }
        $timeoutEvidence = [ordered]@{
            OccurredUtc = [DateTime]::UtcNow.ToString("O")
            Reason = $timeoutReason
            InactivityTimeoutSeconds = $InactivitySeconds
            AbsoluteFailSafeTimeoutSeconds = $AbsoluteSeconds
            ElapsedMilliseconds = $clock.Elapsed.TotalMilliseconds
            HarnessExitCode = $process.ExitCode
            StandardOutput = $stdout
            StandardError = $stderr
            LatestCheckpointPath = if ($checkpointPresent) {
                $checkpointFullPath
            } else {
                $null
            }
            LatestCheckpointSha256 = $checkpointHash
        }
        Write-AtomicJson $timeoutEvidencePath $timeoutEvidence
        if ([IO.File]::Exists($activityFullPath)) {
            [IO.File]::Delete($activityFullPath)
        }

        throw "$FileName stopped at $timeoutReason. Timeout evidence is $timeoutEvidencePath. The checkpoint was not changed."
    }

    if ([IO.File]::Exists($activityFullPath)) {
        [IO.File]::Delete($activityFullPath)
    }

    if ($process.ExitCode -ne 0) {
        throw "$FileName exited $($process.ExitCode).`n$stdout`n$stderr"
    }

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        StandardOutput = $stdout
        StandardError = $stderr
    }
}

$dockerInfo = Invoke-Bounded "docker" @("info", "--format", "{{.NCPU}}") 30000
$dockerCpuCount = [int]$dockerInfo.StandardOutput.Trim()
if ($dockerCpuCount -lt 2) {
    throw "The matrix requires at least two Docker-visible CPUs."
}

$commonCpuSet = "0-$($dockerCpuCount - 1)"
$safeCpuSet = $commonCpuSet
$namCpuSet = $commonCpuSet

if (-not $SkipBuild) {
    Invoke-Bounded "dotnet" @(
        "build",
        (Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\SharedContract\SharedContract.csproj"),
        "-c",
        "Release",
        "--no-restore") 60000 | Out-Null
    Invoke-Bounded "dotnet" @(
        "build",
        (Join-Path $RepoRoot "Supprocom.NativeAllocationManagement\Supprocom.NativeAllocationManagement.csproj"),
        "-c",
        "Release",
        "--no-restore") 60000 | Out-Null
    Invoke-Bounded "dotnet" @(
        "build",
        (Join-Path $RepoRoot "Supprocom.NativeAllocationManagement.Analyzers\Supprocom.NativeAllocationManagement.Analyzers.csproj"),
        "-c",
        "Release",
        "--no-restore") 60000 | Out-Null
    Invoke-Bounded "dotnet" @(
        "build",
        (Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\Harness\Harness.csproj"),
        "-c",
        "Release",
        "--no-restore") 60000 | Out-Null
}

$harness = Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\Harness\bin\Release\net10.0\VoxelChunkPipeline.Harness.dll"
$compilationArguments = @(
    $harness,
    "--compile-gate",
    "--repo",
    $RepoRoot,
    "--output",
    $CompilationOutputPath,
    "--warmup-pairs",
    "1",
    "--pairs",
    $CompilationPairs.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--compile-timeout-ms",
    "30000")
$compilationResult = Invoke-Bounded "dotnet" $compilationArguments ($CompilationTimeoutSeconds * 1000)
$compilationResult.StandardOutput

if (-not $SkipBuild) {
    Invoke-Bounded "dotnet" @(
        "publish",
        (Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\SafeCSharp\SafeCSharp.csproj"),
        "-c",
        "Release",
        "-r",
        "linux-x64",
        "--self-contained",
        "false") 60000 | Out-Null
    Invoke-Bounded "dotnet" @(
        "publish",
        (Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\NAM\NAM.csproj"),
        "-c",
        "Release",
        "-r",
        "linux-x64",
        "--self-contained",
        "false") 60000 | Out-Null
}

$binaryPaths = [ordered]@{
    Harness = Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\Harness\bin\Release\net10.0\VoxelChunkPipeline.Harness.dll"
    SafeCSharp = Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\SafeCSharp\bin\Release\net10.0\linux-x64\publish\VoxelChunkPipeline.SafeCSharp.dll"
    SafeSharedContract = Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\SafeCSharp\bin\Release\net10.0\linux-x64\publish\VoxelChunkPipeline.SharedContract.dll"
    NAM = Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\NAM\bin\Release\net10.0\linux-x64\publish\VoxelChunkPipeline.NAM.dll"
    NamSharedContract = Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\NAM\bin\Release\net10.0\linux-x64\publish\VoxelChunkPipeline.SharedContract.dll"
    NativeAllocationManagement = Join-Path $RepoRoot ".Demos\01-VoxelChunkPipeline\NAM\bin\Release\net10.0\linux-x64\publish\Supprocom.NativeAllocationManagement.dll"
}
foreach ($component in $binaryPaths.Keys) {
    $binaryPath = $binaryPaths[$component]
    if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
        throw "The required $component binary does not exist."
    }

    $informationalVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
        $binaryPath).ProductVersion
    $informationalCommit = if (
        $informationalVersion -match '\+([0-9a-f]{40})$'
    ) {
        $Matches[1]
    } else {
        ""
    }
    if ($informationalCommit -ne $commit) {
        throw "The $component binary does not match HEAD $commit."
    }
}

if (-not $SkipImageBuild) {
    Invoke-Bounded "docker" @(
        "build",
        "--file",
        (Join-Path $PSScriptRoot "Dockerfile"),
        "--tag",
        $Image,
        $PSScriptRoot) 180000 | Out-Null
}

if ($SetupOnly) {
    Write-Output "Setup completed for image $Image. Run the matrix with -SkipBuild -SkipImageBuild."
    exit 0
}

$activityPath = "$OutputPath.activity"
$arguments = @(
    $harness,
    "--pressure-matrix",
    "--repo",
    $RepoRoot,
    "--image",
    $Image,
    "--output",
    $OutputPath,
    "--activity",
    $activityPath,
    "--profiles",
    $Profiles,
    "--safe-cpuset",
    $safeCpuSet,
    "--nam-cpuset",
    $namCpuSet,
    "--cap-bytes",
    $CapBytes.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--deadline-ms",
    $DeadlineMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--retention",
    $RetentionDepth.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--progress-every",
    "4",
    "--seed",
    "17",
    "--pids-limit",
    "128",
    "--samples-per-profile",
    $SamplesPerProfile.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--inactivity-timeout-seconds",
    $InactivityTimeoutSeconds.ToString(
        [Globalization.CultureInfo]::InvariantCulture),
    "--absolute-fail-safe-timeout-seconds",
    $AbsoluteFailSafeTimeoutSeconds.ToString(
        [Globalization.CultureInfo]::InvariantCulture),
    "--gc-hard-limit-percent",
    $GcHeapHardLimitPercent.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($Enforce) {
    $arguments += "--enforce"
}

$result = Invoke-HarnessWatchdog `
    "dotnet" `
    $arguments `
    $OutputPath `
    $activityPath `
    $InactivityTimeoutSeconds `
    $AbsoluteFailSafeTimeoutSeconds
$result.StandardOutput
