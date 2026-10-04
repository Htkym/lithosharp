[CmdletBinding()]
param(
    [string] $ExtensionDirectory,
    [string] $Output,
    [string] $VsceCli,
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
    $nodePath = Get-NativeTool 'node'
    foreach ($step in @(
        @{ exe = $nodePath; args = @('node_modules/typescript/bin/tsc', '-p', './') },
        @{ exe = $nodePath; args = @('eng/stage-worker.mjs') },
        @{ exe = $nodePath; args = @('eng/stage-language-server.mjs') }
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
    if ($VsceCli) {
        $packExe = $nodePath
        $packArguments = @([IO.Path]::GetFullPath($VsceCli))
    } else {
        $npxPath = Get-NativeTool 'npx'
        $packExe = $npxPath
        $packArguments = @('--yes', '@vscode/vsce@4.0.0')
        if ([IO.Path]::GetExtension($npxPath) -eq '.cmd') {
            # Run npm's JavaScript entry directly, avoiding cmd argument parsing.
            $npxCli = Join-Path (Split-Path -Parent $npxPath) 'node_modules/npm/bin/npx-cli.js'
            if (!(Test-Path -LiteralPath $npxCli -PathType Leaf)) { throw "npm CLI is missing beside '$npxPath'; fix PATH or supply -VsceCli." }
            $packExe = $nodePath
            $packArguments = @($npxCli) + $packArguments
        }
    }
    $pack = [Diagnostics.ProcessStartInfo]::new($packExe)
    $pack.WorkingDirectory = $ExtensionDirectory
    $pack.UseShellExecute = $false
    if (!$VsceCli) {
        # npm exec locks its cache while preparing the pinned CLI. Give only
        # this child a fresh cache, away from every packaged/uploaded tree.
        function Get-ResolvedDirectoryPath([string] $DirectoryPath, [int] $LinkDepth = 0) {
            if ($LinkDepth -gt 64) { throw 'Too many directory aliases while locating the isolated npm cache.' }
            $fullPath = [IO.Path]::GetFullPath($DirectoryPath)
            $resolved = [IO.Path]::GetPathRoot($fullPath)
            $relative = [IO.Path]::GetRelativePath($resolved, $fullPath)
            if ($relative -eq '.') { return $resolved }
            $parts = $relative.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)
            foreach ($part in $parts) {
                $directory = [IO.DirectoryInfo]::new((Join-Path $resolved $part))
                if (!$directory.Exists) { throw "npm cache containment directory does not exist: $directory" }
                if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    $target = $directory.ResolveLinkTarget($true)
                    if (!$target) { throw "Cannot resolve npm cache containment directory: $directory" }
                    $resolved = Get-ResolvedDirectoryPath $target.FullName ($LinkDepth + 1)
                } else { $resolved = $directory.FullName }
            }
            return $resolved
        }
        $npmTemp = Get-ResolvedDirectoryPath ([IO.Path]::GetTempPath())
        $npmCache = Join-Path $npmTemp ('lithosharp-vsce-npm-' + [guid]::NewGuid().ToString('N'))
        # Conservatively reject differently cased aliases even on Unix volumes.
        $comparison = [StringComparison]::OrdinalIgnoreCase
        foreach ($tree in @($repo, $ExtensionDirectory, $Output)) {
            $tree = [IO.Path]::TrimEndingDirectorySeparator((Get-ResolvedDirectoryPath $tree))
            $treePrefix = if ([IO.Path]::EndsInDirectorySeparator($tree)) { $tree } else { $tree + [IO.Path]::DirectorySeparatorChar }
            if ($npmCache.Equals($tree, $comparison) -or $npmCache.StartsWith($treePrefix, $comparison)) {
                throw 'The temporary npm cache must be outside the repository, extension and VSIX output directories.'
            }
        }
        $null = New-Item -ItemType Directory -Path $npmCache
        # Unix environments can contain several differently cased keys.
        foreach ($cacheVariable in @($pack.Environment.Keys)) {
            if ($cacheVariable.Equals('npm_config_cache', [StringComparison]::OrdinalIgnoreCase)) {
                $null = $pack.Environment.Remove($cacheVariable)
            }
        }
        $pack.Environment['npm_config_cache'] = $npmCache
        Write-Host "Retained isolated VSCE npm cache: $npmCache"
    }
    foreach ($argument in ($packArguments + @('package', '--no-dependencies', '--no-update-package-json', '--out', $vsix))) { $pack.ArgumentList.Add($argument) }
    $packing = [Diagnostics.Process]::Start($pack)
    $null = $packing.WaitForExit(600000)
    if (!$packing.HasExited) { $packing.Kill($true); throw 'vsce packaging timed out.' }
    if ($packing.ExitCode -ne 0) { throw "vsce packaging failed with exit $($packing.ExitCode)." }
    $packing.Dispose()
}
else {
    $packages = @(Get-ChildItem -LiteralPath $Output -Filter '*.vsix' -File)
    if ($packages.Count -ne 1) { throw "Expected exactly one VSIX in $Output with -SkipPack; found $($packages.Count)." }
    $vsix = $packages[0].FullName
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
# dependencies and fixtures must never ship. LSP binaries are allowed only
# when listed in the trusted staged deps.json and identical to the publish output.
$forbidden = @(
    '(^|/)node_modules(/|$)', '(^|/)\.git(/|$)', '(^|/)\.vscode-test(/|$)',
    '(^|/)\.local(/|$)', '(^|/)tests?(/|$)', '(^|/)eng(/|$)',
    '\.env$', '\.pem$', '\.pfx$', '\.key$', '(^|/)credentials', '(^|/)secrets',
    '\.node$', '(^|/)\.vs($|/)',
    '(^|/)(bin|obj|\.cache|cache|\.tmp|\.artifacts|\.aws|\.agents|\.codex)(/|$)',
    '(^|/)\.language-server-(stage|backup)-', '\.(pdb|log|binlog|trx)$',
    '(^|/)\.(npmrc|netrc|env)(\..*)?$', '(^|/)id_(rsa|ed25519)(\.pub)?$'
)
foreach ($pattern in $forbidden) {
    $hit = @($entries | Where-Object { $_ -match $pattern })
    if ($hit.Count -gt 0) { Fail "forbidden entry '$($hit[0])' matches '$pattern'." }
}

# Required runtime: compiled shell, manifest, docs and the worker source that
# the restore command installs from (lockfile included, node_modules excluded).
foreach ($required in @(
    'extension/package.json', 'extension/out/src/extension.js', 'extension/LICENSE.txt',
    'extension/resources/language-server/licenses/YamlDotNet.LICENSE.txt',
    'extension/resources/language-server/licenses/AngleSharp.LICENSE.txt',
    'extension/resources/language-server/licenses/SkiaSharp.LICENSE.txt',
    'extension/resources/language-server/licenses/SkiaSharp.THIRD-PARTY-NOTICES.txt',
    'extension/resources/language-server/licenses/THIRD-PARTY-NOTICES.md',
    'extension/resources/worker/worker.mjs', 'extension/resources/worker/compiler.mjs',
    'extension/resources/worker/package.json', 'extension/resources/worker/package-lock.json',
    'extension/resources/worker/worker.json', 'extension/resources/worker/runtime/components.mjs',
    'extension/resources/language-server/LithoSharp.LanguageServer.dll',
    'extension/resources/language-server/LithoSharp.LanguageServer.deps.json',
    'extension/resources/language-server/LithoSharp.LanguageServer.runtimeconfig.json',
    'extension/resources/language-server/LithoSharp.dll',
    'extension/resources/language-server/LithoSharp.Mdx.dll',
    'extension/resources/language-server/worker/worker.mjs',
    'extension/resources/language-server/worker/compiler.mjs',
    'extension/resources/language-server/worker/package.json',
    'extension/resources/language-server/worker/package-lock.json',
    'extension/resources/language-server/worker/runtime/components.mjs',
    'extension/resources/language-server/runtimes/win-x64/native/libSkiaSharp.dll',
    'extension/resources/language-server/runtimes/linux-x64/native/libSkiaSharp.so',
    'extension/resources/language-server/runtimes/osx/native/libSkiaSharp.dylib'
)) {
    if ($entries -notcontains $required) { Fail "missing required entry '$required'." }
}

# Reject traversal/ambiguous archive entries before extracting executable payload.
foreach ($entryName in $entries) {
    if ($entryName -match '[:\\]' -or $entryName.StartsWith('/') -or '..' -in $entryName.Split('/')) {
        Fail "unsafe archive path '$entryName'."
    }
}
if (@($entries | Group-Object | Where-Object Count -gt 1).Count -ne 0) { Fail 'archive contains duplicate paths.' }

$serverPrefix = 'extension/resources/language-server/'
$stagedServer = Join-Path $ExtensionDirectory 'resources/language-server'
$depsFile = Join-Path $stagedServer 'LithoSharp.LanguageServer.deps.json'
if (!(Test-Path -LiteralPath $depsFile -PathType Leaf)) { Fail 'stage the current language server before validating a VSIX.' }
$deps = Get-Content -LiteralPath $depsFile -Raw | ConvertFrom-Json
$allowedBinaries = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($target in $deps.targets.PSObject.Properties) {
    foreach ($library in $target.Value.PSObject.Properties) {
        foreach ($groupName in @('runtime', 'native', 'resources', 'runtimeTargets')) {
            $group = $library.Value.PSObject.Properties[$groupName]
            if ($group) {
                foreach ($asset in $group.Value.PSObject.Properties) {
                    # Portable publish flattens managed lib/<tfm> paths. RID
                    # runtimeTargets keep their runtimes/<rid>/... directory.
                    $assetPath = if ($groupName -eq 'runtime' -or $groupName -eq 'native') {
                        [IO.Path]::GetFileName($asset.Name)
                    } elseif ($groupName -eq 'resources') {
                        $asset.Value.locale + '/' + [IO.Path]::GetFileName($asset.Name)
                    } else { $asset.Name }
                    $null = $allowedBinaries.Add($serverPrefix + $assetPath)
                }
            }
        }
    }
}
foreach ($entryName in $entries) {
    if ($entryName -match '\.(dll|so(?:\.[0-9]+)*|dylib|exe)$' -and !$allowedBinaries.Contains($entryName)) {
        Fail "binary '$entryName' is outside the staged language server dependency allowlist."
    }
}

# Compare every distributed server/worker file with the current trusted staging
# output. A stale bundle, extra native payload or altered dependency manifest fails.
$expectedPayload = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
$payloadRoots = [ordered]@{
    $serverPrefix = $stagedServer
    'extension/resources/worker/' = (Join-Path $ExtensionDirectory 'resources/worker')
}
foreach ($root in $payloadRoots.GetEnumerator()) {
    foreach ($source in Get-ChildItem -LiteralPath $root.Value -Recurse -File) {
        if ($source.Extension -in @('.pdb', '.xml')) { continue }
        $relative = [IO.Path]::GetRelativePath($root.Value, $source.FullName).Replace('\', '/')
        $expectedPayload.Add($root.Key + $relative, $source.FullName)
    }
}
$expectedPayload.Add('extension/LICENSE.txt', (Join-Path $ExtensionDirectory 'LICENSE'))
$packagedServer = Join-Path $Output ('packaged-language-server-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $packagedServer
$zip = [System.IO.Compression.ZipFile]::OpenRead($vsix)
try {
    foreach ($entry in $zip.Entries) {
        $isServer = $entry.FullName.StartsWith($serverPrefix, [StringComparison]::Ordinal)
        $isWorker = $entry.FullName.StartsWith('extension/resources/worker/', [StringComparison]::Ordinal)
        if (!$isServer -and !$isWorker -and $entry.FullName -cne 'extension/LICENSE.txt') { continue }
        if (!$expectedPayload.ContainsKey($entry.FullName)) { Fail "unexpected language server payload '$($entry.FullName)'." }
        $stream = $entry.Open()
        try { $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        $expectedHash = (Get-FileHash -LiteralPath $expectedPayload[$entry.FullName] -Algorithm SHA256).Hash
        if ($actualHash -cne $expectedHash) { Fail "packaged payload '$($entry.FullName)' differs from current staging." }
        if (!$isServer) { continue }
        $relative = $entry.FullName.Substring($serverPrefix.Length)
        $destination = Join-Path $packagedServer $relative
        $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination)
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination)
    }
}
finally { $zip.Dispose() }
foreach ($entryName in $expectedPayload.Keys) {
    if ($entries -notcontains $entryName) { Fail "missing staged payload '$entryName'." }
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

# Execute the LSP extracted from the actual VSIX, not a bin/staging directory.
$smokeOutput = Join-Path $packagedServer 'verification'
& (Join-Path $repo 'eng/Test-LspDistribution.ps1') -SkipPublish -PublishDirectory $packagedServer -Output $smokeOutput

$report = [ordered]@{
    schemaVersion = '1.0'
    vsix = [IO.Path]::GetFileName($vsix)
    version = $expectedVersion
    entryCount = $entries.Count
    bytes = (Get-Item -LiteralPath $vsix).Length
    sha256 = (Get-FileHash -LiteralPath $vsix -Algorithm SHA256).Hash.ToLowerInvariant()
    languageServerPayloadFiles = @($expectedPayload.Keys | Where-Object { $_.StartsWith($serverPrefix, [StringComparison]::Ordinal) }).Count
    languageServerSmokes = @('markdown-with-node', 'mdx-without-node', 'mdx-without-worker-deps')
    packagedLanguageServer = $packagedServer
    entries = @($entries)
}
$reportPath = Join-Path $Output 'vsix-contents.json'
[IO.File]::WriteAllText($reportPath, (($report | ConvertTo-Json -Depth 6)))
Write-Host ("VSIX contents passed: {0} entries, {1} bytes, sha256={2}." -f $entries.Count, $report.bytes, $report.sha256)
