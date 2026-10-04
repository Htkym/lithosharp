[CmdletBinding()]
param([string] $Output)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$Output) { $Output = Join-Path (Join-Path $PSScriptRoot '../.tmp') ('migration-normalization-' + [Guid]::NewGuid().ToString('N')) }
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Normalization fixture output must be new.' }
$null = New-Item -ItemType Directory -Path $Output

# Import only the actual comparison functions, without running migration,
# starting a host or modifying original corpus/oracle files.
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-MigrationCandidate.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Migration candidate harness has syntax errors.' }
$functions = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -in @('Get-NormalizedText', 'Get-NormalizedTreeHash')
}, $false))
if ($functions.Count -ne 2) { throw 'Expected both real normalization functions.' }
foreach ($function in $functions) { Invoke-Expression $function.Extent.Text }

function New-Fixture([string] $Name, [string] $FirstBundle, [string] $SecondBundle,
    [string] $FirstBody = 'globalThis.example = "A";', [string] $SecondBody = 'globalThis.example = "B";',
    [switch] $Swap, [switch] $Missing) {
    $root = Join-Path $Output $Name
    foreach ($directory in @('one', 'two', '_mdx/pages')) { $null = New-Item -ItemType Directory -Path (Join-Path $root $directory) }
    $firstReference = if ($Swap) { $SecondBundle } else { $FirstBundle }
    $secondReference = if ($Swap) { $FirstBundle } else { $SecondBundle }
    [IO.File]::WriteAllText((Join-Path $root 'one/index.html'), '<script src="/_mdx/pages/' + $firstReference + '"></script>')
    [IO.File]::WriteAllText((Join-Path $root 'two/index.html'), '<script src="/_mdx/pages/' + $secondReference + '"></script>')
    [IO.File]::WriteAllText((Join-Path $root "_mdx/pages/$FirstBundle"), $FirstBody)
    if (!$Missing) { [IO.File]::WriteAllText((Join-Path $root "_mdx/pages/$SecondBundle"), $SecondBody) }
    [IO.File]::WriteAllText((Join-Path $root '.lithosharp-output-manifest.json'), (@{
        version = 1; files = @('one/index.html', 'two/index.html', "_mdx/pages/$FirstBundle", "_mdx/pages/$SecondBundle")
    } | ConvertTo-Json))
    return $root
}
$baseline = New-Fixture 'baseline' 'first-AAAAAAAA.js' 'second-BBBBBBBB.js'
$baseHash = (Get-NormalizedTreeHash $baseline).hash
$results = [Collections.Generic.List[object]]::new()
function Assert-Comparison([string] $Name, [string] $Root, [bool] $ExpectedEqual) {
    $hash = (Get-NormalizedTreeHash $Root).hash
    if (($hash -ceq $baseHash) -ne $ExpectedEqual) { throw "Incorrect normalization acceptance: $Name" }
    $results.Add(@{ name = $Name; passed = $true; equal = $ExpectedEqual; normalizedHash = $hash })
}
Assert-Comparison 'renamed-bundles-same-wiring' (New-Fixture 'renamed' 'new-name-CCCCCCCC.js' 'other-name-DDDDDDDD.js') $true
Assert-Comparison 'swapped-page-wiring-rejected' (New-Fixture 'swapped' 'first-AAAAAAAA.js' 'second-BBBBBBBB.js' -Swap) $false
Assert-Comparison 'changed-body-rejected' (New-Fixture 'changed' 'first-AAAAAAAA.js' 'second-BBBBBBBB.js' -SecondBody 'globalThis.example = "changed";') $false

function Assert-Error([string] $Name, [string] $Root, [string] $Pattern) {
    $caught = $null
    try { $null = Get-NormalizedTreeHash $Root } catch { $caught = $_.Exception.ToString() }
    if (!$caught -or $caught -notmatch $Pattern) { throw "Expected normalization rejection '$Name': $caught" }
    $results.Add(@{ name = $Name; passed = $true; diagnostic = $Pattern })
}
Assert-Error 'missing-reference-rejected' (New-Fixture 'missing' 'first-AAAAAAAA.js' 'second-BBBBBBBB.js' -Missing) 'Missing referenced MDX bundle'
Assert-Error 'cyclic-graph-rejected' (New-Fixture 'cycle' 'first-AAAAAAAA.js' 'second-BBBBBBBB.js' -FirstBody 'import "/_mdx/pages/second-BBBBBBBB.js";' -SecondBody 'import "/_mdx/pages/first-AAAAAAAA.js";') 'Unsupported cyclic MDX bundle graph'

