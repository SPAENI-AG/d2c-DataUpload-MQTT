---
description: Neue Version vorbereiten und als GitHub-Release auslösen (Argument: Versionsnummer, z.B. 1.3.0)
---

Bereite das Release **v$ARGUMENTS** vor:

1. Prüfe `git status` – der Arbeitsbereich muss sauber sein, Branch `main`.
2. Setze `<Version>$ARGUMENTS</Version>` in `Directory.Build.props`.
3. Verschiebe in `CHANGELOG.md` die Einträge unter *Unveröffentlicht* in einen neuen Abschnitt
   `## [$ARGUMENTS] - <heutiges Datum>` (Unterabschnitte Neu / Geändert / Behoben). Ist *Unveröffentlicht* leer,
   frage nach, was in die Release-Notiz soll.
4. Führe `dotnet build D2cDataUpload.slnx -c Release` und `dotnet run --project tests/D2cDataUpload.Tests -c Release` aus.
   Bei Fehlern abbrechen und berichten.
5. Prüfe mit `git diff`, dass keine Kunden-/Gerätedaten, Passwörter oder internen Hostnamen enthalten sind.
6. Commit `Release v$ARGUMENTS`, dann **zeige mir die Befehle** `git push`, `git tag v$ARGUMENTS`,
   `git push origin v$ARGUMENTS` und führe sie erst nach meiner Bestätigung aus.
