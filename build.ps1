param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Resonite',
    [string]$DevProfile = $GamePath
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build .\checks\HideRMLMenu.Checks.csproj -c Release --nologo "-p:GamePath=$GamePath" "-p:DevProfile=$DevProfile"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet .\checks\bin\Release\net10.0\HideRMLMenu.Checks.dll $GamePath $DevProfile
    if ($LASTEXITCODE -ne 0) { throw 'Standalone checks failed.' }

    $manifest = Get-Content -LiteralPath .\manifest.json -Raw | ConvertFrom-Json
    [xml]$project = Get-Content -LiteralPath .\HideRMLMenu\HideRMLMenu.csproj -Raw
    if ($manifest.version_number -ne $project.Project.PropertyGroup.Version) { throw 'Version mismatch.' }
    $dist = New-Item -ItemType Directory -Path .\dist -Force
    $dll = Join-Path $dist.FullName 'HideRMLMenu.dll'
    Copy-Item -LiteralPath .\HideRMLMenu\bin\Release\net10.0\HideRMLMenu.dll -Destination $dll -Force
    $zipPath = Join-Path $dist.FullName "HideRMLMenu-$($manifest.version_number).zip"
    # Package only distribution files; exclude dependencies and stale output.
    $files = [ordered]@{
        'plugins/HideRMLMenu/HideRMLMenu.dll' = $dll
        'README.md' = Join-Path $PSScriptRoot 'README.md'
        'manifest.json' = Join-Path $PSScriptRoot 'manifest.json'
        'icon.png' = Join-Path $PSScriptRoot 'icon.png'
        'LICENSE' = Join-Path $PSScriptRoot 'LICENSE'
    }
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $files.GetEnumerator()) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entry.Value, $entry.Key) | Out-Null
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    Write-Host "Package: $zipPath"
} finally { Pop-Location }
