[CmdletBinding()]
param(
    [string] $Output,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Output) { $Output = Join-Path $repo '.tmp/lsp-distribution' }
$Output = [IO.Path]::GetFullPath($Output)

function Fail([string] $Message) { throw "LSP distribution check failed: $Message" }

$publishDir = Join-Path $Output 'server'
if (!(Test-Path -LiteralPath $publishDir) -or !$SkipPublish) {
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.WorkingDirectory = $repo
    $info.UseShellExecute = $false
    foreach ($argument in @('publish', 'src/LithoSharp.LanguageServer/LithoSharp.LanguageServer.csproj', '-c', 'Release', '-o', $publishDir)) {
        $info.ArgumentList.Add($argument)
    }
    $publish = [Diagnostics.Process]::Start($info)
    $publish.WaitForExit()
    if ($publish.ExitCode -ne 0) { Fail "dotnet publish failed with exit $($publish.ExitCode)." }
    $publish.Dispose()
}

$dll = Join-Path $publishDir 'LithoSharp.LanguageServer.dll'
foreach ($required in @(
    'LithoSharp.LanguageServer.dll',
    'LithoSharp.LanguageServer.deps.json',
    'LithoSharp.LanguageServer.runtimeconfig.json',
    'LithoSharp.dll',
    'LithoSharp.Mdx.dll',
    'worker/worker.mjs',
    'worker/compiler.mjs',
    'worker/package.json',
    'worker/package-lock.json',
    'worker/runtime/components.mjs'
)) {
    if (!(Test-Path -LiteralPath (Join-Path $publishDir $required) -PathType Leaf)) {
        Fail "published server is missing '$required'."
    }
}
if (Test-Path -LiteralPath (Join-Path $publishDir 'worker/node_modules')) {
    Fail 'published worker must not bundle restored node_modules.'
}
# Framework-dependent only: no trimming or single-file markers in the project.
$csproj = Get-Content -LiteralPath (Join-Path $repo 'src/LithoSharp.LanguageServer/LithoSharp.LanguageServer.csproj') -Raw
foreach ($marker in @('PublishTrimmed', 'PublishSingleFile', 'PublishAot')) {
    if ($csproj -match "<$marker>true</$marker>") { Fail "server project enables $marker." }
}
if (!(Test-Path -LiteralPath (Join-Path $publishDir 'runtimes'))) {
    Fail 'published server is missing native runtime assets.'
}

$files = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})

