[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Environment', 'Artifact')] [string] $Check,
    [string] $EnvironmentFile,
    [string] $Bundle,
    [string] $ExpectedSHA256,
    [string] $SourceCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Check -eq 'Environment') {
    $policy = Get-Content -LiteralPath $EnvironmentFile -Raw | ConvertFrom-Json
    $approval = @($policy.protection_rules | Where-Object { $_.type -ceq 'required_reviewers' })
    if ($approval.Count -ne 1 -or @($approval[0].reviewers).Count -eq 0) {
        throw 'The marketplace environment must have at least one required reviewer.'
    }
    Write-Host 'Marketplace required-reviewer gate is configured.'
    return
}

if ($ExpectedSHA256 -cnotmatch '^[a-f0-9]{64}$' -or $SourceCommit -cnotmatch '^[a-f0-9]{40}$') {
    throw 'Expected producer SHA256 and source commit are required.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package = Get-Content -LiteralPath (Join-Path $repo 'extensions/lithosharp-vscode/package.json') -Raw | ConvertFrom-Json
$expectedFile = "lithosharp-$($package.version).vsix"
$manifest = Get-Content -LiteralPath (Join-Path $Bundle 'installed-extension-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -cne '1.0' -or $manifest.sourceCommit -cne $SourceCommit) {
    throw 'The artifact manifest does not belong to this source commit.'
}
if ($manifest.vsix.file -cne $expectedFile -or $manifest.vsix.sha256 -cne $ExpectedSHA256) {
    throw 'The artifact manifest differs from the certified producer identity.'
}
# The manifest never supplies a path: only the source version selects the file.
$vsix = [IO.Path]::GetFullPath((Join-Path $Bundle "vsix-validation/$expectedFile"))
if ((Get-FileHash -LiteralPath $vsix -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedSHA256) {
    throw 'The VSIX bytes differ from the certified producer SHA256.'
}
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::OpenRead($vsix)
try {
    foreach ($name in @('extension/package.json', 'extension.vsixmanifest')) {
        $entries = @($zip.Entries | Where-Object { $_.FullName -ceq $name })
        if ($entries.Count -ne 1) { throw "The VSIX must contain exactly one $name." }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($name -ceq 'extension/package.json') {
            $identity = $text | ConvertFrom-Json
            if ($identity.name -cne $package.name -or $identity.publisher -cne $package.publisher -or $identity.version -cne $package.version) {
                throw 'The packaged extension identity differs from the source manifest.'
            }
        } else {
            [xml] $xml = $text
            $identity = $xml.SelectSingleNode('/*[local-name()="PackageManifest"]/*[local-name()="Metadata"]/*[local-name()="Identity"]')
            if (!$identity -or $identity.Id -cne $package.name -or $identity.Publisher -cne $package.publisher -or $identity.Version -cne $package.version) {
                throw 'The VSIX Marketplace identity differs from the source manifest.'
            }
        }
    }
} finally { $zip.Dispose() }
Write-Output $vsix
