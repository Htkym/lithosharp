[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $ExpectedVersion,
    [string] $Output
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$feed = [IO.Path]::GetFullPath($PackageDirectory)
$ids = @('LithoSharp', 'LithoSharp.Generators', 'LithoSharp.Images', 'LithoSharp.Tool',
    'LithoSharp.Testing', 'LithoSharp.Mdx', 'LithoSharp.ProjectTemplates')
$expected = @($ids | ForEach-Object { "$_.$ExpectedVersion.nupkg" }) + "LithoSharp.$ExpectedVersion.snupkg"
$actual = @(Get-ChildItem -LiteralPath $feed -File | Where-Object {
    $_.Extension -in @('.nupkg', '.snupkg')
} | ForEach-Object Name)
if (@(Compare-Object $expected $actual -CaseSensitive).Count -ne 0) {
    throw 'Security gate requires exactly the seven candidate packages and matching Core symbols archive.'
}
if (!$Output) { $Output = Join-Path $repo ('.tmp/package-security-' + [Guid]::NewGuid().ToString('N')) }
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Security fixture output must be a new directory.' }
$null = New-Item -ItemType Directory -Path $Output
$validator = Join-Path $PSScriptRoot 'Validate-Package.ps1'
$before = @{}
foreach ($name in $expected) { $before[$name] = (Get-FileHash -LiteralPath (Join-Path $feed $name) -Algorithm SHA256).Hash }
$results = [Collections.Generic.List[object]]::new()

function Invoke-Validator([string] $Directory, [string] $Id) {
    $log = & pwsh -NoProfile -File $validator -PackageDirectory $Directory -PackageId $Id -ExpectedVersion $ExpectedVersion 2>&1 | Out-String
    return @{ exitCode = $LASTEXITCODE; log = $log }
}

foreach ($id in $ids) {
    $result = Invoke-Validator $feed $id
    if ($result.exitCode -ne 0) { throw "Actual candidate archive failed for ${id}: $($result.log)" }
    $results.Add(@{ name = "actual/$id"; passed = $true; exitCode = $result.exitCode })
}

function New-CoreFixture([string] $Name) {
    $directory = Join-Path $Output $Name
    $null = New-Item -ItemType Directory -Path $directory
    foreach ($extension in @('nupkg', 'snupkg')) {
        Copy-Item -LiteralPath (Join-Path $feed "LithoSharp.$ExpectedVersion.$extension") -Destination $directory
    }
    return $directory
}

function Add-ZipEntry([string] $ArchivePath, [string] $Name) {
    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry($Name).Open())
        try { $writer.Write('security fixture') }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Assert-Rejected([string] $Name, [string] $Directory, [string] $Pattern, [string] $Id = 'LithoSharp') {
    $result = Invoke-Validator $Directory $Id
    [IO.File]::WriteAllText((Join-Path $Directory 'validation.txt'), $result.log)
    if ($result.exitCode -eq 0 -or $result.log -notmatch $Pattern) {
        throw "Negative fixture '$Name' did not fail for its intended reason (exit $($result.exitCode)): $($result.log)"
    }
    $results.Add(@{ name = $Name; passed = $true; exitCode = $result.exitCode; diagnostic = $Pattern })
}

# Mutate copies of valid shipping archives, so unrelated missing metadata cannot
# make a negative test appear to work. Sweep both package and symbols payloads.
$entries = @(
    @{ label = 'private'; path = '.local/plan.md'; diagnostic = 'Package contains private' },
    @{ label = 'credential'; path = 'nested/.env.production'; diagnostic = 'Package contains private' },
    @{ label = 'credential-directory'; path = 'credentials/service.json'; diagnostic = 'Package contains private' },
    @{ label = 'key'; path = 'nested/credential.pfx'; diagnostic = 'Package contains private' },
    @{ label = 'traversal'; path = '../escape.txt'; diagnostic = 'Unsafe archive path' },
    @{ label = 'absolute'; path = '/escape.txt'; diagnostic = 'Unsafe archive path' },
    @{ label = 'drive'; path = 'C:/escape.txt'; diagnostic = 'Unsafe archive path' },
    @{ label = 'backslash'; path = 'nested\escape.txt'; diagnostic = 'Unsafe archive path' },
    @{ label = 'dot-segment'; path = 'nested/./escape.txt'; diagnostic = 'Unsafe archive path' },
    @{ label = 'windows-alias'; path = 'nested/escape.txt.'; diagnostic = 'Unsafe archive path' },
    @{ label = 'reserved-name'; path = 'nested/NUL.txt'; diagnostic = 'Unsafe archive path' },
    @{ label = 'invalid-character'; path = 'nested/a?.txt'; diagnostic = 'Unsafe archive path' },
    @{ label = 'repeated-directory-slash'; path = 'nested//'; diagnostic = 'Unsafe archive path' },
    @{ label = 'file-directory-conflict'; path = 'lib'; diagnostic = 'Conflicting archive path' }
)
foreach ($extension in @('nupkg', 'snupkg')) {
    foreach ($entry in $entries) {
        $name = "$extension/$($entry.label)"
        $directory = New-CoreFixture "$extension-$($entry.label)"
        Add-ZipEntry (Join-Path $directory "LithoSharp.$ExpectedVersion.$extension") $entry.path
        Assert-Rejected $name $directory $entry.diagnostic
    }
    $duplicate = if ($extension -eq 'nupkg') { 'README.md' } else { 'lib/net10.0/LithoSharp.pdb' }
    foreach ($variant in @('exact', 'case')) {
        $directory = New-CoreFixture "$extension-duplicate-$variant"
        $path = if ($variant -eq 'case') { $duplicate.ToUpperInvariant() } else { $duplicate }
        Add-ZipEntry (Join-Path $directory "LithoSharp.$ExpectedVersion.$extension") $path
        Assert-Rejected "$extension/duplicate-$variant" $directory 'Duplicate archive path'
    }
    $directory = New-CoreFixture "$extension-file-directory-conflict-first"
    $archivePath = Join-Path $directory "LithoSharp.$ExpectedVersion.$extension"
    Add-ZipEntry $archivePath 'conflict'
    Add-ZipEntry $archivePath 'conflict/file.txt'
    Assert-Rejected "$extension/file-directory-conflict-first" $directory 'Conflicting archive path'
}

$directory = New-CoreFixture 'symbols-filename'
Move-Item -LiteralPath (Join-Path $directory "LithoSharp.$ExpectedVersion.snupkg") -Destination (Join-Path $directory 'Other.1.0.0.snupkg')
Assert-Rejected 'symbols/filename' $directory 'Expected exactly the matching Core symbols package'

foreach ($field in @('id', 'version', 'repository-commit')) {
    $directory = New-CoreFixture "symbols-$field"
    $archive = [IO.Compression.ZipFile]::Open((Join-Path $directory "LithoSharp.$ExpectedVersion.snupkg"), [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry('LithoSharp.nuspec')
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$xml = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        if ($field -eq 'id') { $xml.package.metadata.id = 'Other' }
        elseif ($field -eq 'version') { $xml.package.metadata.version = '999.0.0' }
        else { $xml.package.metadata.repository.SetAttribute('commit', ('0' * 40)) }
        $entry.Delete()
        $writer = [IO.StreamWriter]::new($archive.CreateEntry('LithoSharp.nuspec').Open())
        try { $writer.Write($xml.OuterXml) }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
    Assert-Rejected "symbols/$field" $directory 'Core symbols nuspec identity or repository provenance differs'
}

$directory = Join-Path $Output 'other-package-private'
$null = New-Item -ItemType Directory -Path $directory
$images = "LithoSharp.Images.$ExpectedVersion.nupkg"
Copy-Item -LiteralPath (Join-Path $feed $images) -Destination $directory
Add-ZipEntry (Join-Path $directory $images) '.npmrc'
Assert-Rejected 'other-package/private' $directory 'Package contains private' 'LithoSharp.Images'

foreach ($name in $expected) {
    if ((Get-FileHash -LiteralPath (Join-Path $feed $name) -Algorithm SHA256).Hash -cne $before[$name]) {
        throw "Source candidate archive was changed: $name"
    }
}
$report = @{ schemaVersion = '1.0'; candidateVersion = $ExpectedVersion; archiveCount = $expected.Count;
    sourceArchiveHashes = $before; passed = $results.Count; failed = 0; results = @($results) }
[IO.File]::WriteAllText((Join-Path $Output 'package-security.json'), ($report | ConvertTo-Json -Depth 7))
Write-Host "Package security gate passed: $($ids.Count) actual package validators (8 archives), $($results.Count - $ids.Count) negative cases; source hashes unchanged. Evidence: $Output"

# Expected negative child exits must not become the successful gate exit code.
exit 0
