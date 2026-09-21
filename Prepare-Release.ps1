param(
    [Parameter(Mandatory=$true)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [Parameter(Mandatory=$true)][string]$ZipPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not [IO.Path]::IsPathRooted($ZipPath)) { $ZipPath = Join-Path $PSScriptRoot $ZipPath }
$source = (Resolve-Path -LiteralPath $ZipPath).Path
$rootPrefix = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $source.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($source) -ne '.zip') { throw 'Select a local ZIP inside this project.' }
$relative = $source.Substring($rootPrefix.Length).Replace('\','/')
git -c "safe.directory=$PSScriptRoot" -C $PSScriptRoot check-ignore --quiet -- $relative
if ($LASTEXITCODE -ne 0) { throw 'Source ZIP must be Git-ignored development output.' }
$name = [IO.Path]::GetFileName($source)
if ($name -notmatch ('(^|[-_])' + [regex]::Escape($Version) + '([-_.]|$)')) { throw 'Selected ZIP filename must contain the release version.' }
$product = if ($name.StartsWith('AF-SignalGenerator-')) { 'AF-SignalGenerator' } else { 'AF-SDR' }
$name = "$product-$Version-Windows.zip"
$destination = Join-Path (Join-Path $PSScriptRoot 'Distribution') $name
foreach ($path in @($destination,"$destination.sha256","$destination.json")) {
    if (Test-Path -LiteralPath $path) { throw "Release already exists: $path" }
}
$hashPath = "$source.sha256"
if (-not (Test-Path -LiteralPath $hashPath)) { throw 'Validate the package and create its SHA256 sidecar before release preparation.' }
$expected = ((Get-Content -LiteralPath $hashPath -Raw).Trim() -split '\s+')[0]
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $expected) { throw 'Source ZIP checksum mismatch.' }
if ((Get-Item -LiteralPath $source).Length -ge 100MB) { throw 'ZIP exceeds the size supported by this Git distribution workflow.' }
$archive = [IO.Compression.ZipFile]::OpenRead($source)
try {
    if ($archive.Entries.Count -eq 0) { throw 'Empty archive.' }
    foreach ($entry in $archive.Entries) {
        $entryName = $entry.FullName.Replace('\','/')
        if ($entryName -match '^/|(^|/)\.\.(/|$)|:|(?i)\.(pfx|key)$') { throw "Unsafe archive entry: $entryName" }
        $stream = $entry.Open()
        try { $stream.CopyTo([IO.Stream]::Null) } finally { $stream.Dispose() }
    }
} finally { $archive.Dispose() }
$commit = git -c "safe.directory=$PSScriptRoot" -C $PSScriptRoot rev-parse HEAD
if ($LASTEXITCODE) { throw 'Cannot read preparation commit.' }
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
# CreateNew refuses overwrite even if another process prepared the same name meanwhile.
$inputStream = [IO.File]::OpenRead($source)
try {
    $outputStream = [IO.File]::Open($destination,[IO.FileMode]::CreateNew)
    try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
} finally { $inputStream.Dispose() }
if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw 'Published copy checksum mismatch.' }
"$hash  $name" | Set-Content -LiteralPath "$destination.sha256" -Encoding ascii
[ordered]@{
    ReleaseVersion = $Version
    PreparedAt = (Get-Date).ToString('o')
    PreparationCommit = $commit
    SourceZip = $relative
    SHA256 = $hash
    Size = (Get-Item -LiteralPath $destination).Length
    Note = 'Explicitly selected local package; complete product-specific validation before committing and pushing.'
} | ConvertTo-Json | Set-Content -LiteralPath "$destination.json" -Encoding utf8
Write-Host "Prepared $destination"
Write-Host 'Record HISTORY.md and commit only selected release files and matching source. No automatic push.'
