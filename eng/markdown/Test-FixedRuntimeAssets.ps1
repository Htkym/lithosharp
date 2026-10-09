[CmdletBinding()]
param([Parameter(Mandatory)] [string] $AssetsPath)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$pair = Get-Content -LiteralPath (Join-Path $repo 'eng/markdown/component-pair.json') -Raw | ConvertFrom-Json
$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
$keys = @($assets.libraries.Keys | Where-Object { $_ -match '^LithoSharp\.Markdown/' })
$expected = "Syntamark/$($pair.componentVersion)"
if ($keys.Count -ne 1 -or $keys[0] -cne $expected -or $assets.libraries[$expected].type -ne 'package') {
    throw "Expected the fixed runtime package $expected, without a ProjectReference substitute."
}
$found = $false
foreach ($folder in $assets.packageFolders.Keys) {
    $directory = Join-Path $folder "syntamark/$($pair.componentVersion)"
    $archive = Join-Path $directory $pair.artifacts.runtime.fileName.ToLowerInvariant()
    if (!(Test-Path -LiteralPath $archive)) { continue }
    $dll = Join-Path $directory 'lib/net10.0/Syntamark.dll'
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pair.artifacts.runtime.sha256 -or
        (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pair.artifacts.runtime.assemblySha256) {
        throw 'Restored runtime nupkg/DLL bytes differ from the fixed pair.'
    }
    $found = $true
}
if (!$found) { throw 'The fixed runtime archive was not found in the actual restore folders.' }
Write-Host "Verified restored fixed runtime: $expected"
