#Requires -RunAsAdministrator
param(
    [string]$Target = "$env:ProgramFiles\FaxDrucker",
    [string]$PrinterName = 'FaxDrucker'
)
$ErrorActionPreference = 'Continue'
$portName = '\\.\pipe\FaxDrucker'

Get-Process FaxDrucker -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue) { Remove-Printer -Name $PrinterName }
Start-Sleep -Seconds 1
if (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue) { Remove-PrinterPort -Name $portName }
Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'FaxDrucker' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\FaxDrucker.lnk') -ErrorAction SilentlyContinue

# Programmordner erst nach dem Beenden dieses Skripts löschen (Skript liegt evtl. darin)
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$Target`"" -WindowStyle Hidden

Write-Host 'FaxDrucker wurde entfernt. Einstellungen und Berichte bleiben unter %APPDATA%\FaxDrucker erhalten.'
