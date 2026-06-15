# Builds and runs the unit-test suite.
# The test project is x86 (native SQLite interop) and targets .NET Framework 4.7.2,
# so we build with VS MSBuild and run with vstest, pointing at the MSTest adapter.

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$vstest  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe"
$proj    = Join-Path $root "DawloomAttendance.Tests\DawloomAttendance.Tests.csproj"
$dll     = Join-Path $root "DawloomAttendance.Tests\bin\x86\Debug\DawloomAttendance.Tests.dll"

# Resolve the MSTest adapter from the NuGet cache (version pinned in the csproj).
$adapter = Get-ChildItem "$env:USERPROFILE\.nuget\packages\mstest.testadapter\*\build\net462" -Directory |
           Select-Object -Last 1 -ExpandProperty FullName

& $msbuild $proj /t:Restore,Build /p:Configuration=Debug /p:Platform=x86 /v:minimal /nologo /p:ZkSdk=true
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

& $vstest $dll /Platform:x86 /Framework:".NETFramework,Version=v4.7.2" /TestAdapterPath:$adapter
exit $LASTEXITCODE
