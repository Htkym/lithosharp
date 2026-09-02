[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$package = Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    Select-Object -First 1
$symbols = Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.snupkg' | Select-Object -First 1

if ($null -eq $package -or $null -eq $symbols) {
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
foreach ($expected in @('Markdig', 'SkiaSharp', 'SkiaSharp.NativeAssets.Linux.NoDependencies', 'YamlDotNet')) {
    if ($dependencyIds -notcontains $expected) {
        throw "Package metadata is missing dependency: $expected"
    }
}

Write-Host "Validated package contents: $($package.Name), $($symbols.Name)"
