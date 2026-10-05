param([Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$namRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$namCapture = [IO.File]::ReadAllText((Join-Path $namRoot 'capture.json')) | ConvertFrom-Json
if (-not $namCapture.CaptureComplete -or $namCapture.PerformanceAccepted -or $namCapture.PublishedVersion -cne '0.2.3') {
    throw 'An incomplete capture or invented prior verdict cannot satisfy comparison acceptance.'
}
$namInputs = @([IO.File]::ReadAllText((Join-Path $namRoot 'input-identities.json')) | ConvertFrom-Json)
foreach ($namInput in $namInputs) {
    if ($namInput.Variant -cnotin @('Published', 'Candidate') -or
        [IO.Path]::GetFileName($namInput.Name) -cne $namInput.Name -or
        $namInput.Path -cne (Join-Path $namRoot "$($namInput.Variant)/$($namInput.Name)")) {
        throw 'A comparison input is outside its exact variant boundary.'
    }
    $namFile = Get-Item -LiteralPath $namInput.Path
    if ($namFile.Length -ne $namInput.Length -or (Get-FileHash -LiteralPath $namFile.FullName -Algorithm SHA256).Hash -cne $namInput.SHA256) {
        throw 'A real comparison input changed before acceptance.'
    }
}
$namPublished = @($namInputs | Where-Object Variant -eq 'Published')
$namCandidates = @($namInputs | Where-Object Variant -eq 'Candidate')
if ($namPublished.Count -lt 4 -or $namPublished.Count -ne $namCandidates.Count) { throw 'Complete variant input graphs are required.' }
foreach ($namPublishedInput in $namPublished) {
    $namCandidateInput = @($namCandidates | Where-Object Name -ceq $namPublishedInput.Name)
    if ($namCandidateInput.Count -ne 1) { throw 'Candidate input is missing or duplicated.' }
    if ($namPublishedInput.Name -ceq 'Supprocom.NativeAllocationManagement.dll') {
        if ($namPublishedInput.SHA256 -cne '87B8131B0D05B626ACA4DD570E686E0519D3E0D93A8DE4865CF210C8A479F68B' -or
            $namCandidateInput[0].SHA256 -cne $namCapture.CandidateSHA256) { throw 'Actual published/candidate runtime identities differ.' }
    } elseif ($namPublishedInput.SHA256 -cne $namCandidateInput[0].SHA256 -or $namPublishedInput.Length -ne $namCandidateInput[0].Length) {
        throw 'Comparison variants differ in more than the actual NAM runtime.'
    }
}
$namCommands = @([IO.File]::ReadAllText((Join-Path $namRoot 'commands.json')) | ConvertFrom-Json)
if ($namCommands.Count -ne 36 -or @($namCommands.Name | Sort-Object -Unique).Count -ne 36) { throw 'All exact compiler/identity/32 worker commands are required.' }
foreach ($namVariant in @('Published', 'Candidate')) {
    $namVariantInputs = @($namInputs | Where-Object Variant -ceq $namVariant)
    $namActualFiles = @(Get-ChildItem -LiteralPath (Join-Path $namRoot $namVariant) -Force -Recurse)
    if ($namActualFiles.Count -ne $namVariantInputs.Count -or
        @($namActualFiles | Where-Object { $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -ne 0 -or
        @($namVariantInputs.Name | Sort-Object -Unique).Count -ne $namVariantInputs.Count) {
        throw 'Variant payload files are missing, unexpected, duplicated or linked.'
    }
    $namIdentityCommand = @($namCommands | Where-Object Name -ceq "identity-$namVariant")[0]
    $namActual = [IO.File]::ReadAllText((Join-Path $namRoot "identity-$namVariant/stdout.log")) | ConvertFrom-Json
    if ($namIdentityCommand.ObservedExitCode -ne 0 -or $namIdentityCommand.TimedOut -or $namActual.Framework -cne '.NET 10.0.10' -or
        $namActual.Rid -cne $namCapture.NativeRid -or
        -not [string]::Equals($namActual.Architecture, $namCapture.NativeRid.Split('-')[1], [StringComparison]::OrdinalIgnoreCase) -or
        $namActual.RuntimePath -cne (Join-Path $namRoot "$namVariant/Supprocom.NativeAllocationManagement.dll")) {
        throw 'Actual native worker/runtime identity is unverified.'
    }
}
foreach ($namBuildName in @('worker-restore', 'worker-build')) {
    $namBuildCommand = @($namCommands | Where-Object Name -ceq $namBuildName)[0]
    if ($namBuildCommand.ObservedExitCode -ne 0 -or $namBuildCommand.TimedOut -or $namBuildCommand.DeadlineSeconds -ne 300) {
        throw 'Actual comparison compiler/restore commands did not pass.'
    }
}
function Assert-NamPublishedNumber {
    param([double]$Observed, [double]$Expected)
    if (-not [double]::IsFinite($Observed) -or [Math]::Abs($Observed - $Expected) -gt 1e-12 * [Math]::Max(1, [Math]::Abs($Expected))) {
        throw 'Reported performance arithmetic differs from original samples.'
    }
}
function Get-NamPublishedInterval {
    param([double[]]$Ratios)
    if ($Ratios.Count -ne 8 -or @($Ratios | Where-Object { -not [double]::IsFinite($_) -or $_ -le 0 }).Count -ne 0) { throw 'Eight valid paired ratios are required.' }
    $namLogs = @($Ratios | ForEach-Object { [Math]::Log($_) })
    $namMean = ($namLogs | Measure-Object -Average).Average
    $namVariance = (($namLogs | ForEach-Object { [Math]::Pow($_ - $namMean, 2) }) | Measure-Object -Sum).Sum / 7
    $namMargin = 2.365 * [Math]::Sqrt($namVariance / 8)
    [pscustomobject]@{ CandidateOverPublished = [Math]::Exp($namMean); Lower95 = [Math]::Exp($namMean - $namMargin);
        Upper95 = [Math]::Exp($namMean + $namMargin); DegreesOfFreedom = 7; Ratios = $Ratios }
}
$namReports = @{}
$namFloorFailures = [Collections.Generic.List[object]]::new()
$namCandidateFloorsPass = $true
$namPreviousEnd = [DateTimeOffset]::MinValue
foreach ($namMode in @('Controlled', 'Default')) {
    foreach ($namPair in 0..7) {
        $namExpectedOrder = if (($namPair -band 1) -eq 0) { @('Published', 'Candidate') } else { @('Candidate', 'Published') }
        foreach ($namVariant in $namExpectedOrder) {
            $namName = "$namMode-$namPair-$namVariant"
            $namCommand = @($namCommands | Where-Object Name -ceq $namName)[0]
            $namReport = [IO.File]::ReadAllText((Join-Path $namRoot "$namName/stdout.log")) | ConvertFrom-Json
            $namKnob = if ($namMode -eq 'Controlled') { '0' } else { 'unset' }
            if ($namCommand.TimedOut -or $null -eq $namCommand.ObservedExitCode -or $namCommand.DeadlineSeconds -ne 10 -or
                [DateTimeOffset]$namCommand.StartedAt -lt $namPreviousEnd -or [DateTimeOffset]$namCommand.EndedAt -lt [DateTimeOffset]$namCommand.StartedAt -or
                -not [string]::IsNullOrEmpty([IO.File]::ReadAllText((Join-Path $namRoot "$namName/stderr.log"))) -or
                @($namCommand.Arguments).Count -ne 1 -or $namCommand.Arguments[0] -cne (Join-Path $namRoot "$namVariant/PublishedRegionWorker.dll") -or
                $namReport.Iterations -ne 512 -or $namReport.SampleCount -ne 8 -or @($namReport.Pairs).Count -ne 8 -or
                $namReport.MinimumSpeedup -ne 1.50 -or $namReport.TieredCompilation -cne $namKnob -or $namReport.TieredPgo -cne $namKnob) {
                throw 'A declared actual worker boundary differs or overlaps its paired predecessor.'
            }
            $namPreviousEnd = [DateTimeOffset]$namCommand.EndedAt
            Assert-NamPublishedNumber $namCommand.ElapsedMilliseconds (([DateTimeOffset]$namCommand.EndedAt - [DateTimeOffset]$namCommand.StartedAt).TotalMilliseconds)
            $namSettings = @($namCommand.Settings.PSObject.Properties)
            if ($namMode -ceq 'Controlled') {
                if ($namCommand.Settings.DOTNET_TieredCompilation -cne '0' -or $namCommand.Settings.DOTNET_TieredPGO -cne '0') {
                    throw 'Actual controlled settings differ.'
                }
            }
            if (@($namSettings | Where-Object { $_.Name -match '^(DOTNET_|COMPlus_)(Jit|Tiered|TC_)' -and
                ($namMode -ceq 'Default' -or $_.Name -cnotin @('DOTNET_TieredCompilation', 'DOTNET_TieredPGO')) }).Count -ne 0) {
                throw 'An undeclared JIT or tiering setting contaminated measurement.'
            }
            $namCpuPath = Join-Path $namRoot "$namName/worker-cpu.json"
            $namCpu = [IO.File]::ReadAllText($namCpuPath) | ConvertFrom-Json
            if ($namCommand.WorkerCpuEvidence -cne $namCpuPath -or -not [double]::IsFinite($namCpu.ProcessorMilliseconds) -or
                $namCpu.ProcessorMilliseconds -le 0 -or [DateTimeOffset]$namCpu.ObservedAt -lt [DateTimeOffset]$namCommand.StartedAt -or
                [DateTimeOffset]$namCpu.ObservedAt -gt [DateTimeOffset]$namCommand.EndedAt) { throw 'Actual in-process CPU observation is missing or invalid.' }
            $namSampleIndex = 0
            $namSpeedups = @()
            $namRegionTotal, $namManagedTotal = 0.0, 0.0
            foreach ($namSample in $namReport.Pairs) {
                $namSampleOrder = if (($namSampleIndex -band 1) -eq 0) { 'ArrayPool-Region' } else { 'Region-ArrayPool' }
                if ($namSample.Sample -ne $namSampleIndex -or $namSample.Order -cne $namSampleOrder -or
                    $namSample.Region.Implementation -cne 'NativeRegion' -or $namSample.ArrayPool.Implementation -cne 'ArrayPool') {
                    throw 'Original sample index, order or implementation differs.'
                }
                $namSampleIndex++
                if ([long]$namSample.Region.Checksum -ne 9025015936285474816 -or
                    [long]$namSample.ArrayPool.Checksum -ne [long]$namSample.Region.Checksum -or
                    [long]$namSample.Region.LogicalBytes -ne 18612224 -or
                    [long]$namSample.ArrayPool.LogicalBytes -ne [long]$namSample.Region.LogicalBytes -or
                    $namSample.Region.ManagedAllocatedBytes -ne 0 -or $namSample.Region.FreshSegmentCount -ne 0) { throw 'Actual output/work/allocation parity differs.' }
                foreach ($namPhase in @($namSample.ArrayPool, $namSample.Region)) {
                    if ($namPhase.Gen0Collections -ne 0 -or $namPhase.Gen1Collections -ne 0 -or $namPhase.Gen2Collections -ne 0 -or
                        $namPhase.Attempt -lt 1 -or $namPhase.Attempt -gt 3 -or -not [double]::IsFinite($namPhase.ElapsedMilliseconds) -or
                        $namPhase.ElapsedMilliseconds -lt 10) { throw 'Original sample acceptance differs.' }
                }
                $namSpeedup = [double]$namSample.ArrayPool.ElapsedMilliseconds / [double]$namSample.Region.ElapsedMilliseconds
                Assert-NamPublishedNumber $namSample.Speedup $namSpeedup
                $namSpeedups += $namSpeedup
                $namRegionTotal += [double]$namSample.Region.ElapsedMilliseconds
                $namManagedTotal += [double]$namSample.ArrayPool.ElapsedMilliseconds
            }
            $namSorted = @($namSpeedups | Sort-Object)
            Assert-NamPublishedNumber $namReport.MedianSpeedup (($namSorted[3] + $namSorted[4]) / 2)
            Assert-NamPublishedNumber $namReport.AggregateSpeedup ($namManagedTotal / $namRegionTotal)
            Assert-NamPublishedNumber $namReport.RegionElapsedMilliseconds $namRegionTotal
            Assert-NamPublishedNumber $namReport.ArrayPoolElapsedMilliseconds $namManagedTotal
            $namActualFloorPass = $namReport.MedianSpeedup -ge 1.50 -and $namReport.AggregateSpeedup -ge 1.50
            $namExpectedExit = if ($namActualFloorPass) { 0 } else { 3 }
            if ($namReport.Passed -ne $namActualFloorPass -or $namCommand.ObservedExitCode -ne $namExpectedExit) { throw 'Reported/actual floor disposition differs.' }
            if (-not $namActualFloorPass) {
                $namFloorFailures.Add([pscustomobject]@{ Name = $namName; ActualExit = $namCommand.ObservedExitCode;
                    Median = $namReport.MedianSpeedup; Aggregate = $namReport.AggregateSpeedup })
                if ($namVariant -eq 'Candidate') { $namCandidateFloorsPass = $false }
            }
            $namReports[$namName] = $namReport
        }
    }
}
$namComparisons = [Collections.Generic.List[object]]::new()
$namNoMaterialRegression = $true
foreach ($namMode in @('Controlled', 'Default')) {
    $namRegionRatios, $namManagedRatios, $namWholeRatios = @(), @(), @()
    foreach ($namPair in 0..7) {
        $namOld = $namReports["$namMode-$namPair-Published"]
        $namNew = $namReports["$namMode-$namPair-Candidate"]
        $namRegionRatios += [double]$namNew.RegionElapsedMilliseconds / [double]$namOld.RegionElapsedMilliseconds
        $namManagedRatios += [double]$namNew.ArrayPoolElapsedMilliseconds / [double]$namOld.ArrayPoolElapsedMilliseconds
        $namOldCommand = @($namCommands | Where-Object Name -ceq "$namMode-$namPair-Published")[0]
        $namNewCommand = @($namCommands | Where-Object Name -ceq "$namMode-$namPair-Candidate")[0]
        $namWholeRatios += [double]$namNewCommand.ElapsedMilliseconds / [double]$namOldCommand.ElapsedMilliseconds
    }
    $namRegion = Get-NamPublishedInterval $namRegionRatios
    $namNoMaterialRegression = $namNoMaterialRegression -and $namRegion.Upper95 -le 1.10
    $namComparisons.Add([pscustomobject]@{ Mode = $namMode; Region = $namRegion; ExpertManagedControl = Get-NamPublishedInterval $namManagedRatios;
        WholeChild = Get-NamPublishedInterval $namWholeRatios; Upper95TimeRatioCeiling = 1.10 })
}
$namAcceptance = Join-Path $namRoot 'acceptance.json'
if (Test-Path -LiteralPath $namAcceptance) { throw 'An existing performance interpretation must not be overwritten.' }
$namPassed = $namNoMaterialRegression -and $namCandidateFloorsPass
[pscustomobject]@{ Passed = $namPassed; CurrentManagedFloorsPassed = $namCandidateFloorsPass;
    NoMaterialRegression = $namNoMaterialRegression; Comparisons = $namComparisons.ToArray(); OriginalFloorFailures = $namFloorFailures.ToArray();
    Scope = 'One actual native published/current/managed uninstrumented Region scenario, not full NAM performance acceptance.' } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $namAcceptance -Encoding utf8
Write-Output ([IO.File]::ReadAllText($namAcceptance))
if (-not $namPassed) { throw 'Additional published-runtime performance criteria failed; original raw failed outcomes remain retained.' }
