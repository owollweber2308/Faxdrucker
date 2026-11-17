#Requires -RunAsAdministrator
<#
  FaxDrucker installieren:
   - Programm nach C:\Program Files\FaxDrucker kopieren
   - Lizenzschlüssel abfragen
   - Drucker "FaxDrucker" mit dem in Windows enthaltenen PostScript-Treiber anlegen
   - Druckeranschluss \\.\pipe\FaxDrucker (Übergabe an das Programm im Infobereich)
   - Autostart für alle Benutzer
#>
param(
    [string]$Target = "$env:ProgramFiles\FaxDrucker",
    [string]$PrinterName = 'FaxDrucker'
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot

Write-Host "FaxDrucker wird installiert nach $Target" -ForegroundColor Cyan

# Aus dem Internet geladene Dateien freigeben
Get-ChildItem $src -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

# Laufende Instanz beenden
Get-Process FaxDrucker -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Dateien kopieren
New-Item -ItemType Directory -Force $Target | Out-Null
Get-ChildItem $src | Where-Object { $_.Name -notin @('install.ps1', 'Installieren.cmd') } |
    Copy-Item -Destination $Target -Recurse -Force

# Lizenz: wird für alle Benutzer unter %ProgramData%\FaxDrucker\lizenz.txt gespeichert
$faxsend = Join-Path $Target 'faxsend.exe'
& $faxsend lizenz | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host ("Vorhandene Lizenz: " + ((& $faxsend lizenz) -replace '^Lizenziert für ', ''))
} else {
    while ($true) {
        $key = Read-Host 'Bitte den Lizenzschlüssel eingeben (FD1-…)'
        if ([string]::IsNullOrWhiteSpace($key)) {
            Write-Warning 'Ohne Lizenzschlüssel fragt FaxDrucker beim ersten Start danach.'
            break
        }
        & $faxsend lizenz $key
        if ($LASTEXITCODE -eq 0) { break }
    }
}

# Drucker, Treiber und Anschluss
& "$Target\printer-setup.ps1" -PrinterName $PrinterName

# Autostart für alle Benutzer
New-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'FaxDrucker' `
    -Value "`"$Target\FaxDrucker.exe`"" -PropertyType String -Force | Out-Null

# Startmenü-Eintrag
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path $startMenu 'FaxDrucker.lnk'))
$lnk.TargetPath = "$Target\FaxDrucker.exe"
$lnk.WorkingDirectory = $Target
$lnk.Save()

# Ghostscript prüfen
$gs = Get-ChildItem "$env:ProgramFiles\gs\*\bin\gswin64c.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $gs) {
    Write-Warning 'Ghostscript ist nicht installiert. Es wird zum Umwandeln der Druckaufträge benötigt.'
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        $answer = Read-Host 'Ghostscript jetzt mit winget installieren? (j/n)'
        if ($answer -match '^[jJyY]') {
            winget install --id ArtifexSoftware.GhostScript -e --accept-package-agreements --accept-source-agreements
        }
    } else {
        Write-Host 'Bitte von https://ghostscript.com/releases/gsdnld.html die 64-Bit-Version installieren.'
    }
}

# Programm ohne Administratorrechte im Kontext des angemeldeten Benutzers starten
Start-Process explorer.exe "`"$Target\FaxDrucker.exe`""

Write-Host ''
Write-Host "Fertig. Der Drucker '$PrinterName' ist eingerichtet." -ForegroundColor Green
Write-Host 'Beim ersten Start öffnen sich die Einstellungen für den SIP-Zugang.'
Read-Host 'Mit Enter schließen'
