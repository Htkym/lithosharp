param(
    [Parameter(Mandatory)][string[]]$AssemblyFiles,
    [Parameter(Mandatory)][string]$RoslynDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$null = [Reflection.Assembly]::LoadFrom((Join-Path $RoslynDirectory 'Microsoft.CodeAnalysis.dll'))
$null = [Reflection.Assembly]::LoadFrom((Join-Path $RoslynDirectory 'Microsoft.CodeAnalysis.CSharp.dll'))
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$selected = [Collections.Generic.Dictionary[string, Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($path in ([AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES') -split [IO.Path]::PathSeparator) + $AssemblyFiles) {
    $absolute = [IO.Path]::GetFullPath($path)
    if ($paths.Add($absolute)) { $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($absolute)) }
}
foreach ($path in $AssemblyFiles) {
    $absolute = [IO.Path]::GetFullPath($path)
    $selected[$absolute] = $references | Where-Object FilePath -EQ $absolute | Select-Object -First 1
}
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('WebsiteApiInventory', $null, $references, $null)
$typeMembers = [Microsoft.CodeAnalysis.INamespaceOrTypeSymbol].GetMethod('GetTypeMembers', [Type[]]@())
$members = [Microsoft.CodeAnalysis.INamespaceOrTypeSymbol].GetMethod('GetMembers', [Type[]]@())
$namespaceMembers = [Microsoft.CodeAnalysis.INamespaceSymbol].GetMethod('GetNamespaceMembers', [Type[]]@())
$documentationId = [Microsoft.CodeAnalysis.ISymbol].GetMethod('GetDocumentationCommentId', [Type[]]@())
function Is-Exported([Microsoft.CodeAnalysis.ISymbol]$symbol) {
    if ($symbol.DeclaredAccessibility.ToString() -notin @('Public', 'Protected', 'ProtectedOrInternal')) { return $false }
    for ($parent = $symbol.ContainingType; $null -ne $parent; $parent = $parent.ContainingType) {
        if ($parent.DeclaredAccessibility.ToString() -notin @('Public', 'Protected', 'ProtectedOrInternal')) { return $false }
    }
    return $true
}
function Add-TypeMembers([Microsoft.CodeAnalysis.INamedTypeSymbol]$type, $ids) {
    if (!(Is-Exported $type)) { return }
    $id = $documentationId.Invoke($type, @())
    if ($id) { $null = $ids.Add($id) }
    foreach ($member in $members.Invoke($type, @())) {
        if ($member -is [Microsoft.CodeAnalysis.INamedTypeSymbol]) { Add-TypeMembers $member $ids; continue }
        if (!(Is-Exported $member)) { continue }
        if ($member -is [Microsoft.CodeAnalysis.IMethodSymbol] -and $member.MethodKind.ToString() -in @('PropertyGet', 'PropertySet', 'EventAdd', 'EventRemove', 'EventRaise')) { continue }
        $id = $documentationId.Invoke($member, @())
        if ($id) { $null = $ids.Add($id) }
    }
}
function Add-Namespace([Microsoft.CodeAnalysis.INamespaceSymbol]$space, $ids) {
    foreach ($type in $typeMembers.Invoke($space, @())) { Add-TypeMembers $type $ids }
    foreach ($child in $namespaceMembers.Invoke($space, @())) { Add-Namespace $child $ids }
}
$inventory = @()
foreach ($path in $AssemblyFiles) {
    $absolute = [IO.Path]::GetFullPath($path)
    $assembly = [Microsoft.CodeAnalysis.IAssemblySymbol]$compilation.GetAssemblyOrModuleSymbol($selected[$absolute])
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    Add-Namespace $assembly.GlobalNamespace $ids
    [xml]$document = [IO.File]::ReadAllText([IO.Path]::ChangeExtension($absolute, '.xml'))
    foreach ($member in @($document.doc.members.member)) {
        if (!$ids.Contains($member.name)) { $null = $member.ParentNode.RemoveChild($member) }
    }
    $output = Join-Path $OutputDirectory ($assembly.Identity.Name + '.xml')
    $document.Save($output)
    $inventory += @{ assembly = $assembly.Identity.Name; version = $assembly.Identity.Version.ToString(); ids = @($ids | Sort-Object); documented = @($document.doc.members.member).Count }
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'public-symbols.json'), (@{ schemaVersion = 1; assemblies = $inventory } | ConvertTo-Json -Depth 8))
$inventory | ForEach-Object { Write-Output "$($_.assembly): $($_.ids.Count) exported IDs; $($_.documented) compiler XML entries" }
