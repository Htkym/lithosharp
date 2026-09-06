[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,
    [ValidateSet('LithoSharp', 'LithoSharp.Generators', 'LithoSharp.Images', 'LithoSharp.Tool', 'LithoSharp.ProjectTemplates')]
    [string] $PackageId = 'LithoSharp'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$package = Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' |
    Where-Object { $_.Name -match ('^' + [regex]::Escape($PackageId) + '\.\d') -and $_.Name -notlike '*.symbols.nupkg' } |
    Select-Object -First 1
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
if ($PackageId -eq 'LithoSharp.Tool') {
    foreach ($required in @('tools/net10.0/any/LithoSharp.Tool.dll', 'tools/net10.0/any/LithoSharp.Tool.runtimeconfig.json', 'tools/net10.0/any/DotnetToolSettings.xml', 'tools/net10.0/any/LithoSharp.dll', 'README.md')) {
        if ($packageEntries -notcontains $required) { throw "Package is missing required entry: $required" }
    }
    Write-Host "Validated package contents: $($package.Name)"
    return
}
if ($PackageId -eq 'LithoSharp.ProjectTemplates') {
    foreach ($kind in @('docs', 'blog', 'empty')) {
        foreach ($file in @('.template.config/template.json', 'Program.cs')) {
            $required = "content/$kind/$file"
            if ($packageEntries -notcontains $required) { throw "Package is missing required entry: $required" }
        }
    }
    if ($packageEntries | Where-Object { $_ -match '/(bin|obj)/' }) { throw 'Template package contains build artifacts.' }
    Write-Host "Validated package contents: $($package.Name)"
    return
}
if ($PackageId -eq 'LithoSharp.Images') {
    foreach ($required in @('lib/net10.0/LithoSharp.Images.dll', 'lib/net10.0/LithoSharp.Images.xml', 'README.md')) {
        if ($packageEntries -notcontains $required) { throw "Package is missing required entry: $required" }
    }
    Write-Host "Validated package contents: $($package.Name)"
    return
}
if ($PackageId -eq 'LithoSharp.Generators') {
    foreach ($required in @('analyzers/dotnet/cs/LithoSharp.Generators.dll', 'analyzers/dotnet/cs/YamlDotNet.dll', 'buildTransitive/LithoSharp.Generators.props', 'README.md')) {
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

$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $nuspecEntry = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    if ($null -eq $nuspecEntry) {
        throw 'Package is missing its nuspec metadata.'
    }

    $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
    try {
        [xml] $nuspec = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$dependencyIds = @($nuspec.package.metadata.dependencies.group.dependency.id)
foreach ($expected in @('AngleSharp', 'Markdig', 'SkiaSharp', 'SkiaSharp.NativeAssets.Linux.NoDependencies', 'YamlDotNet')) {
    if ($dependencyIds -notcontains $expected) {
        throw "Package metadata is missing dependency: $expected"
    }
}

Write-Host "Validated package contents: $($package.Name), $($symbols.Name)"
