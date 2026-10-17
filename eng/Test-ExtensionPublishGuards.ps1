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

$workflow = Get-Content -LiteralPath (Join-Path $repo '.github/workflows/publish-extension.yml') -Raw
$pipelineFixture = Join-Path $fixture 'pipeline'
foreach ($relative in @('eng/Test-ReleasePipeline.ps1', 'eng/Test-VsixContents.ps1', 'extensions/lithosharp-vscode/package.json')) {
    $target = Join-Path $pipelineFixture $relative
    $null = New-Item -ItemType Directory -Path (Split-Path $target) -Force
    Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $target
}
$fixtureWorkflow = Join-Path $pipelineFixture '.github/workflows/publish-extension.yml'
$null = New-Item -ItemType Directory -Path (Split-Path $fixtureWorkflow) -Force
function Check-Workflow([string] $Text) {
    $Text | Set-Content -LiteralPath $fixtureWorkflow -Encoding utf8
    & (Join-Path $pipelineFixture 'eng/Test-ReleasePipeline.ps1') -Check ExtensionTag
}
Check-Workflow $workflow
$passed++
Reject { Check-Workflow ($workflow.Replace('--azure-credential', '')) } 'without PAT or client-secret fallback'
Reject { Check-Workflow ($workflow.Replace("VSCE_PAT: ''", 'VSCE_PAT: ${{ secrets.VSCE_PAT }}')) } 'without PAT or client-secret fallback'
Reject { Check-Workflow ($workflow -replace 'azure/login@[0-9a-f]{40}', 'azure/login@v3') } 'pinned Azure login'
Reject { Check-Workflow ($workflow.Replace('allow-no-subscriptions: true', 'allow-no-subscriptions: false')) } 'pinned Azure login'
Reject { Check-Workflow ($workflow.Replace('secrets.MARKETPLACE_AZURE_TENANT_ID', 'secrets.OTHER_TENANT_ID')) } 'configured environment secrets'
Reject { Check-Workflow ($workflow.Replace('  actions: read', "  actions: read`n  id-token: write")) } 'Only the publish job'

$configuration = [regex]::Match($workflow, '(?ms)^      - name: Check Marketplace federation configuration\r?\n        shell: pwsh\r?\n        run: \|\r?\n(.*?)(?=^      - )')
if (!$configuration.Success) { throw 'Marketplace federation configuration step not found.' }
$checkConfiguration = [scriptblock]::Create(($configuration.Groups[1].Value -replace '(?m)^          ', ''))
$savedClient = $env:AZURE_CLIENT_ID
$savedTenant = $env:AZURE_TENANT_ID
try {
    $env:AZURE_CLIENT_ID = '11111111-1111-1111-1111-111111111111'
    $env:AZURE_TENANT_ID = '22222222-2222-2222-2222-222222222222'
    & $checkConfiguration
    $passed++
    $env:AZURE_CLIENT_ID = ''
    Reject { & $checkConfiguration } 'AZURE_CLIENT_ID must be configured'
    $env:AZURE_CLIENT_ID = '11111111-1111-1111-1111-111111111111'
    $env:AZURE_TENANT_ID = 'invalid'
    Reject { & $checkConfiguration } 'AZURE_TENANT_ID must be configured'
    $env:AZURE_TENANT_ID = [guid]::Empty.ToString()
    Reject { & $checkConfiguration } 'AZURE_TENANT_ID must be configured'
} finally {
    $env:AZURE_CLIENT_ID = $savedClient
    $env:AZURE_TENANT_ID = $savedTenant
}
Write-Host "Extension publication guards passed: $passed checks; benign fixtures retained at $fixture. No network or publication."
