param([Parameter(Mandatory=$true)][string]$TestRoot,[Parameter(Mandatory=$true)][string]$CompanionTestAssembly)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Native Windows qualification required.'}
if(-not [IO.Directory]::Exists($TestRoot)){throw 'Supply an existing isolated test directory.'}
$companionFolder=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($CompanionTestAssembly))
[Reflection.Assembly]::LoadFrom((Join-Path $companionFolder 'Library.Execution.dll'))|Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $companionFolder 'Library.Companion.dll'))|Out-Null
[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($CompanionTestAssembly))|Out-Null
[Library.Companion.Tests.QualificationPublication]::NativeFixture([IO.Path]::GetFullPath($TestRoot))
