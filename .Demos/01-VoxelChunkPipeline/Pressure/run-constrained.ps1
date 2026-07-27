[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$Image = "",
    [string]$OutputPath = "",
    [string]$CompilationOutputPath = "",
    [string]$Profiles = "50,100,200,300,400,500,600,700,800,900,1000",
    [long]$CapBytes = 268435456,
    [int]$DeadlineMilliseconds = 6000,
    [int]$RetentionDepth = 20,
    [int]$TimeoutSeconds = 120,
    [int]$CompilationTimeoutSeconds = 120,
    [int]$EndToEndTimeoutSeconds = 180,
    [int]$CompilationPairs = 5,
    [int]$SamplesPerProfile = 5,
    [int]$GcHeapHardLimitPercent = 90,
    [switch]$SkipBuild,
    [switch]$SkipImageBuild,
    [switch]$Enforce)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$runClock = [Diagnostics.Stopwatch]::StartNew()

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

    $remainingMilliseconds = [int][Math]::Floor(
        $EndToEndTimeoutSeconds * 1000 - $runClock.Elapsed.TotalMilliseconds)
    if ($remainingMilliseconds -le 0) {
        throw "The complete constrained run exceeded its $EndToEndTimeoutSeconds second hard timeout."
    }

    $effectiveBound = [Math]::Min($BoundMilliseconds, $remainingMilliseconds)
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
    if (-not $process.WaitForExit($effectiveBound)) {
        try {
            $process.Kill($true)
        } catch {
            try {
                $process.Kill()
            } catch {
            }
        }

        throw "$FileName exceeded its $effectiveBound millisecond hard timeout within the complete $EndToEndTimeoutSeconds second run bound."
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

if (-not $SkipImageBuild) {
    Invoke-Bounded "docker" @(
        "build",
        "--file",
        (Join-Path $PSScriptRoot "Dockerfile"),
        "--tag",
        $Image,
        $PSScriptRoot) 180000 | Out-Null
}

$arguments = @(
    $harness,
    "--pressure-matrix",
    "--repo",
    $RepoRoot,
    "--image",
    $Image,
    "--output",
    $OutputPath,
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
    "--gc-hard-limit-percent",
    $GcHeapHardLimitPercent.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($Enforce) {
    $arguments += "--enforce"
}

$result = Invoke-Bounded "dotnet" $arguments ($TimeoutSeconds * 1000)
$result.StandardOutput
