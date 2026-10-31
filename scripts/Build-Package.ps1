param([string]$SptInstallPath = $env:SPT_INSTALL_PATH)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# Without -SptInstallPath/SPT_INSTALL_PATH the build falls back to Directory.Build.local.props, which also validates the path.
$buildArgs = @()
if ($SptInstallPath) {
    if (-not (Test-Path -LiteralPath (Join-Path $SptInstallPath 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll'))) { throw "SptInstallPath '$SptInstallPath' is not an EFT / SPT 4.1 installation." }
    $buildArgs += "-p:SptInstallPath=$SptInstallPath"
}
& dotnet build (Join-Path $repo 'src\FuckThesePhysics\FuckThesePhysics.csproj') -c Release @buildArgs --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'FuckThesePhysics build failed' }
$dll = Join-Path $repo 'obj\FuckThesePhysics\plugin\FuckThesePhysics.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "Built plugin not found: $dll" }
$semver = ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion.Split('.')[0..2] -join '.')
$zip = Join-Path $repo "bin\Release\FuckThesePhysics-$semver-SPT4.1.zip"
New-Item -ItemType Directory -Path (Split-Path $zip) -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$file = [IO.File]::Open($zip, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create)
$entryName = 'BepInEx/plugins/FuckThesePhysics/FuckThesePhysics.dll'
$licenseEntryName = $entryName.Substring(0, $entryName.LastIndexOf('/')) + '/LICENSE'
try {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $dll, $entryName) | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $repo 'LICENSE'), $licenseEntryName) | Out-Null
}
finally { $archive.Dispose(); $file.Dispose() }
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    if ($archive.Entries.Count -ne 2 -or $archive.Entries[0].FullName -ne $entryName -or $archive.Entries[1].FullName -ne $licenseEntryName) { throw 'Unexpected FuckThesePhysics payload' }
    $stream = $archive.Entries[0].Open(); $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($hash -ne (Get-FileHash -LiteralPath $dll).Hash) { throw 'FuckThesePhysics package hash mismatch' }
} finally { $archive.Dispose() }
Get-FileHash -LiteralPath $zip
