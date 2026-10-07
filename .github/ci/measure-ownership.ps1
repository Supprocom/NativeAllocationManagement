param(
    [Parameter(Mandatory)][string]$Dotnet,
    [Parameter(Mandatory)][string]$BaselineWorker,
    [Parameter(Mandatory)][string]$CandidateWorker,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int]$Blocks = 3,
    [int]$PairsPerBlock = 12,
    [ValidateRange(1, 1000000)][int]$Cycles = 2048,
    [ValidateRange(0, 1000000)][int]$WarmupCycles = 256,
    [string[]]$Contracts = @('Unique', 'Shared', 'SharedWeak'),
    [string[]]$Modes = @('Default', 'Controlled'),
    [ValidateRange(1.5, 100)][double]$MinimumSpeedup = 1.5,
    [ValidateRange(1, 1.1)][double]$MaximumSlowdown = 1.1,
    [ValidateRange(1.001, 1.1)][double]$CalibrationMargin = 1.1,
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ownership-measurement-contract.ps1')
$schedule = @(New-NamOwnershipSchedule $Blocks $PairsPerBlock $Contracts $Modes)
$root = [IO.Path]::GetFullPath($OutputDirectory)
$baseline = [IO.Path]::GetFullPath($BaselineWorker)
$candidate = [IO.Path]::GetFullPath($CandidateWorker)
$hostExecutable = [IO.Path]::GetFullPath($Dotnet)
foreach ($file in @($baseline, $candidate, $hostExecutable)) { if (-not [IO.File]::Exists($file)) { throw "Missing input: $file" } }
foreach ($worker in @($baseline, $candidate)) {
    if ([IO.Path]::GetFileName($worker) -cne 'Supprocom.NativeAllocationManagement.Performance.dll') { throw 'The exact maintained worker is required.' }
    $directory = [IO.Path]::GetDirectoryName($worker)
    if ($root -ceq $directory -or $root.StartsWith($directory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Evidence must be outside the input bundle.'
    }
}
if (Test-Path -LiteralPath $root) { throw 'Output directory must be new; evidence is never overwritten.' }
[IO.Directory]::CreateDirectory($root) | Out-Null

function Write-NamOwnershipText {
    param([string]$Name, [string]$Text)
    $stream = [IO.File]::Open((Join-Path $root $Name), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
        $stream.Write($bytes)
    } finally { $stream.Dispose() }
}
function Write-NamOwnershipJson {
    param([string]$Name, $Value)
    Write-NamOwnershipText $Name ($Value | ConvertTo-Json -Depth 40)
}
function Get-NamOwnershipInputManifest {
    param([string]$Worker)
    $directory = [IO.Path]::GetDirectoryName($Worker)
    $entries = @(Get-ChildItem -LiteralPath $directory -Recurse -Force | Sort-Object FullName)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -ne 0) { throw 'Input bundles must not contain links.' }
    @($entries | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
        [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($directory, $_.FullName); Length = $_.Length;
            SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Invoke-NamOwnershipChild {
    param([string]$Id, [string[]]$Arguments, [string]$Mode)
    $start = [Diagnostics.ProcessStartInfo]::new($hostExecutable)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $root
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($setting in @('DOTNET_JitDisasm', 'DOTNET_JitDump', 'COMPlus_JitDisasm', 'COMPlus_JitDump')) {
        if ($start.Environment.ContainsKey($setting) -and -not [string]::IsNullOrEmpty($start.Environment[$setting])) { throw 'Remove JIT instrumentation before timing; use a separate diagnostic capture.' }
    }
    foreach ($setting in @('CORECLR_ENABLE_PROFILING', 'DOTNET_EnableEventPipe', 'COMPlus_EnableEventPipe', 'DOTNET_PerfMapEnabled', 'COMPlus_PerfMapEnabled')) {
        if ($start.Environment.ContainsKey($setting) -and $start.Environment[$setting] -notin @('', '0')) { throw 'Remove inherited profiling before timing; use a separate diagnostic capture.' }
    }
    # Default = unforced tiering/PGO, not inherited controlled-run settings.
    foreach ($prefix in @('DOTNET_', 'COMPlus_')) {
        foreach ($setting in @('TieredCompilation', 'TieredPGO', 'TC_QuickJit', 'TC_QuickJitForLoops')) { $start.Environment.Remove($prefix + $setting) | Out-Null }
    }
    if ($Mode -ceq 'Controlled') {
        $start.Environment['DOTNET_TieredCompilation'] = '0'
        $start.Environment['DOTNET_TieredPGO'] = '0'
    }
    $began = [DateTimeOffset]::UtcNow
    Write-NamOwnershipJson "$Id-request.json" ([pscustomobject]@{ Executable = $hostExecutable; Arguments = $Arguments;
        Mode = $Mode; WorkingDirectory = $root; TimeoutSeconds = $TimeoutSeconds; BeganAtUtc = $began })
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false; $timedOut = $false; $exitCode = $null; $failure = $null; $stdout = ''; $stderr = ''
    $outputTask = $null; $errorTask = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $started = $process.Start()
        if (-not $started) { throw 'Child did not start.' }
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            try { $process.Kill($true) } catch [InvalidOperationException] { } # The exact child may have exited at the deadline.
        }
    } catch {
        $failure = $_.Exception.ToString()
    } finally {
        if ($started) {
            if (-not $process.HasExited) { try { $process.Kill($true) } catch [InvalidOperationException] { } }
            $process.WaitForExit()
            $exitCode = $process.ExitCode
            if ($null -ne $outputTask) { $stdout = $outputTask.GetAwaiter().GetResult() }
            if ($null -ne $errorTask) { $stderr = $errorTask.GetAwaiter().GetResult() }
        }
        $timer.Stop()
        $process.Dispose()
        Write-NamOwnershipText "$Id-stdout.json" $stdout
        Write-NamOwnershipText "$Id-stderr.log" $stderr
        Write-NamOwnershipJson "$Id-exit.json" ([pscustomobject]@{ Started = $started; ExitCode = $exitCode; TimedOut = $timedOut;
            Failure = $failure; BeganAtUtc = $began; CompletedAtUtc = [DateTimeOffset]::UtcNow; ProcessWallMilliseconds = $timer.Elapsed.TotalMilliseconds })
    }
    if ($failure -or $timedOut -or $exitCode -ne 0) { throw "Child $Id failed; actual streams and outcome are retained." }
    $stdout
}

$completed = $false
$failureText = $null
try {
    $baselineManifest = @(Get-NamOwnershipInputManifest $baseline)
    $candidateManifest = @(Get-NamOwnershipInputManifest $candidate)
    Write-NamOwnershipJson 'inputs-before.json' ([pscustomobject]@{ Baseline = $baselineManifest; Candidate = $candidateManifest;
        Dotnet = $hostExecutable; DotnetSHA256 = (Get-FileHash -LiteralPath $hostExecutable -Algorithm SHA256).Hash })
    foreach ($manifest in @($baselineManifest, $candidateManifest)) {
        foreach ($required in @('Supprocom.NativeAllocationManagement.Performance.dll', 'Supprocom.NativeAllocationManagement.dll', 'Supprocom.NativeAllocationManagement.Performance.runtimeconfig.json')) {
            if (@($manifest | Where-Object Path -CEQ $required).Count -ne 1) { throw "Missing required bundle member: $required" }
        }
    }
    $allowedDifferences = @('Supprocom.NativeAllocationManagement.dll', 'Supprocom.NativeAllocationManagement.pdb', 'Supprocom.NativeAllocationManagement.xml')
    $baselineHarness = @($baselineManifest | Where-Object { $_.Path -cnotin $allowedDifferences }) | ConvertTo-Json -Compress
    $candidateHarness = @($candidateManifest | Where-Object { $_.Path -cnotin $allowedDifferences }) | ConvertTo-Json -Compress
    if ($baselineHarness -cne $candidateHarness) { throw 'Benchmark, managed baseline or support bytes differ; only NAM runtime/XML/PDB may differ.' }
    Write-NamOwnershipJson 'plan.json' ([pscustomobject]@{ SchemaVersion = 1; BaselineWorker = $baseline; CandidateWorker = $candidate;
        CalibrationWorker = $baseline; Blocks = $Blocks; PairsPerBlock = $PairsPerBlock; Cycles = $Cycles; WarmupCycles = $WarmupCycles;
        MinimumSpeedup = $MinimumSpeedup; MaximumSlowdown = $MaximumSlowdown; CalibrationMargin = $CalibrationMargin;
        Contracts = $Contracts; Modes = $Modes; Schedule = $schedule;
        StoppingRule = 'Execute every declared child once, sequentially; abort on protocol/correctness failure and retain partial evidence. No retries, outlier removal or optional stopping on timing.';
        Inference = 'Within-block matched process log-ratios only. No pooled IID interval across blocks or inner cycles. Fixed-count warm-up is not proof of Tier1/OSR stabilization.';
        MemoryDomain = 'Cumulative measured current-thread GC bytes plus disjoint successfully requested native extents; not physical backend overhead, RSS or peak live storage.' })
    Invoke-NamOwnershipChild 'dotnet-info' @('--info') 'Default' | Out-Null
    $records = [Collections.Generic.List[object]]::new()
    foreach ($item in $schedule) {
        if ($item.Pair -eq 0 -and $item.Position -eq 0) { Write-Output "Block $($item.Block): $($item.Mode) $($item.Contract)" }
        $worker = if ($item.Variant -ceq 'Candidate') { $candidate } else { $baseline }
        $implementation = if ($item.Variant -ceq 'Managed') { 'Managed' } else { 'Native' }
        $arguments = @($worker, '--ownership-full-cost-worker', '--implementation', $implementation,
            '--contract', $item.Contract, '--cycles', $Cycles.ToString([Globalization.CultureInfo]::InvariantCulture),
            '--warmup-cycles', $WarmupCycles.ToString([Globalization.CultureInfo]::InvariantCulture))
        $stdout = Invoke-NamOwnershipChild $item.Id $arguments $item.Mode
        $metrics = Read-NamOwnershipMetrics $stdout $implementation $item.Contract $Cycles $WarmupCycles
        if (($item.Mode -ceq 'Controlled' -and ($metrics.TieredCompilation -cne '0' -or $metrics.TieredPgo -cne '0')) -or
            ($item.Mode -ceq 'Default' -and ($null -ne $metrics.TieredCompilation -or $null -ne $metrics.TieredPgo))) { throw 'Child compilation mode differs.' }
        $records.Add([pscustomobject]@{ Block = $item.Block; Pair = $item.Pair; Position = $item.Position;
            Mode = $item.Mode; Contract = $item.Contract; Variant = $item.Variant; Id = $item.Id; Metrics = $metrics })
    }
    # Identity hashing and statistics happen only after every timed child stops.
    $baselineAfter = @(Get-NamOwnershipInputManifest $baseline)
    $candidateAfter = @(Get-NamOwnershipInputManifest $candidate)
    Write-NamOwnershipJson 'inputs-after.json' ([pscustomobject]@{ Baseline = $baselineAfter; Candidate = $candidateAfter })
    if (($baselineAfter | ConvertTo-Json -Compress) -cne ($baselineManifest | ConvertTo-Json -Compress) -or
        ($candidateAfter | ConvertTo-Json -Compress) -cne ($candidateManifest | ConvertTo-Json -Compress)) { throw 'Input bytes changed during the declared run.' }
    Write-NamOwnershipJson 'observations.json' $records.ToArray()
    $summaries = @(Get-NamOwnershipBlockSummary $records.ToArray() $MinimumSpeedup $MaximumSlowdown $CalibrationMargin)
    Write-NamOwnershipJson 'summary.json' ([pscustomobject]@{ Completed = $true; ChildCount = $records.Count; Blocks = $summaries;
        EveryTimedLifetimeBlockMeetsThreshold = @($summaries | Where-Object { $_.Window -ceq 'TimedLifetime' -and -not $_.MeetsCombinedThreshold }).Count -eq 0;
        Scope = 'Completed measurement protocol, not release acceptance. Every raw run is retained. FirstUse, PostWarmup and TimedLifetime stay separate; post-warm-up is not asserted to be stabilized. Unbounded A/A noise means unresolved timing, not proof of regression or equivalence. Host pressure is aggregate; process cgroup paths are in each child report.' })
    $completed = $true
    Write-Output "Retained $($records.Count) children and all block summaries in $root"
} catch {
    $failureText = $_.Exception.ToString()
    throw
} finally {
    Write-NamOwnershipJson 'completion.json' ([pscustomobject]@{ Completed = $completed; CompletedAtUtc = [DateTimeOffset]::UtcNow; Failure = $failureText })
}
