param([Parameter(Mandatory=$true)][string]$TestRoot,[Parameter(Mandatory=$true)][string]$TestAssembly,[Parameter(Mandatory=$true)][string]$ToolsRoot,[Parameter(Mandatory=$true)][string]$SyntheticSourceRoot)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Windows qualification required.'}
$root=[IO.Path]::GetFullPath($TestRoot)
$assembly=[IO.Path]::GetFullPath($TestAssembly);$folder=[IO.Path]::GetDirectoryName($assembly)
[Reflection.Assembly]::LoadFrom((Join-Path $folder 'Library.Execution.dll'))|Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $folder 'Library.Companion.dll'))|Out-Null
[Reflection.Assembly]::LoadFrom($assembly)|Out-Null
$commandText=[Library.Companion.Tests.TransportQualification]::Prepare($root,$ToolsRoot,$SyntheticSourceRoot)
$command=$commandText|ConvertFrom-Json
$imageId=& docker image inspect library-media:native-protocol --format '{{.Id}}'
if($LASTEXITCODE -ne 0){throw 'Qualified test image is missing.'}
$container='library-native-protocol-'+$command.jobId+'-'+$imageId.Replace('sha256:','').Substring(0,12)
$volume='library-native-protocol-'+$command.jobId
$configPath=Join-Path $root 'api-config.json'
# The bearer secret exists only in this manually invoked process; never output,
# environment, command line, transcript, config file, URL or receipt.
$token=[Convert]::ToHexStringLower([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$tokenHash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($token)))
$issued=[DateTimeOffset]::UtcNow
$grant=[ordered]@{CommandJson=$commandText;TokenSha256=$tokenHash;IssuedUtc=$issued.ToString('o');ExpiresUtc=$issued.AddMinutes(10).ToString('o')}
$config=@{Library=@{ExecutionBackend='windows-worker';DataPath='/data';WorkerQualification=$grant}}
function Save-Config { $config|ConvertTo-Json -Depth 64|Set-Content -LiteralPath $configPath }
function Wait-TestApi {
    for($i=0;$i -lt 30;$i++) { try { $health=Invoke-RestMethod -Uri 'http://127.0.0.1:5084/api/health' -TimeoutSec 2; if($health.status -eq 'ok'){return} } catch { }; Start-Sleep -Milliseconds 500 }
    throw 'Owned qualification API did not become ready.'
}
$created=$false
try {
    $existing=& docker inspect $container --format '{{index .Config.Labels "library.qualification.job"}}' 2>$null
    if($LASTEXITCODE -eq 0) { if($existing -ne $command.jobId){throw 'Existing container is not this owned qualification.'}; $created=$true }
    Save-Config
    $arguments=@('run','-d','--name',$container,'--pull','never','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges','--tmpfs','/tmp:rw,nosuid','--env','Library__DataPath=/data','--label',"library.qualification.job=$($command.jobId)",'--mount',"type=volume,source=$volume,target=/data",'--mount',"type=bind,source=$configPath,target=/app/appsettings.Production.json,readonly",'-p','127.0.0.1:5084:8080','library-media:native-protocol')
    if($created){& docker start $container|Out-Null}else{& docker @arguments|Out-Null}
    if($LASTEXITCODE -ne 0){throw 'Could not start isolated API.'};$created=$true
    Wait-TestApi
    $resultText=[Library.Companion.Tests.TransportQualification]::Run($root,$ToolsRoot,$token)
    $result=$resultText|ConvertFrom-Json
    $jobs=@(Invoke-RestMethod -Uri 'http://127.0.0.1:5084/api/jobs')
    if($jobs.Count -ne 1 -or $jobs[0].id -ne $command.jobId -or $jobs[0].status -ne 'qualification-completed'){throw 'Catalog did not reconcile exactly one synthetic job.'}
    $summary=Invoke-RestMethod -Uri 'http://127.0.0.1:5084/api/summary'
    if($summary.copies -ne 0 -or $summary.titles -ne 0){throw 'Synthetic test improperly created catalog media.'}
    & docker stop $container|Out-Null;if($LASTEXITCODE -ne 0){throw 'Could not stop owned API.'}
    # Expired-grant fixture uses the same token/job, with its whole ten-minute
    # validity window moved into the past. It only removes access.
    $grant.IssuedUtc=$issued.AddMinutes(-20).ToString('o');$grant.ExpiresUtc=$issued.AddMinutes(-10).ToString('o');Save-Config
    & docker start $container|Out-Null;if($LASTEXITCODE -ne 0){throw 'Could not restart owned API.'};Wait-TestApi
    $expired=[Library.Companion.Tests.TransportQualification]::GrantStatus($root,$token)
    if($expired -ne 401){throw 'Expired grant was not refused.'}
    $reopened=@(Invoke-RestMethod -Uri 'http://127.0.0.1:5084/api/jobs')
    if($reopened.Count -ne 1 -or $reopened[0].status -ne 'qualification-completed'){throw 'Catalog result did not survive API restart.'}
    & docker stop $container|Out-Null
    $config.Library.WorkerQualification=$null;Save-Config
    & docker start $container|Out-Null;if($LASTEXITCODE -ne 0){throw 'Could not restart revoked qualification.'};Wait-TestApi
    $revoked=[Library.Companion.Tests.TransportQualification]::GrantStatus($root,$token)
    if($revoked -ne 404){throw 'Cleared grant remained accessible.'}
    $report=[ordered]@{JobId=$command.jobId;Result=$result;ExpiredGrantStatus=$expired;RevokedGrantStatus=$revoked;CatalogJobs=1;CatalogCopies=0;CatalogSurvivedRestart=$true;Volume=$volume;GrantIssuedUtc=$issued;OriginalGrantExpiresUtc=$issued.AddMinutes(10);TokenPersisted=$false;MediaTransferred=$false;OffMachineExposure=$false}
    $report|ConvertTo-Json -Depth 64|Set-Content -LiteralPath (Join-Path $root 'transport-evidence.json')
    [pscustomobject]@{JobId=$command.jobId;Bytes=$result.result.output.bytes;ProcessRuns=$result.processRuns;Expired=$expired;Revoked=$revoked;CatalogJobs=1;CatalogCopies=0;Volume=$volume}|ConvertTo-Json
} finally {
    if($created){& docker stop $container|Out-Null}
    $config.Library.WorkerQualification=$null;Save-Config
    $token=$null;$tokenHash=$null;$grant=$null
}
