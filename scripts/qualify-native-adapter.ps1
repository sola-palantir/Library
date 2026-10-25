param([Parameter(Mandatory=$true)][string]$TestRoot,[Parameter(Mandatory=$true)][string]$TestAssembly,[Parameter(Mandatory=$true)][string]$ToolsRoot,[ValidateSet('Resources','BufferedInput','Comparison','Color','Window','Media','Media1080','OwnerExit','All')][string]$Mode='All')
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Native Windows qualification required.'}
$root=[IO.Path]::GetFullPath($TestRoot)
if(-not [IO.Directory]::Exists($root)){throw 'Supply an existing isolated qualification directory.'}
$assembly=[IO.Path]::GetFullPath($TestAssembly)
$folder=[IO.Path]::GetDirectoryName($assembly)
[Reflection.Assembly]::LoadFrom((Join-Path $folder 'Library.Execution.dll'))|Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $folder 'Library.Companion.dll'))|Out-Null
[Reflection.Assembly]::LoadFrom($assembly)|Out-Null
if($Mode -eq 'OwnerExit'){[Library.Companion.Tests.NativeAdapterQualification]::ExitOwner($root,(Get-Process -Id $PID).Path);throw 'Expected independent-process exit.'}
if($Mode -in @('BufferedInput','All')){[Library.Companion.Tests.NativeAdapterQualification]::BufferedInput((Get-Process -Id $PID).Path)}
if($Mode -in @('Comparison','All')){[Library.Companion.Tests.NativeComparisonQualification]::Preflight([IO.Path]::GetFullPath($ToolsRoot))}
if($Mode -in @('Color','All')){[Library.Companion.Tests.NativeColorQualification]::Run([IO.Path]::GetFullPath($ToolsRoot))}
if($Mode -in @('Window','All')){[Library.Companion.Tests.NativeWindowQualification]::Run($root,[IO.Path]::GetFullPath($ToolsRoot))}
if($Mode -in @('Resources','All')){
    [Library.Companion.Tests.NativeAdapterQualification]::Resources($root,(Get-Process -Id $PID).Path)
    $crashRoot=Join-Path $root ('owner-exit-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $crashRoot | Out-Null
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    foreach($argument in @('-NoLogo','-NoProfile','-File',$PSCommandPath,'-TestRoot',$crashRoot,'-TestAssembly',$assembly,'-ToolsRoot',$ToolsRoot,'-Mode','OwnerExit')){$start.ArgumentList.Add($argument)}
    $child=[Diagnostics.Process]::Start($start)
    try{if(-not $child.WaitForExit(30000)){$child.Kill($true);throw 'Owned crash qualification exceeded its deadline.'};if($child.ExitCode -ne 79){throw 'Owner did not exit at the expected checkpoint.'}}finally{$child.Dispose()}
    $processes=Get-Content -LiteralPath (Join-Path $crashRoot 'processes.json') -Raw|ConvertFrom-Json
    foreach($ownedPid in @($processes.rootProcess,$processes.childProcess)){
        $process=Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
        if($null -ne $process){try{if(-not $process.WaitForExit(5000)){throw "Owned process survived job owner exit: $ownedPid"}}finally{$process.Dispose()}}
    }
    @{OwnerExitedWithoutCleanup=$true;RootAndChildTerminated=$true;ProcessIds=$processes}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $crashRoot 'evidence.json')
    Write-Output "Independent owner-exit qualification passed: $crashRoot"
}
if($Mode -in @('Media','All')){[Library.Companion.Tests.NativeAdapterQualification]::Media($root,[IO.Path]::GetFullPath($ToolsRoot))}
if($Mode -in @('Media1080','All')){[Library.Companion.Tests.NativeAdapterQualification]::Media1080($root,[IO.Path]::GetFullPath($ToolsRoot))}
