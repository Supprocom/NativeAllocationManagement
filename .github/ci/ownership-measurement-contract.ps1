# Cold evidence/scheduling helpers. Dot-source from the runner or deterministic tests.
Set-StrictMode -Version Latest

function New-NamOwnershipSchedule {
    param([int]$Blocks, [int]$PairsPerBlock, [string[]]$Contracts, [string[]]$Modes)
    if ($Blocks -lt 2 -or $Blocks -gt 10 -or $PairsPerBlock -lt 4 -or $PairsPerBlock -gt 64 -or $PairsPerBlock % 4 -ne 0) {
        throw 'Require 2..10 blocks and 4..64 pairs per block divisible by four.'
    }
    if ($Contracts.Count -eq 0 -or $Modes.Count -eq 0 -or
        @($Contracts | Select-Object -Unique).Count -ne $Contracts.Count -or
        @($Modes | Select-Object -Unique).Count -ne $Modes.Count -or
        @($Contracts | Where-Object { $_ -cnotin @('Unique', 'Shared', 'SharedWeak') }).Count -ne 0 -or
        @($Modes | Where-Object { $_ -cnotin @('Default', 'Controlled') }).Count -ne 0) { throw 'Invalid or duplicate scenario.' }
    $variants = @('Managed', 'Baseline', 'Calibration', 'Candidate')
    $scenarios = @($Modes | ForEach-Object { $mode = $_; $Contracts | ForEach-Object { [pscustomobject]@{ Mode = $mode; Contract = $_ } } })
    for ($block = 0; $block -lt $Blocks; $block++) {
        for ($scenarioIndex = 0; $scenarioIndex -lt $scenarios.Count; $scenarioIndex++) {
            $scenario = $scenarios[($scenarioIndex + $block) % $scenarios.Count]
            for ($pair = 0; $pair -lt $PairsPerBlock; $pair++) {
                for ($position = 0; $position -lt 4; $position++) {
                    $orderedPosition = if (([int][Math]::Floor($pair / 4) + $block) % 2 -eq 0) { $position } else { 3 - $position }
                    $variant = $variants[($orderedPosition + $pair + $block) % 4]
                    [pscustomobject]@{ Block = $block; Pair = $pair; Position = $position; Mode = $scenario.Mode;
                        Contract = $scenario.Contract; Variant = $variant;
                        Id = ('b{0}-{1}-{2}-p{3:D2}-{4}' -f $block, $scenario.Mode, $scenario.Contract, $pair, $variant) }
                }
            }
        }
    }
}

function Get-NamOwnershipRatio {
    param([double[]]$Left, [double[]]$Right)
    if ($Left.Count -ne $Right.Count -or $Left.Count -lt 2) { throw 'Matched process samples are required.' }
    $ratios = [Collections.Generic.List[double]]::new()
    for ($index = 0; $index -lt $Left.Count; $index++) {
        if (-not [double]::IsFinite($Left[$index]) -or -not [double]::IsFinite($Right[$index]) -or
            $Left[$index] -le 0 -or $Right[$index] -le 0) { throw 'Ratio observations must be finite and positive.' }
        $ratios.Add($Left[$index] / $Right[$index])
    }
    $logs = @($ratios | ForEach-Object { [Math]::Log($_) })
    $mean = ($logs | Measure-Object -Average).Average
    $squares = ($logs | ForEach-Object { ($_ - $mean) * ($_ - $mean) } | Measure-Object -Sum).Sum
    # Two-sided t(.975), conservative df=30 for larger samples (maximum 64).
    $critical = @(0, 12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262,
        2.228, 2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042)
    $margin = $critical[[Math]::Min(30, $logs.Count - 1)] * [Math]::Sqrt($squares / ($logs.Count - 1) / $logs.Count)
    [pscustomobject]@{ Count = $logs.Count; GeometricMean = [Math]::Exp($mean); Lower95 = [Math]::Exp($mean - $margin);
        Upper95 = [Math]::Exp($mean + $margin); Ratios = $ratios.ToArray() }
}

