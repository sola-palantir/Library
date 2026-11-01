#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$StateFile)
$ErrorActionPreference='Stop'
$state=Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json
if($state.container -notmatch '^library-review-[a-f0-9]{32}$'){throw 'Not an isolated review preview.'}
function Docker([string[]]$Arguments){
 $start=[Diagnostics.ProcessStartInfo]::new('C:\Program Files\Docker\Docker\resources\bin\docker.exe');$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($arg in $Arguments){$start.ArgumentList.Add($arg)}
 $p=[Diagnostics.Process]::Start($start);$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync()
 if(-not $p.WaitForExit(15000)){$p.Kill($true);throw 'Docker stop/status exceeded 15 seconds; inspect before retry.'}
 $text=$out.GetAwaiter().GetResult();$detail=$err.GetAwaiter().GetResult();if($p.ExitCode -ne 0){throw $detail};$text.Trim()
}
if((Docker @('inspect',$state.container,'--format','{{index .Config.Labels "library.readonly-preview"}}')) -ne 'true'){throw 'Preview ownership label mismatch.'}
Docker @('stop','--time','3',$state.container) | Out-Null
$worker=Get-Process -Id $state.workerPid -ErrorAction SilentlyContinue
if($worker -and $worker.Path -eq $state.workerExe -and $state.workerStartedUtc -and $worker.StartTime.ToUniversalTime().ToString('O') -eq $state.workerStartedUtc){
 if(-not $worker.WaitForExit(7000)){$worker.Kill($true);if(-not $worker.WaitForExit(3000)){throw 'Owned worker did not stop; inspect before retry.'}}
}
Write-Output 'Isolated review preview stopped. Catalog volume, session metadata and evidence retained; no files or volumes deleted.'
