param(
    [Parameter(Mandatory)][ValidateSet('linux-x64', 'linux-arm64', 'win-x64', 'win-arm64')][string]$ExpectedRid,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'validation-contract.ps1')
$namRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$namSource = [IO.Path]::GetFullPath((Get-Location).Path)
if ([string]::Equals($namRoot, $namSource, [StringComparison]::OrdinalIgnoreCase) -or
    $namRoot.StartsWith($namSource + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence must be outside the exact source checkout.'
}
foreach ($namDirectory in @('logs', 'trx', 'tmp', 'build', 'cli-home', 'restore-cache')) {
    [IO.Directory]::CreateDirectory((Join-Path $namRoot $namDirectory)) | Out-Null
}
$env:DOTNET_CLI_HOME = Join-Path $namRoot 'cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:UseSharedCompilation = 'false'
$env:ContinuousIntegrationBuild = 'true'
$env:UseArtifactsOutput = 'true'
$env:ArtifactsPath = Join-Path $namRoot 'build'
$env:NUGET_PACKAGES = Join-Path $namRoot 'restore-cache'
$env:NAM_RUN_VOXEL_DEMO = '1'
$env:NAM_RETAIN_PACKAGE_EVIDENCE = '1'
$env:POWERSHELL_TELEMETRY_OPTOUT = '1'
$env:TMPDIR = Join-Path $namRoot 'tmp'
$env:TMP = $env:TMPDIR
$env:TEMP = $env:TMPDIR
$namCommands = [Collections.Generic.List[object]]::new()

function Invoke-NamCommand {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments, [int]$DeadlineSeconds = 900)
    $namLog = Join-Path $namRoot "logs/$Name"
    if (Test-Path -LiteralPath "$namLog.command.json") { throw 'An existing command boundary must not be overwritten.' }
    $namStart = [Diagnostics.ProcessStartInfo]::new($Executable)
    $namStart.WorkingDirectory = $namSource
    $namStart.UseShellExecute = $false
    $namStart.RedirectStandardOutput = $true
    $namStart.RedirectStandardError = $true
    foreach ($namArgument in $Arguments) { $namStart.ArgumentList.Add($namArgument) }
    $namProcess = [Diagnostics.Process]::new()
    $namProcess.StartInfo = $namStart
    $namStarted = [DateTimeOffset]::UtcNow
    $namExit = $null
    $namTimedOut = $false
    $namOutput = ''
    $namError = ''
    try {
        if (-not $namProcess.Start()) { throw 'Child process did not start.' }
        $namStdout = $namProcess.StandardOutput.ReadToEndAsync()
        $namStderr = $namProcess.StandardError.ReadToEndAsync()
        if (-not $namProcess.WaitForExit($DeadlineSeconds * 1000)) {
            $namTimedOut = $true
            $namProcess.Kill($true)
            $namProcess.WaitForExit()
        }
        $namOutput = $namStdout.GetAwaiter().GetResult()
        $namError = $namStderr.GetAwaiter().GetResult()
        $namExit = $namProcess.ExitCode
    } catch {
        $namError += $_.ToString()
        throw
    } finally {
        $namRecord = [pscustomobject]@{ Name = $Name; Executable = $Executable; Arguments = $Arguments;
            WorkingDirectory = $namSource; StartedAt = $namStarted; EndedAt = [DateTimeOffset]::UtcNow;
            ObservedExitCode = $namExit; ExpectedExitCode = 0; TimedOut = $namTimedOut; DeadlineSeconds = $DeadlineSeconds }
        $namRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$namLog.command.json" -Encoding utf8
        [IO.File]::WriteAllText("$namLog.stdout.log", $namOutput)
        [IO.File]::WriteAllText("$namLog.stderr.log", $namError)
        $namCommands.Add($namRecord)
        $namProcess.Dispose()
    }
    Write-Output "${Name}: observed exit $namExit, timed out $namTimedOut"
    if ($namTimedOut -or $namExit -ne 0) {
        Write-Output $namOutput
        [Console]::Error.WriteLine($namError)
        throw "Required command $Name did not pass. Full streams are retained."
    }
}