function New-LspSession([string] $ServerDll, [hashtable] $Environment, [string] $WorkerDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($ServerDll)
    foreach ($entry in $Environment.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
    $process = [Diagnostics.Process]::Start($start)
    # Stderr is drained synchronously at stop: async handlers run without a
    # PowerShell runspace on threadpool threads and crash the host.
    return [ordered]@{
        process = $process
        stderrTail = ''
        workerDirectory = $WorkerDirectory
        sequence = 1
        buffer = [Collections.Generic.List[byte]]::new()
    }
}

function Send-Lsp($Session, $Message) {
    $json = ($Message | ConvertTo-Json -Depth 12 -Compress)
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $header = [Text.Encoding]::ASCII.GetBytes("Content-Length: $($bytes.Length)`r`n`r`n")
    $stdin = $Session.process.StandardInput.BaseStream
    $stdin.Write($header, 0, $header.Length)
    $stdin.Write($bytes, 0, $bytes.Length)
    $stdin.Flush()
}

function Read-StreamBytes($Stream, [int] $Count, [DateTime] $Deadline, [Collections.Generic.List[byte]] $Sink) {
    # BaseStream disallows ReadTimeout; async reads with an explicit wait bound it instead.
    # ReadAsync returns as soon as any bytes arrive: only the read prefix is valid.
    # Bytes go straight into the sink because PowerShell enumerates function
    # return values, which would shred a byte array into boxed elements.
    $chunk = New-Object byte[] $Count
    $offset = 0
    while ($offset -lt $Count) {
        $remaining = [int]($Deadline - [DateTime]::UtcNow).TotalMilliseconds
        if ($remaining -le 0) { throw 'Timed out waiting for LSP message bytes.' }
        $task = $Stream.ReadAsync($chunk, $offset, $Count - $offset)
        if (!$task.Wait($remaining)) { throw 'Timed out waiting for LSP message bytes.' }
        if ($task.Result -eq 0) {
            if ($offset -eq 0) { throw 'LSP server closed stdout.' }
            break
        }
        $offset += $task.Result
        if ($offset -ge 4) { break }
    }
    for ($index = 0; $index -lt $offset; $index++) {
        $null = $Sink.Add($chunk[$index])
    }
}

function Read-LspMessage($Session, [int] $TimeoutMs) {
    $stream = $Session.process.StandardOutput.BaseStream
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    for (;;) {
        if ($Session.buffer.Count -ge 4) {
            $text = [Text.Encoding]::ASCII.GetString($Session.buffer.ToArray())
            $end = $text.IndexOf("`r`n`r`n")
            if ($end -ge 0) {
                $header = $text.Substring(0, $end)
                $length = [int]([regex]::Match($header, 'Content-Length:\s*(\d+)').Groups[1].Value)
                $total = $end + 4 + $length
                while ($Session.buffer.Count -lt $total) {
                    $null = Read-StreamBytes $stream ($total - $Session.buffer.Count) $deadline $Session.buffer
                }
                $body = $Session.buffer.GetRange($end + 4, $length).ToArray()
                $Session.buffer.RemoveRange(0, $total)
                return ([Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json -AsHashtable)
            }
        }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Timed out waiting for LSP message header.' }
        $null = Read-StreamBytes $stream 4096 $deadline $Session.buffer
    }
}

function Wait-Publish($Session, [string] $Uri, [int] $TimeoutMs) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    for (;;) {
        $remaining = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
        if ($remaining -le 0) { throw "Timed out waiting for diagnostics of $Uri." }
        $message = Read-LspMessage $Session $remaining
        if ($message.method -ceq 'textDocument/publishDiagnostics' -and $message.params.uri -ceq $Uri) {
            return $message.params
        }
    }
}

function Stop-Lsp($Session) {
    try {
        $id = "shutdown-$($Session.sequence)"
        $Session.sequence++
        Send-Lsp $Session @{ jsonrpc = '2.0'; id = $id; method = 'shutdown'; params = $null }
        $null = Read-LspMessage $Session 10000
        Send-Lsp $Session @{ jsonrpc = '2.0'; method = 'exit'; params = $null }
        if (!$Session.process.WaitForExit(10000)) { $Session.process.Kill($true) }
    }
    catch { try { $Session.process.Kill($true) } catch { } }
    finally {
        try { $Session.stderrTail = $Session.process.StandardError.ReadToEnd() } catch { }
        $Session.process.Dispose()
    }
}

function Invoke-Smoke([string] $Name, [hashtable] $Environment, [string] $WorkerDirectory, [scriptblock] $Probe) {
    $session = New-LspSession $dll $Environment $WorkerDirectory
    try {
        $id = "init-$($session.sequence)"
        $session.sequence++
        Send-Lsp $session @{
            jsonrpc = '2.0'; id = $id; method = 'initialize'
            params = @{
                processId = $null; capabilities = @{}
                initializationOptions = if ($WorkerDirectory) { @{ workerDirectory = $WorkerDirectory } } else { @{} }
            }
        }
        $response = Read-LspMessage $session 60000
        if ($null -eq $response.result -or $null -eq $response.result.capabilities) {
            Fail "$Name`: initialize response has no capabilities."
        }
        if ([string]$response.result.serverInfo.name -cne 'lithosharp') {
            Fail "$Name`: unexpected serverInfo name."
        }
        Send-Lsp $session @{ jsonrpc = '2.0'; method = 'initialized'; params = @{} }
        & $Probe $session
        Write-Host "$Name`: passed."
    }
    finally { Stop-Lsp $session }
}

$markdownDoc = "---`ntitle: Smoke`n---`nSee [^missing] here.`n"
$mdxDoc = "---`ntitle: Smoke`n---`nimport Counter from './Counter.jsx';`n`n<Counter />`n"

# 1. Full stack: Markdown diagnostics arrive with their original IDs.
Invoke-Smoke 'markdown-with-node' @{} (Join-Path $publishDir 'worker') {
    param($session)
    Send-Lsp $session @{ jsonrpc = '2.0'; method = 'textDocument/didOpen'; params = @{
        textDocument = @{ uri = 'file:///smoke/note.md'; languageId = 'markdown'; version = 1; text = $markdownDoc } } }
    $published = Wait-Publish $session 'file:///smoke/note.md' 60000
    if (@($published.diagnostics | Where-Object { $_.code -ceq 'LIT001' }).Count -ne 1) {
        Fail "markdown diagnostics did not carry exactly one LIT001."
    }
}

# 2. Node absent: the server stays alive and explains instead of crashing.
$nodeDir = Split-Path -Parent (Get-Command node -ErrorAction Stop).Source
$pathWithoutNode = ($env:PATH -split ';' | Where-Object { $_ -and $_ -ne $nodeDir }) -join ';'
Invoke-Smoke 'mdx-without-node' @{ PATH = $pathWithoutNode } (Join-Path $publishDir 'worker') {
    param($session)
    Send-Lsp $session @{ jsonrpc = '2.0'; method = 'textDocument/didOpen'; params = @{
        textDocument = @{ uri = 'file:///smoke/widget.mdx'; languageId = 'mdx'; version = 1; text = $mdxDoc } } }
    $published = Wait-Publish $session 'file:///smoke/widget.mdx' 120000
    if (@($published.diagnostics).Count -ne 0) {
        Fail 'MDX without node must publish no fabricated diagnostics.'
    }
    # Still alive: Markdown analysis answers afterwards.
    Send-Lsp $session @{ jsonrpc = '2.0'; method = 'textDocument/didOpen'; params = @{
        textDocument = @{ uri = 'file:///smoke/after.md'; languageId = 'markdown'; version = 1; text = $markdownDoc } } }
    $later = Wait-Publish $session 'file:///smoke/after.md' 60000
    if (@($later.diagnostics | Where-Object { $_.code -ceq 'LIT001' }).Count -ne 1) {
        Fail 'server did not stay usable after node-less MDX analysis.'
    }
}

# 3. Worker dependencies missing: same graceful degradation, Markdown unaffected.
$emptyWorker = Join-Path $Output 'empty-worker'
$null = New-Item -ItemType Directory -Force -Path $emptyWorker
Invoke-Smoke 'mdx-without-worker-deps' @{} $emptyWorker {
    param($session)
    Send-Lsp $session @{ jsonrpc = '2.0'; method = 'textDocument/didOpen'; params = @{
        textDocument = @{ uri = 'file:///smoke/bare.mdx'; languageId = 'mdx'; version = 1; text = $mdxDoc } } }
    $published = Wait-Publish $session 'file:///smoke/bare.mdx' 120000
    if (@($published.diagnostics).Count -ne 0) {
        Fail 'MDX without worker dependencies must publish no fabricated diagnostics.'
    }
    Send-Lsp $session @{ jsonrpc = '2.0'; method = 'textDocument/didOpen'; params = @{
        textDocument = @{ uri = 'file:///smoke/still.md'; languageId = 'markdown'; version = 1; text = $markdownDoc } } }
    $later = Wait-Publish $session 'file:///smoke/still.md' 60000
    if (@($later.diagnostics | Where-Object { $_.code -ceq 'LIT001' }).Count -ne 1) {
        Fail 'Markdown analysis broke after unrestored-worker MDX analysis.'
    }
}

$report = [ordered]@{
    schemaVersion = '1.0'
    publishDirectory = $publishDir
    frameworkDependent = $true
    trimsSingleFileOrAot = $false
    fileCount = $files.Count
    files = @($files)
    smokes = @('markdown-with-node', 'mdx-without-node', 'mdx-without-worker-deps')
}
[IO.File]::WriteAllText((Join-Path $Output 'lsp-distribution.json'), (($report | ConvertTo-Json -Depth 8)))
Write-Host 'LSP distribution passed: framework-dependent layout with graceful Node/worker degradation.'
