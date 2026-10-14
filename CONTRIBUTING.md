# Mitarbeit / Contributing

Issues und Pull Requests sind willkommen. Bitte kurz beschreiben, was und warum.

## Regeln

- Alle sichtbaren Texte über `Loc` (`Core/Strings.cs`, DE **und** EN). XAML: `{l:Tr Key}`, Code: `Loc.T("Key", …)`.
- Firmenname immer **SPÄNI Elektro-Mechanik AG**.
- Passwörter und andere Geheimnisse nie im Klartext speichern oder loggen.
- Keine echten Kunden-, Geräte- oder Broker-Daten in Code, Tests, Beispielen oder Screenshots.
- Kernlogik in `D2cDataUpload.Core` (ohne UI, ohne NuGet-Pakete), Oberfläche in `D2cDataUpload.App`.
- Vor dem Pull Request: `dotnet build D2cDataUpload.slnx -c Release` und `dotnet run --project tests/D2cDataUpload.Tests` ohne Fehler.

## Release (Maintainer)

1. `<Version>` in `Directory.Build.props` erhöhen (z.B. `1.3.0`).
2. In `CHANGELOG.md` den Abschnitt `## [1.3.0] - JJJJ-MM-TT` schreiben (wird zur Release-Notiz).
3. Committen und pushen, dann `git tag v1.3.0` und `git push origin v1.3.0`.
4. Der Workflow *Release* prüft Tag = Version, testet, baut die ZIP (win-x64, Einzeldatei), signiert optional
   und erstellt das GitHub-Release. Installierte Programme zeigen danach den Update-Hinweis.

Optionales Code-Signing: Repository-Secrets `SIGN_PFX_BASE64` (PFX als Base64) und `SIGN_PFX_PASSWORD`.
