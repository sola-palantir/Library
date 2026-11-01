#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$StateFile,[Parameter(Mandatory=$true)][string]$SampleFolder)
$ErrorActionPreference='Stop'
$state=Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json
if($state.container -notmatch '^library-review-[a-f0-9]{32}$' -or $state.url -notmatch '^http://127\.0\.0\.1:[0-9]+$'){throw 'Not an isolated review preview.'}
$url=$state.url
function GetJson([string]$Path){Invoke-RestMethod "$url/api/$Path" -TimeoutSec 5}
function PostJson([string]$Path,[object]$Body){Invoke-RestMethod "$url/api/$Path" -Method Post -ContentType application/json -Body ($Body | ConvertTo-Json -Compress) -TimeoutSec 5}
function Docker([string[]]$Arguments){
 $start=[Diagnostics.ProcessStartInfo]::new('C:\Program Files\Docker\Docker\resources\bin\docker.exe');$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($arg in $Arguments){$start.ArgumentList.Add($arg)}
 $p=[Diagnostics.Process]::Start($start);$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync()
 if(-not $p.WaitForExit(15000)){$p.Kill($true);throw 'Docker lifecycle command exceeded 15 seconds; inspect before retry.'}
 $text=$out.GetAwaiter().GetResult();$detail=$err.GetAwaiter().GetResult();if($p.ExitCode -ne 0){throw $detail};$text.Trim()
}
if((Docker @('inspect',$state.container,'--format','{{index .Config.Labels "library.readonly-preview"}}')) -ne 'true'){throw 'Preview ownership label mismatch.'}
$before=GetJson 'summary'
$unauthorized=Invoke-WebRequest "$url/api/review-worker/command" -SkipHttpErrorCheck -TimeoutSec 5
if($unauthorized.StatusCode -ne 401){throw 'Worker command accepted without a grant.'}
$wrong=Invoke-WebRequest "$url/api/review-worker/command" -Headers @{Authorization=('Bearer '+('f'*64))} -SkipHttpErrorCheck -TimeoutSec 5
if($wrong.StatusCode -ne 401){throw 'Worker command accepted a wrong grant.'}
$outside=Invoke-WebRequest "$url/api/review-scan" -Method Post -ContentType application/json -Body '{"folder":"C:\\outside-approved-scope"}' -SkipHttpErrorCheck -TimeoutSec 5
if($outside.StatusCode -ne 409){throw 'Scan accepted an unapproved folder.'}
$active=PostJson 'review-scan' @{folder=$SampleFolder}
$running=$false
for($i=0;$i -lt 40;$i++){
 $jobs=GetJson 'review-scan/jobs';$job=$jobs | Where-Object id -EQ $active.id
 if($job.status -eq 'running'){$running=$true;break};if($job.status -eq 'completed'){throw 'Scan completed before active cancellation qualification.'};Start-Sleep -Milliseconds 50
}
if(-not $running){throw 'Worker did not claim the scan.'}
PostJson "review-scan/jobs/$($active.id)/cancel" @{} | Out-Null
Start-Sleep -Milliseconds 800
$cancelled=(GetJson 'review-scan/jobs') | Where-Object id -EQ $active.id
if($cancelled.status -ne 'cancelled'){throw 'Active cancellation failed.'}
if((GetJson 'summary').copies -ne $before.copies){throw 'Cancelled scan changed catalog count.'}
$interrupted=PostJson 'review-scan' @{folder=$SampleFolder}
Docker @('stop','--time','1',$state.container) | Out-Null
Docker @('start',$state.container) | Out-Null
$ready=$false
for($i=0;$i -lt 20;$i++){try{if((GetJson 'health').status -eq 'ok'){$ready=$true;break}}catch{};Start-Sleep -Milliseconds 250}
if(-not $ready){throw 'Preview did not restart.'}
$recovered=(GetJson 'review-scan/jobs') | Where-Object id -EQ $interrupted.id
if($recovered.status -ne 'failed' -or $recovered.error -notlike '*API restarted*'){throw 'Interrupted scan was not failed closed on restart.'}
$after=GetJson 'summary';if($after.copies -ne $before.copies){throw 'Restart altered the completed catalog inventory.'}
$processing=GetJson 'media/processing'
if($processing.sampleEncode -or $processing.sampleRemux -or $processing.hdrProcessing){throw 'Processing unexpectedly enabled.'}
$result=@{noGrantRefused=$true;wrongGrantRefused=$true;outsideFolderRefused=$true;runningCancellation=$cancelled;restartRecovery=$recovered;preservedCopies=$after.copies;encoding=$false;mediaWrites=$false}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path (Split-Path $StateFile) 'resilience.json') -Encoding utf8
$result | ConvertTo-Json -Depth 6
