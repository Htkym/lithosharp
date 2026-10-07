[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package = Get-Content -LiteralPath (Join-Path $repo 'extensions/lithosharp-vscode/package.json') -Raw | ConvertFrom-Json
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('lithosharp-publish-guards-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path (Join-Path $fixture 'vsix-validation')
$commit = '1234567890123456789012345678901234567890'
$vsix = Join-Path $fixture "vsix-validation/lithosharp-$($package.version).vsix"
$manifestPath = Join-Path $fixture 'installed-extension-manifest.json'
$policyPath = Join-Path $fixture 'marketplace-policy.json'
$passed = 0

function Reject([scriptblock] $Action, [string] $Reason) {
    $caught = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notlike "*$Reason*") { throw }
        $caught = $true
    }
    if (!$caught) { throw "Expected rejection: $Reason" }
    $script:passed++
}
function New-Artifact([string] $Publisher = $package.publisher, [string] $XmlPublisher = $package.publisher) {
    # Replace only this test's exact owned file; never touch product artifacts.
    $file = [IO.File]::Open($vsix, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $contents = @{
            'extension/package.json' = (@{ name = $package.name; publisher = $Publisher; version = $package.version } | ConvertTo-Json)
            'extension.vsixmanifest' = "<PackageManifest xmlns=`"http://schemas.microsoft.com/developer/vsx-schema/2011`"><Metadata><Identity Id=`"$($package.name)`" Publisher=`"$XmlPublisher`" Version=`"$($package.version)`" /></Metadata></PackageManifest>"
        }
        foreach ($name in $contents.Keys) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try { $writer.Write($contents[$name]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose(); $file.Dispose() }
    $hash = (Get-FileHash -LiteralPath $vsix -Algorithm SHA256).Hash.ToLowerInvariant()
    @{ schemaVersion = '1.0'; sourceCommit = $commit; vsix = @{ file = [IO.Path]::GetFileName($vsix); sha256 = $hash } } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    return $hash
}
function Check-Artifact([string] $Hash, [string] $Commit = $commit) {
    & (Join-Path $PSScriptRoot 'Test-ExtensionPublish.ps1') -Check Artifact -Bundle $fixture -ExpectedSHA256 $Hash -SourceCommit $Commit
}

'{"protection_rules":[]}' | Set-Content -LiteralPath $policyPath
Reject { & "$PSScriptRoot/Test-ExtensionPublish.ps1" -Check Environment -EnvironmentFile $policyPath } 'at least one required reviewer'
'{"protection_rules":[{"type":"required_reviewers","reviewers":[]}]}' | Set-Content -LiteralPath $policyPath
Reject { & "$PSScriptRoot/Test-ExtensionPublish.ps1" -Check Environment -EnvironmentFile $policyPath } 'at least one required reviewer'
'{"protection_rules":[{"type":"required_reviewers","reviewers":[{"type":"User","reviewer":{"login":"maintainer"}}]}]}' | Set-Content -LiteralPath $policyPath
& "$PSScriptRoot/Test-ExtensionPublish.ps1" -Check Environment -EnvironmentFile $policyPath
$passed++

$hash = New-Artifact
if ((Check-Artifact $hash) -cne [IO.Path]::GetFullPath($vsix)) { throw 'Valid artifact did not return its exact path.' }
$passed++
Reject { Check-Artifact '' } 'Expected producer SHA256'
Reject { Check-Artifact $hash ('0' * 40) } 'does not belong to this source commit'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.vsix.file = '../other.vsix'
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath
Reject { Check-Artifact $hash } 'certified producer identity'
$hash = New-Artifact
Reject { Check-Artifact ('0' * 64) } 'certified producer identity'
[IO.File]::AppendAllText($vsix, 'modified during transfer')
Reject { Check-Artifact $hash } 'VSIX bytes differ'
$hash = New-Artifact -Publisher 'other'
Reject { Check-Artifact $hash } 'packaged extension identity'
$hash = New-Artifact -XmlPublisher 'other'
Reject { Check-Artifact $hash } 'VSIX Marketplace identity'
Reject { & "$PSScriptRoot/Test-ReleasePipeline.ps1" -Check ExtensionTag -Tag 'extension/lithosharp-vscode/9.9.9' } 'does not match package version'
Write-Host "Extension publication guards passed: $passed checks; benign fixtures retained at $fixture. No network or publication."
