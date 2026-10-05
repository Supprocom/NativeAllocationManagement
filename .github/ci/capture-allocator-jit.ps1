param(
    [Parameter(Mandatory)][ValidateSet('linux-x64', 'linux-arm64', 'win-x64', 'win-arm64')][string]$ExpectedRid,
    [Parameter(Mandatory)][string]$Worker,
    [Parameter(Mandatory)][string]$EvidenceRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier -ne $ExpectedRid) {
    throw 'JIT capture requires the actual requested native process.'
}
$namWorker = [IO.Path]::GetFullPath($Worker)
if (-not (Test-Path -LiteralPath $namWorker -PathType Leaf)) { throw 'The built performance worker is missing.' }
$namRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $namRoot) { throw 'An existing JIT capture boundary must not be overwritten.' }
[IO.Directory]::CreateDirectory($namRoot) | Out-Null
$namWorkerFile = Get-Item -LiteralPath $namWorker
[pscustomobject]@{ Path = $namWorker; Length = $namWorkerFile.Length;
    SHA256 = (Get-FileHash -LiteralPath $namWorker -Algorithm SHA256).Hash;
    Rid = $ExpectedRid; Architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString();
    TimingAcceptance = 'Not applicable: JIT instrumentation is diagnostic, not a performance verdict.' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $namRoot 'identity.json') -Encoding utf8

# Execute the unchanged workers in separate child processes. Never replace the
# uninstrumented floor, change preparation, or convert a floor failure into a pass.
foreach ($namConfiguration in @('controlled', 'default')) {
    foreach ($namKind in @('Probe', 'Region', 'Arena')) {
        $namName = "$namConfiguration-$namKind"
        $namLog = Join-Path $namRoot $namName
        $namDisassembly = "$namLog.jit.log"
        $namStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
        $namStart.UseShellExecute = $false
        $namStart.RedirectStandardOutput = $true
        $namStart.RedirectStandardError = $true
        $namStart.ArgumentList.Add($namWorker)
        if ($namKind -eq 'Probe') {
            $namStart.ArgumentList.Add('--region-jit-probe')
        } else {
            $namStart.ArgumentList.Add('--allocator-regression-worker')
            $namStart.ArgumentList.Add('--kind')
            $namStart.ArgumentList.Add($namKind)
        }
        foreach ($namVariable in @($namStart.Environment.Keys)) {
            if ($namVariable -match '^(DOTNET|COMPlus)_(TieredCompilation|TieredPGO|Jit.*)$') {
                $namStart.Environment.Remove($namVariable) | Out-Null
            }
        }
        if ($namConfiguration -eq 'controlled') {
            $namStart.Environment['DOTNET_TieredCompilation'] = '0'
            $namStart.Environment['DOTNET_TieredPGO'] = '0'
        }
        $namStart.Environment['DOTNET_JitDisasm'] = 'MeasureRegion MeasureArena MeasureArrayPools ProbeNewLease ProbeNewAccess LeaseInitialized Initialize EnterBorrow Read Reserve'
        $namStart.Environment['DOTNET_JitStdOutFile'] = $namDisassembly
        $namSettings = [ordered]@{}
        foreach ($namVariable in $namStart.Environment.Keys | Sort-Object) {
            if ($namVariable -match '^(DOTNET|COMPlus)_(TieredCompilation|TieredPGO|Jit.*)$') {
                $namSettings[$namVariable] = $namStart.Environment[$namVariable]
            }
        }
        $namProcess = [Diagnostics.Process]::new()
        $namProcess.StartInfo = $namStart
        $namStarted = [DateTimeOffset]::UtcNow
        $namExit = $null
        $namTimedOut = $false
        $namOutput = ''
        $namError = ''
        try {
            if (-not $namProcess.Start()) { throw 'The JIT capture worker did not start.' }
            $namStdout = $namProcess.StandardOutput.ReadToEndAsync()
            $namStderr = $namProcess.StandardError.ReadToEndAsync()
            if (-not $namProcess.WaitForExit(120000)) {
                $namTimedOut = $true
                $namProcess.Kill($true)
                $namProcess.WaitForExit()
            }
            $namOutput = $namStdout.GetAwaiter().GetResult()
            $namError = $namStderr.GetAwaiter().GetResult()
            $namExit = $namProcess.ExitCode
        } finally {
            [IO.File]::WriteAllText("$namLog.stdout.log", $namOutput)
            [IO.File]::WriteAllText("$namLog.stderr.log", $namError)
            [pscustomobject]@{ Executable = 'dotnet'; Arguments = @($namStart.ArgumentList);
                Settings = $namSettings; StartedAt = $namStarted; EndedAt = [DateTimeOffset]::UtcNow;
                ObservedExitCode = $namExit; TimedOut = $namTimedOut; DeadlineSeconds = 120;
                Instrumented = $true; PerformanceAccepted = $false } |
                ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$namLog.command.json" -Encoding utf8
            $namProcess.Dispose()
        }
        if ($namTimedOut -or $namError.Length -ne 0 -or -not (Test-Path -LiteralPath $namDisassembly -PathType Leaf)) {
            throw "JIT capture $namName failed; its actual command and streams are retained."
        }
        $namText = [IO.File]::ReadAllText($namDisassembly)
        $namRequiredMethod = if ($namKind -eq 'Probe') { 'NativeRegionJitProbe:ProbeNewLease' } else { "Measure$namKind" }
        if (-not $namText.Contains($namRequiredMethod, [StringComparison]::Ordinal)) {
            throw "Actual $namName code generation was not captured."
        }
        if ($namKind -eq 'Probe') {
            if ($namExit -ne 0 -or $namOutput.Trim() -ne '28') { throw 'The unchanged JIT probe did not produce its exact output.' }
        } else {
            $namReport = ConvertFrom-Json -InputObject $namOutput
            $namExpectedExit = if ($namReport.Passed) { 0 } else { 3 }
            if ($namExit -ne $namExpectedExit -or $namReport.SampleCount -ne 8 -or $namReport.Pairs.Count -ne 8 -or $namReport.MinimumSpeedup -ne 1.50) {
                throw 'The unchanged instrumented floor output and actual exit do not reconcile.'
            }
            foreach ($namPair in $namReport.Pairs) {
                if ($namPair.ArrayPool.Checksum -ne $namPair.$namKind.Checksum -or
                    $namPair.ArrayPool.LogicalBytes -ne $namPair.$namKind.LogicalBytes) {
                    throw 'The instrumented native and managed workers did not produce equivalent output.'
                }
            }
        }
        Write-Output "JIT capture ${namName}: actual exit $namExit; instrumentation is NOT timing acceptance."
        # Retain code generation in the job log as well as the complete payload so
        # native diagnosis does not depend on extracting a multi-gigabyte archive.
        Write-Output $namText
    }
}
