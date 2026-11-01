#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$ToolsRoot, [int]$Port=5096)
$ErrorActionPreference='Stop'
# Isolated public-token synthetic fixture. Never activates remembered user access.
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id=[Guid]::NewGuid().ToString('N'); $root=Join-Path $repo ".local/connection-qualification/run-$id"
$media=Join-Path $root 'synthetic'; [IO.Directory]::CreateDirectory($media)|Out-Null
Set-Content -LiteralPath (Join-Path $media 'synthetic-qualification.txt') -Value 'Library synthetic connection qualification only' -NoNewline
function StartChild([string]$Exe,[string[]]$Arguments,[hashtable]$Environment=@{}) {
  $start=[Diagnostics.ProcessStartInfo]::new($Exe); $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WorkingDirectory=$repo
  $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
  foreach($item in $Arguments){$start.ArgumentList.Add($item)}
  foreach($key in $Environment.Keys){$start.Environment[$key]=$Environment[$key]}
  $process=[Diagnostics.Process]::Start($start)
  return @{ process=$process; stdout=$process.StandardOutput.ReadToEndAsync(); stderr=$process.StandardError.ReadToEndAsync() }
}
function Finish($Child,[int]$Seconds,[string]$Log) {
  if(-not $Child.process.WaitForExit($Seconds*1000)){$Child.process.Kill($true);throw "Owned qualification process exceeded $Seconds seconds; inspect $root before retrying."}
  $text=$Child.stdout.GetAwaiter().GetResult()+$Child.stderr.GetAwaiter().GetResult(); Set-Content -LiteralPath $Log -Value $text
  return @{ code=$Child.process.ExitCode; text=$text }
}
function LinuxPath([string]$Path) {
  $child=StartChild 'wsl.exe' @('--exec','wslpath','-u',$Path); $result=Finish $child 10 (Join-Path $root 'wslpath.log')
  if($result.code -ne 0){throw 'WSL path conversion failed'}; return $result.text.Trim()
}
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$Port); try{$listener.Start()}finally{$listener.Stop()}
$ffmpeg=Join-Path $ToolsRoot 'bin/ffmpeg.exe'; $ffprobe=Join-Path $ToolsRoot 'bin/ffprobe.exe'
if((Get-FileHash -LiteralPath $ffmpeg -Algorithm SHA256).Hash.ToLowerInvariant() -ne '3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec'){throw 'Pinned FFmpeg differs'}
if((Get-FileHash -LiteralPath $ffprobe -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52'){throw 'Pinned FFprobe differs'}
$file=Join-Path $media 'invented.mkv'
$make=StartChild $ffmpeg @('-hide_banner','-v','error','-n','-f','lavfi','-i','color=c=gray:s=64x64:r=2','-t','1','-c:v','ffv1','-an',$file)
if((Finish $make 15 (Join-Path $root 'fixture.log')).code -ne 0){throw 'Synthetic fixture failed'}
$before=Get-FileHash -LiteralPath $file -Algorithm SHA256
$origin="http://127.0.0.1:$Port"; $now=[DateTimeOffset]::UtcNow
$token='a'*64 # Public fixture value, never usable for actual user access.
$profile=@{accessId=$id;folder=$media;apiOrigin=$origin;tokenSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($token))).ToLowerInvariant();issuedUtc=$now;expiresUtc=$now.AddMinutes(4)}
$config=Join-Path $root 'api.json'; @{Library=@{ExecutionBackend='windows-worker';DataPath="/tmp/library-review-connection-$id";ReadOnlyConnection=$profile;Roots=@();EnableSampleEncode=$false;EnableSampleRemux=$false;EnableSampleHandoff=$false}}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $config
$workerConfig=Join-Path $root 'synthetic-worker.json'; @{profile=$profile;toolsRoot=$ToolsRoot;token=$token}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $workerConfig
$linuxRepo=LinuxPath $repo; $linuxConfig=LinuxPath $config
$api=$null; $worker=$null
try {
  $api=StartChild 'wsl.exe' @('--exec','env',"DOTNET_CLI_HOME=$linuxRepo/.local/dotnet-home",'DOTNET_GENERATE_ASPNET_CERTIFICATE=false',"LIBRARY_CONFIG=$linuxConfig",'timeout','160',"$linuxRepo/.dotnet/dotnet","$linuxRepo/.local/connection-qualification/api/Library.Api.dll",'--urls',$origin,'--webroot',"$linuxRepo/client/dist")
  $ready=$false
  for($i=0;$i -lt 20;$i++){try {if((Invoke-RestMethod "$origin/api/health" -TimeoutSec 2).status -eq 'ok'){$ready=$true;break}}catch{}; Start-Sleep -Milliseconds 250}
  if(-not $ready){throw 'Isolated API readiness deadline'}
  $worker=StartChild (Join-Path $repo '.local/connection-qualification/worker/Library.ConnectionQualification.exe') @($workerConfig)
  Write-Output "Synthetic native connection qualification running at $origin; evidence $root"
  $browser=StartChild 'C:/Program Files/nodejs/node.exe' @((Join-Path $repo 'client/node_modules/@playwright/test/cli.js'),'test','worker-connection.spec.ts','--config',(Join-Path $repo 'client/playwright.config.ts'),'--output',(Join-Path $root 'browser')) @{LIBRARY_BASE_URL=$origin;LIBRARY_CONNECTION_TEST='1';CHROMIUM_PATH='C:/Program Files/Google/Chrome/Application/chrome.exe'}
  $result=Finish $browser 130 (Join-Path $root 'browser.log'); Write-Output $result.text
  if($result.code -ne 0){throw 'Synthetic native/browser connection qualification failed'}
  $workerResult=Finish $worker 10 (Join-Path $root 'worker.log'); Write-Output $workerResult.text
  if($workerResult.code -ne 0){throw 'Synthetic worker failed'}
  if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $before.Hash){throw 'Synthetic input changed'}
  $readiness=Invoke-RestMethod "$origin/api/deployment/readiness" -TimeoutSec 5
  if(-not $readiness.ready -or $readiness.production -or $readiness.pairing){throw 'Safety readiness differs'}
  @{passed=$true;inputHashUnchanged=$true;readiness=$readiness;root=$root}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $root 'result.json')
  Write-Output 'Browser/native lifecycle passed. Waiting for the isolated API hard deadline; existing services untouched.'
} finally {
  if($worker -and -not $worker.process.HasExited){$worker.process.Kill($true);$worker.process.WaitForExit(5000)|Out-Null}
  if($api){$ended=Finish $api 170 (Join-Path $root 'api.log');if($ended.code -notin @(0,124)){Write-Warning "Isolated API exit $($ended.code); inspect its retained log."}}
}
$restart=StartChild 'wsl.exe' @('--exec','env',"DOTNET_CLI_HOME=$linuxRepo/.local/dotnet-home",'DOTNET_GENERATE_ASPNET_CERTIFICATE=false',"LIBRARY_CONFIG=$linuxConfig",'timeout','15',"$linuxRepo/.dotnet/dotnet","$linuxRepo/.local/connection-qualification/api/Library.Api.dll",'--urls',$origin,'--webroot',"$linuxRepo/client/dist")
try {
  $state=$null
  for($i=0;$i -lt 20;$i++){try{$state=Invoke-RestMethod "$origin/api/review-scan" -TimeoutSec 1;break}catch{};Start-Sleep -Milliseconds 250}
  if($state.connection.state -ne 'revoked' -or $state.enabled){throw 'Restart lost revocation'}
  $denied=Invoke-WebRequest "$origin/api/review-connection/worker" -Headers @{Authorization="Bearer $token"} -SkipHttpErrorCheck -TimeoutSec 3
  if($denied.StatusCode -ne 401){throw 'Revoked test token accepted after restart'}
  $catalog=Invoke-RestMethod "$origin/api/catalog?pageSize=100" -TimeoutSec 3
  if($catalog.total -ne 1){throw 'Saved synthetic catalog did not survive restart'}
  @{restartRevocation=$true;savedCatalog=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'restart.json')
} finally { $ended=Finish $restart 20 (Join-Path $root 'restart-api.log'); if($ended.code -notin @(0,124)){throw 'Restart qualification API failed'} }
Write-Output "Qualification stopped; evidence retained at $root"
