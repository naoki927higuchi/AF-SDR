$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'AF-SignalGenerator/AF-SignalGenerator.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'AF-SignalGenerator build failed.' }
