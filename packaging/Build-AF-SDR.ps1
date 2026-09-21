$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'AF-SDR/AF-SDR/AF-SDR.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'AF-SDR build failed.' }
