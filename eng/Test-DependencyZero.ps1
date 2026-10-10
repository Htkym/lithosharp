<#
.SYNOPSIS
Rejects AngleSharp identities in explicitly selected shipping inputs.
.EXAMPLE
./eng/Test-DependencyZero.ps1 -RepositoryGraph -Paths artifacts/packages, extensions/lithosharp-vscode/resources/language-server -Output .tmp/dependency-zero.json
#>
[CmdletBinding()]
param(
    [string[]] $Paths = @(),
    [switch] $RepositoryGraph,
    [string] $Output
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Reflection.Metadata
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$files = [Collections.Generic.List[object]]::new()
$inspections = [Collections.Generic.List[object]]::new()
$findings = [Collections.Generic.List[object]]::new()

function Forbidden([string] $Name) { return $Name -match '^AngleSharp(?:\..+)?$' }
function Finding([string] $Path, [string] $Reason, [string] $Identity) {
    $findings.Add([ordered]@{ path = $Path; reason = $Reason; identity = $Identity })
}
function Supported([string] $Path) {
    return $Path -match '\.(csproj|props|dll|nupkg|snupkg|vsix)$' -or
        [IO.Path]::GetFileName($Path) -match '^AngleSharp(?:\..+)?\.LICENSE\.txt$' -or
        [IO.Path]::GetFileName($Path) -in @('packages.lock.json', 'project.assets.json') -or $Path.EndsWith('.deps.json', [StringComparison]::OrdinalIgnoreCase)
}
function Select-Tree([string] $Root, [bool] $GraphOnly) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Shipping input must not traverse a link: $($item.FullName)" }
            if ($item.PSIsContainer) {
                if ($GraphOnly -and $item.Name -in @('bin', 'obj', 'node_modules', '.git')) { continue }
                $pending.Push($item.FullName)
            } elseif ($GraphOnly) {
                if ($item.Extension -in @('.csproj', '.props') -or $item.Name -eq 'packages.lock.json') {
                    $null = $selected.Add($item.FullName)
                    if ($item.Extension -eq '.csproj') {
                        $assets = Join-Path $directory 'obj/project.assets.json'
                        if (Test-Path -LiteralPath $assets -PathType Leaf) { $null = $selected.Add($assets) }
                    }
                }
            } elseif (Supported $item.FullName) { $null = $selected.Add($item.FullName) }
        }
    }
}
function Inspect-Xml([string] $Text, [string] $Path, [bool] $Nuspec) {
    [xml] $xml = $Text
    foreach ($element in $xml.SelectNodes('//*')) {
        if ($Nuspec -and $element.LocalName -eq 'dependency') {
            $identity = $element.GetAttribute('id')
            if (Forbidden $identity) { Finding $Path 'nuspec-dependency' $identity }
        } elseif (!$Nuspec -and $element.LocalName -in @('PackageReference', 'PackageVersion', 'Reference')) {
            foreach ($attribute in @('Include', 'Update')) {
                foreach ($identity in $element.GetAttribute($attribute).Split(';')) {
                    $name = ($identity.Split(',')[0]).Trim()
                    if (Forbidden $name) { Finding $Path 'project-reference' $name }
                }
            }
        }
    }
}
function Inspect-JsonNode($Node, [string] $Path) {
    if ($Node -is [Collections.IDictionary]) {
        foreach ($key in $Node.Keys) {
            $identity = ([string]$key).Split('/')[0]
            if (Forbidden $identity) { Finding $Path 'dependency-graph' ([string]$key) }
            Inspect-JsonNode $Node[$key] $Path
        }
    } elseif ($Node -is [Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($value in $Node) { Inspect-JsonNode $value $Path }
    }
}
function Inspect-Assembly([IO.Stream] $Stream, [string] $Path) {
    $reader = [Reflection.PortableExecutable.PEReader]::new($Stream, [Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
    try {
        if (!$reader.HasMetadata) { return @{ kind = 'native'; assemblyDefinition = $null; assemblyReferences = @() } }
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reader)
        $definitionName = $null
        $references = [Collections.Generic.List[string]]::new()
        if ($metadata.IsAssembly) {
            $definition = $metadata.GetAssemblyDefinition()
            $name = $metadata.GetString($definition.Name)
            $definitionName = $name
            if (Forbidden $name) { Finding $Path 'assembly-definition' $name }
        }
        foreach ($handle in $metadata.AssemblyReferences) {
            $reference = $metadata.GetAssemblyReference($handle)
            $name = $metadata.GetString($reference.Name)
            $references.Add($name)
            if (Forbidden $name) { Finding $Path 'assembly-reference' $name }
        }
        return @{ kind = 'managed'; assemblyDefinition = $definitionName; assemblyReferences = @($references) }
    } finally { $reader.Dispose() }
}
function Inspect-Archive([string] $Path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            $entryPath = "$Path!$name"
            if ([IO.Path]::GetFileName($name) -match '^AngleSharp(?:\..+)?\.(dll|LICENSE\.txt)$') {
                Finding $entryPath 'bundled-angle-sharp-file' ([IO.Path]::GetFileName($name))
            }
            $projectXml = $name -match '\.(csproj|props)$'
            $graphJson = [IO.Path]::GetFileName($name) -in @('packages.lock.json', 'project.assets.json') -or $name.EndsWith('.deps.json', [StringComparison]::OrdinalIgnoreCase)
            if ($name -notmatch '\.(dll|nuspec)$' -and !$projectXml -and !$graphJson) { continue }
            $source = $entry.Open()
            $memory = [IO.MemoryStream]::new()
            try {
                $source.CopyTo($memory)
                $memory.Position = 0
                $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($memory)).ToLowerInvariant()
                $memory.Position = 0
                $kind = 'dependency-manifest'
                $assembly = $null
                if ($name -match '\.dll$') { $assembly = Inspect-Assembly $memory $entryPath; $kind = $assembly.kind }
                else {
                    $textReader = [IO.StreamReader]::new($memory, [Text.Encoding]::UTF8, $true, 1024, $true)
                    try { $text = $textReader.ReadToEnd() } finally { $textReader.Dispose() }
                    if ($name -match '\.nuspec$' -or $projectXml) { Inspect-Xml $text $entryPath (!$projectXml) }
                    else { Inspect-JsonNode (ConvertFrom-Json -InputObject $text -AsHashtable) $entryPath }
                }
                $inspection = [ordered]@{ path = $entryPath; bytes = $entry.Length; sha256 = $sha; kind = $kind }
                if ($assembly) { $inspection.assemblyDefinition = $assembly.assemblyDefinition; $inspection.assemblyReferences = $assembly.assemblyReferences }
                $inspections.Add($inspection)
            } finally { $source.Dispose(); $memory.Dispose() }
        }
    } finally { $archive.Dispose() }
}

