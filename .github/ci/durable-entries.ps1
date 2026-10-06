function Get-NamDurableEntries {
    param([Parameter(Mandatory)][string]$EvidenceRoot,
        [Parameter(Mandatory)][ValidatePattern('^[a-z-]+\.json$')][string]$ObservationName)
    $namRoot = [IO.Path]::GetFullPath($EvidenceRoot)
    $namObservation = Join-Path $namRoot $ObservationName
    if (Test-Path -LiteralPath $namObservation) { throw 'An existing endpoint observation must not be overwritten.' }
    $namEntries = [Collections.Generic.List[object]]::new()
    $namEndpoints = [Collections.Generic.List[object]]::new()
    foreach ($namEntry in Get-ChildItem -LiteralPath $namRoot -Force -Recurse | Sort-Object FullName) {
        if (($namEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Durable payloads must not contain links.' }
        if ([OperatingSystem]::IsWindows() -or $namEntry.PSIsContainer -or $namEntry.UnixStat.ItemType.ToString() -ceq 'File') {
            $namEntries.Add($namEntry)
            continue
        }
        $namRelative = [IO.Path]::GetRelativePath($namRoot, $namEntry.FullName).Replace('\', '/')
        $namMatch = [regex]::Match($namRelative, '^tmp/clr-debug-pipe-([1-9][0-9]*)-([0-9]+)-(in|out)$')
        $namProcessId = 0
        if ($namEntry.UnixStat.ItemType.ToString() -cne 'NamedPipe' -or -not $namMatch.Success -or
            -not [int]::TryParse($namMatch.Groups[1].Value, [ref]$namProcessId)) {
            throw "Unexpected special evidence entry: $namRelative"
        }
        $namProcess = $null
        try { $namProcess = [Diagnostics.Process]::GetProcessById($namProcessId) }
        catch [ArgumentException] { }
        if ($null -ne $namProcess) {
            $namProcess.Dispose()
            throw "A runtime endpoint still has an active process: $namRelative"
        }
        $namEndpoints.Add([pscustomobject]@{ Path = $namRelative; ItemType = $namEntry.UnixStat.ItemType.ToString();
            ProcessId = $namProcessId; ProcessPresent = $false; RuntimeStartIdentifier = $namMatch.Groups[2].Value;
            UnixMode = $namEntry.UnixMode; Inode = $namEntry.UnixStat.Inode; UserId = $namEntry.UnixStat.UserId;
            GroupId = $namEntry.UnixStat.GroupId; ObservedAt = [DateTimeOffset]::UtcNow })
    }
    [pscustomobject]@{ ObservedAt = [DateTimeOffset]::UtcNow; ExcludedEndpoints = $namEndpoints.ToArray();
        Scope = 'Only exact abandoned runtime protocol endpoints are excluded from durable file hashing/archive data. They have no durable content digest. No endpoint was removed.' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $namObservation -Encoding utf8
    $namEntries.Add((Get-Item -LiteralPath $namObservation -Force))
    $namEntries.ToArray()
}
