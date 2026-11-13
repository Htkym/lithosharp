param(
    [string]$LithoSharpVersion = '3.0.0',
    [string[]]$PackageFeed = @(),
    [string]$Output = (Join-Path $PSScriptRoot 'dist'),
    [string]$WorkDirectory,
    [string]$BaseUrl = 'https://example.invalid/'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (!$WorkDirectory) {
    $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ls-web-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$work = [IO.Path]::GetFullPath($WorkDirectory)
$outputPath = [IO.Path]::GetFullPath($Output)
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$null = New-Item -ItemType Directory -Force -Path $work
$packages = Join-Path $work 'p'
$sources = @('https://api.nuget.org/v3/index.json')
foreach ($feed in $PackageFeed) {
    if ($feed -match '^https://') { $sources += $feed }
    else { $sources += (Resolve-Path -LiteralPath $feed).Path }
}
$config = Join-Path $work 'NuGet.Config'
$sourceXml = for ($index = 0; $index -lt $sources.Count; $index++) {
    '<add key="source' + $index + '" value="' + [Security.SecurityElement]::Escape($sources[$index]) + '" />'
}
[IO.File]::WriteAllText($config, '<configuration><packageSources><clear />' + ($sourceXml -join '') +
    '</packageSources><packageSourceMapping><clear /></packageSourceMapping></configuration>')

$longestInput = Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|dist)[\\/]' } |
    Sort-Object { $_.FullName.Length } -Descending | Select-Object -First 1
$expandedPaths = @($longestInput.FullName, (Join-Path $packages "lithosharp.generators/$LithoSharpVersion/analyzers/dotnet/cs/LithoSharp.Generators.dll"),
    (Join-Path $outputPath 'examples/ja/typed-content/index.html'))
foreach ($feed in $sources | Where-Object { $_ -notmatch '^https://' }) {
    foreach ($package in Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File) {
        $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $spec = $archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
            if (!$spec) { throw "Missing package specification: $($package.FullName)" }
            $reader = [IO.StreamReader]::new($spec.Open())
            try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $packageRoot = $metadata.package.metadata.id.ToLowerInvariant() + '/' + $metadata.package.metadata.version.ToLowerInvariant()
            foreach ($entry in $archive.Entries) {
                $expandedPaths += Join-Path $packages ($packageRoot + '/' + $entry.FullName)
            }
        } finally { $archive.Dispose() }
    }
}
$maxPath = $expandedPaths | Sort-Object Length -Descending | Select-Object -First 1
if ($maxPath.Length -gt 240) { throw "Choose shorter source, work and output directories. Longest planned path: $maxPath" }
Write-Host "Longest planned path: $($maxPath.Length) characters. Work: $work"

function Invoke-Checked([scriptblock]$Action) {
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE." }
}
Invoke-Checked { node (Join-Path $root 'validate-coverage.mjs') }
Invoke-Checked { node (Join-Path $root 'update-documentation-inventory.mjs') }
$project = Join-Path $root 'LithoSharp.Website.csproj'
$checks = Join-Path $root 'checks/LithoSharp.Website.Checks.csproj'
Push-Location $root
$previousVersion = $env:LITHOSHARP_SITE_PACKAGE_VERSION
$env:LITHOSHARP_SITE_PACKAGE_VERSION = $LithoSharpVersion
try {
    Invoke-Checked { dotnet restore $project --configfile $config --packages $packages "-p:LithoSharpVersion=$LithoSharpVersion" }
    Invoke-Checked { dotnet restore $checks --configfile $config --packages $packages "-p:LithoSharpVersion=$LithoSharpVersion" }
    Invoke-Checked { dotnet build $project --configuration Release --no-restore "-p:LithoSharpVersion=$LithoSharpVersion" }
    Invoke-Checked { dotnet build $checks --configuration Release --no-restore "-p:LithoSharpVersion=$LithoSharpVersion" }
    Invoke-Checked { dotnet run --project $project --configuration Release --no-build --no-restore -- --project $root --export-api-material --generated-inputs (Join-Path $work 'g/api') }
    $apiInventory = Get-Content -LiteralPath (Join-Path $work 'g/api/public-symbols.json') -Raw | ConvertFrom-Json
    $longestApiPath = $apiInventory.plannedOutputs | ForEach-Object { Join-Path $outputPath $_ } | Sort-Object Length -Descending | Select-Object -First 1
    if ($longestApiPath.Length -gt 240) { throw "Choose a shorter output directory. Longest actual API output path: $longestApiPath" }
    Write-Host "Longest actual API output path: $($longestApiPath.Length) characters."
    Invoke-Checked { node (Join-Path $root 'validate-api-localization.mjs') --material (Join-Path $work 'g/api/english-units.json') }
    Invoke-Checked { dotnet run --project $project --configuration Release --no-build --no-restore -- --project $root --output $outputPath --base-url $BaseUrl --generated-inputs (Join-Path $work 'g') }
    Invoke-Checked { node (Join-Path $root 'validate-generated-site.mjs') --inputs (Join-Path $work 'g') --output $outputPath }
    Invoke-Checked { dotnet run --project $project --configuration Release --no-build --no-restore -- --project $root --verify-snippets --version $LithoSharpVersion }
} finally { $env:LITHOSHARP_SITE_PACKAGE_VERSION = $previousVersion; Pop-Location }
Write-Host "Website and mapped snippets checked: $outputPath"