$transitive = New-Fixture 'transitive' 'first-AAAAAAAA.js' 'second-BBBBBBBB.js' -FirstBody 'import "../chunks/first-EEEEEEEE.js";' -SecondBody 'import "/_mdx/chunks/second-FFFFFFFF.js";'
$transitiveRenamed = New-Fixture 'transitive-renamed' 'first-CCCCCCCC.js' 'second-DDDDDDDD.js' -FirstBody 'import "../chunks/renamed-GGGGGGGG.js";' -SecondBody 'import "/_mdx/chunks/other-HHHHHHHH.js";'
foreach ($root in @($transitive, $transitiveRenamed)) { $null = New-Item -ItemType Directory -Path (Join-Path $root '_mdx/chunks') }
[IO.File]::WriteAllText((Join-Path $transitive '_mdx/chunks/first-EEEEEEEE.js'), 'export const example = "A";')
[IO.File]::WriteAllText((Join-Path $transitive '_mdx/chunks/second-FFFFFFFF.js'), 'export const example = "B";')
[IO.File]::WriteAllText((Join-Path $transitiveRenamed '_mdx/chunks/renamed-GGGGGGGG.js'), 'export const example = "A";')
[IO.File]::WriteAllText((Join-Path $transitiveRenamed '_mdx/chunks/other-HHHHHHHH.js'), 'export const example = "B";')
$transitiveHash = (Get-NormalizedTreeHash $transitive).hash
if ((Get-NormalizedTreeHash $transitiveRenamed).hash -cne $transitiveHash) { throw 'Equivalent renamed transitive bundles differ.' }
$results.Add(@{ name = 'transitive-renaming-preserves-wiring'; passed = $true })
[IO.File]::WriteAllText((Join-Path $transitiveRenamed '_mdx/pages/first-CCCCCCCC.js'), 'import "/_mdx/chunks/other-HHHHHHHH.js";')
if ((Get-NormalizedTreeHash $transitiveRenamed).hash -ceq $transitiveHash) { throw 'Changed transitive wiring was accepted.' }
$results.Add(@{ name = 'changed-transitive-wiring-rejected'; passed = $true })
# Prism correction must not erase content regressions outside its HTML classes.
$prismBaseline = New-Fixture 'prism-baseline' 'first-AAAAAAAA.js' 'second-BBBBBBBB.js'
$prismDuplicate = New-Fixture 'prism-duplicate' 'first-AAAAAAAA.js' 'second-BBBBBBBB.js'
[IO.File]::AppendAllText((Join-Path $prismBaseline 'one/index.html'), '<pre class="language-js"><code class="language-js">example</code></pre>')
[IO.File]::AppendAllText((Join-Path $prismDuplicate 'one/index.html'), '<pre class="language-js language-js"><code class="language-js language-js">example</code></pre>')
if ((Get-NormalizedTreeHash $prismBaseline).hash -cne (Get-NormalizedTreeHash $prismDuplicate).hash) { throw 'Prism duplicate class correction differs.' }
$results.Add(@{ name = 'prism-class-only-correction'; passed = $true })
foreach ($case in @(
    @{ name = 'ordinary-page-text'; before = '<p>language-js language-js</p>'; after = '<p>language-js</p>'; file = 'one/index.html' },
    @{ name = 'javascript-string'; before = 'const text = "language-js language-js";'; after = 'const text = "language-js";'; file = '_mdx/pages/first-AAAAAAAA.js' },
    @{ name = 'inline-script-string'; before = '<script>const text = "<code class=\"language-js language-js\">";</script>'; after = '<script>const text = "<code class=\"language-js\">";</script>'; file = 'one/index.html' },
    @{ name = 'ordinary-attribute'; before = '<code title="language-js language-js">example</code>'; after = '<code title="language-js">example</code>'; file = 'one/index.html' },
    @{ name = 'tag-inside-attribute'; before = '<div data-example=''<code class="language-js language-js">''></div>'; after = '<div data-example=''<code class="language-js">''></div>'; file = 'one/index.html' },
    @{ name = 'custom-element'; before = '<code-example class="language-js language-js"></code-example>'; after = '<code-example class="language-js"></code-example>'; file = 'one/index.html' },
    @{ name = 'raw-text-xmp'; before = '<xmp><code class="language-js language-js"></xmp>'; after = '<xmp><code class="language-js"></xmp>'; file = 'one/index.html' },
    @{ name = 'plaintext-through-eof'; before = '<plaintext><code class="language-js language-js">'; after = '<plaintext><code class="language-js">'; file = 'one/index.html' }
)) {
    $beforeRoot = New-Fixture ($case.name + '-before') 'first-AAAAAAAA.js' 'second-BBBBBBBB.js'
    $afterRoot = New-Fixture ($case.name + '-after') 'first-AAAAAAAA.js' 'second-BBBBBBBB.js'
    [IO.File]::AppendAllText((Join-Path $beforeRoot $case.file), $case.before)
    [IO.File]::AppendAllText((Join-Path $afterRoot $case.file), $case.after)
    if ((Get-NormalizedTreeHash $beforeRoot).hash -ceq (Get-NormalizedTreeHash $afterRoot).hash) { throw "Content regression was normalized away: $($case.name)" }
    $results.Add(@{ name = ($case.name + '-change-rejected'); passed = $true })
}
[IO.File]::WriteAllText((Join-Path $Output 'migration-normalization.json'), (@{
    schemaVersion = '1.0'; passed = $results.Count; failed = 0; results = @($results)
} | ConvertTo-Json -Depth 5))
Write-Host "Migration normalization gate passed: $($results.Count) cases. Evidence: $Output"
