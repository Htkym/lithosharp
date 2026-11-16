[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PythonExecutable,
    [Parameter(Mandatory)][string] $LintScript,
    [string] $ToolPath,
    [string] $EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$ToolPath) { $ToolPath = Join-Path $PSScriptRoot '../src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll' }
$ToolPath = [IO.Path]::GetFullPath($ToolPath)
if (!(Test-Path -LiteralPath $ToolPath -PathType Leaf)) { throw "Build the tool before this explicit integration check: $ToolPath" }
if (!(Test-Path -LiteralPath $LintScript -PathType Leaf)) { throw "Select the installed Yomiyasu lint script: $LintScript" }
if (!$EvidenceDirectory) { $EvidenceDirectory = Join-Path ([IO.Path]::GetTempPath()) ('pr23-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if ((Test-Path -LiteralPath $EvidenceDirectory) -and @(Get-ChildItem -LiteralPath $EvidenceDirectory -Force).Count -ne 0) {
    throw 'Proofreading acceptance requires a new or empty evidence directory.'
}
$null = New-Item -ItemType Directory -Path $EvidenceDirectory -Force
$source = Join-Path $EvidenceDirectory 'guide.md'
$raw = [char]0xfeff + [string]::Join("`r`n", @('---', 'title: Hidden metadata', '---', '# 見出し😀', '',
    '本文と System.String を使います。', '', '```text', '隠す😀', '```', ''))
[IO.File]::WriteAllBytes($source, [Text.Encoding]::UTF8.GetBytes($raw))
$before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$sequence = 0

function Invoke-Proofread([string[]] $Arguments, [int] $ExpectedExit) {
    $script:sequence++
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($ToolPath, 'proofread') + $Arguments + @('--format', 'json')) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $null = $process.WaitForExitAsync().GetAwaiter().GetResult()
        $text = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "$script:sequence.stdout.json"), $text)
        [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "$script:sequence.stderr.txt"), $errorText)
        if ($process.ExitCode -ne $ExpectedExit) { throw "Expected $ExpectedExit, got $($process.ExitCode): $errorText $text" }
        return $text | ConvertFrom-Json
    }
    finally { $process.Dispose() }
}

Write-Host "Proofreading evidence: $EvidenceDirectory; longest planned evidence path: $((Join-Path $EvidenceDirectory '2.stdout.json').Length) characters."
$result = Invoke-Proofread @($source, '--python', $PythonExecutable, '--yomiyasu', ([IO.Path]::GetFullPath($LintScript)),
    '--profile', 'syntamark-docs/2', '--protect', 'System.String', '--preview', '0:3:1', '--replacement', '絵') 0
if ($result.state -ne 'Complete' -or !$result.wasRun -or !$result.toolVersion.StartsWith('3.')) { throw 'The actual supported Yomiyasu did not complete.' }
$emoji = @($result.diagnostics | Where-Object { $_.message.StartsWith('emoji_prohibited:') })
if ($emoji.Count -ne 1 -or $emoji[0].location.line -ne 4 -or $emoji[0].location.column -ne 3 -or $emoji[0].mappingPrecision -ne 'CoveringTokens') {
    throw 'The real line-only finding did not map to the original BOM/CRLF heading, or included code reached lint.'
}
if (!$result.preview.canApply -or $result.preview.originalText -ne '😀' -or $result.preview.replacement -ne '絵' -or $result.preview.mappingPrecision -ne 'Exact') {
    throw 'The explicitly selected code-point replacement did not produce an exact reviewable preview.'
}
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $before) { throw 'Proofreading or preview wrote the original source.' }
$codeOnly = Join-Path $EvidenceDirectory 'code.md'
[IO.File]::WriteAllText($codeOnly, '```text' + "`nnot prose`n" + '```' + "`n")
$notRun = Invoke-Proofread @($codeOnly, '--python', 'missing-python', '--yomiyasu', (Join-Path $EvidenceDirectory 'missing.py')) 1
if ($notRun.wasRun -or $notRun.state -ne 'NotRun') { throw 'A document with no selected prose tried to execute a proofreader.' }
Write-Host "Actual Yomiyasu mapping, explicit preview and optional-environment acceptance passed. Evidence: $EvidenceDirectory"
