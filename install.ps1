<#
  install.ps1 - Put startup-apps on this PC: copy the script next to its C# helper
  in ~\.local\bin and add the Startup-folder shortcut that runs it at sign-in.
  Run it from the unzipped folder:  pwsh -ExecutionPolicy Bypass -File .\install.ps1
#>
$ErrorActionPreference = 'Stop'

$pwsh = (Get-Command pwsh.exe -ErrorAction SilentlyContinue).Source
if (-not $pwsh) { throw 'PowerShell 7 (pwsh.exe) is not installed. Install it first: winget install Microsoft.PowerShell' }

$target = Join-Path $env:USERPROFILE '.local\bin'
New-Item -ItemType Directory -Force -Path $target | Out-Null
foreach ($file in 'startup-apps.ps1', 'WindowLayout.cs') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $target -Force
}

$script = Join-Path $target 'startup-apps.ps1'
$link = Join-Path ([Environment]::GetFolderPath('Startup')) 'startup-apps.lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
$shortcut.TargetPath = $pwsh
$shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" -AtLogon"
$shortcut.WindowStyle = 7   # minimized
$shortcut.Save()

Write-Host "Installed to $target"
Write-Host "Startup shortcut: $link"
Write-Host 'Nothing is reopened yet: the recorder starts at your next sign-in and learns your layout from then on.'
