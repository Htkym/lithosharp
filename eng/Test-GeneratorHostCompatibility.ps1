<#
.SYNOPSIS
Verifies that the LithoSharp source generator can load in the active .NET SDK compiler.

.DESCRIPTION
A Roslyn analyzer/generator is refused with CS9057 when it references a newer
Microsoft.CodeAnalysis than the running compiler. That refusal is silent for the
generator: the build continues and generated members simply disappear, which then
shows up as unrelated CS0117/CS1503 errors in consumer projects.

This check compares the Microsoft.CodeAnalysis assembly versions referenced by the
generator against the compiler that ships with the selected SDK and fails with an
actionable message. Run it after building the generator (Release).

.EXAMPLE
& ./eng/Test-GeneratorHostCompatibility.ps1
#>
[CmdletBinding()]
param(
    [string] $GeneratorPath,
    [string] $SdkVersion,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$GeneratorPath) { $GeneratorPath = Join-Path $repo 'src/LithoSharp.Generators/bin/Release/netstandard2.0/LithoSharp.Generators.dll' }
$GeneratorPath = [IO.Path]::GetFullPath($GeneratorPath)
if (!(Test-Path -LiteralPath $GeneratorPath -PathType Leaf)) { throw "Generator assembly not found: $GeneratorPath (build it in Release first)." }

if (!$SdkVersion) { $SdkVersion = (& dotnet --version).Trim() }
$sdkLines = @(& dotnet --list-sdks)
$sdkRoot = $null
foreach ($line in $sdkLines) {
    if ($line -match '^' + [regex]::Escape($SdkVersion) + '\s+\[(.+)\]\s*$') { $sdkRoot = $Matches[1]; break }
}
if (!$sdkRoot) { throw "SDK $SdkVersion was not found in 'dotnet --list-sdks'." }
$compilerAssembly = Join-Path (Join-Path $sdkRoot $SdkVersion) 'Roslyn/bincore/Microsoft.CodeAnalysis.dll'
if (!(Test-Path -LiteralPath $compilerAssembly -PathType Leaf)) { throw "Compiler assembly not found for SDK ${SdkVersion}: $compilerAssembly" }

function Get-ReferencedAssemblyVersions([string] $Path) {
    Add-Type -AssemblyName System.Reflection.Metadata -ErrorAction SilentlyContinue
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reader)
            $references = [ordered]@{}
            foreach ($handle in $metadata.AssemblyReferences) {
                $reference = $metadata.GetAssemblyReference($handle)
                $references[$metadata.GetString($reference.Name)] = $reference.Version
            }
            return $references
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

$compilerVersion = [Reflection.AssemblyName]::GetAssemblyName($compilerAssembly).Version
$references = Get-ReferencedAssemblyVersions $GeneratorPath
$checked = @('Microsoft.CodeAnalysis', 'Microsoft.CodeAnalysis.CSharp')
$violations = [Collections.Generic.List[string]]::new()
$found = 0
foreach ($name in $checked) {
    if (!$references.Contains($name)) { continue }
    $found++
    $referenced = [Version]$references[$name]
    if ($referenced -gt $compilerVersion) {
        $violations.Add("$name $referenced > compiler $compilerVersion")
    }
}
if ($found -eq 0) {
    $violations.Add('The generator references neither Microsoft.CodeAnalysis nor Microsoft.CodeAnalysis.CSharp, so host compatibility cannot be proven.')
}

if (!$Quiet) {
    Write-Host ("generator : {0}" -f ([IO.Path]::GetRelativePath($repo, $GeneratorPath).Replace('\', '/')))
    Write-Host ("sdk       : {0} ({1})" -f $SdkVersion, $sdkRoot)
    Write-Host ("compiler  : Microsoft.CodeAnalysis {0}" -f $compilerVersion)
    foreach ($name in $checked) {
        if ($references.Contains($name)) { Write-Host ("reference : {0} {1}" -f $name, $references[$name]) }
    }
}

if ($violations.Count -gt 0) {
    Write-Host 'generator host compatibility: FAILED'
    foreach ($violation in $violations) { Write-Host ("  {0}" -f $violation) }
    Write-Host ("Pin Microsoft.CodeAnalysis.CSharp to a version at or below the compiler of the documented minimum SDK ({0}), or raise the documented minimum SDK and global.json together." -f $compilerVersion)
    exit 1
}

if (!$Quiet) { Write-Host ("generator host compatibility: OK (generator references <= compiler {0})" -f $compilerVersion) }
exit 0
