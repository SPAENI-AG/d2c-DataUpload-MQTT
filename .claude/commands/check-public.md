---
description: Repository vor dem Push auf vertrauliche Daten und Stil-Regeln prüfen
---

Prüfe das Repository (nur lesen, nichts ändern) und berichte kurz:

1. Vertrauliche Daten: Suche nach Passwörtern, Tokens, `.pfx`/`.key`-Dateien, echten Broker-Hostnamen,
   Geräte-UUIDs, Kundennamen, internen Servern (`srv-`, UNC-Pfade `\\`), E-Mail-Adressen ausser support@elektro-spaeni.ch.
2. Schreibweise: `Späni`, `SPAENI`, `Spaeni` dürfen nur in `elektro-spaeni.ch` vorkommen.
3. Übersetzung: neue `Loc.T("…")`/`{l:Tr …}`-Schlüssel ohne Eintrag in `Core/Strings.cs`.
4. Hilfe: `?`-Anker ohne Abschnitt in `Help/de.html` und `Help/en.html`.
5. `CHANGELOG.md` *Unveröffentlicht* enthält die aktuellen Änderungen.
Liste Funde mit Datei:Zeile und Vorschlag.
