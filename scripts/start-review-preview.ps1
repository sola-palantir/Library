#requires -Version 7.0
param(
  [Parameter(Mandatory=$true)][string]$SampleFolder,
  [Parameter(Mandatory=$true)][string]$PreviewRoot,
  [Parameter(Mandatory=$true)][string]$ToolsRoot,
  [string]$Image = 'library-readonly-review:local',
  [int]$Port = 5090
)
$ErrorActionPreference = 'Stop'
# Development preview only. A manually launched, ten-minute read-only session;
# no service, login task, permanent credential, media mount or pairing record.
$PreviewRoot = [IO.Path]::GetFullPath($PreviewRoot)
$SampleFolder = [IO.Path]::GetFullPath($SampleFolder).TrimEnd('\')
$ToolsRoot = [IO.Path]::GetFullPath($ToolsRoot)
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Use an unprivileged localhost port.' }
if (-not (Test-Path -LiteralPath $SampleFolder -PathType Container)) { throw 'Approved sample folder is absent.' }
$workerExe = Join-Path $PreviewRoot 'worker/Library.Companion.exe'
if (-not (Test-Path -LiteralPath $workerExe -PathType Leaf)) { throw 'Publish the local worker first; see docs/read-only-scan.md.' }
$sessionPath = Join-Path $PreviewRoot ('session-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($sessionPath) | Out-Null
$docker = 'C:\Program Files\Docker\Docker\resources\bin\docker.exe'
function Docker([string[]]$Arguments, [int]$Seconds = 30) {
  $start = [Diagnostics.ProcessStartInfo]::new($docker)
  $start.UseShellExecute = $false; $start.CreateNoWindow = $true
  $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
  foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
  $process = [Diagnostics.Process]::Start($start)
  $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
  if (-not $process.WaitForExit($Seconds * 1000)) { $process.Kill($true); throw "Docker exceeded its $Seconds-second deadline. Inspect the named preview container before retrying an uncertain action." }
  $text = $stdout.GetAwaiter().GetResult(); $detail = $stderr.GetAwaiter().GetResult()
  if ($process.ExitCode -ne 0) { throw "Docker failed: $detail" }
  return $text.Trim()
}
# Refuse an occupied port without touching the existing listener.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } finally { $listener.Stop() }
$sessionId = [Guid]::NewGuid().ToString('N')
$container = 'library-review-' + $sessionId
$volume = $container + '-data'
$now = [DateTimeOffset]::UtcNow; $expires = $now.AddMinutes(10)
$token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($token))).ToLowerInvariant()
$origin = "http://127.0.0.1:$Port"
$grant = @{ sessionId=$sessionId; folder=$SampleFolder; apiOrigin=$origin; tokenSha256=$hash; issuedUtc=$now; expiresUtc=$expires }
$apiConfiguration = @{ Library=@{ ExecutionBackend='windows-worker'; DataPath='/data'; ReviewSession=$grant; EnableSampleEncode=$false; EnableSampleRemux=$false; EnableSampleHandoff=$false; Roots=@() } }
$apiPath = Join-Path $sessionPath 'api.json'; $apiConfiguration | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $apiPath -Encoding utf8
$workerPath = Join-Path $sessionPath 'worker.json'
@{ apiOrigin=$origin; sessionId=$sessionId; folder=$SampleFolder; toolsRoot=$ToolsRoot; issuedUtc=$now; expiresUtc=$expires } | ConvertTo-Json | Set-Content -LiteralPath $workerPath -Encoding utf8
$statePath = Join-Path $sessionPath 'state.json'
$state = @{ container=$container; volume=$volume; url=$origin; expiresUtc=$expires; workerPid=$null; workerExe=$workerExe; production=$false; persistentPairing=$false }
$state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
try {
  Docker @('volume','create','--label','library.readonly-preview=true',$volume) | Out-Null
  Docker @('run','--detach','--name',$container,'--label','library.readonly-preview=true','--read-only','--tmpfs','/tmp:rw,noexec,nosuid,size=64m','--publish',"127.0.0.1:${Port}:8080",'--mount',"type=volume,source=$volume,target=/data",'--mount',"type=bind,source=$apiPath,target=/review-config.json,readonly",'--env','LIBRARY_CONFIG=/review-config.json',$Image) | Out-Null
  $ready = $false
  for ($i=0; $i -lt 20; $i++) {
    try { $health=Invoke-RestMethod "$origin/api/health" -TimeoutSec 2; if ($health.status -eq 'ok') { $ready=$true; break } } catch { }
    Start-Sleep -Milliseconds 500
  }
  if (-not $ready) { throw 'Isolated preview failed readiness; no worker was started.' }
  $start = [Diagnostics.ProcessStartInfo]::new($workerExe); $start.UseShellExecute=$false; $start.CreateNoWindow=$true
  $start.ArgumentList.Add('review-session'); $start.ArgumentList.Add($workerPath)
  $start.Environment['LIBRARY_REVIEW_TOKEN']=$token
  $worker = [Diagnostics.Process]::Start($start); $token=$null; $start.Environment.Remove('LIBRARY_REVIEW_TOKEN') | Out-Null
  $state.workerPid=$worker.Id; $state.workerStartedUtc=$worker.StartTime.ToUniversalTime().ToString('O'); $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
  $connected=$false
  for ($i=0; $i -lt 20; $i++) {
    $capabilities=Invoke-RestMethod "$origin/api/review-scan" -TimeoutSec 2
    if ($capabilities.connected) { $connected=$true; break }
    if ($worker.HasExited) { throw 'Read-only worker exited before connecting; no permanent pairing was created.' }
    Start-Sleep -Milliseconds 250
  }
  if (-not $connected) { throw 'Read-only worker connection timed out.' }
  $state | ConvertTo-Json
  Write-Output "Session state: $statePath"
} catch {
  # Do not remove containers, volumes or evidence. Stop only this named preview.
  try { Docker @('stop','--time','3',$container) 10 | Out-Null }
  catch { Write-Warning 'Preview cleanup failed. Inspect the recorded container state; the original startup error follows.' }
  throw
} finally { $token=$null }
