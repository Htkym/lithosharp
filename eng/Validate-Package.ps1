[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,
    [ValidateSet('LithoSharp', 'LithoSharp.Generators', 'LithoSharp.Images', 'LithoSharp.Tool', 'LithoSharp.ProjectTemplates', 'LithoSharp.Testing', 'LithoSharp.Mdx', 'LithoSharp.Analyzers', 'LithoSharp.Markdown', 'LithoSharp.Markdown.Source')]
    [string] $PackageId = 'LithoSharp',
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$markdownPair = Get-Content -LiteralPath (Join-Path $repo 'eng/markdown/component-pair.json') -Raw | ConvertFrom-Json
$isMarkdownComponent = $PackageId -in @('LithoSharp.Markdown', 'LithoSharp.Markdown.Source')

$matchingPackages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' |
    Where-Object { $_.Name -match ('^' + [regex]::Escape($PackageId) + '\.\d') -and $_.Name -notlike '*.symbols.nupkg' -and
        (!$ExpectedVersion -or $_.Name -ceq "$PackageId.$ExpectedVersion.nupkg") })
if ($matchingPackages.Count -ne 1) { throw "Expected exactly one $PackageId package in $PackageDirectory." }
$package = $matchingPackages[0]
$symbols = $null
if ($PackageId -eq 'LithoSharp') {
    $symbolName = [IO.Path]::GetFileNameWithoutExtension($package.Name) + '.snupkg'
    $matchingSymbols = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.snupkg' -File |
        Where-Object { $_.Name -match '^LithoSharp\.\d' })
    if ($matchingSymbols.Count -ne 1 -or $matchingSymbols[0].Name -cne $symbolName) {
        throw "Expected exactly the matching Core symbols package: $symbolName."
    }
    $symbols = $matchingSymbols[0]
}

function Get-ZipEntries([string] $path) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            $canonical = if ($name.EndsWith('/')) { $name.Substring(0, $name.Length - 1) } else { $name }
            $segments = $canonical.Split('/')
            if ([string]::IsNullOrWhiteSpace($canonical) -or $name -match '[:\\\p{Cc}<>"|?*]' -or
                $name.StartsWith('/') -or @($segments | Where-Object {
                    $_ -in @('', '.', '..') -or $_ -match '[. ]$' -or
                    $_ -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)'
                }).Count -gt 0) {
                throw "Unsafe archive path '$name' in $path."
            }
            $canonical = $canonical.Normalize([Text.NormalizationForm]::FormC)
            if (!$names.Add($canonical)) {
                throw "Duplicate archive path '$name' in $path."
            }
            $segments = $canonical.Split('/')
            for ($index = 1; $index -lt $segments.Length; $index++) {
                $parent = $segments[0..($index - 1)] -join '/'
                if ($files.Contains($parent)) { throw "Conflicting archive path '$name' in $path." }
                $null = $directories.Add($parent)
            }
            if ($name.EndsWith('/')) {
                if ($files.Contains($canonical)) { throw "Conflicting archive path '$name' in $path." }
                $null = $directories.Add($canonical)
            }
            else {
                if ($directories.Contains($canonical)) { throw "Conflicting archive path '$name' in $path." }
                $null = $files.Add($canonical)
            }
            if ($name -match '(^|/)(\.local|\.git|\.tmp|\.artifacts|\.agents|\.codex|\.aws|\.vs|\.vscode-test|benchmarks|tests?|TestResults|node_modules|obj|\.cache|cache)(/|$)' -or
                $name -match '(^|/)(\.(env|npmrc|netrc)(\.[^/]*)?|id_(rsa|ed25519)(\.pub)?|credentials(?:\.[^/]*)?|secrets?(?:\.[^/]*)?)(/|$)' -or
                $name -match '\.(pem|pfx|p12|key|log|binlog|trx)$') {
                throw "Package contains private, restored, development-only or credential entry '$name' in $path."
            }
        }
        return @($archive.Entries | ForEach-Object FullName)
    }
    finally {
        $archive.Dispose()
    }
}

