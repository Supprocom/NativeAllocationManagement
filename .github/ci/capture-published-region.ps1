param(
    [Parameter(Mandatory)][ValidateSet('linux-x64', 'linux-arm64', 'win-x64', 'win-arm64')][string]$ExpectedRid,
    [Parameter(Mandatory)][string]$CandidateRuntime,
    [Parameter(Mandatory)][string]$EvidenceRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier -cne $ExpectedRid) { throw 'Actual native RID does not match.' }
$namCandidate = [IO.Path]::GetFullPath($CandidateRuntime)
if (-not (Test-Path -LiteralPath $namCandidate -PathType Leaf)) { throw 'The actual built candidate runtime is missing.' }
$namRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$namSource = [IO.Path]::GetFullPath((Get-Location).Path)
if ($namRoot -eq $namSource -or $namRoot.StartsWith($namSource + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $namRoot)) { throw 'Published comparison requires a new exact boundary outside the source checkout.' }
[IO.Directory]::CreateDirectory($namRoot) | Out-Null
$namInputs = Join-Path $namRoot 'worker-inputs'
[IO.Directory]::CreateDirectory((Join-Path $namInputs 'runtime')) | Out-Null
$namPackage = Join-Path $namRoot 'published-0.2.3.nupkg'
$namClient = [Net.Http.HttpClient]::new()
try {
    $namBytes = $namClient.GetByteArrayAsync('https://api.nuget.org/v3-flatcontainer/supprocom.nativeallocationmanagement/0.2.3/supprocom.nativeallocationmanagement.0.2.3.nupkg').GetAwaiter().GetResult()
    [IO.File]::WriteAllBytes($namPackage, $namBytes)
} finally { $namClient.Dispose() }
if ((Get-Item -LiteralPath $namPackage).Length -ne 232530 -or
    (Get-FileHash -LiteralPath $namPackage -Algorithm SHA256).Hash -cne '7B4603D59E2CE52D957CA4E038991A6FD677DDB035EFDC79650F7E875069903B') {
    throw 'Published signed package differs from the exact accepted publication.'
}
$namOld = Join-Path $namInputs 'runtime/Supprocom.NativeAllocationManagement.dll'
$namZip = [IO.Compression.ZipFile]::OpenRead($namPackage)
try {
    $namEntry = $namZip.GetEntry('lib/net10.0/Supprocom.NativeAllocationManagement.dll')
    if ($null -eq $namEntry) { throw 'Actual published NET10 runtime is absent.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($namEntry, $namOld, $false)
} finally { $namZip.Dispose() }
if ((Get-FileHash -LiteralPath $namOld -Algorithm SHA256).Hash -cne '87B8131B0D05B626ACA4DD570E686E0519D3E0D93A8DE4865CF210C8A479F68B') {
    throw 'Published runtime does not equal the accepted payload.'
}
foreach ($namFile in @('Directory.Build.props', 'BannedSymbols.txt', '.editorconfig')) {
    [IO.File]::Copy((Join-Path $namSource $namFile), (Join-Path $namInputs $namFile), $false)
}
[IO.File]::Copy((Join-Path $namSource 'Supprocom.NativeAllocationManagement.Performance/AllocatorPerformanceRegression.cs'),
    (Join-Path $namInputs 'AllocatorPerformanceRegression.cs'), $false)
[IO.File]::Copy((Join-Path $PSScriptRoot 'published-region/Program.cs'), (Join-Path $namInputs 'Program.cs'), $false)
[IO.File]::Copy((Join-Path $PSScriptRoot 'published-region/PublishedRegionWorker.csproj.in'), (Join-Path $namInputs 'PublishedRegionWorker.csproj'), $false)
$namCommands = [Collections.Generic.List[object]]::new()
function Invoke-NamPublishedChild {
    param([string]$Name, [string]$Executable, [string[]]$Arguments, [int]$DeadlineSeconds, [string]$Mode = 'Build')
    $namDirectory = Join-Path $namRoot $Name
    [IO.Directory]::CreateDirectory($namDirectory) | Out-Null
    $namStart = [Diagnostics.ProcessStartInfo]::new($Executable)
    $namStart.WorkingDirectory = $namInputs
    $namStart.UseShellExecute = $false
    $namStart.RedirectStandardOutput = $true
    $namStart.RedirectStandardError = $true
    foreach ($namArgument in $Arguments) { $namStart.ArgumentList.Add($namArgument) }
    $namSettings = [ordered]@{}
    if ($Mode -ne 'Build') {
        $namStart.Environment['NAM_REGION_CPU_EVIDENCE'] = Join-Path $namDirectory 'worker-cpu.json'
        foreach ($namVariable in @($namStart.Environment.Keys)) {
            if ($namVariable -match '^(DOTNET_|COMPlus_)(Jit|Tiered|TC_)') { $namStart.Environment.Remove($namVariable) | Out-Null }
        }
        if ($Mode -eq 'Controlled') {
            $namStart.Environment['DOTNET_TieredCompilation'] = '0'
            $namStart.Environment['DOTNET_TieredPGO'] = '0'
        }
        foreach ($namVariable in $namStart.Environment.Keys | Sort-Object) {
            if ($namVariable -match '^(DOTNET_|COMPlus_)(Jit|Tiered|TC_|GC|ReadyToRun)') { $namSettings[$namVariable] = $namStart.Environment[$namVariable] }
        }
    }
    $namProcess = [Diagnostics.Process]::new()
    $namProcess.StartInfo = $namStart
    $namStarted = [DateTimeOffset]::UtcNow
    $namExit = $null
    $namTimedOut = $false
    $namOutput, $namError = '', ''
    try {
        if (-not $namProcess.Start()) { throw 'Actual comparison child did not start.' }
        $namStdout = $namProcess.StandardOutput.ReadToEndAsync()
        $namStderr = $namProcess.StandardError.ReadToEndAsync()
        if (-not $namProcess.WaitForExit($DeadlineSeconds * 1000)) { $namTimedOut = $true; $namProcess.Kill($true); $namProcess.WaitForExit() }
        $namOutput, $namError = $namStdout.GetAwaiter().GetResult(), $namStderr.GetAwaiter().GetResult()
        $namExit = $namProcess.ExitCode
    } catch { $namError += $_.ToString(); throw }
    finally {
        $namEnded = [DateTimeOffset]::UtcNow
        [IO.File]::WriteAllText((Join-Path $namDirectory 'stdout.log'), $namOutput)
        [IO.File]::WriteAllText((Join-Path $namDirectory 'stderr.log'), $namError)
        $namRecord = [pscustomobject]@{ Name = $Name; Executable = $Executable; Arguments = $Arguments; WorkingDirectory = $namInputs;
            Settings = $namSettings; StartedAt = $namStarted; EndedAt = $namEnded; ElapsedMilliseconds = ($namEnded - $namStarted).TotalMilliseconds;
            WorkerCpuEvidence = if ($Mode -ne 'Build') { $namStart.Environment['NAM_REGION_CPU_EVIDENCE'] } else { $null };
            ObservedExitCode = $namExit; TimedOut = $namTimedOut; DeadlineSeconds = $DeadlineSeconds }
        $namRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $namDirectory 'command.json') -Encoding utf8
        $namCommands.Add($namRecord)
        $namProcess.Dispose()
    }
    if ($namTimedOut -or $null -eq $namExit) { throw 'Comparison child did not complete; actual unavailable/timeout record is retained.' }
    [pscustomobject]@{ Exit = $namExit; Output = $namOutput; Error = $namError; Record = $namRecord }
}
$namCaptureComplete = $false
try {
    $namProject = Join-Path $namInputs 'PublishedRegionWorker.csproj'
    $namBuildRoot = Join-Path $namRoot 'build'
    foreach ($namBuildCommand in @(
        @('restore', '--force', '--no-cache'), @('build', '--no-restore', '-c', 'Release', '--disable-build-servers'))) {
        $namBuildArguments = @($namBuildCommand[0], $namProject, ('-p:ArtifactsPath=' + $namBuildRoot), '--nologo', '-v', 'minimal')
        $namBuildArguments += @($namBuildCommand[1..($namBuildCommand.Count - 1)])
        $namResult = Invoke-NamPublishedChild -Name "worker-$($namBuildCommand[0])" -Executable dotnet -Arguments $namBuildArguments -DeadlineSeconds 300
        if ($namResult.Exit -ne 0) { throw 'Exact standalone comparison worker failed compilation; full diagnostics are retained.' }
    }
    $namBuild = Join-Path $namBuildRoot 'bin/PublishedRegionWorker/release'
    $namInputIdentities = [Collections.Generic.List[object]]::new()
    foreach ($namVariant in @('Published', 'Candidate')) {
        $namDirectory = Join-Path $namRoot $namVariant
        [IO.Directory]::CreateDirectory($namDirectory) | Out-Null
        foreach ($namFile in Get-ChildItem -LiteralPath $namBuild -File) {
            $namInput = if ($namFile.Name -eq 'Supprocom.NativeAllocationManagement.dll' -and $namVariant -eq 'Candidate') { $namCandidate } else { $namFile.FullName }
            $namDestination = Join-Path $namDirectory $namFile.Name
            [IO.File]::Copy($namInput, $namDestination, $false)
            $namInputIdentities.Add([pscustomobject]@{ Variant = $namVariant; Name = $namFile.Name; Path = $namDestination;
                Length = (Get-Item -LiteralPath $namDestination).Length; SHA256 = (Get-FileHash -LiteralPath $namDestination -Algorithm SHA256).Hash })
        }
        $namWorker = Join-Path $namDirectory 'PublishedRegionWorker.dll'
        $namIdentity = Invoke-NamPublishedChild "identity-$namVariant" dotnet @($namWorker, '--identity') 10 'Default'
        if ($namIdentity.Exit -ne 0 -or $namIdentity.Error.Length -ne 0) { throw 'Actual native worker identity failed.' }
        $namActual = $namIdentity.Output | ConvertFrom-Json
        if ($namActual.Framework -cne '.NET 10.0.10' -or $namActual.Rid -cne $ExpectedRid -or
            -not [string]::Equals($namActual.Architecture, $ExpectedRid.Split('-')[1], [StringComparison]::OrdinalIgnoreCase) -or
            $namActual.RuntimePath -cne (Join-Path $namDirectory 'Supprocom.NativeAllocationManagement.dll')) { throw 'Actual worker/runtime selection differs.' }
    }
    $namInputIdentities | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $namRoot 'input-identities.json') -Encoding utf8
    foreach ($namMode in @('Controlled', 'Default')) {
        foreach ($namPair in 0..7) {
            $namOrder = if (($namPair -band 1) -eq 0) { @('Published', 'Candidate') } else { @('Candidate', 'Published') }
            foreach ($namVariant in $namOrder) {
                $namArguments = @(Join-Path $namRoot "$namVariant/PublishedRegionWorker.dll")
                $namResult = Invoke-NamPublishedChild -Name "$namMode-$namPair-$namVariant" -Executable dotnet -Arguments $namArguments -DeadlineSeconds 10 -Mode $namMode
                $namReport = $namResult.Output | ConvertFrom-Json
                $namExpectedExit = if ($namReport.Passed) { 0 } else { 3 }
                if ($namResult.Exit -ne $namExpectedExit -or $namResult.Error.Length -ne 0) { throw 'Actual worker floor exit/output differs.' }
                Write-Output "$namMode/$namPair/$namVariant actual exit $($namResult.Exit), median $($namReport.MedianSpeedup), aggregate $($namReport.AggregateSpeedup); acceptance is separate."
            }
        }
    }
    $namCaptureComplete = $true
} finally {
    $namCommands | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $namRoot 'commands.json') -Encoding utf8
    [pscustomobject]@{ CaptureComplete = $namCaptureComplete; PerformanceAccepted = $false; NativeRid = $ExpectedRid;
        PublishedVersion = '0.2.3'; CandidateRuntime = $namCandidate; CandidateSHA256 = (Get-FileHash -LiteralPath $namCandidate -Algorithm SHA256).Hash;
        Scope = 'Uninstrumented published/current/managed Region comparison; retained floor failures are not passes.' } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $namRoot 'capture.json') -Encoding utf8
}
