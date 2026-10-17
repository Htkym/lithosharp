# Reliable owner/machine witnesses only. Best-effort lifecycle sidecars are never inputs.
function Get-WatchField($Value, [string] $Name) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [Collections.IDictionary]) { return $Value[$Name] }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}
function Get-WatchNonnegative($Value) {
    if (($Value -is [int] -or $Value -is [long]) -and $Value -ge 0) { return $Value }
    return $null
}
function Test-WatchId($Value) { return $Value -is [string] -and $Value -cmatch '^[0-9a-f]{32}$' -and $Value -cne ('0' * 32) }
function ConvertTo-WatchWitness($Value) {
    $owner = Get-WatchField $Value 'ownerId'; $hostId = Get-WatchField $Value 'hostId'
    $previous = Get-WatchField $Value 'previousHostId'
    $revision = Get-WatchNonnegative (Get-WatchField $Value 'inputRevision')
    $reasons = Get-WatchNonnegative (Get-WatchField $Value 'restartReasons')
    if (!(Test-WatchId $owner) -or !(Test-WatchId $hostId) -or
        ($null -ne $previous -and !(Test-WatchId $previous)) -or $null -eq $revision -or $null -eq $reasons -or $reasons -gt 511) { return $null }
    return [ordered]@{ ownerId = $owner; hostId = $hostId; previousHostId = $previous; inputRevision = $revision; restartReasons = $reasons }
}
function Get-WatchImportedWindow($Barrier, [object[]] $Terminals, [long] $ObservedGeneration,
    [bool] $ContentMatched, [bool] $StateMatched) {
    $result = [ordered]@{ status = 'INVALID'; reason = 'OWNER_BARRIER_MISSING'; normalReuseProven = $false }
    $baseline = Get-WatchNonnegative (Get-WatchField $Barrier 'generation')
    $baseWatch = ConvertTo-WatchWitness (Get-WatchField $Barrier 'watch')
    if ((Get-WatchField $Barrier 'schemaVersion') -cne '1.0' -or
        (Get-WatchField $Barrier 'scope') -cne 'admitted-inputs-only' -or
        (Get-WatchField $Barrier 'success') -isnot [bool] -or !(Get-WatchField $Barrier 'success') -or
        $null -eq $baseline -or $baseline -lt 1 -or $null -eq $baseWatch) { return $result }
    $result.reason = 'TERMINAL_WINDOW_INVALID'
    if ($ObservedGeneration -le $baseline) { $result.status = 'PENDING'; return $result }
    $window = @($Terminals | Where-Object { $_.generation -gt $baseline -and $_.generation -le $ObservedGeneration })
    if ($window.Count -ne $ObservedGeneration - $baseline) { return $result }
    $hostId = $baseWatch.hostId; $revision = $baseWatch.inputRevision; $witness = $null; $recovery = $false
    for ($i = 0; $i -lt $window.Count; $i++) {
        $event = $window[$i]; $watch = ConvertTo-WatchWitness (Get-WatchField $event 'watch')
        $work = Get-WatchField $event 'mdxWork'
        if ($event.generation -ne $baseline + $i + 1 -or $event.event -cne 'rebuild-succeeded' -or
            $event.success -isnot [bool] -or !$event.success -or $event.exitCode -cne 0 -or $null -eq $watch -or
            $watch.ownerId -cne $baseWatch.ownerId -or $watch.previousHostId -cne $hostId -or $watch.inputRevision -lt $revision) { return $result }
        $workers = Get-WatchNonnegative (Get-WatchField $work 'workerStarts')
        $rendered = Get-WatchNonnegative (Get-WatchField $work 'renderedPages')
        $compiled = Get-WatchNonnegative (Get-WatchField $work 'compiledModules'); $hit = Get-WatchField $work 'cacheHit'
        if ($null -eq $workers -or $null -eq $rendered -or $null -eq $compiled -or $hit -isnot [bool]) { return $result }
        if ($watch.restartReasons -eq 144) {
            # WatcherError|Other requires conservative host replacement, not ordinary component reuse.
            $coldRecovery = !$hit -and $workers -eq 1 -and $rendered -ge 1
            $cachedRecovery = $hit -and $workers -eq 0 -and $rendered -eq 0 -and $compiled -eq 0
            if ($watch.hostId -ceq $hostId -or !($coldRecovery -or $cachedRecovery)) { return $result }
            $recovery = $true
        } elseif ($watch.restartReasons -ne 0 -or $watch.hostId -cne $hostId -or $workers -ne 0) {
            $result.reason = 'UNEXPLAINED_RESTART'; return $result
        } elseif ($rendered -ge 1 -and !$hit) { $witness = $event.generation }
        elseif (!($hit -and $rendered -eq 0 -and $compiled -eq 0)) { return $result }
        $hostId = $watch.hostId; $revision = $watch.inputRevision
    }
    $result.baselineGeneration = $baseline; $result.observedGeneration = $ObservedGeneration
    $result.hostId = $hostId; $result.terminalWindow = $window; $result.witnessGeneration = $witness
    if (!$ContentMatched -or !$StateMatched) { $result.status = 'PENDING'; return $result }
    if ($recovery) { $result.status = 'RECOVERY_OBSERVED_REUSE_UNPROVEN'; $result.reason = 'WATCHER_RECOVERY'; return $result }
    if ($null -eq $witness) { $result.status = 'PENDING'; return $result }
    $result.status = 'REUSE_PROVEN'; $result.reason = $null; $result.normalReuseProven = $true
    return $result
}
