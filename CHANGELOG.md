# Changelog

Alle wichtigen Änderungen. Format nach [Keep a Changelog](https://keepachangelog.com/de/1.1.0/),
Versionen nach [Semantic Versioning](https://semver.org/lang/de/).
Der Abschnitt einer Version wird beim Release automatisch als Release-Notiz verwendet.

## [Unveröffentlicht]

### Geändert
- Neues Programm-Icon (Wolke mit Aufwärtspfeil) statt Platzhalter.
- GitHub-Repository `SPAENI-AG/d2c-DataUpload-MQTT` eingetragen; die Update-Prüfung ist damit aktiv.

## [1.2.0] - 2026-10-14

### Neu
- Update-Hinweis im Programm: prüft beim Start (höchstens täglich) die neueste Version auf GitHub;
  Herunterladen, Was ist neu, Version überspringen; Menü *Hilfe › Nach Updates suchen*
- Hinweis „Aktualisiert auf Version …“ nach einem Update
- Fenster *Hilfe › Info* mit Firma, Entwickler, Support-Webseite und E-Mail
- Neue Optik: Karten, hervorgehobene Hauptaktion, kompaktere Sendeseite
- Hilfe-Abschnitt *Installation & Updates*

### Geändert
- Passwort wird verschlüsselt gespeichert (Windows-DPAPI); alte Klartext-Einträge werden automatisch umgewandelt
- Footer: links Firma, rechts Version
- Verteilung über GitHub Releases (portable ZIP) statt ClickOnce

## [1.1.0] - 2026-10-13

### Neu
- WPF-Version (.NET 10) mit Spalten-Zuordnung, Sprache DE/EN, Hilfe im Browser, Einstellungsseite
