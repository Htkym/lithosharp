[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$WatchScript = (Join-Path $PSScriptRoot 'Test-MdxWatch.ps1')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = [IO.Path]::GetFullPath($WatchScript)
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
$sourceStart = $null; $negativeSourceText = $null
function File-Pin([string]$Path) { return [ordered]@{ file=$Path;sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $Path).Length } }
function Hash-Text([string]$Text) { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant() }
function Assert-True([bool]$Condition,[string]$Message) { if (!$Condition) {throw $Message} }
function Physical([string]$Path,[bool]$Exists) {
    $full=[IO.Path]::GetFullPath($Path)
    if ($full -cne $Path -or ![IO.Path]::IsPathFullyQualified($full)) {throw 'Exact absolute source/evidence path required.'}
    $q=$full
    while ($q) {
        if ((Test-Path -LiteralPath $q) -and ((Get-Item -Force -LiteralPath $q).Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw 'Reparse path rejected.'}
        $next=[IO.Path]::GetDirectoryName($q);if ($next -eq $q) {break};$q=$next
    }
    if ($Exists -and !(Test-Path -LiteralPath $Path -PathType Leaf)) {throw 'Missing source.'}
}
Physical $source $true;Physical $EvidenceDirectory $false
$sourceStart = File-Pin $source
Assert-True (!(Test-Path -LiteralPath $EvidenceDirectory) -and (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($EvidenceDirectory)) -PathType Container)) 'Fresh owned evidence directory required.'
$null=New-Item -ItemType Directory -Path $EvidenceDirectory
$extraction=[Collections.Generic.List[object]]::new()
function Parse-Source([string]$Path) {
    $t=$null;$e=$null;$a=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$t,[ref]$e)
    if (@($e).Count) {throw 'Watch source parser failure.'};return $a
}
$ast=Parse-Source $source
$sourceText=[IO.File]::ReadAllText($source)
$nl=if ($sourceText.Contains("`r`n")) {"`r`n"}else{"`n"}
# A deliberate negative fixture, not an exported previous revision or prior proof.
# Remove exactly the four reviewed drain edits in memory; assert each unique hit.
$negativeSourceText=$sourceText
$inverse=@(
    @{ new=('    for ($edit = 1; $edit -le $StressEdits; $edit++) {'+$nl+'        Receive-WatchMachineEvents'); old='    for ($edit = 1; $edit -le $StressEdits; $edit++) {' },
    @{ new=('        [IO.File]::WriteAllText($pagePath, $baseSource + "`nRevision $edit.`n")'+$nl+'        Receive-WatchMachineEvents'); old='        [IO.File]::WriteAllText($pagePath, $baseSource + "`nRevision $edit.`n")' },
    @{ new='            while ($soak.Elapsed.TotalSeconds -lt $due) { Receive-WatchMachineEvents; Start-Sleep -Milliseconds 150; Receive-WatchMachineEvents }'; old='            while ($soak.Elapsed.TotalSeconds -lt $due) { Start-Sleep -Milliseconds 150 }' },
    @{ new=('        if ($edit % 5 -eq 0) { $resources.Add((Sample-ServeResources "edit-$edit")) }'+$nl+'        Receive-WatchMachineEvents'); old='        if ($edit % 5 -eq 0) { $resources.Add((Sample-ServeResources "edit-$edit")) }' }
)
for ($i=$inverse.Count-1;$i -ge 0;$i--) {
    $change=$inverse[$i]
    Assert-True ([regex]::Matches($negativeSourceText,[regex]::Escape($change.new)).Count -eq 1) 'Unique four-change drain inverse required.'
    $negativeSourceText=$negativeSourceText.Replace($change.new,$change.old)
}
$negativeTokens=$null;$negativeErrors=$null
$oldAst=[Management.Automation.Language.Parser]::ParseInput($negativeSourceText,[ref]$negativeTokens,[ref]$negativeErrors)
Assert-True (@($negativeErrors).Count -eq 0) 'Synthetic negative fixture parser failure.'
$forward=$negativeSourceText
foreach ($change in $inverse) {
    Assert-True ([regex]::Matches($forward,[regex]::Escape($change.old)).Count -eq 1) 'Unique four-change drain forward required.'
    $forward=$forward.Replace($change.old,$change.new)
}
Assert-True ($forward -ceq $sourceText) 'Drain inverse does not preserve all other source text.' 
$selected=@('Known-Count','Known-Bool','Get-WatchMachineWork','Receive-WatchMachineEvents')
foreach ($name in $selected) {
    $f=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    $of=@($oldAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    Assert-True ($f.Count -eq 1 -and $of.Count -eq 1 -and $f[0].Extent.Text -ceq $of[0].Extent.Text) 'Selected receive function changed.'
    $extraction.Add([ordered]@{kind='function';name=$name;sha256=Hash-Text $f[0].Extent.Text})
    . ([ScriptBlock]::Create($f[0].Extent.Text))
}
function Reader-Code($A) {
    $x=@($A.FindAll({param($n)$n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Add-Type'},$true))
    Assert-True ($x.Count -eq 1 -and $x[0].CommandElements.Count -eq 3 -and $x[0].CommandElements[1].ParameterName -ceq 'TypeDefinition' -and $x[0].CommandElements[2] -is [Management.Automation.Language.StringConstantExpressionAst]) 'Unique literal reader required.'
    return $x[0].CommandElements[2].Value
}
$readerCode=Reader-Code $ast;Assert-True ($readerCode -ceq (Reader-Code $oldAst)) 'Reader bounds or algorithm changed.'
Assert-True ($null -eq ('LithoSharp.WatchR7.LiveStdout' -as [type])) 'Fresh PowerShell host required.'
Add-Type -TypeDefinition $readerCode
$extraction.Add([ordered]@{kind='actual-CSharp-reader';sha256=Hash-Text $readerCode})
# An owned synchronized StreamReader drives the ORIGINAL LiveStdout, not a queue mirror.
# WaitConsumed completes only after LiveStdout processes the batch and requests its next read.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace LithoSharp.WatchLoopFixture {
 public sealed class ControlledReader : StreamReader {
  private readonly object gate=new object();
  private char[] buffer; private int index,count,delivered,consumed;
  private TaskCompletionSource<int> waiting; private bool finished;
  public ControlledReader():base(new MemoryStream(),System.Text.Encoding.UTF8,false,4096,false) {}
  public override Task<int> ReadAsync(char[] target,int offset,int length) {
   lock(gate) {
    consumed=delivered;Monitor.PulseAll(gate);
    if(finished)return Task.FromResult(0);
    if(waiting!=null)throw new InvalidOperationException("Concurrent read");
    buffer=target;index=offset;count=length;
    waiting=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    return waiting.Task;
   }
  }
  public int Feed(string text) {
   lock(gate) {
    if(finished||waiting==null||text.Length==0||text.Length>count)throw new InvalidOperationException("Owned controlled feed boundary invalid");
    text.CopyTo(0,buffer,index,text.Length);var w=waiting;waiting=null;delivered++;w.SetResult(text.Length);return delivered;
   }
  }
  public void WaitConsumed(int expected) {
   var clock=System.Diagnostics.Stopwatch.StartNew();
   lock(gate) {while(consumed<expected){var remaining=5000-(int)clock.ElapsedMilliseconds;if(remaining<=0||!Monitor.Wait(gate,remaining))throw new TimeoutException("Owned reader five-second boundary exceeded");}}
  }
  public void Finish() {lock(gate){finished=true;if(waiting!=null){var w=waiting;waiting=null;w.SetResult(0);}}}
  protected override void Dispose(bool disposing){Finish();base.Dispose(disposing);}
 }
}
'@
function Find-EditLoop($A) {
    $loops=@($A.FindAll({param($n)$n -is [Management.Automation.Language.ForStatementAst] -and $n.Initializer.Extent.Text -cmatch '^\$edit\s*=\s*1$'},$true))
    Assert-True ($loops.Count -eq 1) 'Unique actual edit loop required.';return $loops[0]
}
function Test-LoopBlock($A,[bool]$Paced) {
    $loop=Find-EditLoop $A
    if ($Paced) {
        $w=@($loop.FindAll({param($n)$n -is [Management.Automation.Language.WhileStatementAst]},$true))
        Assert-True ($w.Count -eq 1) 'Unique existing paced-sleep loop required.'
        $text=$w[0].Extent.Text
        $extraction.Add([ordered]@{kind='actual-paced-loop';sha256=Hash-Text $text})
        return [ScriptBlock]::Create($text)
    }
    $writes=@($loop.FindAll({param($n)$n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Member.Extent.Text -ceq 'WriteAllText'},$true))
    Assert-True ($writes.Count -eq 1) 'Unique actual edit write required.'
    $call=$writes[0].Extent.Text
    Assert-True ($call -ceq '[IO.File]::WriteAllText($pagePath, $baseSource + "`nRevision $edit.`n")') 'Actual edit write changed.'
    # Only the owned file-write boundary is replaced; this callback performs the
    # same WriteAllText and synchronously releases the controlled stdout producer.
    $text=$loop.Extent.Text.Replace($call,'Write-ControlledFixtureEdit $pagePath ($baseSource + "`nRevision $edit.`n")')
    $extraction.Add([ordered]@{kind='actual-edit-loop';rawSHA256=Hash-Text $loop.Extent.Text;controlledSHA256=Hash-Text $text;substitution='Only real WriteAllText delegates through owned controlled producer callback.'})
    return [ScriptBlock]::Create($text)
}
$newLoop=Test-LoopBlock $ast $false;$oldLoop=Test-LoopBlock $oldAst $false
$newPaced=Test-LoopBlock $ast $true;$oldPaced=Test-LoopBlock $oldAst $true
function Event-Line([string]$Name,[int]$Generation) {
    $r=[ordered]@{schemaVersion='1.0';event=$Name}
    if ($Name -cne 'rebuild-started') {$r.generation=$Generation}
    if ($Name -ceq 'rebuild-succeeded') {$r.success=$true;$r.exitCode=0;$r.mdxWork=[ordered]@{workerStarts=0;renderedPages=1;compiledModules=0;cacheHit=$false}}
    return ($r|ConvertTo-Json -Depth 5 -Compress)+"`n"
}
function Feed-Build {
    $script:sentGeneration++
    $s=(Event-Line 'rebuild-started' 0)+(Event-Line 'rebuild-succeeded' $script:sentGeneration)
    $script:raw.Append($s)|Out-Null
    $ticket=$script:controlled.Feed($s);$script:controlled.WaitConsumed($ticket)
}
function Write-ControlledFixtureEdit([string]$Path,[string]$Text) {[IO.File]::WriteAllText($Path,$Text);Feed-Build}
function Sample-ServeResources([string]$Label) {return [ordered]@{label=$Label}}
function Start-Sleep([int]$Milliseconds) {
    Assert-True ($Milliseconds -eq 150) 'Existing sleep interval changed.'
    Feed-Build;$script:soak.Elapsed.TotalSeconds++
}
$cases=[Collections.Generic.List[object]]::new();$primary=$null
function Run-ControlledCase([string]$Name,[scriptblock]$Loop,[bool]$ExpectedOverflow,[bool]$Paced) {
    $script:watchEvidence=[ordered]@{failureCategory=$null}
    $script:liveTerminalEvents=[Collections.Generic.List[object]]::new()
    $script:liveLastGeneration=$null;$script:livePendingBuild=$false;$script:liveStartupSeen=$false;$script:liveShutdownSeen=$false;$script:liveDroppedTerminals=0
    $script:sentGeneration=1;$script:raw=[Text.StringBuilder]::new();$script:controlled=$null;$script:stdoutReader=$null
    $errorCaught=$null;$cleanup=[Collections.Generic.List[Exception]]::new();$completedRaw=$null;$closed=$false
    try {
        $script:controlled=[LithoSharp.WatchLoopFixture.ControlledReader]::new()
        $script:stdoutReader=[LithoSharp.WatchR7.LiveStdout]::new($script:controlled)
        $initial=Event-Line 'startup' 1;$script:raw.Append($initial)|Out-Null
        $ticket=$script:controlled.Feed($initial);$script:controlled.WaitConsumed($ticket)
        # Old and new begin from the same validated startup, leaving exactly258
        # subsequently queued records for129 completed builds on the undrained path.
        Receive-WatchMachineEvents
        $StressEdits=129;$SoakMinutes=0;$pagePath=Join-Path $EvidenceDirectory ($Name+'.mdx');$baseSource='# owned queue fixture'
        $resources=[Collections.Generic.List[object]]::new()
        $script:soak=[pscustomobject]@{Elapsed=[pscustomobject]@{TotalSeconds=0.0}};$due=129
        & $Loop
        Assert-True ($script:sentGeneration -eq 130) 'Exactly129 completed builds required.'
        if ($ExpectedOverflow) {
            Assert-True $script:stdoutReader.Invalid 'Original undrained source did not latch overflow.'
            $reject=$null;try {Receive-WatchMachineEvents} catch {$reject=$_}
            Assert-True ($null -ne $reject -and $script:stdoutReader.Invalid -and $script:watchEvidence.failureCategory -ceq 'LIVE_MACHINE_WITNESS_INVALID') 'Overflow was cleared or accepted.'
        } else {
            Receive-WatchMachineEvents
            Assert-True (!$script:stdoutReader.Invalid -and $script:liveLastGeneration -eq 130 -and $script:liveTerminalEvents.Count -eq 129 -and !$script:livePendingBuild -and $script:liveDroppedTerminals -eq 0) 'Drained loop lost or invalidated work.'
            for ($i=0;$i -lt 129;$i++) {Assert-True ($script:liveTerminalEvents[$i].generation -eq $i+2 -and $script:liveTerminalEvents[$i].mdxWork.workerStarts -eq 0) 'Terminal sequence/work changed.'}
        }
        $script:controlled.Finish()
        Assert-True ($script:stdoutReader.Completion.Wait([TimeSpan]::FromSeconds(5))) 'Completion exceeded five seconds.'
        $completedRaw=$script:stdoutReader.Completion.GetAwaiter().GetResult()
        Assert-True ($completedRaw -ceq $script:raw.ToString()) 'Raw stdout was not conserved.'
        if (!$Paced) {Assert-True ([IO.File]::ReadAllText($pagePath) -ceq ($baseSource+"`nRevision 129.`n")) 'Final exact edit was lost.'}
    } catch {$errorCaught=$_.Exception}
    finally {
        if ($null -ne $script:controlled) {try {$script:controlled.Finish();$script:controlled.Dispose();$closed=$true} catch {$cleanup.Add($_.Exception)}}
        if ($null -ne $script:stdoutReader) {try {if (!$script:stdoutReader.Completion.Wait([TimeSpan]::FromSeconds(5))) {throw 'Owned reader cleanup exceeded five seconds.'}} catch {$cleanup.Add($_.Exception)}}
    }
    $cases.Add([ordered]@{name=$Name;outcome=if ($null -eq $errorCaught -and !$cleanup.Count) {'PASS'}else{'FAIL'};completedBuilds=$script:sentGeneration-1;expectedOverflow=$ExpectedOverflow;invalid=if ($null -ne $script:stdoutReader) {$script:stdoutReader.Invalid}else{$null};terminalCount=$script:liveTerminalEvents.Count;lastGeneration=$script:liveLastGeneration;rawCharacters=if ($null -ne $completedRaw) {$completedRaw.Length}else{$null};rawSHA256=if ($null -ne $completedRaw) {Hash-Text $completedRaw}else{$null};ownedReaderDisposed=$closed;cleanupFailureCount=$cleanup.Count})
    if ($null -ne $errorCaught -or $cleanup.Count) {
        $all=[Collections.Generic.List[Exception]]::new();if ($null -ne $errorCaught) {$all.Add($errorCaught)};foreach ($e in $cleanup) {$all.Add($e)}
        $message=if ($null -ne $errorCaught) {$errorCaught.ToString()}else{'Owned reader cleanup failed.'}
        if ($cleanup.Count) {$message+='; Owned cleanup also failed: '+(($cleanup|ForEach-Object {$_.ToString()})-join'; ')}
        throw [AggregateException]::new($message,[Exception[]]$all.ToArray())
    }
}
try {
    Run-ControlledCase 'synthetic-undrained-zero-soak129-overflow' $oldLoop $true $false
    Run-ControlledCase 'current-zero-soak129-drained' $newLoop $false $false
    Run-ControlledCase 'synthetic-undrained-paced129-overflow' $oldPaced $true $true
    Run-ControlledCase 'current-paced129-drained' $newPaced $false $true
} catch {$primary=$_.Exception}
finally {
    $finalSource=$null; $finalFixture=$null; $pinFailures=[Collections.Generic.List[object]]::new()
    foreach ($target in @(@{kind='source';path=$source}, @{kind='fixture';path=$PSCommandPath})) {
        try {
            $observedPin=File-Pin $target.path
            if ($target.kind -ceq 'source') {
                $finalSource=$observedPin
                if ($finalSource.sha256 -cne $sourceStart.sha256 -or $finalSource.bytes -ne $sourceStart.bytes) {throw [InvalidOperationException]::new('Watch source changed during regression.')}
            } else {$finalFixture=$observedPin}
        } catch {
            $secondary=$_.Exception
            $pinFailures.Add([ordered]@{kind=$target.kind;failureType=$secondary.GetType().FullName})
            $primary=if ($null -eq $primary) {$secondary}else{[AggregateException]::new($primary.ToString()+'; Owned source verification also failed: '+$secondary.ToString(),[Exception[]]@($primary,$secondary))}
        }
    }
    $report=[ordered]@{status=if ($null -eq $primary -and $cases.Count -eq 4) {'PASS_REAL_READER_AND_EXTRACTED_LOOP_FIXTURES_ONLY'}else{'FAIL_LOOP_DRAIN_FIXTURES_PRESERVE_FAILURE'};source=$sourceStart;finalSource=$finalSource;negativeFixture=[ordered]@{kind='IN_MEMORY_EXACT_FOUR_DRAIN_INVERSE';sha256=Hash-Text $negativeSourceText;historicalProof=$false};fixture=$finalFixture;pinFailures=@($pinFailures.ToArray());cases=@($cases.ToArray());extracted=@($extraction.ToArray());actualServer=$false;actualWatch=$false;formalCredit=$false;SDK=0;controlledSchedulerScope='Real unchanged queue/parser and exact extracted edit/paced loops. Owned StreamReader batches synchronized at actual next ReadAsync, not a real filesystem watcher/build scheduler.'}
    try {
        $path=Join-Path $EvidenceDirectory 'receipt.json';$stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try {$b=[Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 12));$stream.Write($b,0,$b.Length)}finally{$stream.Dispose()}
    }catch{$e=$_.Exception;if ($null -ne $primary) {throw [AggregateException]::new($primary.ToString()+'; Owned evidence also failed: '+$e.ToString(),[Exception[]]@($primary,$e))};throw}
}
if ($null -ne $primary) {throw $primary}
Write-Output $report.status