try {
    foreach ($inputPath in $Paths) {
        $item = Get-Item -LiteralPath $inputPath -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Shipping input is a link: $inputPath" }
        if ($item.PSIsContainer) { Select-Tree $item.FullName $false }
        elseif (Supported $item.FullName) { $null = $selected.Add($item.FullName) }
        else { throw "Unsupported shipping input: $inputPath" }
    }
    if ($RepositoryGraph) {
        Select-Tree (Join-Path $repo 'src') $true
        foreach ($name in @('Directory.Build.props', 'Directory.Packages.props')) {
            $path = Join-Path $repo $name
            if (Test-Path -LiteralPath $path -PathType Leaf) { $null = $selected.Add($path) }
        }
    }
    if ($selected.Count -eq 0) { throw 'No shipping inputs selected.' }
    foreach ($path in @($selected | Sort-Object)) {
        $item = Get-Item -LiteralPath $path
        $files.Add([ordered]@{ path = $path; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() })
        try {
            if ([IO.Path]::GetFileName($path) -match '^AngleSharp(?:\..+)?\.LICENSE\.txt$') {
                Finding $path 'bundled-angle-sharp-file' ([IO.Path]::GetFileName($path))
            } elseif ($path -match '\.(nupkg|snupkg|vsix)$') { Inspect-Archive $path }
            elseif ($path -match '\.dll$') {
                if (Forbidden ([IO.Path]::GetFileNameWithoutExtension($path))) { Finding $path 'bundled-angle-sharp-file' ([IO.Path]::GetFileName($path)) }
                $stream = [IO.File]::OpenRead($path)
                try { $assembly = Inspect-Assembly $stream $path } finally { $stream.Dispose() }
                $inspections.Add([ordered]@{ path = $path; kind = $assembly.kind; assemblyDefinition = $assembly.assemblyDefinition; assemblyReferences = $assembly.assemblyReferences })
            } elseif ($path -match '\.(csproj|props)$') { Inspect-Xml (Get-Content -LiteralPath $path -Raw) $path $false }
            else { Inspect-JsonNode (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable) $path }
        } catch { Finding $path 'unreadable-shipping-input' $_.Exception.Message }
    }
} catch { Finding '' 'invalid-input-selection' $_.Exception.Message }

$report = [ordered]@{ schemaVersion = '1.0'; passed = $findings.Count -eq 0; fileCount = $files.Count;
    inspectedPayloadCount = $inspections.Count; files = @($files); inspections = @($inspections); findings = @($findings);
    scope = 'Explicit shipping inputs and optional src project graph; historical oracle/docs are not selected.' }
if ($Output) {
    $outputPath = [IO.Path]::GetFullPath($Output)
    $null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($outputPath))
    [IO.File]::WriteAllText($outputPath, ($report | ConvertTo-Json -Depth 9))
}
foreach ($finding in $findings) { Write-Host "$($finding.reason): $($finding.identity) [$($finding.path)]" }
Write-Host "Dependency zero: passed=$($report.passed) files=$($files.Count) payloads=$($inspections.Count) findings=$($findings.Count)"
if (!$report.passed) { throw "Dependency zero failed: $($findings.Count) finding(s)." }
$global:LASTEXITCODE = 0