function Read-Nuspec([string] $Path, [string] $Id) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' })
        if ($entries.Count -ne 1 -or $entries[0].FullName -cne "$Id.nuspec") {
            throw "Expected exactly one root nuspec named $Id.nuspec in $Path."
        }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { return [xml]$reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

$packageEntries = Get-ZipEntries $package.FullName
$symbolsEntries = if ($symbols) { Get-ZipEntries $symbols.FullName } else { @() }
$requiredMetadata = if ($isMarkdownComponent) { @('README.md', 'LICENSE', 'markdown/manifest.json') } else { @('README.md', 'icon.png') }
foreach ($required in $requiredMetadata) {
    if ($packageEntries -notcontains $required) { throw "Package is missing required entry: $required" }
}
# Bundled third-party assemblies retain exact upstream redistribution notices.
if ($PackageId -in @('LithoSharp.Generators', 'LithoSharp.Tool') -or
    ($PackageId -eq 'LithoSharp.Analyzers' -and $packageEntries -contains 'analyzers/dotnet/cs/YamlDotNet.dll')) {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $prefix = if ($PackageId -eq 'LithoSharp.Tool') { 'tools/net10.0/any/licenses/' } else { 'licenses/' }
    $notices = if ($PackageId -eq 'LithoSharp.Tool') {
        @('YamlDotNet.LICENSE.txt', 'AngleSharp.LICENSE.txt', 'SkiaSharp.LICENSE.txt', 'SkiaSharp.THIRD-PARTY-NOTICES.txt')
    } else { @('YamlDotNet.LICENSE.txt') }
    $noticeArchive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        foreach ($name in @($notices) + @('THIRD-PARTY-NOTICES.md')) {
            $entry = $noticeArchive.GetEntry($prefix + $name)
            if ($null -eq $entry) { throw "Package is missing redistribution notice: $prefix$name" }
            $source = if ($name -eq 'THIRD-PARTY-NOTICES.md') { Join-Path $repo $name } else { Join-Path $repo "licenses/$name" }
            $stream = $entry.Open()
            try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if ($actual -cne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) {
                throw "Package redistribution notice differs from upstream source: $name"
            }
        }
    } finally { $noticeArchive.Dispose() }
}
$nuspec = Read-Nuspec $package.FullName $PackageId
$metadata = $nuspec.package.metadata
if ($metadata.id -cne $PackageId -or ($ExpectedVersion -and $metadata.version -cne $ExpectedVersion)) {
    throw "Unexpected package identity: $($metadata.id) $($metadata.version)."
}
if ($package.Name -cne "$PackageId.$($metadata.version).nupkg") {
    throw 'Package filename does not match its nuspec identity.'
}
if ($symbols) {
    $symbolsMetadata = (Read-Nuspec $symbols.FullName $PackageId).package.metadata
    if ($symbolsMetadata.id -cne $metadata.id -or $symbolsMetadata.version -cne $metadata.version -or
        $symbolsMetadata.repository.type -cne $metadata.repository.type -or
        $symbolsMetadata.repository.url -cne $metadata.repository.url -or
        $symbolsMetadata.repository.commit -cne $metadata.repository.commit) {
        throw 'Core symbols nuspec identity or repository provenance differs from its package.'
    }
}
$validLicense = if ($isMarkdownComponent) {
    $metadata.license.type -eq 'file' -and $metadata.license.'#text' -eq 'LICENSE' -and
        $metadata.version -ceq $markdownPair.componentVersion -and $metadata.repository.commit -ceq $markdownPair.sourceCommit
} else { $metadata.license.type -eq 'expression' -and $metadata.license.'#text' -eq 'MIT' -and $metadata.icon -eq 'icon.png' }
$validRepository = if ($isMarkdownComponent) {
    # A local migration candidate has no public repository URL until its destination is approved.
    $metadata.repository.type -eq 'git' -and $metadata.repository.commit -ceq $markdownPair.sourceCommit -and
        $metadata.repository.GetAttribute('url') -eq ''
} else { $metadata.repository.type -eq 'git' -and $metadata.repository.url -eq 'https://github.com/Htkym/lithosharp' }
if (!$validLicense -or $metadata.readme -ne 'README.md' -or !$validRepository) {
    throw 'Package license, icon, README or repository metadata is incorrect.'
}
# Analyzer-only packages ship no dependency group; strict mode would throw on
# the missing property, so the sweeps below run only when present.
$nuspecDependencies = @()
if ($null -ne $metadata.PSObject.Properties['dependencies']) {
    $nuspecDependencies = @($metadata.SelectNodes('./*[local-name()="dependencies"]//*[local-name()="dependency"]'))
}
if ($nuspecDependencies.Count -gt 0 -and $nuspecDependencies.id -contains 'Markdig') {
    throw 'Package metadata must not reference Markdig.'
}
if ($isMarkdownComponent) {
    & node (Join-Path $repo 'eng/markdown/Verify-FixedPair.mjs') verify-fixed-package $package.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Fixed Markdown artifact/source verification failed.' }
    if ($PackageId -eq 'LithoSharp.Markdown') {
        if ($nuspecDependencies.Count -ne 1 -or $nuspecDependencies[0].id -cne 'YamlDotNet' -or $nuspecDependencies[0].version -cne '[18.1.0]') {
            throw 'Fixed Markdown runtime requires exactly YamlDotNet [18.1.0].'
        }
    } elseif ($nuspecDependencies.Count -ne 0) { throw 'Markdown source hosts must declare dependencies themselves.' }
    Write-Host "Validated fixed Markdown component: $($package.Name)"
    return
}
foreach ($dependency in $nuspecDependencies) {
    if ($dependency.id -eq 'LithoSharp.Markdown') {
        if ($dependency.version -cne "[$($markdownPair.componentVersion)]") { throw "Markdown runtime dependency must match the exact fixed pair: $($dependency.version)." }
        continue
    }
    if ($dependency.id -like 'LithoSharp*' -and $dependency.version -notin @($metadata.version, "[$($metadata.version), )")) {
        throw "Package dependency version is not aligned: $($dependency.id) $($dependency.version)."
    }
}
if ($PackageId -eq 'LithoSharp.Analyzers') {
    foreach ($required in @('analyzers/dotnet/cs/LithoSharp.Analyzers.dll', 'analyzers/dotnet/cs/LithoSharp.Analyzers.xml')) {
        if ($packageEntries -notcontains $required) { throw "Analyzer package is missing required entry: $required" }
    }
    if ($nuspecDependencies.Count -ne 0 -or @($packageEntries | Where-Object { $_ -match '^lib/' -or ($_ -like '*.dll' -and $_ -notin @('analyzers/dotnet/cs/LithoSharp.Analyzers.dll', 'analyzers/dotnet/cs/YamlDotNet.dll')) }).Count -ne 0) {
        throw 'Analyzer package contains a runtime dependency or unexpected assembly payload.'
    }
    Write-Host "Validated analyzer package contents: $($package.Name)"
    return
}
if ($PackageId -eq 'LithoSharp.Tool') {
    foreach ($required in @('tools/net10.0/any/LithoSharp.Tool.dll', 'tools/net10.0/any/LithoSharp.Tool.runtimeconfig.json', 'tools/net10.0/any/DotnetToolSettings.xml', 'tools/net10.0/any/LithoSharp.dll', 'README.md')) {
        if ($packageEntries -notcontains $required) { throw "Package is missing required entry: $required" }
    }
    Write-Host "Validated package contents: $($package.Name)"
    return
}
if ($PackageId -eq 'LithoSharp.ProjectTemplates') {
    foreach ($kind in @('docs', 'blog', 'empty', 'mdx')) {
        foreach ($file in @('.template.config/template.json', 'Program.cs')) {
            $required = "content/$kind/$file"
            if ($packageEntries -notcontains $required) { throw "Package is missing required entry: $required" }
        }
    }
    if ($packageEntries | Where-Object { $_ -match '/(bin|obj)/' }) { throw 'Template package contains build artifacts.' }
    Write-Host "Validated package contents: $($package.Name)"
    return
}
if ($PackageId -in @('LithoSharp.Images', 'LithoSharp.Testing', 'LithoSharp.Mdx')) {
    foreach ($required in @("lib/net10.0/$PackageId.dll", "lib/net10.0/$PackageId.xml", 'README.md')) {
        if ($packageEntries -notcontains $required) { throw "Package is missing required entry: $required" }
    }
    if ($PackageId -eq 'LithoSharp.Mdx') {
        foreach ($file in @('worker.mjs', 'compiler.mjs', 'package.json', 'package-lock.json', 'runtime/components.mjs', 'runtime/islands.mjs', 'runtime/live-code.mjs')) {
            if ($packageEntries -notcontains "contentFiles/any/any/worker/$file") { throw "MDX package is missing worker/$file" }
        }
        if ($packageEntries | Where-Object { $_ -match '/(node_modules|\.cache|tests)/' }) { throw 'MDX package contains restored dependencies or private test/cache data.' }
    }
    Write-Host "Validated package contents: $($package.Name)"
    return
}
if ($PackageId -eq 'LithoSharp.Generators') {
    foreach ($required in @('analyzers/dotnet/cs/LithoSharp.Generators.dll', 'analyzers/dotnet/cs/LithoSharp.Generators.xml', 'analyzers/dotnet/cs/YamlDotNet.dll', 'buildTransitive/LithoSharp.Generators.props', 'README.md')) {
        if ($packageEntries -notcontains $required) { throw "Package is missing required entry: $required" }
    }
    if ($packageEntries | Where-Object { $_ -like 'lib/*' }) { throw 'Generator package must not add runtime library references.' }
    Write-Host "Validated package contents: $($package.Name)"
    return
}

foreach ($required in @(
    'lib/net10.0/LithoSharp.dll',
    'lib/net10.0/LithoSharp.xml',
    'README.md',
    'icon.png'
)) {
    if ($packageEntries -notcontains $required) {
        throw "Package is missing required entry: $required"
    }
}

if ($symbolsEntries -notcontains 'lib/net10.0/LithoSharp.pdb') {
    throw 'Symbols package is missing lib/net10.0/LithoSharp.pdb.'
}

$dependencyIds = @($nuspec.package.metadata.dependencies.group.dependency.id)
foreach ($expected in @('AngleSharp', 'SkiaSharp', 'SkiaSharp.NativeAssets.Linux.NoDependencies', 'YamlDotNet', 'LithoSharp.Markdown')) {
    if ($dependencyIds -notcontains $expected) {
        throw "Package metadata is missing dependency: $expected"
    }
}

Write-Host "Validated package contents: $($package.Name), $($symbols.Name)"
