# FileDetective

Windows-Programm (C#, .NET 10, WPF), das Dateien und Ordner über ihren Namen findet, lokal und auf verbundenen Netzwerkfreigaben. Öffentlich unter MIT-Lizenz gedacht und für den Einsatz in einer Firma ausgelegt.

**Vor jeder Arbeit `PLAN.md` lesen.** Der Plan ist verbindlich. Gearbeitet wird etappenweise nach Abschnitt 10; der aktuelle Stand steht in Abschnitt 12.

## Regeln, die nie verletzt werden dürfen

1. Auf Netzwerkfreigaben nur Verzeichnisse auflisten, nie eine Datei öffnen.
2. Auf durchsuchten Orten nie schreiben. Eigene Daten nur unter `%LOCALAPPDATA%\FileDetective\`.
3. Freigaben mit genau einem Thread und mit Pausen durchlaufen, automatisch höchstens einmal täglich.
4. Ein fehlgeschlagenes Auflisten löscht nie Einträge. `IgnoreInaccessible = true` ist verboten.
5. Keine Adminrechte, keine Telemetrie.
6. Nur Abhängigkeiten mit freizügiger Lizenz; neue Pakete nur nach Rückfrage beim Nutzer.
7. Das Programm löscht nie selbstständig Dateien.

## Arbeitsweise

- Nur umsetzen, was unter „Umfang“ der aktuellen Etappe steht. Abweichungen vom Plan zuerst mit dem Nutzer klären und dann in `PLAN.md` nachtragen.
- Logik ohne Oberfläche gehört nach `FileDetective.Core` und bekommt Tests.
- Zugriffe auf Dateisystem, Uhr und Netz laufen über Schnittstellen, damit Tests Fehler und Zeit simulieren können.
- Code und Bezeichner auf Englisch. Oberflächentexte immer in beiden Sprachdateien (`Strings.resx`, `Strings.de.resx`), nie fest im XAML oder Code.
- Eine Etappe ist fertig, wenn ihre Abnahme erfüllt ist, `CHANGELOG.md` ergänzt und das Kästchen in `PLAN.md` Abschnitt 12 abgehakt ist.

## Befehle

```
dotnet build
dotnet test
dotnet run --project src/FileDetective.App
```
