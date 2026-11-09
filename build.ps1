# build.ps1 – buduje całe rozwiązanie (Release x64) do folderu bin\.
#
#   C++  : BenchCpp, GuiCpp (MSVC v145 – wymaga obciążenia „Programowanie aplikacji klasycznych w C++”)
#   C#   : BenchCs, GuiCs, Runner (.NET 10)
#   Java : BenchJava.jar, GuiJava.jar – javac + jar wywoływane przez Runner.csproj (Java.targets)
#
# Użycie:  .\build.ps1
$ErrorActionPreference = 'Stop'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild `
    -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Nie znaleziono MSBuild (Visual Studio).' }

& $msbuild (Join-Path $PSScriptRoot 'Benchmark.sln') -restore -p:Configuration=Release -p:Platform=x64 -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "Budowanie nie powiodło się (kod $LASTEXITCODE)." }

Write-Host "Gotowe – programy w folderze bin\:" -ForegroundColor Green
Get-ChildItem (Join-Path $PSScriptRoot 'bin') -Include *.exe, *.jar -Name
