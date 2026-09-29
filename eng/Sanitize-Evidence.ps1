<#
.SYNOPSIS
Sanitizes verification evidence for public distribution.

.DESCRIPTION
Copies an evidence tree, replaces machine-specific absolute roots with stable
tokens, and scans for secrets. Stage results, counts and statuses are copied
byte-for-byte otherwise: the sanitizer never drops runs or failures to make
statistics look better. Existing files are never overwritten.

Checks (all reported, any hit fails the run unless -AllowSecrets):
- token/password/secret/cookie style assignments and bearer tokens
- private URLs (RFC 1918 / loopback literals are allowed only for the documented
  loopback serve origin 127.0.0.1; other IP literals and intranet hosts fail)
- personal profile paths (C:\Users\<name>, /home/<name>) outside the declared roots
- unresolved absolute paths under a declared root that was not replaced
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InputPath,
    [Parameter(Mandatory)] [string] $OutputPath,
    [string[]] $RootToken, # each 'token=absolute-path', e.g. 'repo=D:\gitroot\lithosharp'
    [switch] $AllowSecrets
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$roots = [Collections.Generic.List[object]]::new()
# Values may also arrive as one ';'-separated string when the launcher joins
# array arguments; ';' never appears in the supported absolute root paths.
$rootSpecs = [Collections.Generic.List[string]]::new()
foreach ($entry in @($RootToken)) {
    foreach ($part in ([string]$entry).Split(';')) {
        if ($part.Length -gt 0) { $null = $rootSpecs.Add($part) }
    }
}
foreach ($pair in $rootSpecs) {
    $split = $pair.IndexOf('=')
    if ($split -lt 1) { throw "RootToken must look like 'name=absolute-path': $pair" }
    $roots.Add([ordered]@{
        token = $pair.Substring(0, $split)
        path = [IO.Path]::GetFullPath($pair.Substring($split + 1)).Replace('\', '/')
    })
}
# Longest root first so nested roots replace correctly.
$roots = @($roots | Sort-Object { $_.path.Length } -Descending)

$source = [IO.Path]::GetFullPath($InputPath)
$destination = [IO.Path]::GetFullPath($OutputPath)
if (!(Test-Path -LiteralPath $source -PathType Container)) { throw "Input directory not found: $source" }
if (!(Test-Path -LiteralPath $destination -PathType Container)) { $null = New-Item -ItemType Directory -Path $destination }

function Convert-Roots([string] $Text) {
    $result = $Text
    foreach ($root in $roots) {
        $forward = $root.path
        $backward = $root.path.Replace('/', '\')
        $escaped = $root.path.Replace('/', '\\')
        foreach ($form in @($forward, $backward, $escaped)) {
            $result = $result.Replace($form, "<$($root.token)>")
        }
    }
    return $result
}

$findings = [Collections.Generic.List[object]]::new()
function Add-Finding([string] $File, [int] $Line, [string] $Rule) {
    # Never echo the matched text: logs must not carry the secret itself.
    $findings.Add([ordered]@{ file = $File; line = $Line; rule = $Rule })
}

$secretPatterns = @(
    @{ rule = 'secret-assignment'; pattern = '(?i)\b(token|password|passwd|secret|api[_-]?key|auth[_-]?token|cookie|set-cookie)\b\s*[:=]' },
    @{ rule = 'bearer-token'; pattern = '(?i)\bbearer\s+[A-Za-z0-9\-._~+/]+' },
    @{ rule = 'private-url'; pattern = '(?i)https?://(?!127\.0\.0\.1(?::|/)|localhost(?::|/))([A-Za-z0-9_.-]*\.(local|internal|intranet|lan|corp)([.:/]|$)|(local|internal|intranet|lan|corp)([.:/]|$)|10\.\d|172\.(1[6-9]|2\d|3[01])\.|192\.168\.)' },
    @{ rule = 'personal-path'; pattern = '(?i)(C:/Users/[^/\"''\s]+|/home/[^/\"''\s]+|/Users/[^/\"''\s]+)' }
)

foreach ($file in (Get-ChildItem -LiteralPath $source -Recurse -File -Force | Sort-Object FullName)) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
    $target = Join-Path $destination $relative
    if (Test-Path -LiteralPath $target) { throw "Refusing to overwrite existing file: $target" }
    $null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target))
    $text = [IO.File]::ReadAllText($file.FullName)
    $converted = Convert-Roots $text
    # Canonical committed form is LF: Windows tooling emits CRLF, which would
    # make committed bytes depend on the checkout platform.
    [IO.File]::WriteAllText($target, $converted.Replace("`r`n", "`n"))

    $lineNumber = 0
    foreach ($line in ($converted -split "`n")) {
        $lineNumber++
        foreach ($entry in $secretPatterns) {
            if ($line -match $entry.pattern) { Add-Finding $relative $lineNumber $entry.rule }
        }
        foreach ($root in $roots) {
            if ($line.Contains($root.path)) { Add-Finding $relative $lineNumber 'unresolved-root' }
        }
    }
}

# Absolute Windows drive paths that survive replacement are personal or machine
# state and must not ship.
foreach ($file in (Get-ChildItem -LiteralPath $destination -Recurse -File -Force | Sort-Object FullName)) {
    $relative = [IO.Path]::GetRelativePath($destination, $file.FullName).Replace('\', '/')
    $lineNumber = 0
    foreach ($line in (([IO.File]::ReadAllText($file.FullName)) -split "`n")) {
        $lineNumber++
        if ($line -match '^[A-Za-z]:[\\/]' -or $line -match '[^<":A-Za-z][A-Za-z]:[\\/]') {
            Add-Finding $relative $lineNumber 'absolute-drive-path'
        }
    }
}

$manifest = [ordered]@{
    schemaVersion = '1.0'
    roots = @($roots | ForEach-Object { [ordered]@{ token = $_.token; note = 'machine-specific absolute prefix replaced' } })
    files = @(Get-ChildItem -LiteralPath $destination -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($destination, $_.FullName).Replace('\', '/')
    })
    findingCount = $findings.Count
    findings = @($findings)
}
[IO.File]::WriteAllText((Join-Path $destination 'sanitizer-manifest.json'), (($manifest | ConvertTo-Json -Depth 8).Replace("`r`n", "`n")))

Write-Host ("sanitize: {0} files, {1} findings" -f $manifest.files.Count, $findings.Count)
foreach ($finding in $findings) { Write-Host ("  [{0}] {1}:{2}" -f $finding.rule, $finding.file, $finding.line) }
if ($findings.Count -gt 0 -and !$AllowSecrets) { exit 1 }
