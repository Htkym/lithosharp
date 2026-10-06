[CmdletBinding()]
param(
    [ValidateSet('All', 'CoreTag', 'ExtensionTag')]
    [string] $Check = 'All',
    [string] $Tag
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Fail([string] $Message) { throw "Release pipeline check failed: $Message" }

function Get-Workflow([string] $Name) {
    $path = Join-Path $repo ".github/workflows/$Name"
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { Fail "workflow missing: $Name." }
    return Get-Content -LiteralPath $path -Raw
}

function Test-CoreTag {
    $nuget = Get-Workflow 'publish-nuget.yml'
    # Check the actual workflow trigger; fixed tag prefixes need no glob self-test.
    if ($nuget -notmatch "(?m)^\s*-\s*'v\*'") { Fail 'publish-nuget.yml lost its v* tag trigger.' }
    $projects = @(
        'src/LithoSharp/LithoSharp.csproj',
        'src/LithoSharp.Generators/LithoSharp.Generators.csproj',
        'src/LithoSharp.Images/LithoSharp.Images.csproj',
        'src/LithoSharp.Tool/LithoSharp.Tool.csproj',
        'src/LithoSharp.Testing/LithoSharp.Testing.csproj',
        'src/LithoSharp.Mdx/LithoSharp.Mdx.csproj',
        'templates/LithoSharp.ProjectTemplates/LithoSharp.ProjectTemplates.csproj'
    )
    $versions = @{}
    foreach ($project in $projects) {
        [xml] $xml = Get-Content -LiteralPath (Join-Path $repo $project) -Raw
        $declared = @($xml.SelectNodes('/Project/PropertyGroup/Version') | ForEach-Object { $_.InnerText })
        if ($declared.Count -ne 1) { Fail "$project must declare exactly one Version." }
        $versions[$project] = $declared[0]
    }
    $distinct = @($versions.Values | Sort-Object -Unique)
    if ($distinct.Count -ne 1) {
        Fail ("Core package versions are not aligned: " + (($versions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))
    }
    if ($nuget -notmatch [regex]::Escape('artifacts/packages/*.nupkg') -or $nuget -notmatch 'dotnet nuget push') {
        Fail 'publish-nuget.yml lost its pack-then-push shape.'
    }
    # A wrong tag fails closed: the workflow throws when any csproj version
    # differs from the tag instead of publishing a mismatched set.
    if ($nuget -notmatch 'does not match tag version') {
        Fail 'publish-nuget.yml lost its wrong-tag version guard.'
    }
    Write-Host "Core tag series: v* triggers NuGet publish, 7 packages align at $($distinct[0])."
}

function Test-ExtensionTag {
    $extension = Get-Workflow 'publish-extension.yml'
    # Tag pushes only pack and validate. Publishing runs exclusively from a
    # manual dispatch behind the marketplace environment approval.
    if ($extension -notmatch "extension/lithosharp-vscode/\*") { Fail 'publish-extension.yml lost its extension tag filter.' }
    if ($extension -match '(?m)^\s+tags:\s*$' -and $extension -match 'publish:' -and $extension -notmatch 'workflow_dispatch') {
        Fail 'publish-extension.yml publishes from a tag push.'
    }
    if ($extension -notmatch 'workflow_dispatch') { Fail 'publish-extension.yml lost its manual dispatch.' }
    if ($extension -notmatch 'environment:\s*marketplace') { Fail 'publish-extension.yml lost its marketplace environment approval.' }
    if ($extension -notmatch 'VSCE_PUBLISHER') { Fail 'publish-extension.yml lost its publisher-account gate.' }
    if ($extension -notmatch 'npm pkg set "publisher=') { Fail 'publish-extension.yml does not package the VSIX with its configured publisher.' }
    if ($extension -notmatch 'VSCE_PAT') { Fail 'publish-extension.yml lost its Marketplace credential gate.' }
    if ($extension -match 'vsce publish' -and $extension -notmatch "inputs\.publish == 'true'") {
        Fail 'publish-extension.yml can publish without the explicit publish input.'
    }
    $package = Get-Content -LiteralPath (Join-Path $repo 'extensions/lithosharp-vscode/package.json') -Raw | ConvertFrom-Json
    $version = [string]$package.version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { Fail "extension version '$version' is not a plain release version." }
    if ($package.PSObject.Properties['publisher']) { Fail 'extension package.json must not claim a publisher before the account exists.' }
    $expectedTag = "extension/lithosharp-vscode/$version"
    if ($Tag -and $Tag -cne $expectedTag) { Fail "extension tag '$Tag' does not match package version $version (expected '$expectedTag')." }
    foreach ($script in @('eng/Test-VsixContents.ps1')) {
        $text = Get-Content -LiteralPath (Join-Path $repo $script) -Raw
        if ($text -match 'vsce publish|nuget push') { Fail "$script mixes packaging with publishing." }
    }
    Write-Host "Extension tag series: $expectedTag packs and validates only; publish needs dispatch plus approval."
}

if ($Check -in @('All', 'CoreTag')) { Test-CoreTag }
if ($Check -in @('All', 'ExtensionTag')) { Test-ExtensionTag }
Write-Host "Release pipeline checks passed ($Check)."