function Test-NamOwnershipCalibration {
    param($Ratio, [double]$MaximumRatio)
    if (-not [double]::IsFinite($MaximumRatio) -or $MaximumRatio -le 1) { throw 'Invalid calibration margin.' }
    $Ratio.Lower95 -ge 1 / $MaximumRatio -and $Ratio.Upper95 -le $MaximumRatio
}

function Test-NamOwnershipNonInferiority {
    param($Ratio, [double]$MaximumSlowdown)
    if (-not [double]::IsFinite($MaximumSlowdown) -or $MaximumSlowdown -lt 1) { throw 'Invalid regression margin.' }
    $Ratio.Lower95 -ge 1 / $MaximumSlowdown
}

function Read-NamOwnershipMetrics {
    param([string]$Json, [string]$Implementation, [string]$Contract, [int]$Cycles, [int]$WarmupCycles)
    $report = $Json | ConvertFrom-Json -Depth 30
    $contractIndex = [Array]::IndexOf(@('Unique', 'Shared', 'SharedWeak'), $Contract)
    $implementationIndex = [Array]::IndexOf(@('Managed', 'Native'), $Implementation)
    if ($contractIndex -lt 0 -or $implementationIndex -lt 0 -or $Cycles -le 0 -or $WarmupCycles -lt 0) { throw 'Invalid expected contract.' }
    $oneChecksum = @(917504L, 917616L, 1146992L)[$contractIndex]
    if ($report.SchemaVersion -ne 2 -or $report.Implementation -ne $implementationIndex -or $report.Contract -ne $contractIndex -or
        $report.Cycles -ne $Cycles -or $report.WarmupCycles -ne $WarmupCycles -or $report.PayloadBytes -ne 4096 -or
        $report.SliceBytes -ne 16 -or $report.MovesPerCycle -ne 16 -or $report.ReadsPerCycle -ne 32 -or
        $report.UpgradesPerCycle -ne $(if ($Contract -ceq 'SharedWeak') { 8 } else { 0 }) -or
        $report.ExactOutput -cne $true -or $report.ExactCleanup -cne $true -or $report.ExactNativeAccounting -cne $true -or
        $report.FirstUseChecksum -ne $oneChecksum -or $report.ExpectedFirstUseChecksum -ne $oneChecksum -or
        $report.Checksum -ne $oneChecksum * $Cycles -or $report.ExpectedChecksum -ne $oneChecksum * $Cycles -or
        $report.WarmupChecksum -ne $oneChecksum * $WarmupCycles -or $report.ExpectedWarmupChecksum -ne $oneChecksum * $WarmupCycles -or
        $report.TimestampFrequency -le 0 -or $report.CycleTicks.Count -ne $Cycles -or $report.WarmupCycleTicks.Count -ne $WarmupCycles -or
        @($report.CycleTicks | Where-Object { $_ -le 0 }).Count -ne 0 -or
        @($report.WarmupCycleTicks | Where-Object { $_ -le 0 }).Count -ne 0) { throw 'Ownership identity/output contract differs.' }
    $names = @('preparation', 'first-use full lifecycle', 'counted warm-up full lifecycle', 'post-warm-up full lifecycle', 'terminal diagnostics')
    $operations = @(1, 1, $WarmupCycles, $Cycles, 1)
    $checksums = @(0L, $oneChecksum, ($oneChecksum * $WarmupCycles), ($oneChecksum * $Cycles), 0L)
    if ($report.Phases.Count -ne 5) { throw 'All five disjoint phases are required.' }
    for ($index = 0; $index -lt 5; $index++) {
        $phase = $report.Phases[$index]
        if ($phase.Name -cne $names[$index] -or $phase.Operations -ne $operations[$index] -or $phase.Checksum -ne $checksums[$index]) { throw 'Phase contract differs.' }
        foreach ($field in @('WallMilliseconds', 'CpuMilliseconds', 'ManagedAllocatedBytes', 'Gen0Collections', 'Gen1Collections', 'Gen2Collections')) {
            if ($null -eq $phase.$field -or -not [double]::IsFinite($phase.$field) -or $phase.$field -lt 0) { throw 'Invalid phase observation.' }
        }
    }
    $lifecycles = 1L + $WarmupCycles + $Cycles
    $extent = if ($Contract -ceq 'Unique') { 4096L } else { 4112L }
    if ($report.PeakLiveBackingBytes -ne $extent -or $report.CopiedBytesPerCycle -ne $extent - 4096) { throw 'Backing/copy contract differs.' }
    if ($Implementation -ceq 'Native') {
        $acquisitions = $lifecycles * $(if ($Contract -ceq 'Unique') { 1 } else { 2 })
        if ($report.NativeBackingAcquisitions -ne $acquisitions -or $report.Budget.AllocationCount -ne $acquisitions -or
            $report.Budget.FreeCount -ne $acquisitions -or $report.Budget.PeakAdmittedBytes -ne $extent -or
            $report.Budget.CommittedBytes -ne 0 -or $report.Budget.ReservedBytes -ne 0 -or
            $report.Budget.RejectedAllocationCount -ne 0 -or $report.Budget.FailedAllocationCount -ne 0 -or
            $report.TerminalUnique.OwnedBackingBytes -ne 0 -or $report.TerminalUnique.ActiveBorrowCount -ne 0 -or
            $report.TerminalUnique.HasReturnObligation -cne $false) { throw 'Native accounting/cleanup differs.' }
        if ($Contract -cne 'Unique' -and ($report.TerminalShared.StrongBindingCount -ne 0 -or
            $report.TerminalShared.WeakBindingCount -ne 0 -or $report.TerminalShared.ActiveReadCount -ne 0 -or
            $report.TerminalShared.ManagedBankBytes -ne 0 -or $report.TerminalShared.OwnedBackingBytes -ne 0 -or
            $report.TerminalShared.PayloadReleased -cne $true -or $report.TerminalShared.Expired -cne $true -or
            $report.TerminalDetached.OwnedBackingBytes -ne 0 -or $report.TerminalDetached.HasReturnObligation -cne $false)) { throw 'Shared cleanup differs.' }
    } else {
        foreach ($field in @('NativeBackingAcquisitions', 'Budget', 'TerminalUnique', 'TerminalShared', 'TerminalDetached')) {
            if ($null -ne $report.$field) { throw 'Unsupported managed observations must be unavailable.' }
        }
    }
    foreach ($hostObservation in @($report.HostBefore, $report.HostAfter)) {
        if ($hostObservation.ProcessId -le 0 -or @($hostObservation.LinuxFiles.PSObject.Properties).Count -ne 10 -or
            [DateTimeOffset]$hostObservation.CompletedAtUtc -lt [DateTimeOffset]$hostObservation.BeganAtUtc) { throw 'Invalid host observation.' }
    }
    if ($report.HostBefore.ProcessId -ne $report.HostAfter.ProcessId) { throw 'Observation process identity changed.' }
    $windows = [ordered]@{}
    foreach ($window in @('FirstUse', 'PostWarmup', 'TimedLifetime')) {
        $phases = switch ($window) { 'FirstUse' { @($report.Phases[1]) }; 'PostWarmup' { @($report.Phases[3]) }; 'TimedLifetime' { @($report.Phases) } }
        $nativeBytes = if ($Implementation -ceq 'Native') { $extent * $(switch ($window) { 'FirstUse' { 1 }; 'PostWarmup' { $Cycles }; 'TimedLifetime' { $lifecycles } }) } else { 0L }
        $managedBytes = [long]($phases | Measure-Object -Property ManagedAllocatedBytes -Sum).Sum
        $windows[$window] = [pscustomobject]@{ WallMilliseconds = ($phases | Measure-Object -Property WallMilliseconds -Sum).Sum;
            CpuMilliseconds = ($phases | Measure-Object -Property CpuMilliseconds -Sum).Sum;
            ManagedAllocatedBytes = $managedBytes; RequestedNativeBytes = $nativeBytes; DefinedAllocatedBytes = $managedBytes + $nativeBytes }
    }
    [pscustomobject]@{ Windows = $windows; ProcessId = $report.HostBefore.ProcessId; Runtime = $report.Runtime; Architecture = $report.Architecture;
        RuntimeConfiguration = $report.RuntimeConfiguration; TieredCompilation = $report.TieredCompilation; TieredPgo = $report.TieredPgo }
}

