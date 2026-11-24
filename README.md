# IP-Umschalter

Netzwerkprofile (DHCP oder statisch mit mehreren IPs, Gateway, DNS) per Klick umschalten.

## Installation
`IP-Umschalter.exe` aus dem [neuesten Release](https://github.com/epm-enline/ip-umschalter/releases/latest) in einen eigenen Ordner legen und starten. Beim ersten Start fragt Windows einmal nach Adminrechten; danach startet das Programm ohne Abfrage (Aufgabe „IP-Umschalter“ in der Aufgabenplanung).

## Bedienung
- **Infobereich** (Symbol „IP“ neben der Uhr): Rechtsklick → Profil wählen (✓ = aktiv), Linksklick → Fenster.
- **Fenster schließen/minimieren** → läuft im Infobereich weiter. Beenden über Rechtsklick → „Beenden“.
- **Einstellungen:** Autostart mit Windows, Start ohne Admin-Abfrage, automatisch aktualisieren.
- **Desktop-Verknüpfung** je Profil: `IP-Umschalter.exe --apply "Profilname"`.
- **Profile** liegen in `profiles.json` neben der exe (bleiben bei Updates erhalten).
- **Getrennter Adapter** wird abgelehnt: Windows schaltet DHCP dort nicht um. Erst Kabel anstecken.

## Entwicklung
```
powershell -ExecutionPolicy Bypass -File build.ps1      # csc aus .NET Framework 4.x, kein SDK nötig
IP-Umschalter.exe --selftest ergebnis.txt                # ohne Adminrechte, ändert nichts; Exitcode 0 = grün
```
**Neue Version:** `Version` in `src/App.cs` erhöhen, Abschnitt in `CHANGELOG.md`, committen + pushen, dann `release.ps1`. Installierte Programme aktualisieren sich innerhalb von 6 h (oder Einstellungen → „Jetzt suchen“).
