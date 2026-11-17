# FaxDrucker

Virtueller Windows-Drucker, der Ausdrucke als Fax über einen SIP-Account versendet
(T.38 oder G.711), z. B. über **Placetel**.

## So funktioniert es

1. In einem beliebigen Programm auf den Drucker **„FaxDrucker“** drucken.
2. Es öffnet sich ein Fenster: Empfänger auswählen oder Faxnummer eintippen, **Senden**.
3. FaxDrucker wählt über den SIP-Zugang. Bietet die Gegenstelle T.38 an, wird per T.38
   gesendet, sonst als Töne über G.711.
4. Ein kleines Statusfenster zeigt den Fortschritt. Am Ende gibt es einen **Sendebericht als PDF**
   und einen Eintrag im Sendeprotokoll (Doppelklick auf das Symbol im Infobereich).

Bei „besetzt“ oder Abbruch wählt FaxDrucker automatisch erneut (einstellbar).

## Qualität

In den Einstellungen (Standard) und im Sendefenster (nur für dieses Fax) wählbar:

| Auflösung | dpi | Wofür | Dauer |
|---|---|---|---|
| Normal | 204×98 | einfache Texte | am schnellsten |
| **Fein** (Standard) | 204×196 | Briefe, Rechnungen | normal |
| Superfein | 204×391 | kleine Schrift, Pläne, Unterschriften | etwa doppelt so lang |

| Bildmodus | Wofür |
|---|---|
| **Text** (Standard) | scharfe Kanten für Schrift und Tabellen |
| Foto | Graustufen werden fein gerastert (Fehlerdiffusion), gut für Fotos, Logos, Scans |

Kann der Empfänger kein Superfein, sendet FaxDrucker automatisch noch einmal in Fein.
Fotoseiten enthalten viel mehr Bildpunkte und brauchen pro Seite oft mehrere Minuten.

## Installation

Voraussetzungen: Windows 10/11 (64 Bit) und [Ghostscript](https://ghostscript.com/releases/gsdnld.html)
(64 Bit). Das Installationsskript bietet an, Ghostscript per `winget` zu installieren.

1. Das Paket `FaxDrucker-win-x64` herunterladen (GitHub → Actions → letzter Build → Artifacts) und entpacken.
2. **`Installieren.cmd`** doppelklicken und die Administratorabfrage bestätigen.
3. Beim ersten Start öffnen sich die Einstellungen. Eintragen:

| Feld | Placetel |
|---|---|
| SIP-Domain / Registrar | `fpbx.de` |
| SIP-Benutzername | SIP-Benutzer des Faxgeräts/Endgeräts, z. B. `777123456` |
| SIP-Passwort | zugehöriges SIP-Passwort |
| Eigene Faxkennung | z. B. `+49 221 1234567` (max. 20 Zeichen) |

Die SIP-Zugangsdaten stehen im Placetel-Webportal beim jeweiligen SIP-Endgerät.
Am besten ein **eigenes Endgerät für den FaxDrucker** anlegen. Das Passwort wird mit
Windows-DPAPI verschlüsselt unter `%APPDATA%\FaxDrucker\settings.json` gespeichert.

Entfernen: `Deinstallieren.cmd` im Programmordner `C:\Program Files\FaxDrucker`.

## Kommandozeile

```
faxsend einrichten                       SIP-Zugang eintragen
faxsend senden 0221123456 Brief.pdf      Datei faxen (--g711 erzwingt G.711, --trace zeigt SIP,
                                         --normal/--fein/--superfein, --text/--foto)
faxsend selbsttest                       Ghostscript und Faxmodem lokal prüfen
faxsend protokoll                        letzte Sendungen
```

`FaxDrucker.exe Datei.pdf` öffnet das Sendefenster für eine Datei (z. B. über „Senden an“).

## Aufbau

```
Windows-Drucker "FaxDrucker" (Microsoft PS Class Driver)
   │  PostScript über lokalen Anschluss \\.\pipe\FaxDrucker
   ▼
FaxDrucker.exe (Infobereich, WinForms)  ── Sendefenster, Einstellungen, Protokoll
   │
   ▼
FaxDrucker.Core
   ├─ Ghostscript  → Fax-TIFF (G3, 204×196 dpi, 1728 Pixel breit)
   ├─ SIP (SIPSorcery): INVITE, Digest-Anmeldung, Re-INVITE auf T.38, BYE
   ├─ Medien: RTP/G.711 (PCMA/PCMU) bzw. UDPTL/T.38 mit Redundanz
   └─ faxshim → spandsp (T.30-Protokoll, Faxmodems V.17/V.29/V.27ter, T.38-Terminal)
```

| Verzeichnis | Inhalt |
|---|---|
| `src/FaxDrucker.Core` | Faxlogik: SIP, Medien, Umwandlung, Einstellungen, Bericht |
| `src/FaxDrucker.App` | Windows-Programm im Infobereich |
| `src/FaxDrucker.Cli` | `faxsend` für Tests und Skripte |
| `native/faxshim` | kleine C-Schicht über spandsp (stabile API für .NET) |
| `installer` | Installations-/Deinstallationsskripte |
| `tests` | Unit-Tests und Ende-zu-Ende-Tests mit simulierter Gegenstelle |

## Entwickeln

Linux (Tests):

```
sudo apt install libspandsp-dev libtiff-dev ghostscript dotnet-sdk-8.0
make -C native/faxshim
dotnet test tests/FaxDrucker.Tests
```

Die Ende-zu-Ende-Tests bauen einen echten SIP-Anruf über 127.0.0.1 gegen eine simulierte
Gegenstelle auf (407-Anmeldung, Annahme mit Audio, Re-INVITE auf T.38) und übertragen ein
zweiseitiges Dokument per T.38 und per G.711.

Windows: GitHub Actions baut spandsp und `faxshim.dll` mit MSYS2 (`native/build-windows.sh`),
führt die Tests unter Windows aus und erstellt das Paket `FaxDrucker-win-x64`.

## Fehlersuche

- Protokoll: `%APPDATA%\FaxDrucker\faxdrucker.log` (in den Einstellungen „Ausführliches Protokoll“
  aktivieren, um auch die T.30-Abläufe zu sehen).
- `faxsend senden <Nummer> <Datei> --trace` zeigt alle SIP-Nachrichten.
- Druckauftrag bleibt in der Warteschlange hängen: FaxDrucker im Infobereich muss laufen.

## Lizenzen

spandsp steht unter LGPL 2.1 (wird als eigene DLL dynamisch eingebunden), SIPSorcery unter
BSD-3-Clause, Ghostscript unter AGPL (wird separat installiert und nur aufgerufen).
