param(
    [Parameter(Mandatory)][string[]]$XmlFiles,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$units = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$skip = @('code', 'c', 'paramref', 'typeparamref', 'see', 'seealso', 'inheritdoc')
function Visit-Documentation([Xml.XmlElement]$element, [string]$context) {
    if ($element.LocalName -in $skip) { return }
    $parts = [Collections.Generic.List[string]]::new()
    $index = 0
    foreach ($node in $element.ChildNodes) {
        if ($node -is [Xml.XmlElement]) {
            Visit-Documentation $node ($context + '/' + $node.LocalName)
            $parts.Add('{' + $index + '}')
            $index++
        } elseif ($node -is [Xml.XmlText] -or $node -is [Xml.XmlWhitespace] -or $node -is [Xml.XmlSignificantWhitespace]) {
            $parts.Add($node.Value)
        }
    }
    $pattern = [regex]::Replace(($parts -join ''), '\s+', ' ').Trim()
    if ([regex]::Replace($pattern, '\{\d+\}', '') -notmatch '[A-Za-z]') { return }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($pattern))).ToLowerInvariant()
    if (!$units.ContainsKey($hash)) {
        $units[$hash] = @{ english = $pattern; japanese = $null; contexts = [Collections.Generic.List[string]]::new() }
    }
    if ($units[$hash].contexts.Count -lt 3) { $units[$hash].contexts.Add($context) }
}
foreach ($file in $XmlFiles) {
    [xml]$document = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $file).Path)
    foreach ($member in $document.doc.members.member) {
        foreach ($section in $member.ChildNodes | Where-Object { $_ -is [Xml.XmlElement] }) {
            Visit-Documentation $section ($member.name + '/' + $section.LocalName)
        }
    }
}
$result = [ordered]@{ schemaVersion = 1; units = [ordered]@{} }
foreach ($key in $units.Keys | Sort-Object) { $result.units[$key] = $units[$key] }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Output), ($result | ConvertTo-Json -Depth 10) + "`n")
Write-Output "Exported $($units.Count) distinct documentation translation units."
