<#
.SYNOPSIS
Proves the dependency-zero scanner rejects contaminated copies, without changing shipping inputs.
.EXAMPLE
./eng/Test-DependencyZeroGate.ps1 -Output D:/evidence/in02-negative -ShippingPaths D:/evidence/feed -AngleSharpAssembly D:/baseline/AngleSharp.dll -ReferenceCarrierAssembly D:/baseline/LithoSharp.dll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Output,
    [string[]] $ShippingPaths = @(),
    [string] $AngleSharpAssembly,
    [string] $ReferenceCarrierAssembly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
$Output = [IO.Path]::GetFullPath($Output)
if ($env:GITHUB_ACTIONS -ne 'true' -and $Output -notmatch '^[Dd]:[\\/]') { throw 'Local negative gate output must be a dedicated D-drive directory; CI uses its runner temporary directory.' }
if (Test-Path -LiteralPath $Output) { throw 'Negative gate output must be new.' }
$null = New-Item -ItemType Directory -Path $Output
$validator = Join-Path $PSScriptRoot 'Test-DependencyZero.ps1'
$pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$results = [Collections.Generic.List[object]]::new()
$originals = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
$candidate = ''
$assemblyFixtureMode = 'provided-assemblies'

function Quote([string] $Text) { return "'" + $Text.Replace("'", "''") + "'" }
function Invoke-Scanner([string] $Name, [string[]] $Inputs) {
    $reportPath = Join-Path $Output "$Name.json"
    $command = '& ' + (Quote $validator) + ' -Paths @(' + (($Inputs | ForEach-Object { Quote $_ }) -join ',') + ') -Output ' + (Quote $reportPath)
    $log = & $pwsh -NoProfile -Command $command 2>&1 | Out-String
    $code = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $Output "$Name.log"), $log)
    if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "Scanner did not save evidence for $Name (exit=$code): $log" }
    return @{ exitCode = $code; report = (Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json) }
}
function Add-Entry([string] $ArchivePath, [string] $Name, [string] $Text, [string] $Source = '') {
    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $target = $archive.CreateEntry($Name).Open()
        try {
            if ($Source) {
                $inputStream = [IO.File]::OpenRead($Source)
                try { $inputStream.CopyTo($target) } finally { $inputStream.Dispose() }
            } else {
                $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
                $target.Write($bytes, 0, $bytes.Length)
            }
        } finally { $target.Dispose() }
    } finally { $archive.Dispose() }
}
function Copy-Candidate([string] $Name, [string] $Source) {
    $directory = Join-Path $Output $Name
    $null = New-Item -ItemType Directory -Path $directory
    $path = Join-Path $directory ([IO.Path]::GetFileName($Source))
    Copy-Item -LiteralPath $Source -Destination $path
    return $path
}
function Assert-Rejected([string] $Name, [string[]] $Inputs, [string] $Reason) {
    $result = Invoke-Scanner $Name $Inputs
    $detected = $result.exitCode -ne 0 -and @($result.report.findings | Where-Object reason -eq $Reason).Count -gt 0
    $results.Add([ordered]@{ name = $Name; passed = $detected; exitCode = $result.exitCode; expectedReason = $Reason; findings = $result.report.findings })
    if (!$detected) { throw "Negative '$Name' did not reject for $Reason." }
}
function Build-AssemblyFixtures {
    $directory = Join-Path $Output 'assembly-fixtures'
    $null = New-Item -ItemType Directory -Path $directory
    [IO.File]::WriteAllText((Join-Path $directory 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>')
    $definitionDirectory = Join-Path $directory 'definition'
    $carrierDirectory = Join-Path $directory 'carrier'
    $null = New-Item -ItemType Directory -Path $definitionDirectory, $carrierDirectory
    $definitionProject = Join-Path $definitionDirectory 'Definition.csproj'
    [IO.File]::WriteAllText($definitionProject, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>AngleSharp</AssemblyName></PropertyGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $definitionDirectory 'Marker.cs'), 'namespace AngleSharp { public sealed class Marker { } }')
    $definitionDll = Join-Path $definitionDirectory 'bin/Release/net10.0/AngleSharp.dll'
    $carrierProject = Join-Path $carrierDirectory 'Carrier.csproj'
    $escaped = [Security.SecurityElement]::Escape($definitionDll)
    [IO.File]::WriteAllText($carrierProject, "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>DependencyCarrier</AssemblyName></PropertyGroup><ItemGroup><Reference Include=`"AngleSharp`"><HintPath>$escaped</HintPath></Reference></ItemGroup></Project>")
    [IO.File]::WriteAllText((Join-Path $carrierDirectory 'Carrier.cs'), 'public sealed class Carrier { public AngleSharp.Marker Read() => new AngleSharp.Marker(); }')
    $userDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $rtk = Join-Path $userDirectory '.agents/skills/rtk-dotnet-verify/scripts/Invoke-RtkDotNet.ps1'
    foreach ($project in @($definitionProject, $carrierProject)) {
        $arguments = @($project, '-c', 'Release', '--nologo', '-v', 'minimal')
        if (Test-Path -LiteralPath $rtk -PathType Leaf) {
            $command = '& ' + (Quote $rtk) + ' -Action build -Arguments @(' + (($arguments | ForEach-Object { Quote $_ }) -join ',') + ')'
        } else {
            Write-Host 'RTK wrapper unavailable; building only the isolated dependency gate fixture with dotnet.'
            $command = '& dotnet build ' + (($arguments | ForEach-Object { Quote $_ }) -join ' ')
        }
        # Only this newly created fixture is built. No repository source or project is compiled.
        $log = & $pwsh -NoProfile -Command $command 2>&1 | Out-String
        $code = $LASTEXITCODE
        [IO.File]::WriteAllText((Join-Path $directory ([IO.Path]::GetFileNameWithoutExtension($project) + '-build.log')), $log)
        if ($code -ne 0) { throw "Dependency assembly fixture compile failed (exit=$code): $log" }
    }
    return @($definitionDll, (Join-Path $carrierDirectory 'bin/Release/net10.0/DependencyCarrier.dll'))
}

try {
    if (!$AngleSharpAssembly -and !$ReferenceCarrierAssembly) {
        $generated = Build-AssemblyFixtures
        $AngleSharpAssembly = $generated[0]
        $ReferenceCarrierAssembly = $generated[1]
        $assemblyFixtureMode = 'isolated-net10-managed-definition-and-reference'
    } elseif (!$AngleSharpAssembly -or !$ReferenceCarrierAssembly) { throw 'Provide both assembly inputs or neither.' }
    foreach ($inputAssembly in @($AngleSharpAssembly, $ReferenceCarrierAssembly)) {
        $resolved = (Get-Item -LiteralPath $inputAssembly).FullName
        $originals[$resolved] = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    # A small positive is available without a product build. When supplied,
    # actual shipping roots are additionally validated and their hashes retained.
    $clean = Join-Path $Output 'clean.nupkg'
    $zip = [IO.Compression.ZipFile]::Open($clean, [IO.Compression.ZipArchiveMode]::Create)
    $zip.Dispose()
    Add-Entry $clean 'DependencyGate.nuspec' '<package><metadata><id>DependencyGate</id><version>2.0.0</version><dependencies><group targetFramework="net10.0" /></dependencies></metadata></package>'
    $cleanProject = Join-Path $Output 'clean.csproj'
    [IO.File]::WriteAllText($cleanProject, '<Project><ItemGroup><PackageReference Include="YamlDotNet" Version="18.1.0" /></ItemGroup></Project>')
    $positive = Invoke-Scanner 'positive-fixture' @($clean, $cleanProject)
    if ($positive.exitCode -ne 0) { throw 'Clean dependency fixture failed.' }
    $results.Add([ordered]@{ name = 'positive-fixture'; passed = $true; exitCode = 0 })
    $candidate = $clean
    if ($ShippingPaths.Count -gt 0) {
        $shipping = Invoke-Scanner 'positive-shipping' $ShippingPaths
        if ($shipping.exitCode -ne 0) { throw 'Actual shipping inputs are not dependency-zero.' }
        foreach ($file in $shipping.report.files) { $originals[$file.path] = $file.sha256 }
        $results.Add([ordered]@{ name = 'positive-shipping'; passed = $true; exitCode = 0; fileCount = $shipping.report.fileCount })
        $packages = @($shipping.report.files | Where-Object { $_.path -match '\.nupkg$' } | Sort-Object path)
        if ($packages.Count -gt 0) { $candidate = $packages[0].path }
    }
    $originals[$clean] = (Get-FileHash -LiteralPath $clean -Algorithm SHA256).Hash.ToLowerInvariant()
    $originals[$cleanProject] = (Get-FileHash -LiteralPath $cleanProject -Algorithm SHA256).Hash.ToLowerInvariant()

    $contaminated = Copy-Candidate 'nuspec' $candidate
    $archive = [IO.Compression.ZipFile]::Open($contaminated, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $nuspec = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($nuspec.Count -ne 1) { throw 'Candidate must contain exactly one nuspec.' }
        $reader = [IO.StreamReader]::new($nuspec[0].Open())
        try { [xml] $xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $dependencies = $metadata.SelectSingleNode('*[local-name()="dependencies"]')
        if (!$dependencies) {
            $dependencies = $xml.CreateElement('dependencies', $metadata.NamespaceURI)
            $null = $metadata.AppendChild($dependencies)
        }
        $group = $dependencies.SelectSingleNode('*[local-name()="group"]')
        $dependencyParent = if ($group) { $group } else { $dependencies }
        $dependency = $xml.CreateElement('dependency', $metadata.NamespaceURI)
        $dependency.SetAttribute('id', 'AngleSharp.Css')
        $dependency.SetAttribute('version', '1.0.0')
        $null = $dependencyParent.AppendChild($dependency)
        $entryName = $nuspec[0].FullName
        $nuspec[0].Delete()
        $writer = [IO.StreamWriter]::new($archive.CreateEntry($entryName).Open())
        try { $writer.Write($xml.OuterXml) } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    Assert-Rejected 'negative-nuspec' @($contaminated) 'nuspec-dependency'

    $graph = Join-Path $Output 'project.assets.json'
    [IO.File]::WriteAllText($graph, '{"libraries":{"AngleSharp/1.8.3":{"type":"package"}}}')
    Assert-Rejected 'negative-graph' @($graph) 'dependency-graph'
    if ($ShippingPaths.Count -gt 0) {
        foreach ($file in @($shipping.report.files | Where-Object { [IO.Path]::GetFileName($_.path) -in @('project.assets.json', 'packages.lock.json') })) {
            $name = if ([IO.Path]::GetFileName($file.path) -eq 'project.assets.json') { 'negative-actual-assets' } else { 'negative-actual-lock' }
            $copy = Copy-Candidate $name $file.path
            $json = Get-Content -LiteralPath $copy -Raw | ConvertFrom-Json -AsHashtable
            if ($name -eq 'negative-actual-assets') {
                $json.libraries['AngleSharp/1.8.3'] = @{ type = 'package'; path = 'anglesharp/1.8.3' }
            } else {
                $framework = @($json.dependencies.Keys)[0]
                $json.dependencies[$framework]['AngleSharp'] = @{ type = 'Transitive'; resolved = '1.8.3' }
            }
            [IO.File]::WriteAllText($copy, ($json | ConvertTo-Json -Depth 100))
            Assert-Rejected $name @($copy) 'dependency-graph'
        }
    }
    $project = Join-Path $Output 'negative.csproj'
    [IO.File]::WriteAllText($project, '<Project><ItemGroup><PackageReference Include="AngleSharp.Css" Version="1.0.0" /></ItemGroup></Project>')
    Assert-Rejected 'negative-project' @($project) 'project-reference'
    $template = Copy-Candidate 'bundled-template' $candidate
    Add-Entry $template 'content/Template.csproj' '<Project><ItemGroup><PackageReference Include="AngleSharp" Version="1.8.3" /></ItemGroup></Project>'
    Assert-Rejected 'negative-bundled-template' @($template) 'project-reference'

    $definition = Copy-Candidate 'renamed-definition' $candidate
    Add-Entry $definition 'lib/net10.0/innocent.dll' '' $AngleSharpAssembly
    Assert-Rejected 'negative-renamed-definition' @($definition) 'assembly-definition'
    $reference = Copy-Candidate 'reference-carrier' $candidate
    Add-Entry $reference 'lib/net10.0/innocent.dll' '' $ReferenceCarrierAssembly
    Assert-Rejected 'negative-reference-carrier' @($reference) 'assembly-reference'
    $license = Copy-Candidate 'bundled-license' $candidate
    Add-Entry $license 'licenses/AngleSharp.LICENSE.txt' 'Dependency gate fixture notice'
    Assert-Rejected 'negative-license' @($license) 'bundled-angle-sharp-file'
    $licenseDirectory = Join-Path $Output 'shipping-license-root'
    $null = New-Item -ItemType Directory -Path $licenseDirectory
    [IO.File]::WriteAllText((Join-Path $licenseDirectory 'AngleSharp.LICENSE.txt'), 'Dependency gate fixture notice')
    Assert-Rejected 'negative-directory-license' @($licenseDirectory) 'bundled-angle-sharp-file'
    $deps = Copy-Candidate 'bundled-deps' $candidate
    Add-Entry $deps 'tools/fixture.deps.json' '{"libraries":{"AngleSharp.Css/1.0.0":{"type":"package"}}}'
    Assert-Rejected 'negative-bundled-deps' @($deps) 'dependency-graph'
} catch {
    $results.Add([ordered]@{ name = 'gate-error'; passed = $false; detail = $_.Exception.Message })
    Write-Host $_.Exception.Message
}

foreach ($entry in $originals.GetEnumerator()) {
    $unchanged = (Get-FileHash -LiteralPath $entry.Key -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $entry.Value
    $results.Add([ordered]@{ name = 'source-unchanged'; path = $entry.Key; sha256 = $entry.Value; passed = $unchanged })
}
$failed = @($results | Where-Object { !$_.passed }).Count
$report = [ordered]@{ schemaVersion = '1.0'; passed = $failed -eq 0; failed = $failed; results = @($results);
    shippingInputsProvided = $ShippingPaths.Count -gt 0; mutationCandidate = $candidate; assemblyFixtureMode = $assemblyFixtureMode; originalInputHashes = $originals }
[IO.File]::WriteAllText((Join-Path $Output 'dependency-zero-gate.json'), ($report | ConvertTo-Json -Depth 10))
Write-Host "Dependency zero gate: passed=$($report.passed) failed=$failed"
if ($failed -gt 0) { exit 1 }
exit 0
