$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ownership-measurement-contract.ps1')
$cases = [Collections.Generic.List[string]]::new()
function Assert-NamOwnershipCase {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw "Fixture failed: $Name" }
    $cases.Add($Name)
}
function Assert-NamOwnershipRejection {
    param([scriptblock]$Action, [string]$Pattern, [string]$Name)
    $rejected = $false
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; $rejected = $true }
    Assert-NamOwnershipCase $rejected $Name
}
$schedule = @(New-NamOwnershipSchedule 2 8 @('Unique', 'Shared', 'SharedWeak') @('Default', 'Controlled'))
Assert-NamOwnershipCase ($schedule.Count -eq 384 -and @($schedule.Id | Select-Object -Unique).Count -eq 384) 'finite-unique-schedule'
foreach ($group in $schedule | Group-Object Block, Mode, Contract, Pair) {
    if ($group.Count -ne 4 -or @($group.Group.Variant | Select-Object -Unique).Count -ne 4 -or
        @($group.Group.Position | Select-Object -Unique).Count -ne 4) { throw 'Each matched group must contain four distinct positions and variants.' }
}
foreach ($group in $schedule | Group-Object Block, Mode, Contract, Variant, Position) {
    if ($group.Count -ne 2) { throw 'Each block must balance every variant into each position.' }
}
$cases.Add('every-block-position-balanced')
Assert-NamOwnershipRejection { New-NamOwnershipSchedule 1 8 @('Unique') @('Default') } 'Require' 'single-block-refused'
Assert-NamOwnershipRejection { New-NamOwnershipSchedule 2 6 @('Unique') @('Default') } 'Require' 'unbalanced-count-refused'
Assert-NamOwnershipRejection { New-NamOwnershipSchedule 2 8 @('Unique', 'Unique') @('Default') } 'duplicate' 'duplicate-scenario-refused'
Assert-NamOwnershipRejection { New-NamOwnershipSchedule 2 8 @('unique') @('Default') } 'Invalid' 'ambiguous-scenario-refused'
$same = Get-NamOwnershipRatio @(1, 2, 4, 8) @(1, 2, 4, 8)
Assert-NamOwnershipCase ($same.Count -eq 4 -and $same.GeometricMean -eq 1 -and $same.Lower95 -eq 1 -and $same.Upper95 -eq 1) 'identical-ratio-oracle'
Assert-NamOwnershipCase (Test-NamOwnershipCalibration $same 1.1) 'bounded-aa-calibration'
$shift = Get-NamOwnershipRatio @(2, 4, 8, 16) @(1, 2, 4, 8)
Assert-NamOwnershipCase ([Math]::Abs($shift.Lower95 - 2) -lt 1e-12 -and [Math]::Abs($shift.Upper95 - 2) -lt 1e-12) 'exact-paired-shift'
Assert-NamOwnershipCase (-not (Test-NamOwnershipCalibration $shift 1.1)) 'biased-aa-refused'
$wide = Get-NamOwnershipRatio @(0.5, 1, 2, 1) @(1, 1, 1, 1)
Assert-NamOwnershipCase ($wide.Upper95 -gt 1 -and $wide.Lower95 -lt 1 / 1.1 -and
    -not (Test-NamOwnershipNonInferiority $wide 1.1) -and -not (Test-NamOwnershipCalibration $wide 1.1)) 'upper-bound-does-not-prove-noninferiority'
Assert-NamOwnershipRejection { Get-NamOwnershipRatio @(1, 2) @(1) } 'Matched' 'unmatched-observations-refused'
Assert-NamOwnershipRejection { Get-NamOwnershipRatio @(1, 0) @(1, 1) } 'positive' 'zero-ratio-refused'
Assert-NamOwnershipRejection { Get-NamOwnershipRatio @(1, [double]::NaN) @(1, 1) } 'finite' 'nonfinite-ratio-refused'
$records = @(New-NamOwnershipSchedule 2 4 @('Unique') @('Default') | ForEach-Object {
    $window = [pscustomobject]@{ WallMilliseconds = 1; DefinedAllocatedBytes = 10 }
    [pscustomobject]@{ Block = $_.Block; Pair = $_.Pair; Position = $_.Position; Mode = $_.Mode; Contract = $_.Contract; Variant = $_.Variant;
        Metrics = [pscustomobject]@{ Windows = @{ FirstUse = $window; PostWarmup = $window; TimedLifetime = $window } } }
})
$summaries = @(Get-NamOwnershipBlockSummary $records 1.5 1.1 1.1)
Assert-NamOwnershipCase ($summaries.Count -eq 6 -and @($summaries | Where-Object {
    -not $_.CalibrationBounded -or -not $_.IncrementalNonInferiority -or $_.MeetsCombinedThreshold -or $_.Calibration.Count -ne 4
}).Count -eq 0) 'blocks-not-pooled-and-parity-not-improvement'

