$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'bin\AiTime.exe'
if (-not (Test-Path -LiteralPath $app)) { throw 'Build the application first: .\Build.ps1' }
$desktopPath = [Environment]::GetFolderPath('Desktop')
$shell = New-Object -ComObject WScript.Shell
$shortcutName = ([string]::Concat([char]0x7231,[char]0x65F6,[char]0x95F4)) + ' Windows.lnk'
$shortcut = $shell.CreateShortcut((Join-Path $desktopPath $shortcutName))
$shortcut.TargetPath = $app
$shortcut.WorkingDirectory = Split-Path -Parent $app
$shortcut.IconLocation = (Join-Path $PSScriptRoot 'bin\AiTime.ico')
$shortcut.Description = 'AiTime Windows - per-session application allowlist and focus timer'
$shortcut.Save()
$startMenu = [Environment]::GetFolderPath('Programs')
$startShortcut = $shell.CreateShortcut((Join-Path $startMenu 'AiTime Windows.lnk'))
$startShortcut.TargetPath = $app
$startShortcut.WorkingDirectory = Split-Path -Parent $app
$startShortcut.IconLocation = (Join-Path $PSScriptRoot 'bin\AiTime.ico')
$startShortcut.Description = 'AiTime Windows - focus timer and application allowlist'
$startShortcut.Save()
[pscustomobject]@{ Shortcut = (Join-Path $desktopPath $shortcutName); StartMenu = (Join-Path $startMenu 'AiTime Windows.lnk'); Application = $app } | ConvertTo-Json