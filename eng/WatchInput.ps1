function Invoke-WatchInputEdit([scriptblock] $Operation) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $retries = 0
    while ($true) {
        try {
            $null = & $Operation
            if ($retries -gt 0) { Write-Host "Retried Windows input edit $retries time(s) after a sharing violation." }
            return $retries
        } catch [IO.IOException] {
            $failure = $_.Exception.GetBaseException()
            if (!$IsWindows -or ($failure.HResult -band 0xffff) -notin @(32, 33) -or
                $timer.Elapsed.TotalSeconds -ge 5) { throw }
            $retries++
            Start-Sleep -Milliseconds 25
        }
    }
}
