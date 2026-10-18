param(
    [Parameter(Mandatory=$true)]
    [string]$FfmpegDirectory
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tools = (Resolve-Path -LiteralPath $FfmpegDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $tools 'ffmpeg.exe') -PathType Leaf)) {
    throw 'FfmpegDirectory must contain ffmpeg.exe and its redistribution notices.'
}
& (Join-Path $root 'build_native.ps1')
$package = Join-Path $root 'dist\SKYVIDEO_EDITOR-Portable'
if (Test-Path -LiteralPath $package) {
    throw "Portable directory already exists. Move it aside before building again: $package"
}
New-Item -ItemType Directory -Path $package | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'dist\SKYVIDEO_EDITOR.exe') -Destination $package
# Keep FFmpeg, optional ffprobe/DLLs, and the original distribution notices together.
Get-ChildItem -LiteralPath $tools | Where-Object { $_.Name -ne 'SKYVIDEO_EDITOR.exe' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $package -Recurse
}
foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $package "SKYVIDEO_EDITOR-$name")
}
@'
請先完整解壓縮 ZIP，再執行 SKYVIDEO_EDITOR.exe。
請保留同層 ffmpeg.exe、ffprobe.exe（若有）、DLL 與授權文件。
'@ | Set-Content -LiteralPath (Join-Path $package '使用說明.txt') -Encoding utf8
$archive = Join-Path $root 'dist\SKYVIDEO_EDITOR-Portable.zip'
Compress-Archive -LiteralPath $package -DestinationPath $archive -Force
$digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$digest  SKYVIDEO_EDITOR-Portable.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Write-Host "Ready: $archive"