function Get-NamOwnershipBlockSummary {
    param([object[]]$Records, [double]$MinimumSpeedup, [double]$MaximumSlowdown, [double]$CalibrationMargin)
    foreach ($group in $Records | Group-Object Block, Mode, Contract) {
        $members = @($group.Group | Sort-Object Pair, Position)
        $first = $members[0]
        foreach ($window in @('FirstUse', 'PostWarmup', 'TimedLifetime')) {
            $sides = @{}
            foreach ($variant in @('Managed', 'Baseline', 'Calibration', 'Candidate')) { $sides[$variant] = @($members | Where-Object Variant -CEQ $variant | Sort-Object Pair) }
            $calibration = Get-NamOwnershipRatio @($sides.Baseline | ForEach-Object { $_.Metrics.Windows[$window].WallMilliseconds }) @($sides.Calibration | ForEach-Object { $_.Metrics.Windows[$window].WallMilliseconds })
            $incremental = Get-NamOwnershipRatio @($sides.Baseline | ForEach-Object { $_.Metrics.Windows[$window].WallMilliseconds }) @($sides.Candidate | ForEach-Object { $_.Metrics.Windows[$window].WallMilliseconds })
            $managed = Get-NamOwnershipRatio @($sides.Managed | ForEach-Object { $_.Metrics.Windows[$window].WallMilliseconds }) @($sides.Candidate | ForEach-Object { $_.Metrics.Windows[$window].WallMilliseconds })
            $fewerThanManaged = 0; $noMoreThanBaseline = 0
            for ($pair = 0; $pair -lt $sides.Candidate.Count; $pair++) {
                if ($sides.Candidate[$pair].Metrics.Windows[$window].DefinedAllocatedBytes -lt $sides.Managed[$pair].Metrics.Windows[$window].DefinedAllocatedBytes) { $fewerThanManaged++ }
                if ($sides.Candidate[$pair].Metrics.Windows[$window].DefinedAllocatedBytes -le $sides.Baseline[$pair].Metrics.Windows[$window].DefinedAllocatedBytes) { $noMoreThanBaseline++ }
            }
            $calibrated = Test-NamOwnershipCalibration $calibration $CalibrationMargin
            [pscustomobject]@{ Block = $first.Block; Mode = $first.Mode; Contract = $first.Contract; Window = $window;
                Samples = $sides.Candidate.Count; Calibration = $calibration; Incremental = $incremental; Managed = $managed;
                CalibrationBounded = $calibrated; IncrementalNonInferiority = $calibrated -and (Test-NamOwnershipNonInferiority $incremental $MaximumSlowdown);
                FewerDefinedBytesThanManagedPairs = $fewerThanManaged; NoMoreDefinedBytesThanBaselinePairs = $noMoreThanBaseline;
                MeetsCombinedThreshold = $calibrated -and $managed.Lower95 -ge $MinimumSpeedup -and
                    (Test-NamOwnershipNonInferiority $incremental $MaximumSlowdown) -and
                    $fewerThanManaged -eq $sides.Candidate.Count -and $noMoreThanBaseline -eq $sides.Candidate.Count }
        }
    }
}
