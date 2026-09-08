[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,
    [ValidateSet('LithoSharp', 'LithoSharp.Generators', 'LithoSharp.Images', 'LithoSharp.Tool', 'LithoSharp.ProjectTemplates', 'LithoSharp.Testing', 'LithoSharp.Mdx')]
    [string] $PackageId = 'LithoSharp',
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$matchingPackages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' |
    Where-Object { $_.Name -match ('^' + [regex]::Escape($PackageId) + '\.\d') -and $_.Name -notlike '*.symbols.nupkg' })
if ($matchingPackages.Count -ne 1) { throw "Expected exactly one $PackageId package in $PackageDirectory." }
$package = $matchingPackages[0]
$symbols = Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.snupkg' | Select-Object -First 1

if ($null -eq $package -or ($PackageId -eq 'LithoSharp' -and $null -eq $symbols)) {
    throw "Expected one .nupkg and one .snupkg in $PackageDirectory."
}

function Get-ZipEntries([string] $path) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        return @($archive.Entries | ForEach-Object FullName)
    }
    finally {
        $archive.Dispose()
    }
}

$packageEntries = Get-ZipEntries $package.FullName
foreach ($required in @('README.md', 'icon.png')) {
    if ($packageEntries -notcontains $required) { throw "Package is missing required entry: $required" }
}
if ($packageEntries | Where-Object { $_ -match '(^|/)(\.local|\.git|\.tmp|benchmarks|tests|TestResults|node_modules|obj)(/|$)' }) {
    throw 'Package contains private, restored or development-only files.'
}
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $nuspecEntry = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    if ($null -eq $nuspecEntry) { throw 'Package is missing its nuspec metadata.' }
    $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
    try { [xml] $nuspec = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}
finally { $archive.Dispose() }
$metadata = $nuspec.package.metadata
if ($metadata.id -cne $PackageId -or ($ExpectedVersion -and $metadata.version -cne $ExpectedVersion)) {
    throw "Unexpected package identity: $($metadata.id) $($metadata.version)."
}
if ($metadata.license.type -ne 'expression' -or $metadata.license.'#text' -ne 'MIT' -or
    $metadata.icon -ne 'icon.png' -or $metadata.readme -ne 'README.md' -or
    $metadata.repository.type -ne 'git' -or $metadata.repository.url -ne 'https://github.com/Htkym/lithosharp') {
    throw 'Package license, icon, README or repository metadata is incorrect.'
}
foreach ($dependency in @($metadata.dependencies.group.dependency)) {
    if ($dependency.id -like 'LithoSharp*' -and $dependency.version -notin @($metadata.version, "[$($metadata.version), )")) {
        throw "Package dependency version is not aligned: $($dependency.id) $($dependency.version)."
    }
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
$symbolsEntries = Get-ZipEntries $symbols.FullName

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
foreach ($expected in @('AngleSharp', 'Markdig', 'SkiaSharp', 'SkiaSharp.NativeAssets.Linux.NoDependencies', 'YamlDotNet')) {
    if ($dependencyIds -notcontains $expected) {
        throw "Package metadata is missing dependency: $expected"
    }
}

Write-Host "Validated package contents: $($package.Name), $($symbols.Name)"
