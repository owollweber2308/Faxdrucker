#Requires -RunAsAdministrator
<#
  Richtet den Windows-Drucker "FaxDrucker" ein bzw. entfernt ihn (-Remove).
  Wird vom MSI-Installer und von install.ps1 aufgerufen.

  Drucker:   FaxDrucker
  Treiber:   Microsoft PS Class Driver (in Windows enthalten)
  Anschluss: \\.\pipe\FaxDrucker  (Übergabe an FaxDrucker.exe im Infobereich)
#>
param(
    [switch]$Remove,
    [string]$PrinterName = 'FaxDrucker'
)
$ErrorActionPreference = 'Stop'
$portName = '\\.\pipe\FaxDrucker'
$driver = 'Microsoft PS Class Driver'

function Ensure-Spooler {
    $svc = Get-Service Spooler
    if ($svc.Status -ne 'Running') {
        Write-Host 'Starte Druckwarteschlange…'
        Start-Service Spooler
        Start-Sleep -Seconds 2
    }
}

if ($Remove) {
    $ErrorActionPreference = 'Continue'
    Get-Process FaxDrucker -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue) {
        Write-Host "Entferne Drucker $PrinterName"
        Remove-Printer -Name $PrinterName
        Start-Sleep -Seconds 1
    }
    if (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue) {
        Write-Host "Entferne Anschluss $portName"
        Remove-PrinterPort -Name $portName
    }
    exit 0
}

Ensure-Spooler

if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) {
    Write-Host "Installiere Druckertreiber '$driver'"
    Add-PrinterDriver -Name $driver
}

if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {
    Write-Host "Lege Anschluss $portName an"
    try {
        Add-PrinterPort -Name $portName
    } catch {
        Write-Host "Add-PrinterPort fehlgeschlagen ($($_.Exception.Message)), lege Anschluss über die Registry an"
        New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Ports' -Name $portName `
            -Value '' -PropertyType String -Force | Out-Null
        Restart-Service Spooler -Force
        Start-Sleep -Seconds 3
    }
}

if (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue) {
    Remove-Printer -Name $PrinterName
}
Write-Host "Lege Drucker $PrinterName an"
Add-Printer -Name $PrinterName -DriverName $driver -PortName $portName -Comment 'Fax über SIP (Placetel)'
try { Set-PrintConfiguration -PrinterName $PrinterName -PaperSize A4 -Color $false } catch { }
Write-Host 'Drucker eingerichtet.'
exit 0
