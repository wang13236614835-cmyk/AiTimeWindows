$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sourceDir = Join-Path $projectRoot 'src'
$binDir = Join-Path $projectRoot 'bin'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(103,181,215))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White),5
$graphics.DrawArc($pen,10,10,44,44,-90,300)
$pen.Width = 3
$graphics.DrawLine($pen,32,19,32,33)
$graphics.DrawLine($pen,32,33,42,38)
$dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(83,157,112))
$graphics.FillEllipse($dot,47,10,9,9)
$memory = New-Object System.IO.MemoryStream
$bmp.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
$png = $memory.ToArray()
$iconPath = Join-Path $binDir 'AiTime.ico'
$writer = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($iconPath))
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]1)
$writer.Write([byte]64); $writer.Write([byte]64); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([UInt16]1); $writer.Write([UInt16]32); $writer.Write([UInt32]$png.Length); $writer.Write([UInt32]22); $writer.Write($png)
$writer.Dispose(); $memory.Dispose(); $dot.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bmp.Dispose()

$sources = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' | Where-Object { $_.Name -ne 'Fixture.cs' } | ForEach-Object { $_.FullName })
$compilerArgs = @('/nologo','/target:winexe','/optimize+','/platform:anycpu','/langversion:5','/codepage:65001','/warn:4',('/out:' + (Join-Path $binDir 'AiTime.exe')),('/win32manifest:' + (Join-Path $sourceDir 'app.manifest')),('/win32icon:' + $iconPath),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll') + $sources
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw ('Application build failed: ' + $LASTEXITCODE) }
foreach ($variant in @('allowed','blocked')) {
  $fixtureDir = Join-Path $binDir ('tests\' + $variant)
  New-Item -ItemType Directory -Force -Path $fixtureDir | Out-Null
  & $compiler '/nologo' '/target:winexe' '/optimize+' '/langversion:5' '/codepage:65001' ('/out:' + (Join-Path $fixtureDir 'Fixture.exe')) '/reference:System.dll' '/reference:System.Drawing.dll' '/reference:System.Windows.Forms.dll' (Join-Path $sourceDir 'Fixture.cs')
  if ($LASTEXITCODE -ne 0) { throw ('Fixture build failed: ' + $LASTEXITCODE) }
}
[pscustomobject]@{ Built = (Join-Path $binDir 'AiTime.exe'); Bytes = (Get-Item (Join-Path $binDir 'AiTime.exe')).Length; SHA256 = (Get-FileHash -LiteralPath (Join-Path $binDir 'AiTime.exe') -Algorithm SHA256).Hash } | ConvertTo-Json