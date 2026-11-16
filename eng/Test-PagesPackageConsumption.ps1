[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SiteRoot,
    [Parameter(Mandatory)][string] $PackageCache,
    [Parameter(Mandatory)][string] $PublishedPackageDirectory,
    [Parameter(Mandatory)][string] $Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($relative in @('obj/project.assets.json', 'checks/obj/project.assets.json')) {
    $assets = Get-Content -LiteralPath (Join-Path $SiteRoot $relative) -Raw | ConvertFrom-Json -AsHashtable
    $consumed = @($assets.libraries.Keys | Where-Object { $_ -cmatch '^LithoSharp(?:\.[^/]+)?/' })
    if (!$consumed.Count) { throw "No restored LithoSharp packages in $relative." }
    foreach ($identity in $consumed) {
        $id, $resolved = $identity.Split('/')
        if ($resolved -cne $Version) { throw "Restored $identity does not match Pages release version $Version." }
        $lower = $id.ToLowerInvariant()
        $name = "$lower.$Version.nupkg"
        $restored = Join-Path $PackageCache "$lower/$Version/$name"
        $published = Join-Path $PublishedPackageDirectory $name
        if (!(Test-Path -LiteralPath $restored -PathType Leaf) -or !(Test-Path -LiteralPath $published -PathType Leaf) -or
            (Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash) {
            throw "Restored $identity archive differs from the verified official package."
        }
    }
}
Write-Host "Both site consumers use the exact verified official LithoSharp $Version archives."
