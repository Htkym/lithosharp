[CmdletBinding()]
param([Parameter(Mandatory)] [string] $PackageDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$pair = Get-Content -LiteralPath (Join-Path $repo 'eng/markdown/component-pair.json') -Raw | ConvertFrom-Json
$feed = Join-Path $PSScriptRoot 'feed'
& (Join-Path $repo 'eng/Validate-Package.ps1') -PackageDirectory $feed -PackageId Syntamark -ExpectedVersion $pair.componentVersion
$null = New-Item -ItemType Directory -Force -Path $PackageDirectory
$source = Join-Path $feed $pair.artifacts.runtime.fileName
$destination = Join-Path $PackageDirectory $pair.artifacts.runtime.fileName
if (Test-Path -LiteralPath $destination) {
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pair.artifacts.runtime.sha256) {
        throw 'Refusing to overwrite different bytes under the fixed runtime identity.'
    }
} else { Copy-Item -LiteralPath $source -Destination $destination }
if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pair.artifacts.runtime.sha256) {
    throw 'Copied runtime differs from the immutable pair.'
}
