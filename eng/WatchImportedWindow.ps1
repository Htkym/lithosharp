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
function ConvertTo-WatchBarrier($Value) {
    $generation = Get-WatchNonnegative (Get-WatchField $Value 'generation')
    $watch = ConvertTo-WatchWitness (Get-WatchField $Value 'watch')
    if ((Get-WatchField $Value 'schemaVersion') -cne '1.0' -or
        (Get-WatchField $Value 'scope') -cne 'admitted-inputs-only' -or
        (Get-WatchField $Value 'success') -isnot [bool] -or !(Get-WatchField $Value 'success') -or
        $null -eq $generation -or $generation -lt 1 -or $null -eq $watch) { return $null }
    return [ordered]@{ generation = $generation; watch = $watch }
}
function Get-WatchImportedWindow($Barrier, [object[]] $Terminals, [long] $ObservedGeneration,
    [bool] $ContentMatched, [bool] $StateMatched, [switch] $Warmup) {
    $result = [ordered]@{ status = 'INVALID'; reason = 'OWNER_BARRIER_MISSING'; normalReuseProven = $false }
    $acknowledged = ConvertTo-WatchBarrier $Barrier
    if ($null -eq $acknowledged) { return $result }
    $baseline = $acknowledged.generation; $baseWatch = $acknowledged.watch
    $result.reason = 'TERMINAL_WINDOW_INVALID'
    if ($ObservedGeneration -le $baseline) { $result.status = 'PENDING'; return $result }
    $window = @($Terminals | Where-Object { $_.generation -gt $baseline -and $_.generation -le $ObservedGeneration })
    if ($window.Count -ne $ObservedGeneration - $baseline) { return $result }
    $hostId = $baseWatch.hostId; $revision = $baseWatch.inputRevision; $witness = $null; $recovery = $false
    $workerStarted = $false
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
            if ($recovery) { $result.reason = 'REPEATED_WATCHER_RECOVERY'; return $result }
            $coldRecovery = !$hit -and $workers -eq 1 -and $rendered -ge 1
            $cachedRecovery = $hit -and $workers -eq 0 -and $rendered -eq 0 -and $compiled -eq 0
            if ($watch.hostId -ceq $hostId -or !($coldRecovery -or $cachedRecovery)) { return $result }
            $recovery = $true
            $workerStarted = $workers -eq 1; $witness = $null
        } else {
            $coldWarmup = $Warmup -and $recovery -and !$workerStarted -and $workers -eq 1 -and $rendered -ge 1 -and !$hit
            if ($watch.restartReasons -ne 0 -or $watch.hostId -cne $hostId -or ($workers -ne 0 -and !$coldWarmup)) {
                $result.reason = 'UNEXPLAINED_RESTART'; return $result
            }
            if ($rendered -ge 1 -and !$hit) { $witness = $event.generation; $workerStarted = $true }
            elseif (!($hit -and $rendered -eq 0 -and $compiled -eq 0)) { return $result }
        }
        $hostId = $watch.hostId; $revision = $watch.inputRevision
    }
    $result.baselineGeneration = $baseline; $result.observedGeneration = $ObservedGeneration
    $result.hostId = $hostId; $result.terminalWindow = $window; $result.witnessGeneration = $witness
    if (!$ContentMatched -or !$StateMatched) { $result.status = 'PENDING'; return $result }
    if ($recovery) {
        if ($Warmup -and $null -ne $witness) { $result.status = 'WARMUP_COMPLETE'; $result.reason = $null; return $result }
        $result.status = 'RECOVERY_OBSERVED_REUSE_UNPROVEN'; $result.reason = 'WATCHER_RECOVERY'; return $result
    }
    if ($null -eq $witness) { $result.status = 'PENDING'; return $result }
    if ($Warmup) { $result.status = 'WARMUP_COMPLETE'; $result.reason = $null; return $result }
    $result.status = 'REUSE_PROVEN'; $result.reason = $null; $result.normalReuseProven = $true
    return $result
}
function Get-WatchImportedHandoff($Barrier, [object[]] $Terminals, $RetryBarrier) {
    $next = ConvertTo-WatchBarrier $RetryBarrier
    if ($null -eq $next) { return [ordered]@{ status = 'INVALID'; reason = 'OWNER_BARRIER_MISSING'; normalReuseProven = $false } }
    $result = Get-WatchImportedWindow $Barrier $Terminals $next.generation $true $true -Warmup
    if ($result.status -cne 'WARMUP_COMPLETE') { return $result }
    $last = ConvertTo-WatchWitness $result.terminalWindow[-1].watch
    foreach ($key in @('ownerId', 'hostId', 'previousHostId', 'inputRevision', 'restartReasons')) {
        if ($last[$key] -cne $next.watch[$key]) {
            $result.status = 'INVALID'; $result.reason = 'HANDOFF_BARRIER_MISMATCH'; return $result
        }
    }
    return $result
}
