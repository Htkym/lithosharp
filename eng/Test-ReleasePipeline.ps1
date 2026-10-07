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
    if ($extension -notmatch 'extension/lithosharp-vscode/\*') { Fail 'publish-extension.yml lost its extension tag filter.' }
    if ($extension -notmatch 'workflow_dispatch' -or $extension -notmatch "default: 'false'") { Fail 'manual dispatch must default to validation only.' }
    if ($extension -notmatch 'environment:\s*marketplace' -or $extension -notmatch 'Test-ExtensionPublish.ps1 -Check Environment') {
        Fail 'publish-extension.yml lost its configured required-reviewer gate.'
    }
    if ($extension -notmatch 'merge-base --is-ancestor') { Fail 'publication requires the tagged commit on main.' }
    if ($extension -notmatch 'uses: \./\.github/workflows/installed-extension.yml' -or $extension -notmatch 'needs: \[preflight, validate\]') {
        Fail 'publication must depend on three-OS installed acceptance.'
    }
    if ($extension -notmatch 'VSCE_PAT' -or $extension -notmatch "needs.preflight.outputs.publish == 'true'") { Fail 'publish request or credential gate missing.' }
    if ($extension -notmatch 'Test-ExtensionPublish.ps1 -Check Artifact' -or $extension -notmatch 'publish --packagePath') {
        Fail 'publication must verify and submit the certified artifact.'
    }
    if ($extension -match 'npm pkg set|vsce package|run vsix:stage') { Fail 'publish workflow must not rebuild the certified VSIX.' }
    $package = Get-Content -LiteralPath (Join-Path $repo 'extensions/lithosharp-vscode/package.json') -Raw | ConvertFrom-Json
    $version = [string]$package.version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { Fail "extension version '$version' is not a plain release version." }
    if ([string]$package.publisher -cne 'htkym') { Fail 'extension must use the existing htkym publisher identity.' }
    $expectedTag = "extension/lithosharp-vscode/$version"
    if ($Tag -and $Tag -cne $expectedTag) { Fail "extension tag '$Tag' does not match package version $version (expected '$expectedTag')." }
    foreach ($script in @('eng/Test-VsixContents.ps1')) {
        $text = Get-Content -LiteralPath (Join-Path $repo $script) -Raw
        if ($text -match 'vsce publish|nuget push') { Fail "$script mixes packaging with publishing." }
    }
    Write-Host "Extension tag series: $expectedTag requests certified-artifact publication from main after marketplace approval; dispatch defaults to validation only."
}

if ($Check -in @('All', 'CoreTag')) { Test-CoreTag }
if ($Check -in @('All', 'ExtensionTag')) { Test-ExtensionTag }
Write-Host "Release pipeline checks passed ($Check)."
