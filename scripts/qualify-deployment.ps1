param(
    [Parameter(Mandatory=$true)][string]$BundleA,
    [Parameter(Mandatory=$true)][string]$BundleB,
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [int]$Port = 5093,
    [int]$RestorePort = 5094
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Use a fresh evidence directory.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$BundleA = (Resolve-Path -LiteralPath $BundleA).Path
$BundleB = (Resolve-Path -LiteralPath $BundleB).Path
$EvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$managerA = Join-Path $BundleA 'manager/Library.Manager.exe'
$managerB = Join-Path $BundleB 'manager/Library.Manager.exe'
$install = Join-Path $EvidenceRoot 'installation'
$restore = Join-Path $EvidenceRoot 'restored'
$hashA = (Get-FileHash -LiteralPath (Join-Path $BundleA 'release.json') -Algorithm SHA256).Hash.ToLowerInvariant()
$hashB = (Get-FileHash -LiteralPath (Join-Path $BundleB 'release.json') -Algorithm SHA256).Hash.ToLowerInvariant()
$results = [Collections.Generic.List[string]]::new()
function Invoke-Manager([string]$exe, [string[]]$arguments, [bool]$expectFailure = $false) {
    $output = & $exe @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | Out-File -LiteralPath (Join-Path $EvidenceRoot 'manager.log') -Append -Encoding utf8
    if (($exitCode -ne 0) -ne $expectFailure) { throw "Unexpected manager exit $exitCode for $($arguments[0]): $output" }
}
function State([string]$homePath) {
    $last = Get-ChildItem -LiteralPath (Join-Path $homePath 'events') -Filter '*.json' | Sort-Object Name | Select-Object -Last 1
    (Get-Content -Raw -LiteralPath $last.FullName | ConvertFrom-Json).state
}
function Check([bool]$condition, [string]$name) { if (-not $condition) { throw $name }; $results.Add($name) }
$url = "http://127.0.0.1:$Port"
$restoredUrl = "http://127.0.0.1:$RestorePort"
try {
    foreach ($bundle in @($BundleA, $BundleB)) {
        $manifest = Get-Content -Raw -LiteralPath (Join-Path $bundle 'release.json') | ConvertFrom-Json
        foreach ($component in @('manager/Library.Manager.exe', 'companion/Library.Companion.exe')) {
            $version = & (Join-Path $bundle $component) --version | ConvertFrom-Json
            Check ($LASTEXITCODE -eq 0 -and $version.version -eq $manifest.version -and -not $version.production -and -not $version.pairing) "Self-contained $component version $($manifest.version)"
        }
    }
    Invoke-Manager $managerA @('install', (Join-Path $EvidenceRoot 'bad-hash'), $BundleA, ('0' * 64), "$Port") $true
    $results.Add('Untrusted manifest rejected without starting API')
    Invoke-Manager $managerA @('install', $install, $BundleA, $hashA, "$Port")
    $ready = Invoke-RestMethod "$url/api/deployment/readiness"
    Check ($ready.ready -and -not $ready.paused -and -not $ready.production -and $ready.backend -eq 'windows-worker') 'Installed API passes readiness with production off'
    $journal = '{"synthetic":true,"state":"never burned","sequence":1}'
    [IO.File]::WriteAllText((Join-Path $install 'worker-state/qualification-journal.json'), $journal)
    $settings = @{ id='preferences'; seedRetentionDays=23; discCapacityBytes=50000000000L; discReserveBytes=1000000000L } | ConvertTo-Json
    Invoke-RestMethod "$url/api/settings" -Method Put -ContentType 'application/json' -Body $settings | Out-Null
    Invoke-RestMethod "$url/api/deployment/quiesce" -Method Post -ContentType 'application/json' -Body '{}' | Out-Null
    $blocked = $false
    try { Invoke-RestMethod "$url/api/settings" -Method Put -ContentType 'application/json' -Body $settings | Out-Null }
    catch { $blocked = [int]$_.Exception.Response.StatusCode -eq 503 }
    Check $blocked 'Quiesced API rejects new mutation with 503'
    Invoke-Manager $managerA @('start', $install)
    Invoke-Manager $managerA @('backup', $install)
    Check (Test-Path -LiteralPath (Join-Path $install "backups/$((State $install).backup)/restore-point.json")) 'Independent frozen catalog snapshot completed'
    Invoke-Manager $managerA @('stop', $install)
    Invoke-Manager $managerA @('start', $install)
    Check ((Invoke-RestMethod "$url/api/settings").preferences.seedRetentionDays -eq 23) 'Settings survive stop/start'
    Invoke-Manager $managerB @('update', $install, $BundleB, $hashB)
    $state = State $install
    Check ($state.phase -eq 'Ready' -and $state.previous.version -ne $state.candidate.version) 'Coordinated update retains previous release'
    Check ((Invoke-RestMethod "$url/api/settings").preferences.seedRetentionDays -eq 23) 'Settings survive snapshot, migration and update'
    Invoke-Manager $managerB @('backup', $install)
    $state = State $install
    $results.Add('Updated release creates a new verified snapshot with its own helper')
    Invoke-Manager $managerB @('rollback', $install) $true
    $results.Add('Rollback after ResumeIntent refused')
    Invoke-Manager $managerB @('restore', $install, $restore, $state.backup, "$RestorePort")
    Check ((State $restore).phase -eq 'RestoredPendingReview') 'Restore writes a new paused catalog and starts nothing'
    Check ((Get-Content -Raw -LiteralPath (Join-Path $restore 'restored-state/worker-state/qualification-journal.json')) -eq $journal) 'Native journal backup restores byte-for-byte into quarantine'
    Check (-not (Test-Path -LiteralPath (Join-Path $restore 'worker-state/qualification-journal.json'))) 'Older worker journal is never activated'
    Invoke-Manager $managerB @('start', $restore) $true
    Invoke-Manager $managerB @('approve-restore', $restore, ('0' * 64)) $true
    $review = (Get-FileHash -LiteralPath (Join-Path $restore 'restore-review.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    Invoke-Manager $managerB @('approve-restore', $restore, $review)
    Check ((Invoke-RestMethod "$restoredUrl/api/settings").preferences.seedRetentionDays -eq 23) 'Reviewed restore preserves synthetic catalog settings'
    Check ((Invoke-RestMethod "$restoredUrl/api/deployment/readiness").pairing -eq $false) 'Restored pairing remains off'
    @{ passed = $results; installation = $install; restored = $restore; publicRelease = $false; production = $false; mediaTouched = $false } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'result.json') -Encoding utf8
}
finally {
    # Only containers recorded under these fresh synthetic installation identities.
    # Never stop unrelated services; volumes and all evidence remain intact.
    foreach ($homePath in @($install, $restore)) {
        if (Test-Path -LiteralPath (Join-Path $homePath 'installation.json')) {
            $id = (Get-Content -Raw -LiteralPath (Join-Path $homePath 'installation.json') | ConvertFrom-Json).id
            $containers = & docker container ls --all --filter "label=com.library.install=$id" --format '{{.Names}}'
            foreach ($container in $containers) { if ($container) { & docker stop --time 30 $container | Out-Null } }
        }
    }
}
Get-Content -Raw -LiteralPath (Join-Path $EvidenceRoot 'result.json')
