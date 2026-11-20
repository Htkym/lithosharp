[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$matrix = Get-Content -LiteralPath (Join-Path $repo 'eng/version-matrix.json') -Raw | ConvertFrom-Json
if ($matrix.schemaVersion -ne 1) { throw 'Unsupported version matrix schema.' }
if ([string]::IsNullOrWhiteSpace($matrix.coreApiCompatibilityBaseline)) { throw 'Core API compatibility baseline is required.' }
foreach ($workflow in @('build.yml', 'publish-nuget.yml')) {
    $text = Get-Content -LiteralPath (Join-Path $repo ".github/workflows/$workflow") -Raw
    $currentBaseline = [regex]::Matches($text, '(?m)^\s*LITHOSHARP_CURRENT_STABLE_VERSION:\s*''([^''\r\n]+)''\s*$')
    if ($currentBaseline.Count -ne 1 -or $currentBaseline[0].Groups[1].Value -cne $matrix.currentStableConsumerBaseline) {
        throw "Current-stable consumer baseline differs from the matrix: $workflow"
    }
    $previousMajor = [regex]::Matches($text, '(?m)^\s*LITHOSHARP_PREVIOUS_MAJOR_VERSION:\s*''([^''\r\n]+)''\s*$')
    if ($previousMajor.Count -ne 1 -or $previousMajor[0].Groups[1].Value -cne $matrix.previousMajorConsumerBaseline) {
        throw "Previous-major consumer baseline differs from the matrix: $workflow"
    }
    $declarations = [regex]::Matches($text, '(?m)^\s*[''"]?LITHOSHARP_PREVIOUS_STABLE_VERSION[''"]?\s*:')
    $baseline = [regex]::Matches($text, '(?m)^\s*LITHOSHARP_PREVIOUS_STABLE_VERSION:\s*''([^''\r\n]+)''\s*$')
    if ($declarations.Count -ne 1 -or $baseline.Count -ne 1 -or $baseline[0].Groups[1].Value -cne $matrix.coreApiCompatibilityBaseline) {
        throw "Previous-stable consumer baseline differs from the matrix: $workflow"
    }
}
$projects = @(
    'src/LithoSharp/LithoSharp.csproj',
    'src/LithoSharp.Mdx/LithoSharp.Mdx.csproj',
    'src/LithoSharp.Images/LithoSharp.Images.csproj',
    'src/LithoSharp.Testing/LithoSharp.Testing.csproj',
    'src/LithoSharp.Generators/LithoSharp.Generators.csproj',
    'src/LithoSharp.Analyzers/LithoSharp.Analyzers.csproj',
    'src/LithoSharp.CodeFixes/LithoSharp.CodeFixes.csproj',
    'src/LithoSharp.Tool/LithoSharp.Tool.csproj',
    'templates/LithoSharp.ProjectTemplates/LithoSharp.ProjectTemplates.csproj'
)
foreach ($relative in $projects) {
    $project = [xml](Get-Content -LiteralPath (Join-Path $repo $relative) -Raw)
    if ($project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText -cne $matrix.lithosharpTarget) {
        throw "Package version differs from the development matrix: $relative"
    }
}
$server = [xml](Get-Content -LiteralPath (Join-Path $repo 'src/LithoSharp.LanguageServer/LithoSharp.LanguageServer.csproj') -Raw)
if ($server.SelectSingleNode('/Project/PropertyGroup/Version').InnerText -cne $matrix.languageServerTarget) { throw 'Language server version differs from the matrix.' }
$extension = Get-Content -LiteralPath (Join-Path $repo 'extensions/lithosharp-vscode/package.json') -Raw | ConvertFrom-Json
$lock = Get-Content -LiteralPath (Join-Path $repo 'extensions/lithosharp-vscode/package-lock.json') -Raw | ConvertFrom-Json -AsHashtable
if ($extension.version -cne $matrix.vscodeExtensionTarget -or $lock['version'] -cne $extension.version -or $lock['packages']['']['version'] -cne $extension.version) {
    throw 'Extension package metadata differs from the matrix.'
}
$pair = Get-Content -LiteralPath (Join-Path $repo $matrix.selectedPairManifest) -Raw | ConvertFrom-Json
$props = [xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)
if ($pair.componentVersion -cne $matrix.selectedSyntamarkCandidate -or $props.Project.PropertyGroup.SyntamarkComponentVersion -cne $pair.componentVersion) {
    throw 'Selected runtime/Source pair differs from the matrix.'
}
foreach ($project in Get-ChildItem -LiteralPath (Join-Path $repo 'templates/LithoSharp.ProjectTemplates/content') -Filter LithoSharpSite.csproj -Recurse) {
    $xml = [xml](Get-Content -LiteralPath $project.FullName -Raw)
    if ($xml.SelectSingleNode('/Project/PropertyGroup/LithoSharpVersion').InnerText -cne $matrix.lithosharpTarget) {
        throw "Template default version differs from the matrix: $($project.FullName)"
    }
}
foreach ($relative in @('site/Directory.Build.props', 'site/LithoSharp.Website.csproj')) {
    $xml = [xml](Get-Content -LiteralPath (Join-Path $repo $relative) -Raw)
    $versions = @($xml.SelectNodes('/Project/PropertyGroup/LithoSharpVersion') | ForEach-Object { $_.InnerText } |
        Where-Object { $_ -notmatch '^\$\(' })
    if ($versions.Count -ne 1 -or $versions[0] -cne $matrix.lithosharpTarget) {
        throw "Official site default version differs from the matrix: $relative"
    }
}
Write-Host 'Development version matrix passed. This does not establish formal publication.'