function New-NamOwnershipFixture {
    param([bool]$Native, [int]$ContractIndex)
    $one = @(917504L, 917616L, 1146992L)[$ContractIndex]
    $extent = if ($ContractIndex -eq 0) { 4096 } else { 4112 }
    $acquisitions = if ($ContractIndex -eq 0) { 6 } else { 12 }
    $files = [ordered]@{}
    foreach ($path in @('/proc/cpuinfo', '/proc/loadavg', '/proc/stat', '/proc/meminfo', '/proc/pressure/cpu', '/proc/pressure/memory',
        '/proc/pressure/io', '/proc/self/status', '/proc/self/cgroup', '/proc/self/mountinfo')) { $files[$path] = $null }
    $hostObservation = [pscustomobject]@{ ProcessId = 123; BeganAtUtc = '2026-10-07T00:00:00Z'; CompletedAtUtc = '2026-10-07T00:00:01Z'; LinuxFiles = $files }
    $phases = @()
    $names = @('preparation', 'first-use full lifecycle', 'counted warm-up full lifecycle', 'post-warm-up full lifecycle', 'terminal diagnostics')
    $counts = @(1, 1, 3, 2, 1); $checksums = @(0L, $one, ($one * 3), ($one * 2), 0L)
    for ($index = 0; $index -lt 5; $index++) {
        $phases += [pscustomobject]@{ Name = $names[$index]; Operations = $counts[$index]; Checksum = $checksums[$index];
            WallMilliseconds = $index + 1; CpuMilliseconds = 0; ManagedAllocatedBytes = 10;
            Gen0Collections = 0; Gen1Collections = 0; Gen2Collections = 0 }
    }
    [pscustomobject]@{ SchemaVersion = 2; Implementation = [int]$Native; Contract = $ContractIndex; Cycles = 2; WarmupCycles = 3;
        PayloadBytes = 4096; SliceBytes = 16; MovesPerCycle = 16; ReadsPerCycle = 32; UpgradesPerCycle = $(if ($ContractIndex -eq 2) { 8 } else { 0 });
        ExactOutput = $true; ExactCleanup = $true; ExactNativeAccounting = $true;
        FirstUseChecksum = $one; ExpectedFirstUseChecksum = $one; Checksum = $one * 2; ExpectedChecksum = $one * 2;
        WarmupChecksum = $one * 3; ExpectedWarmupChecksum = $one * 3; CycleTicks = @(1, 2); WarmupCycleTicks = @(3, 4, 5); TimestampFrequency = 10000000;
        Phases = $phases; PeakLiveBackingBytes = $extent; CopiedBytesPerCycle = $extent - 4096;
        NativeBackingAcquisitions = $(if ($Native) { $acquisitions } else { $null });
        Budget = $(if ($Native) { [pscustomobject]@{ AllocationCount = $acquisitions; FreeCount = $acquisitions; PeakAdmittedBytes = $extent;
            CommittedBytes = 0; ReservedBytes = 0; RejectedAllocationCount = 0; FailedAllocationCount = 0 } } else { $null });
        TerminalUnique = $(if ($Native) { [pscustomobject]@{ OwnedBackingBytes = 0; ActiveBorrowCount = 0; HasReturnObligation = $false } } else { $null });
        TerminalShared = $(if ($Native -and $ContractIndex -ne 0) { [pscustomobject]@{ StrongBindingCount = 0; WeakBindingCount = 0; ActiveReadCount = 0;
            ManagedBankBytes = 0; OwnedBackingBytes = 0; PayloadReleased = $true; Expired = $true } } else { $null });
        TerminalDetached = $(if ($Native -and $ContractIndex -ne 0) { [pscustomobject]@{ OwnedBackingBytes = 0; HasReturnObligation = $false } } else { $null });
        HostBefore = $hostObservation; HostAfter = $hostObservation; Runtime = 'fixture'; Architecture = 'fixture'; RuntimeConfiguration = @{};
        TieredCompilation = $null; TieredPgo = $null }
}
foreach ($contractIndex in @(0, 1, 2)) {
    foreach ($native in @($false, $true)) {
        $fixture = New-NamOwnershipFixture $native $contractIndex
        $implementation = if ($native) { 'Native' } else { 'Managed' }
        $contract = @('Unique', 'Shared', 'SharedWeak')[$contractIndex]
        $metrics = Read-NamOwnershipMetrics ($fixture | ConvertTo-Json -Depth 20) $implementation $contract 2 3
        $nativeBytes = if ($native) { @(24576L, 24672L, 24672L)[$contractIndex] } else { 0L }
        Assert-NamOwnershipCase ($metrics.Windows.TimedLifetime.WallMilliseconds -eq 15 -and $metrics.Windows.FirstUse.WallMilliseconds -eq 2 -and
            $metrics.Windows.PostWarmup.WallMilliseconds -eq 4 -and $metrics.Windows.TimedLifetime.DefinedAllocatedBytes -eq 50 + $nativeBytes) "five-phase-domain-$implementation-$contract"
    }
}
foreach ($mutation in @('schema', 'checksum', 'phase', 'negative', 'acquisition', 'cleanup', 'unavailable', 'process', 'ticks')) {
    $fixture = New-NamOwnershipFixture $true 2
    switch ($mutation) {
        'schema' { $fixture.SchemaVersion = 1 }
        'checksum' { $fixture.FirstUseChecksum++ }
        'phase' { $fixture.Phases[2].Name = 'unmeasured preparation' }
        'negative' { $fixture.Phases[1].ManagedAllocatedBytes = -1 }
        'acquisition' { $fixture.Budget.AllocationCount = 0 }
        'cleanup' { $fixture.TerminalShared.OwnedBackingBytes = 4096 }
        'unavailable' { $fixture.Implementation = 0 }
        'process' { $fixture.HostAfter = $fixture.HostAfter.PSObject.Copy(); $fixture.HostAfter.ProcessId = 124 }
        'ticks' { $fixture.CycleTicks = @(1, 0) }
    }
    $implementation = if ($mutation -ceq 'unavailable') { 'Managed' } else { 'Native' }
    Assert-NamOwnershipRejection { Read-NamOwnershipMetrics ($fixture | ConvertTo-Json -Depth 20) $implementation 'SharedWeak' 2 3 } 'differs|Invalid|unavailable|identity changed' "reject-$mutation"
}
[pscustomobject]@{ Passed = $true; Cases = $cases.ToArray(); Scope = 'Deterministic protocol/statistical fixtures, not measured performance.' } | ConvertTo-Json -Depth 5
