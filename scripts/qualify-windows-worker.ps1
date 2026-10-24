param([Parameter(Mandatory=$true)][string]$TestRoot, [Parameter(Mandatory=$true)][string]$TestAssembly, [string]$CrashStage)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native Windows qualification required.' }
$parent = [IO.Path]::GetFullPath($TestRoot)
if (-not $CrashStage -and -not [IO.Directory]::Exists($parent)) { throw 'Supply an existing isolated fixture directory.' }
$assembly = [IO.Path]::GetFullPath($TestAssembly)
$folder = [IO.Path]::GetDirectoryName($assembly)
[Reflection.Assembly]::LoadFrom((Join-Path $folder 'Library.Execution.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $folder 'Library.Companion.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom($assembly) | Out-Null
if ($CrashStage) { [Library.Companion.Tests.NativeWorkerQualification]::TerminateAt($parent, $CrashStage); exit 1 }
$report = [Library.Companion.Tests.NativeWorkerQualification]::Run($parent) | ConvertFrom-Json
# Junction creation requires no developer mode or privilege change. Both paths are invented.
$junction = Join-Path $report.root 'source-junction'
$target = Join-Path $report.root 'complete/source'
New-Item -ItemType Junction -Path $junction -Target $target | Out-Null
$config = [Library.Companion.WindowsWorkerConfiguration]::new((Join-Path $report.root 'complete/state'), [Library.Companion.WorkerRoot[]]@(
    [Library.Companion.WorkerRoot]::new('source', $junction, 'source'),
    [Library.Companion.WorkerRoot]::new('stage', (Join-Path $report.root 'complete/stage'), 'staging')
))
$worker = [Library.Companion.WindowsMediaWorker]::new($config, $null, $null)
$refused = $false
try { $worker.InspectAsync('source', 'invented.bin', [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null }
catch { if ($_.Exception.ToString() -notmatch 'Reparse points|symlink') { throw }; $refused = $true }
if (-not $refused) { throw 'Junction-root escape accepted.' }
$report.checks += 'Windows worker rejects junction root before any file operation'
foreach ($stage in @('validated','published')) {
    $crashRoot = Join-Path $report.root ('abrupt-exit-' + $stage)
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    foreach ($argument in @('-NoProfile','-File',$PSCommandPath,'-TestRoot',$crashRoot,'-TestAssembly',$assembly,'-CrashStage',$stage)) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    try {
        if (-not $child.WaitForExit(30000)) { $child.Kill($true); throw 'Owned crash fixture child exceeded 30 seconds.' }
        if ($child.ExitCode -ne 73) { throw "Crash child failed before expected checkpoint: $($child.ExitCode)" }
    } finally { $child.Dispose() }
    [Library.Companion.Tests.NativeWorkerQualification]::RecoverAfterExit($crashRoot) | Out-Null
    $report.checks += "Abrupt independent-process exit at $stage recovered without processing"
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $report.root 'result.json')
$report | ConvertTo-Json -Depth 8
