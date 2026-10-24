param([Parameter(Mandatory=$true)][string]$TestRoot, [string]$CompanionAssembly)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This qualification requires Windows PowerShell 7+.' }
$qualifiedParent = [IO.Path]::GetFullPath($TestRoot)
if (-not [IO.Directory]::Exists($qualifiedParent)) { throw 'Supply an existing isolated test directory.' }
$qualifiedRoot = Join-Path $qualifiedParent ('publication-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($qualifiedRoot) | Out-Null
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Compile the actual repository primitive, not a reimplementation. No SDK install.
if ($CompanionAssembly) { [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($CompanionAssembly)) | Out-Null }
else { Add-Type -Path (Join-Path $repo 'companion/Library.Companion/Scope.cs'), (Join-Path $repo 'companion/Library.Companion/WindowsPublication.cs') }
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$scope = [Library.Companion.Scope]::new($qualifiedRoot)
$payload = [Text.Encoding]::UTF8.GetBytes('invented native publication contract')
$result = [ordered]@{ root=$qualifiedRoot; filesystem=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($qualifiedRoot)).DriveFormat }
try {
    $file = [Library.Companion.WindowsPublication]::Open($scope, 'owned.partial', $false)
    try {
        $file.Write($payload); $identity = [Library.Companion.WindowsPublication]::Identity($file.SafeFileHandle)
        $locked = $false
        try { $other = [IO.FileStream]::new((Join-Path $qualifiedRoot 'owned.partial'), [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite); $other.Dispose() } catch [IO.IOException] { $locked=$true }
        Require $locked 'Concurrent writer was not refused.'
        $movedRoot = [IO.Path]::GetFullPath($qualifiedRoot + '-moved')
        Require ($movedRoot.StartsWith($qualifiedParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Test move escaped its isolated parent.'
        $parentLocked = $false
        try { [IO.Directory]::Move($qualifiedRoot, $movedRoot) } catch [IO.IOException] { $parentLocked=$true }
        Require $parentLocked 'Pinned parent replacement was not refused.'
        [Library.Companion.WindowsPublication]::Publish($scope, $file, 'published.bin')
        Require ([Library.Companion.WindowsPublication]::Identity($file.SafeFileHandle) -eq $identity) 'Identity changed.'
        $file.Position=0; $read=New-Object byte[] $payload.Length; Require ($file.Read($read) -eq $payload.Length) 'Short read.'
        Require ([Convert]::ToHexString($read) -eq [Convert]::ToHexString($payload)) 'Payload changed.'
        $result['sameHandlePublication']=$true; $result['flushSucceeded']=$true; $result['writerAndParentReplacementRefused']=$true
    } finally { $file.Dispose() }
    $collision = [Library.Companion.WindowsPublication]::Open($scope, 'collision.partial', $false)
    try {
        $collision.Write($payload); $refused=$false
        try { [Library.Companion.WindowsPublication]::Publish($scope, $collision, 'published.bin') } catch [ComponentModel.Win32Exception] { Require ($_.Exception.NativeErrorCode -eq 183) 'Unexpected collision error.'; $refused=$true }
        Require $refused 'Existing destination overwritten.'
    } finally { $collision.Dispose() }
    $result['collisionRefusedAndFilesRetained'] = [IO.File]::Exists((Join-Path $qualifiedRoot 'collision.partial')) -and [IO.File]::Exists((Join-Path $qualifiedRoot 'published.bin'))
    Require $result.collisionRefusedAndFilesRetained 'Collision files missing.'
    $result['reopenedSha256']=(Get-FileHash -LiteralPath (Join-Path $qualifiedRoot 'published.bin') -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload)).ToLowerInvariant()
    Require ($result.reopenedSha256 -eq $expected) 'Reopened hash mismatch.'
} finally { $scope.Dispose() }
# All links below refer only to newly invented files inside this test directory.
New-Item -ItemType HardLink -Path (Join-Path $qualifiedRoot 'second-link.bin') -Target (Join-Path $qualifiedRoot 'published.bin') | Out-Null
$scope = [Library.Companion.Scope]::new($qualifiedRoot)
try {
    $refused=$false
    try { $linked=[Library.Companion.WindowsPublication]::Open($scope,'published.bin',$true); $linked.Dispose() } catch [IO.IOException] { $refused=$true }
    Require $refused 'Multiple-link file was accepted for publication.'
    $result['hardlinkRefused']=$true
} finally { $scope.Dispose() }
$real = Join-Path $qualifiedRoot 'actual-directory'; [IO.Directory]::CreateDirectory($real) | Out-Null
$junction = Join-Path $qualifiedRoot 'junction'
New-Item -ItemType Junction -Path $junction -Target $real | Out-Null
$refused=$false
try { $unsafe=[Library.Companion.Scope]::new($junction); $unsafe.Dispose() } catch [IO.InvalidDataException] { $refused=$true }
Require $refused 'Junction scope was accepted.'; $result['junctionRefused']=$true
if ($CompanionAssembly) {
    $state = Join-Path $qualifiedRoot 'journal'; [IO.Directory]::CreateDirectory($state) | Out-Null
    $journal = [Library.Companion.Journal]::new($state)
    try {
        $eventJson = @{schemaVersion=1;commandId='synthetic-command';discId='synthetic-disc';manifestSha256=('a'*64);recorderId='no-device';state='planned';atUtc=[DateTime]::UtcNow;simulated=$true;mode='simulation';sequence=1} | ConvertTo-Json
        $event = [System.Text.Json.JsonSerializer]::Deserialize($eventJson,[type][Library.Companion.Evidence],[Library.Companion.Contract]::Json)
        $journal.Append($event)
        Require ($journal.Events('synthetic-disc').Count -eq 1) 'Published journal event unavailable.'
        $refused=$false; try { $journal.Append($event) } catch [InvalidOperationException] { $refused=$true }
        Require $refused 'Duplicate journal sequence accepted.'
    } finally { $journal.Dispose() }
    $journal = [Library.Companion.Journal]::new($state)
    try {
        Require ($journal.Events('synthetic-disc').Count -eq 1) 'Journal did not survive reopen.'
        [IO.File]::WriteAllText((Join-Path $state 'synthetic-disc.00000001.synthetic.partial'),'invented interruption')
        $refused=$false; try { $journal.Events('synthetic-disc') | Out-Null } catch [IO.InvalidDataException] { $refused=$true }
        Require $refused 'Incomplete journal publication did not block further use.'
        $result['compiledJournalPublishReopenAndInterruptionRefusal']=$true
    } finally { $journal.Dispose() }
}
$result['noFilesDeleted']=$true
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $qualifiedRoot 'result.json')
$result | ConvertTo-Json
