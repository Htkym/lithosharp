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

function Test-StableComponentPair([string] $Version) {
    if ($Tag -and $Version -notmatch '-') {
        $component = Get-Content -LiteralPath (Join-Path $repo 'eng/markdown/component-pair.json') -Raw | ConvertFrom-Json
        if ($component.componentVersion -match '-') { Fail 'Stable publication requires an approved stable Syntamark pair; the fixed preview pair must not be repacked or relabeled.' }
    }
}

function Test-MainDispatch([string] $Workflow, [string] $Name) {
    if ($Workflow -notmatch '(?m)^\s+workflow_dispatch:' -or $Workflow -notmatch '(?m)^\s+release_tag:') {
        Fail "$Name requires a manual dispatch with an existing release tag."
    }
    if ($Workflow -match '(?m)^\s+tags:') { Fail "$Name must not publish from a tag push." }
    if ($Workflow -notmatch 'Test-MainRelease.ps1 -CiWorkflow build.yml -ReleaseTag .* -RequireTag') {
        Fail "$Name requires the exact current main/tag SHA and successful normal main CI."
    }
}

function Test-CoreTag {
    $nuget = Get-Workflow 'publish-nuget.yml'
    Test-MainDispatch $nuget 'publish-nuget.yml'
    $projects = @((Get-Content -LiteralPath (Join-Path $repo 'eng/release-packages.json') -Raw | ConvertFrom-Json).project)
    $versions = @{}
    foreach ($project in $projects) {
        [xml] $xml = Get-Content -LiteralPath (Join-Path $repo $project) -Raw
        $declared = @($xml.SelectNodes('/Project/PropertyGroup/Version') | ForEach-Object { $_.InnerText })
        if ($declared.Count -ne 1) { Fail "$project must declare exactly one Version." }
        $versions[$project] = $declared[0]
    }
    if ($Tag -and $Tag -cne "v$($versions[$projects[0]])") { Fail "Core tag does not match tag version $($versions[$projects[0]])." }
    $distinct = @($versions.Values | Sort-Object -Unique)
    if ($distinct.Count -ne 1) {
        Fail ("Core package versions are not aligned: " + (($versions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))
    }
    Test-StableComponentPair $distinct[0]
    if ($nuget -notmatch [regex]::Escape('artifacts/packages/*.nupkg') -or $nuget -notmatch 'dotnet nuget push') {
        Fail 'publish-nuget.yml lost its pack-then-push shape.'
    }
    # A wrong tag fails closed: the workflow throws when any csproj version
    # differs from the tag instead of publishing a mismatched set.
    if ($nuget -notmatch 'does not match tag version') {
        Fail 'publish-nuget.yml lost its wrong-tag version guard.'
    }
    Write-Host "Core release: dispatch from main with v$($distinct[0]); 9 packages align."
}

function Test-ExtensionTag {
    $extension = Get-Workflow 'publish-extension.yml'
    Test-MainDispatch $extension 'publish-extension.yml'
    if ($extension -notmatch 'workflow_dispatch' -or $extension -notmatch "default: 'false'") { Fail 'manual dispatch must default to validation only.' }
    if ($extension -notmatch 'environment:\s*marketplace' -or $extension -notmatch 'Test-ExtensionPublish.ps1 -Check Environment') {
        Fail 'publish-extension.yml lost its configured required-reviewer gate.'
    }
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
    Test-StableComponentPair $version
    foreach ($script in @('eng/Test-VsixContents.ps1')) {
        $text = Get-Content -LiteralPath (Join-Path $repo $script) -Raw
        if ($text -match 'vsce publish|nuget push') { Fail "$script mixes packaging with publishing." }
    }
    Write-Host "Extension release: dispatch from main with $expectedTag after marketplace approval; validation is the default."
}

if ($Check -in @('All', 'CoreTag')) { Test-CoreTag }
if ($Check -in @('All', 'ExtensionTag')) { Test-ExtensionTag }
Write-Host "Release pipeline checks passed ($Check)."