function Assert-NamTrx {
    param([string]$Path)
    [xml]$namTrx = Get-Content -LiteralPath $Path -Raw
    $namCounters = $namTrx.TestRun.ResultSummary.Counters
    $namResults = @($namTrx.TestRun.Results.UnitTestResult)
    if ($namResults.Count -le 0 -or [int]$namCounters.total -ne $namResults.Count -or
        [int]$namCounters.passed -ne $namResults.Count -or [int]$namCounters.failed -ne 0 -or
        [int]$namCounters.notExecuted -ne 0 -or @($namResults | Where-Object outcome -ne 'Passed').Count -ne 0) {
        throw "Actual TRX counters/outcomes failed reconciliation: $Path"
    }
    foreach ($namPowerShellCase in @('WrapperTimeoutValidationRejectsUnsafeBounds', 'WrapperTimeoutValidationReportsPlainErrorsForInvalidPlans')) {
        if ([IO.Path]::GetFileName($Path) -eq 'non-package.trx' -and
            @($namResults | Where-Object { $_.testName.EndsWith($namPowerShellCase, [StringComparison]::Ordinal) -and $_.outcome -eq 'Passed' }).Count -ne 1) {
            throw 'Actual required PowerShell coverage did not execute and pass.'
        }
    }
}

$namPassed = $false
try {
    if ($PSVersionTable.PSVersion.ToString() -ne '7.6.6') { throw 'Exact pinned PowerShell is required.' }
    if ([Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier -ne $ExpectedRid -or
        -not [string]::Equals([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString(), $ExpectedRid.Split('-')[1], [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Actual native process RID/architecture does not match the requested cell.'
    }
    $namHead = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $namHead -ne $ExpectedCommit) { throw 'Checkout does not equal the exact requested source.' }
    $namDirty = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $namDirty.Count -ne 0) { throw 'Validation requires a clean source checkout.' }
    $namSourceIdentities = @(Get-NamSourceInputIdentities -SourceRoot $namSource -RepositoryRoot $namSource -Commit $namHead)
    ConvertTo-Json -InputObject $namSourceIdentities -Depth 4 |
        Set-Content -LiteralPath (Join-Path $namRoot 'source-input-identities.json') -Encoding utf8
    [pscustomobject]@{ Commit = $namHead; Tree = (& git rev-parse 'HEAD^{tree}').Trim(); RequestedRid = $ExpectedRid;
        Framework = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription;
        OS = [Runtime.InteropServices.RuntimeInformation]::OSDescription;
        Architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString();
        PowerShell = $PSVersionTable.PSVersion.ToString(); ProcessorCount = [Environment]::ProcessorCount;
        ImageOS = $env:ImageOS; ImageVersion = $env:ImageVersion; RunnerOS = $env:RUNNER_OS; RunnerArch = $env:RUNNER_ARCH;
        RunID = $env:GITHUB_RUN_ID; RunAttempt = $env:GITHUB_RUN_ATTEMPT } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $namRoot 'native-identity.json') -Encoding utf8
    if ((& dotnet --version).Trim() -ne '10.0.302' -or $LASTEXITCODE -ne 0) { throw 'Exact canonical SDK is required.' }
    $namRuntimes = @(& dotnet --list-runtimes)
    if ($LASTEXITCODE -ne 0) { throw 'Actual runtime enumeration failed.' }
    Assert-NamRuntimeInventory -ExpectedRid $ExpectedRid -Runtimes $namRuntimes
    Invoke-NamCommand source-archive git @('archive', '--format=tar.gz', "--output=$(Join-Path $namRoot 'source.tar.gz')", $namHead)
    Invoke-NamCommand dotnet-identity dotnet @('--info')
    if ([OperatingSystem]::IsWindows()) {
        $namVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (-not (Test-Path -LiteralPath $namVswhere)) { throw 'Actual Windows native compiler discovery tool is missing.' }
        Invoke-NamCommand windows-native-toolchain $namVswhere @('-all', '-products', '*', '-format', 'json', '-utf8')
        Get-CimInstance Win32_Processor | Select-Object Name, Architecture, NumberOfCores, NumberOfLogicalProcessors |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $namRoot 'processor-identity.json') -Encoding utf8
    } else {
        Invoke-NamCommand linux-native-toolchain clang @('--version')
        [IO.File]::Copy('/proc/cpuinfo', (Join-Path $namRoot 'processor-identity.txt'))
        [IO.File]::Copy('/etc/os-release', (Join-Path $namRoot 'os-release.txt'))
    }
    $namSolution = 'Supprocom.NativeAllocationManagement.slnx'
    $namTests = 'Supprocom.NativeAllocationManagement.Tests/Supprocom.NativeAllocationManagement.Tests.csproj'
    Invoke-NamCommand restore dotnet @('restore', $namSolution, '--force', '--no-cache', '--nologo', '-v', 'minimal')
    Invoke-NamCommand solution-build dotnet @('build', $namSolution, '-c', 'Release', '--no-restore', '--nologo', '-v', 'minimal', '--disable-build-servers')
    Invoke-NamCommand explicit-test-graph dotnet @('build', $namTests, '-c', 'Release', '--no-restore', '--nologo', '-v', 'minimal', '--disable-build-servers')
    Invoke-NamCommand unchanged-format dotnet @('format', 'whitespace', $namSolution, '--no-restore', '--verify-no-changes', '--verbosity', 'diagnostic')
    $namPerformanceWorker = Join-Path $env:ArtifactsPath 'bin/Supprocom.NativeAllocationManagement.Performance/release/Supprocom.NativeAllocationManagement.Performance.dll'
    Invoke-NamCommand allocator-jit pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'capture-allocator-jit.ps1'),
        '-ExpectedRid', $ExpectedRid, '-Worker', $namPerformanceWorker, '-EvidenceRoot', (Join-Path $namRoot 'allocator-jit'))
    Write-Output ([IO.File]::ReadAllText((Join-Path $namRoot 'logs/allocator-jit.stdout.log')))
    Invoke-NamCommand package-consumers dotnet @('test', $namTests, '-c', 'Release', '--no-build', '--no-restore', '--nologo', '-v', 'normal', '--filter', 'FullyQualifiedName~PackageSmokeTests', '--logger', 'trx;LogFileName=package.trx', '--results-directory', (Join-Path $namRoot 'trx'), '--disable-build-servers') -DeadlineSeconds 2400
    Assert-NamTrx (Join-Path $namRoot 'trx/package.trx')
    Invoke-NamCommand non-package-tests dotnet @('test', $namTests, '-c', 'Release', '--no-build', '--no-restore', '--nologo', '-v', 'normal', '--filter', 'FullyQualifiedName!~PackageSmokeTests', '--logger', 'trx;LogFileName=non-package.trx', '--results-directory', (Join-Path $namRoot 'trx'), '--disable-build-servers') -DeadlineSeconds 1800
    Assert-NamTrx (Join-Path $namRoot 'trx/non-package.trx')
    Invoke-NamCommand allocator-raw-evidence pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'assert-allocator-evidence.ps1'),
        '-EvidenceRoot', $namRoot, '-Trx', (Join-Path $namRoot 'trx/non-package.trx'),
        '-Worker', (Join-Path $env:ArtifactsPath 'bin/Supprocom.NativeAllocationManagement.Tests/release/Supprocom.NativeAllocationManagement.Performance.dll'),
        '-Runtime', (Join-Path $env:ArtifactsPath 'bin/Supprocom.NativeAllocationManagement.Tests/release/Supprocom.NativeAllocationManagement.dll'))
    if (@(& git status --porcelain).Count -ne 0) { throw 'A producer mutated the exact source checkout.' }
    $namPassed = $true
} finally {
    ConvertTo-Json -InputObject $namCommands.ToArray() -Depth 5 | Set-Content -LiteralPath (Join-Path $namRoot 'commands.json') -Encoding utf8
    [pscustomobject]@{ Passed = $namPassed; RequiredRid = $ExpectedRid; ExactRequestedCommit = $ExpectedCommit;
        CompletedAt = [DateTimeOffset]::UtcNow } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $namRoot 'disposition.json') -Encoding utf8
    $namManifest = Join-Path $namRoot 'MANIFEST.tsv'
    if (Test-Path -LiteralPath $namManifest) { throw 'An existing evidence manifest must not be overwritten.' }
    $namEntries = [Collections.Generic.List[string]]::new()
    foreach ($namFile in Get-ChildItem -LiteralPath $namRoot -Force -Recurse | Sort-Object FullName) {
        if (($namFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Evidence payload must not contain file links.' }
        if ($namFile.PSIsContainer) { continue }
        $namRelative = [IO.Path]::GetRelativePath($namRoot, $namFile.FullName).Replace('\', '/')
        if ($namRelative -match '[\r\n\t]') { throw 'Unsafe evidence-manifest path.' }
        $namEntries.Add("$namRelative`t$($namFile.Length)`t$((Get-FileHash -LiteralPath $namFile.FullName -Algorithm SHA256).Hash)")
    }
    [IO.File]::WriteAllLines($namManifest, $namEntries)
    "$(Get-FileHash -LiteralPath $namManifest -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  MANIFEST.tsv" |
        Set-Content -LiteralPath (Join-Path $namRoot 'MANIFEST.sha256') -Encoding utf8
}
