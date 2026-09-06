[CmdletBinding()]
param([Parameter(Mandatory)] [string] $PackageDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packages = [IO.Path]::GetFullPath($PackageDirectory)
$fixture = Join-Path $repo ('.tmp/template-package-test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture
$oldPackages = $env:NUGET_PACKAGES
$oldHome = $env:DOTNET_CLI_HOME
$oldTimestamp = $env:SOURCE_DATE_EPOCH
$oldCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
try {
    # Keep both the template hive and package cache separate from the user's installations.
    $env:NUGET_PACKAGES = Join-Path $fixture 'packages'
    $env:DOTNET_CLI_HOME = Join-Path $fixture 'cli-home'
    $env:SOURCE_DATE_EPOCH = '1767225600'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $hive = Join-Path $fixture 'template-hive'
    $toolDirectory = Join-Path $fixture 'tool'
    $config = Join-Path $fixture 'NuGet.Config'
    $escapedPackages = [Security.SecurityElement]::Escape($packages)
    [IO.File]::WriteAllText($config, @"
<configuration>
  <packageSources><clear/><add key="local" value="$escapedPackages"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="LithoSharp*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@)
    dotnet tool install LithoSharp.Tool --version 0.2.0 --tool-path $toolDirectory --configfile $config
    if ($LASTEXITCODE -ne 0) { throw 'Tool package installation failed.' }
    dotnet new install (Join-Path $packages 'LithoSharp.ProjectTemplates.0.2.0.nupkg') --debug:custom-hive $hive
    if ($LASTEXITCODE -ne 0) { throw 'Template package installation failed.' }
    $tool = Join-Path $toolDirectory $(if ($IsWindows) { 'lithosharp.exe' } else { 'lithosharp' })
    foreach ($kind in @('docs', 'blog', 'empty')) {
        $project = Join-Path $fixture $kind
        & $tool new $kind "Test$kind" -o $project --debug:custom-hive $hive
        if ($LASTEXITCODE -ne 0) { throw "New $kind failed." }
        & $tool build $project -c Release
        if ($LASTEXITCODE -ne 0) { throw "Build $kind failed." }
        $output = Join-Path $project 'dist'
        if (!(Test-Path -LiteralPath (Join-Path $output 'index.html'))) { throw "$kind produced no home page." }
        if ($kind -eq 'empty') {
            $html = Get-Content -LiteralPath (Join-Path $output 'index.html') -Raw
            if ($html -notmatch '<main><h1' -or $html -match '&lt;main&gt;') { throw 'Empty template did not render its C# layout.' }
        }
        $snapshot = @(Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
            [IO.Path]::GetRelativePath($output, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash
        } | Sort-Object) -join "`n"
        Push-Location $project
        try {
            dotnet run -c Release --no-build
            if ($LASTEXITCODE -ne 0) { throw "Direct library execution of $kind failed." }
        }
        finally { Pop-Location }
        $direct = @(Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
            [IO.Path]::GetRelativePath($output, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash
        } | Sort-Object) -join "`n"
        if ($snapshot -cne $direct) { throw "$kind CLI and direct output differ." }
        & $tool check $project -c Release --format json
        if ($LASTEXITCODE -ne 0) { throw "Quality check of $kind failed." }
    }
    Write-Host "Packaged tool and all three templates passed. Fixture: $fixture"
}
finally {
    $env:NUGET_PACKAGES = $oldPackages
    $env:DOTNET_CLI_HOME = $oldHome
    $env:SOURCE_DATE_EPOCH = $oldTimestamp
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $oldCertificate
}
