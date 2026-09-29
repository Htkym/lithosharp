[CmdletBinding()]
param(
    [string] $ExtensionDirectory,
    [string] $Output,
    [switch] $SkipPack
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ExtensionDirectory) { $ExtensionDirectory = Join-Path $repo 'extensions/lithosharp-vscode' }
$ExtensionDirectory = [IO.Path]::GetFullPath($ExtensionDirectory)
if (!$Output) { $Output = Join-Path $repo '.tmp/vsix-check' }
$Output = [IO.Path]::GetFullPath($Output)

$package = Get-Content -LiteralPath (Join-Path $ExtensionDirectory 'package.json') -Raw | ConvertFrom-Json
$expectedVersion = [string]$package.version
if (!$SkipPack) {
    # Resolve through PATH: bare names fail under constrained hosts. Prefer
    # .cmd shims on Windows: .ps1 shims cannot start without a shell.
    function Get-NativeTool([string] $Name) {
        foreach ($candidate in @(Get-Command "$Name.cmd" -ErrorAction SilentlyContinue) + @(Get-Command $Name -ErrorAction SilentlyContinue)) {
            if ($candidate -and $candidate.Source -notlike '*.ps1') {
                return $candidate.Source
            }
        }
        throw "Required tool '$Name' is not on PATH."
    }
    $npmPath = Get-NativeTool 'npm'
    $nodePath = Get-NativeTool 'node'
    $npxPath = Get-NativeTool 'npx'
    foreach ($step in @(
        @{ exe = $npmPath; args = @('run', 'compile') },
        @{ exe = $nodePath; args = @('eng/stage-worker.mjs') }
    )) {
        $info = [Diagnostics.ProcessStartInfo]::new($step.exe)
        $info.WorkingDirectory = $ExtensionDirectory
        $info.UseShellExecute = $false
        foreach ($argument in $step.args) { $info.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($info)
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "VSIX preparation failed: $($step.exe) $($step.args -join ' ')." }
        $process.Dispose()
    }
    $vsix = Join-Path $Output "lithosharp-$expectedVersion.vsix"
    $null = New-Item -ItemType Directory -Force -Path $Output
    if (Test-Path -LiteralPath $vsix) { Remove-Item -LiteralPath $vsix -Force }
    $pack = [Diagnostics.ProcessStartInfo]::new($npxPath)
    $pack.WorkingDirectory = $ExtensionDirectory
    $pack.UseShellExecute = $false
    foreach ($argument in @('--yes', '@vscode/vsce', 'package', '--no-update-package-json', '--out', $vsix)) { $pack.ArgumentList.Add($argument) }
    $packing = [Diagnostics.Process]::Start($pack)
    $null = $packing.WaitForExit(600000)
    if (!$packing.HasExited) { $packing.Kill($true); throw 'vsce packaging timed out.' }
    if ($packing.ExitCode -ne 0) { throw "vsce packaging failed with exit $($packing.ExitCode)." }
    $packing.Dispose()
}
else {
    $vsix = Get-ChildItem -LiteralPath $Output -Filter '*.vsix' | Select-Object -First 1 -ExpandProperty FullName
    if (!$vsix) { throw "No VSIX found in $Output with -SkipPack." }
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($vsix)
try {
    $entries = @($archive.Entries | ForEach-Object FullName | Sort-Object)
}
finally { $archive.Dispose() }

function Fail([string] $Message) { throw "VSIX content check failed: $Message" }

# Version alignment: the file name and manifest must carry package.json's version.
if ([IO.Path]::GetFileNameWithoutExtension($vsix) -notmatch [regex]::Escape($expectedVersion)) {
    Fail "VSIX file name does not carry version $expectedVersion."
}

# Forbidden content: development state, secrets, credentials, logs, restored
# dependencies, fixtures and platform-native binaries must never ship.
$forbidden = @(
    '(^|/)node_modules(/|$)', '(^|/)\.git(/|$)', '(^|/)\.vscode-test(/|$)',
    '(^|/)\.local(/|$)', '(^|/)tests?(/|$)', '(^|/)eng(/|$)',
    '\.env$', '\.pem$', '\.pfx$', '\.key$', '(^|/)credentials', '(^|/)secrets',
    '\.(node|dll|so|dylib)$', '(^|/)\.vs($|/)'
)
foreach ($pattern in $forbidden) {
    $hit = @($entries | Where-Object { $_ -match $pattern })
    if ($hit.Count -gt 0) { Fail "forbidden entry '$($hit[0])' matches '$pattern'." }
}

# Required runtime: compiled shell, manifest, docs and the worker source that
# the restore command installs from (lockfile included, node_modules excluded).
foreach ($required in @(
    'extension/package.json', 'extension/out/src/extension.js',
    'extension/resources/worker/worker.mjs', 'extension/resources/worker/compiler.mjs',
    'extension/resources/worker/package.json', 'extension/resources/worker/package-lock.json',
    'extension/resources/worker/worker.json', 'extension/resources/worker/runtime/components.mjs'
)) {
    if ($entries -notcontains $required) { Fail "missing required entry '$required'." }
}

# No placeholder publisher: an unpublished package must not claim an identity.
$manifestEntry = $entries | Where-Object { $_ -like 'extension/package.json' }
$zip = [System.IO.Compression.ZipFile]::OpenRead($vsix)
try {
    $entry = $zip.Entries | Where-Object { $_.FullName -eq 'extension/package.json' } | Select-Object -First 1
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { $packaged = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
}
finally { $zip.Dispose() }
if ([string]$packaged.version -cne $expectedVersion) { Fail "packaged version '$($packaged.version)' mismatches '$expectedVersion'." }
if ($packaged.PSObject.Properties['publisher']) { Fail 'packaged manifest must not claim a publisher before the account exists.' }
if ($packaged.main -cne './out/src/extension.js') { Fail "packaged main '$($packaged.main)' is not the compiled entry point." }

$report = [ordered]@{
    schemaVersion = '1.0'
    vsix = [IO.Path]::GetFileName($vsix)
    version = $expectedVersion
    entryCount = $entries.Count
    bytes = (Get-Item -LiteralPath $vsix).Length
    sha256 = (Get-FileHash -LiteralPath $vsix -Algorithm SHA256).Hash.ToLowerInvariant()
    entries = @($entries)
}
$reportPath = Join-Path $Output 'vsix-contents.json'
[IO.File]::WriteAllText($reportPath, (($report | ConvertTo-Json -Depth 6)))
Write-Host ("VSIX contents passed: {0} entries, {1} bytes, sha256={2}." -f $entries.Count, $report.bytes, $report.sha256)
