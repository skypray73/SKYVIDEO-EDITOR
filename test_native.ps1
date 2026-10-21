param([string]$FfmpegPath, [string]$VideoPath)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$work = Join-Path $root 'work'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$testExe = Join-Path $work 'NativeTests.exe'
& $csc /nologo /target:exe /main:NativeTests /out:$testExe /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $root 'src\SKYVIDEO_EDITOR.cs') (Join-Path $root 'tests\NativeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
if ($FfmpegPath -and $VideoPath) { & $testExe $FfmpegPath $VideoPath } else { & $testExe }
if ($LASTEXITCODE -ne 0) { throw 'Native control tests failed' }
