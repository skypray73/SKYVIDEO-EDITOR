$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "C# compiler not found: $csc" }
$src = Join-Path $root 'src\SKYVIDEO_EDITOR.cs'
$manifest = Join-Path $root 'src\app.manifest'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
& $csc /nologo /target:winexe /optimize+ /win32manifest:$manifest /out:"$dist\SKYVIDEO_EDITOR.exe" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $src
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $dist\SKYVIDEO_EDITOR.exe"
