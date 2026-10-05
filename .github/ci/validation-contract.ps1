function Assert-NamRuntimeInventory {
    param([Parameter(Mandatory)][string]$ExpectedRid, [Parameter(Mandatory)][string[]]$Runtimes)
    $namFrameworks = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')
    if ($ExpectedRid.StartsWith('win-', [StringComparison]::Ordinal)) { $namFrameworks += 'Microsoft.WindowsDesktop.App' }
    if ($Runtimes.Count -ne $namFrameworks.Count) { throw 'Unexpected framework inventory in the isolated canonical SDK.' }
    foreach ($namFramework in $namFrameworks) {
        $namPattern = '^' + [regex]::Escape($namFramework) + ' 10\.0\.10 \['
        if (@($Runtimes | Where-Object { $_ -match $namPattern }).Count -ne 1) {
            throw "Missing or noncanonical isolated runtime: $namFramework"
        }
    }
}

function Get-NamSourceInputIdentities {
    param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit)
    $namTracked = @(& git -C $RepositoryRoot ls-tree -r $Commit)
    if ($LASTEXITCODE -ne 0 -or $namTracked.Count -eq 0) { throw 'Exact committed compiler inputs could not be enumerated.' }
    foreach ($namTrackedEntry in $namTracked) {
        if ($namTrackedEntry -notmatch '^(100644|100755) blob ([0-9a-f]{40})\t([^\r\n]+)$') { throw 'Unsupported committed input identity.' }
        $namMode, $namBlob, $namPath = $Matches[1], $Matches[2], $Matches[3]
        $namSourcePath = Join-Path $SourceRoot $namPath
        $namActualBlob = (& git -C $RepositoryRoot hash-object --no-filters -- $namSourcePath).Trim()
        if ($LASTEXITCODE -ne 0 -or $namActualBlob -ne $namBlob) { throw "Checkout bytes differ from the archived source: $namPath" }
        $namSourceFile = Get-Item -LiteralPath $namSourcePath -Force
        if (($namSourceFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'A committed compiler input is a link.' }
        [pscustomobject]@{ Path = $namPath; Mode = $namMode; GitBlob = $namBlob;
            Length = $namSourceFile.Length; SHA256 = (Get-FileHash -LiteralPath $namSourcePath -Algorithm SHA256).Hash }
    }
}
