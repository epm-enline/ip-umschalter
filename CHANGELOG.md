# Changelog

## [1.1.3] – 2026-11-24
- Fenster lässt sich vergrößern und maximieren: Profilliste und IP-Tabelle wachsen mit, Eingabefelder werden breiter. Kleiner als die Ausgangsgröße geht nicht.

## [1.1.2] – 2026-11-24
- Kein Hinweis „läuft weiter“ mehr beim Schließen oder Minimieren des Fensters.

## [1.1.1] – 2026-11-24
- „Jetzt suchen“ installiert eine neue Version sofort und startet neu (schließt die Einstellungen; bei ungespeicherten Profiländerungen kommt die Speichern-Rückfrage).
- Einstellungen ohne Erklärtexte.
- Fix: ein wegen laufender Umschaltung verschobenes Update wurde erst nach 6 h statt nach 1 min erneut versucht.

## [1.1.0] – 2026-11-24
- Start ohne UAC-Abfrage: Aufgabe „IP-Umschalter“ in der Aufgabenplanung (wird beim ersten Start angelegt).
- Einstellungen: Autostart mit Windows, Start ohne Admin-Abfrage, automatisch aktualisieren.
- Auto-Update über GitHub-Releases (Prüfsumme + Versionsprüfung, wartet bei laufender Umschaltung / ungespeicherten Eingaben).
- Infobereich: Rechtsklick = Profile, Linksklick = Fenster; Schließen und Minimieren legen das Fenster in den Infobereich.
- Desktop-Verknüpfungen und zweiter Start übergeben an die laufende Instanz.

## [1.0.0] – 2026-11-24
- Profile mit DHCP oder mehreren statischen IPs, Gateway, DNS; Umschalten per Infobereich oder Desktop-Verknüpfung.
- Getrennte Adapter werden abgelehnt (Windows schaltet dort DHCP nicht um).
