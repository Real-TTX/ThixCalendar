# ThixCalendar – Logo

Ein Kalenderblatt mit Ringen, den Tagen einer Woche und dem markierten „Heute“ auf einer blauen Kachel – oben rechts ein
korallenroter **Erinnerungs-Badge mit Glocke** (um die Erinnerungen geht es). Akzentfarbe **#2F6FED**, dunkles Blau
**#163E9E**, Verlauf der Kachel **#5A9CFF → #1E5BE6**, Badge **#FF7A6B → #F0483B** (die Farbfamilie von ThixClip).

| Datei | Detailstufe | Einsatz |
|---|---|---|
| `logo-icon.svg` | voll (ab 48 px): Blatt mit Ringen, Woche, Heute, Badge mit Glocke | App-Icon (Windows .ico ab 48 px, macOS .icns), Fenster, Über-Seite |
| `logo-small.svg` | reduziert (16–32 px): dickes Blatt, Kopfband, ein „Heute“-Block, roter Punkt, Kachel randlos | Favicon, kleine .ico-Größen, Taskleiste, Windows-Tray |
| `logo-tray.svg` | einfarbig, ohne Kachel | macOS-Menüleiste (Template-Bild, das System färbt es); offene Erinnerungen / Pause zeichnet die App als Badge dazu |
| `logo-full.svg` | Icon + Wortmarke „Thix**Calendar**“ | README, Kopfzeilen, Über-Seiten |

`png/` enthält alle gerenderten Größen. Neu erzeugen (nach jeder Änderung an einer SVG):

```
cd tools && npm install && node render-icons.js
```

Das Skript rendert mit dem installierten Microsoft Edge (headless) und schreibt zusätzlich `src/ThixCalendar/Assets/*`
(thixcalendar.ico, Tray-Icons, Logos). Die macOS-Varianten `png/thixcalendar-mac-*.png` halten Apples Rand um die Kachel
ein; `scripts/package-macos.sh` baut daraus das `.icns`.
