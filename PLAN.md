# FileDetective – Umsetzungsplan

Stand: 2026-11-13. Dieser Plan ist die verbindliche Grundlage für die Umsetzung. Er ist so geschrieben, dass ein Modell ohne Kenntnis der Vorgespräche eine Etappe eigenständig umsetzen kann.

## 1. Ziel

FileDetective ist ein Windows-Programm, mit dem man Dateien und Ordner über ihren Namen in Sekundenbruchteilen findet, auf lokalen Laufwerken und auf verbundenen Netzwerkfreigaben. Es läuft im Hintergrund und erscheint per Tastenkürzel als Suchleiste.

Version 1 sucht nur nach Namen. Version 2 ergänzt eine Speicherplatz-Analyse und eine Dubletten-Suche. Eine Suche nach Inhalt oder Bedeutung ist ausdrücklich nicht Teil dieses Plans.

Das Programm wird unter MIT-Lizenz auf GitHub veröffentlicht. Jeder lädt es selbst herunter, es gibt keine Verteilung durch eine IT-Abteilung.

## 2. Leitplanken

Diese Regeln gelten in jeder Etappe und dürfen nie verletzt werden. Sie sind der Grund, warum das Tool in einer Firma einsetzbar ist.

1. **Auf Netzwerkfreigaben werden nur Verzeichnisse aufgelistet.** Es wird dort nie eine Datei geöffnet, auch nicht für Symbole, Vorschauen oder Prüfsummen.
2. **Auf durchsuchten Orten wird nie geschrieben.** Alle eigenen Daten liegen unter `%LOCALAPPDATA%\FileDetective\`.
3. **Freigaben werden mit genau einem Thread und mit Pausen durchlaufen** (Abschnitt 7.4).
4. **Freigaben werden höchstens einmal täglich automatisch aktualisiert**, zusätzlich nur auf ausdrücklichen Klick.
5. **Ein fehlgeschlagenes Auflisten löscht nie Einträge aus dem Verzeichnis.** Gelöscht wird nur, was ein erfolgreich und vollständig gelesenes Verzeichnis als fehlend ausweist.
6. **Keine Adminrechte**, weder zur Installation noch zur Laufzeit. Einzige Ausnahme (Nutzerentscheidung 2026-11-14): die Speicherplatz-Analyse auf ausdrücklichen Klick als eigener Prozess mit Adminrechten, siehe 11.1.
7. **Keine Telemetrie.** Die einzige Verbindung ins Internet ist die abschaltbare Update-Prüfung bei GitHub.
8. **Nur Abhängigkeiten mit freizügiger Lizenz** (MIT, Apache-2.0, BSD, Public Domain). Keine GPL/LGPL.
9. **Das Programm löscht nie selbstständig Dateien.** In Version 2 verschiebt es nur lokale Dateien auf ausdrücklichen Klick in den Papierkorb. Auf Freigaben gibt es kein Löschen.

## 3. Getroffene Entscheidungen

| Thema | Entscheidung |
|---|---|
| Plattform | nur Windows 10/11, x64 |
| Technik | C#, .NET 10, WPF |
| Erscheinungsbild | Hintergrundprogramm mit Symbol im Infobereich, Suchleiste per Tastenkürzel |
| Tastenkürzel | Alt+Leertaste, änderbar |
| Suchumfang v1 | nur Datei- und Ordnernamen |
| Suchlogik | mehrere Wörter in beliebiger Reihenfolge, alle müssen in Name oder Pfad vorkommen, Groß-/Kleinschreibung egal, `müller` = `mueller`, keine Tippfehler-Toleranz |
| Rangfolge | Treffer im Namen vor Treffern im Pfad, dann über das Tool geöffnete, dann zuletzt geänderte |
| Bedienung | Tab schaltet Alles/Dateien/Ordner/Programme, `.pdf` filtert nach Typ, Enter öffnet, Strg+Enter zeigt im Explorer, Strg+C kopiert den Pfad |
| Orte | Auswahlliste beim ersten Start, Benutzerordner vorausgewählt, Freigaben nur nach bewusstem Anhaken |
| Ausschlüsse | Systemordner und Entwickler-Ballast standardmäßig, Liste anpassbar |
| Aktualität | lokale Orte laufend, Freigaben höchstens täglich und auf Knopfdruck |
| Nicht erreichbare Freigabe | Treffer bleiben ausgegraut sichtbar |
| Start | mit Windows, abschaltbar |
| Auslieferung | Installer ohne Adminrechte und einzelne `.exe`, beide enthalten die .NET-Laufzeit |
| Signatur | keine, Hinweis auf die SmartScreen-Warnung in der Anleitung |
| Updates | Hinweis auf neue Version, Installation per Klick |
| Sprache | Oberfläche Deutsch und Englisch nach Windows-Sprache, Anleitung Englisch |
| Lizenz | MIT |
| Veröffentlichung | Repository erst öffentlich, wenn v1 lokal und am Firmen-Fileshare getestet ist |
| Speicherplatz (v2) | aufklappbare Ordnerliste mit Größenbalken, kein Kachelbild, Freigaben mit dabei |
| Dubletten (v2) | Inhaltsvergleich nur lokal, auf Freigaben nur „mögliche Dubletten“ nach Name und Größe |
| Löschen (v2) | nur lokal, nur in den Papierkorb, nur auf Klick |

Vorbild für Aufbau, Release-Ablauf und Anleitung ist das Projekt `boehla/ScreenKeeper` (C#, .NET 10, WPF, MIT, unsigniert, Inno Setup).

## 4. Offene Punkte

Diese Punkte entscheidet der Nutzer, bevor die jeweilige Etappe beginnt.

| Punkt | Wird gebraucht in | Vorschlag |
|---|---|---|
| Name des Rechteinhabers in `LICENSE` | Etappe 0 | Name des Nutzers |
| GitHub-Konto für das Repository | Etappe 8 | Entschieden 2026-11-14: `christophfetz/FileDetective`, Konstante `UpdateChecker.Repository` |

Am 2026-11-13 vom Nutzer bestätigt: der Arbeitsspeicherbedarf nach Abschnitt 6.5, der eigene Komplett-Durchlauf für die lokale Speicherplatz-Analyse nach Abschnitt 11.1 und die Namensgüte in der Rangfolge nach Abschnitt 6.4.

## 5. Aufbau der Lösung

```
FileDetective.sln
├─ src/FileDetective.Core/      Klassenbibliothek, net10.0-windows, ohne Oberfläche
├─ src/FileDetective.App/       WPF-Programm, net10.0-windows, UseWPF + UseWindowsForms
├─ tests/FileDetective.Tests/   xUnit
├─ installer/FileDetective.iss  Inno Setup
├─ .github/workflows/ci.yml, release.yml
├─ PLAN.md, CLAUDE.md, README.md, LICENSE, CHANGELOG.md
```

Alle Logik ohne Oberfläche liegt in `Core` und ist ohne WPF testbar. `App` enthält nur Fenster, Infobereich-Symbol, Tastenkürzel und die Verdrahtung.

### 5.1 Abhängigkeiten

| Paket | Zweck | Lizenz |
|---|---|---|
| `Microsoft.Data.Sqlite` | lokale Datenbank | MIT (SQLite ist Public Domain) |
| `CommunityToolkit.Mvvm` | ViewModels | MIT |
| `xunit`, `Microsoft.NET.Test.Sdk` | Tests | Apache-2.0 / MIT |
| `System.IO.Hashing` (erst v2) | schnelle Prüfsummen | MIT |

Das Symbol im Infobereich kommt aus `System.Windows.Forms.NotifyIcon`, deshalb `UseWindowsForms`. Weitere Pakete nur nach Rückfrage beim Nutzer.

### 5.2 Bausteine in `Core`

| Klasse | Aufgabe |
|---|---|
| `SearchNormalizer` | macht aus einem Namen die Suchform (Abschnitt 6.2) |
| `MemoryIndex` | Suchstruktur im Arbeitsspeicher (Abschnitt 6.3) |
| `SearchEngine` | zerlegt die Eingabe, sucht, bildet die Rangfolge, liefert Treffer mit vollem Pfad |
| `IndexDatabase` | SQLite-Zugriff (Abschnitt 6.1) |
| `IndexStore` | einzige Stelle, die Änderungen annimmt: schreibt in die Datenbank und danach in den `MemoryIndex` |
| `IDirectoryLister` | listet ein Verzeichnis auf; echte Umsetzung und Test-Attrappe |
| `Scanner` | durchläuft einen Ort oder Teilbaum und gleicht ihn mit dem Verzeichnis ab |
| `ExcludeRules` | entscheidet, was übersprungen wird |
| `LocalWatcher` | meldet Änderungen auf lokalen Orten |
| `RefreshScheduler` | entscheidet, wann welcher Ort durchlaufen wird |
| `ReachabilityMonitor` | weiß, welche Orte gerade erreichbar sind |
| `DriveDiscovery` | findet Laufwerke und verbundene Freigaben |
| `UsageTracker` | merkt sich über das Tool geöffnete Pfade |
| `SettingsService` | liest und schreibt `settings.json` |
| `UpdateChecker` | fragt GitHub nach einer neueren Version |
| `FileLog` | einfache Protokolldatei mit Größenbegrenzung |

## 6. Datenhaltung und Suche

### 6.1 Datenbank

Datei: `%LOCALAPPDATA%\FileDetective\index.db`. Einstellungen beim Öffnen: `journal_mode=WAL`, `synchronous=NORMAL`.

```sql
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);      -- schema_version

CREATE TABLE roots(
  id INTEGER PRIMARY KEY,            -- 1..255
  path TEXT NOT NULL,                -- wie gewählt, z. B. C:\Users\anna oder P:\Projekte
  unc_path TEXT,                     -- bei Netzlaufwerken \\server\freigabe\..., nur zur Anzeige
  kind INTEGER NOT NULL,             -- 0 lokal, 1 Netzwerk, 2 Wechseldatenträger
  enabled INTEGER NOT NULL,
  last_scan_started_utc INTEGER,
  last_scan_completed_utc INTEGER,
  last_scan_status INTEGER NOT NULL, -- 0 nie, 1 läuft, 2 vollständig, 3 abgebrochen, 4 vollständig mit Fehlern
  last_scan_failed_dirs INTEGER NOT NULL DEFAULT 0,
  attempts_today INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE entries(
  id INTEGER PRIMARY KEY,            -- vom IndexStore fortlaufend vergeben, nie wiederverwendet
  parent_id INTEGER NOT NULL,        -- 0 beim Wurzeleintrag eines Ortes
  root_id INTEGER NOT NULL,
  name TEXT NOT NULL,                -- Originalname; beim Wurzeleintrag der ganze Pfad des Ortes
  is_dir INTEGER NOT NULL,
  size INTEGER NOT NULL,             -- Bytes, bei Ordnern 0
  mtime INTEGER NOT NULL,            -- Unix-Sekunden UTC
  attrs INTEGER NOT NULL             -- Windows-Dateiattribute unverändert
);
CREATE INDEX ix_entries_parent ON entries(parent_id);

CREATE TABLE usage(
  path TEXT PRIMARY KEY COLLATE NOCASE,
  open_count INTEGER NOT NULL,
  last_opened_utc INTEGER NOT NULL
);
```

Jeder Eintrag kennt nur seinen eigenen Namen und seinen Elternordner. Der volle Pfad entsteht durch Hochlaufen der Elternkette. Dadurch ist das Umbenennen oder Verschieben eines Ordners eine einzige Zeilenänderung.

Größe und Attribute werden von Anfang an gespeichert, obwohl Version 1 sie nur anzeigt. Sie kommen beim Auflisten kostenlos mit und werden in Version 2 gebraucht.

`usage` hängt am Pfad und nicht an der Eintragsnummer, damit die Nutzungshistorie einen Neuaufbau des Verzeichnisses übersteht.

Ab Etappe 10 kommen die Tabelle `hashes`, der Index `ix_entries_size` auf `entries(size)` und ein Trigger dazu, der die Prüfsummen gelöschter Einträge entfernt. Sie werden ohne Änderung der Schemaversion mit `CREATE … IF NOT EXISTS` in jede Datenbank gelegt (Einzelheiten in 11.2).

### 6.2 Suchform eines Namens

`SearchNormalizer.Normalize(string)` liefert UTF-8-Bytes und arbeitet in dieser Reihenfolge:

1. Unicode-Normalform C herstellen (wichtig für Dateien von Macs, die Umlaute zerlegt speichern).
2. In Kleinbuchstaben wandeln (kulturunabhängig).
3. `ä→ae`, `ö→oe`, `ü→ue`, `ß→ss`.
4. Übrige Akzente entfernen (Normalform D, Zeichen der Klasse „NonSpacingMark“ streichen), also `é→e`.

Dieselbe Funktion wird auf jeden Suchbegriff angewendet. Dadurch findet `müller` auch `Mueller` und umgekehrt. Bekannte Grenze: `muller` findet `Müller` nicht.

Für reine ASCII-Namen gibt es einen schnellen Weg, der nur die Buchstaben A–Z verkleinert.

### 6.3 Suchstruktur im Arbeitsspeicher

Die Suche läuft nicht über SQL, sondern über eine kompakte Struktur im Arbeitsspeicher. Jede Sucheingabe prüft alle Einträge der Reihe nach. Das ist bei Millionen Einträgen schnell genug (Zielwerte in 6.5), erlaubt beliebige Teilwort-Treffer und ist einfach korrekt zu halten.

```csharp
struct EntryRec {            // 16 Bytes
    int    Parent;           // id des Elternordners, 0 = Wurzeleintrag
    uint   NameOffset;       // Segment (obere 8 Bit) und Position (untere 24 Bit) im Namensspeicher
    ushort NameLength;       // Länge der Suchform in Bytes
    byte   Flags;            // IsDir=1, Deleted=2, RootEntry=4
    byte   RootId;
    uint   MTime;            // Unix-Sekunden
}
```

- Die Eintragsnummer `id` ist zugleich die Position: `EntryRec[][]` mit Blöcken zu 65.536 Einträgen (`id >> 16`, `id & 0xFFFF`). So wird beim Wachsen nie ein großes Feld kopiert.
- Der Namensspeicher besteht aus Segmenten zu 16 MiB. Ein Name liegt nie über eine Segmentgrenze hinweg.
- Im Speicher liegt nur die Suchform. Originalname, Größe und Attribute holt die `SearchEngine` für die wenigen angezeigten Treffer aus der Datenbank.
- Gelöschte Einträge bekommen das Flag `Deleted`. Beim Umbenennen wird die neue Suchform angehängt, die alte bleibt als Lücke. Beides bereinigt sich beim nächsten Programmstart, weil die Struktur dann neu aus der Datenbank geladen wird.
- Zugriffsschutz über `ReaderWriterLockSlim`: Suchen halten die Lesesperre, Änderungen kommen in kleinen Stapeln unter der Schreibsperre.
- Beim Programmstart lädt ein Hintergrund-Thread alle Einträge aus der Datenbank. Bis dahin zeigt die Suchleiste „Verzeichnis wird geladen“.

Wenn beim Start die höchste vergebene Nummer deutlich über der Zahl der Einträge liegt (`maxId > 1,5 × Anzahl + 100.000`), nummeriert das Programm die Einträge in der Datenbank einmalig neu durch (Etappe 7).

### 6.4 Suchablauf

**Eingabe zerlegen**

- An Leerzeichen trennen. Text in Anführungszeichen bleibt ein Begriff. Höchstens 8 Begriffe, weitere werden ignoriert.
- Ein Begriff mit `\` oder `/` wird an diesen Zeichen in mehrere Begriffe geteilt.
- Ein Begriff der Form `.` plus 1–10 Buchstaben/Ziffern ist ein Typfilter (Namensende). Mehrere Typfilter gelten als „oder“.
- Jeder Begriff wird in die Suchform gebracht.
- Dazu kommt der Modus Alles / Dateien / Ordner / Programme. „Programme“ (nachgetragen am 2026-11-14 nach Rückmeldung des Nutzers) zeigt nur Dateien mit den Endungen `.lnk`, `.appref-ms`, `.url` und `.exe`; `.exe` unterhalb eines Ordners `bin`, `obj`, `publish`, `packages` oder `.nuget` zählt nicht dazu.

**Treffer bestimmen**

Ein Eintrag ist ein Treffer, wenn jeder Begriff im eigenen Namen oder im Namen eines übergeordneten Ordners vorkommt.

1. Für alle Ordner berechnen, welche Begriffe ihr eigener Name enthält (Bitmaske, ein Bit je Begriff). Parallel über alle Kerne.
2. Für jeden Ordner die geerbte Maske bilden: eigene Maske oder Maske des Elternordners. Über die Elternkette mit Merken des Ergebnisses, damit die Reihenfolge der Nummern keine Rolle spielt.
3. Für jeden Eintrag: eigene Maske oder geerbte Maske des Elternordners. Sind alle Bits gesetzt und passen Typfilter und Modus, ist es ein Treffer. Parallel über alle Kerne.

Der Teilwort-Vergleich ist `ReadOnlySpan<byte>.IndexOf`, das .NET vektorisiert ausführt.

**Rangfolge** (absteigend, jeweils bei Gleichstand das nächste Kriterium)

1. Namensstufe: 2 = alle Begriffe stehen im eigenen Namen, 1 = mindestens einer, 0 = nur im Pfad.
2. Namensgüte, nur bei Stufe 2: 3 = Name (ohne Endung) ist genau die Eingabe, 2 = Name beginnt mit dem ersten Begriff, 1 = ein Begriff beginnt an einer Wortgrenze, 0 = sonst.
2a. Programme zuerst: Dateien mit der Endung `.lnk`, `.exe`, `.appref-ms` oder `.url` vor allen anderen Einträgen. Nachgetragen am 2026-11-14 nach Rückmeldung des Nutzers: Wer „claude“ sucht, will das Programm, nicht die vielen `CLAUDE.md`.
3. Über das Tool geöffnet: ja vor nein, dann jüngstes Öffnen zuerst.
4. Änderungsdatum, neuestes zuerst.
5. Kürzerer Name zuerst, dann alphabetisch.

Je Thread wird nur eine begrenzte Bestenliste (200 Einträge) geführt und am Ende zusammengeführt. Zurück kommen die besten 100 Treffer und die Gesamtzahl.

**Ablauf in der Oberfläche**

Jede Eingabeänderung startet nach 30 ms Ruhe eine Suche im Thread-Pool und bricht die vorherige ab (`CancellationToken`). Bei leerer Eingabe erscheinen die zuletzt über das Tool geöffneten Einträge.

### 6.5 Zielwerte

| Größe | Ziel |
|---|---|
| Suche über 1 Mio. Einträge | unter 50 ms je Eingabe auf 4 Kernen |
| Suchleiste erscheint nach Tastenkürzel | unter 100 ms |
| Arbeitsspeicher | rund 100 MB Grundlast (.NET und WPF) plus rund 45 MB je 1 Mio. Einträge (gemessen in Etappe 7: rund 65 MB je 1 Mio.) |
| Prozessorlast im Leerlauf | praktisch 0 |
| Erster Durchlauf lokal, 500.000 Einträge, SSD | unter 3 Minuten |
| Laden beim Start | rund 1 Mio. Einträge je Sekunde |

Der Arbeitsspeicher wächst mit der Zahl der Einträge. Ein kompletter Firmen-Fileshare mit 10 Mio. Dateien würde nach der Messung rund 650 MB belegen. Die Ortsauswahl empfiehlt deshalb, von großen Freigaben nur die benötigten Unterordner aufzunehmen, und zeigt nach dem Durchlauf die Zahl der Einträge je Ort.

## 7. Durchlauf der Orte

### 7.1 Auflisten

```csharp
public readonly record struct RawEntry(
    string Name, bool IsDirectory, long Size, DateTime LastWriteUtc, FileAttributes Attributes);

public interface IDirectoryLister {
    // Liefert den vollständigen Inhalt eines Verzeichnisses oder wirft eine Ausnahme.
    // Liefert nie ein Teilergebnis.
    IReadOnlyList<RawEntry> List(string directoryPath);
}
```

Die echte Umsetzung nutzt `System.IO.Enumeration.FileSystemEnumerable<RawEntry>` mit `RecurseSubdirectories = false`, `AttributesToSkip = 0`, `ReturnSpecialDirectories = false` und **`IgnoreInaccessible = false`**. Name, Größe, Datum und Attribute kommen dabei in einer Antwort je Verzeichnis, es gibt keine Einzelabfragen je Datei.

`IgnoreInaccessible = true` ist verboten: Dann käme bei einem Zugriffsfehler eine leere Liste zurück, und der Abgleich würde den ganzen Teilbaum löschen. Tritt mitten im Auflisten eine Ausnahme auf, wird das Teilergebnis verworfen und die Ausnahme weitergereicht.

### 7.2 Abgleich

Der `Scanner` läuft mit einem eigenen Stapel (keine Rekursion) durch den Baum. Für jedes Verzeichnis:

1. Verzeichnis auflisten. Bei einer Ausnahme (`UnauthorizedAccessException`, `IOException`, `DirectoryNotFoundException`): Zähler für fehlgeschlagene Verzeichnisse erhöhen, den bisherigen Teilbaum im Verzeichnis **unverändert lassen**, weiter mit dem nächsten.
2. Ausschlüsse anwenden (7.3).
3. Mit den bekannten Kindern vergleichen (`SELECT … WHERE parent_id = ?`, Namensvergleich `OrdinalIgnoreCase`): neue einfügen, geänderte aktualisieren (Größe, Datum, Attribute), fehlende samt Teilbaum löschen.
4. Unterordner auf den Stapel legen. Ausnahme: Ordner mit dem Attribut `ReparsePoint`, deren `DirectoryInfo.LinkTarget` nicht `null` ist (symbolische Links, Junctions), werden eingetragen, aber nicht betreten. Ordner mit `ReparsePoint` ohne Linkziel (OneDrive, DFS) werden normal betreten.

Änderungen gehen in Stapeln von 5.000 Zeilen je Transaktion an den `IndexStore`. Beim ersten Durchlauf eines Ortes entfällt Schritt 3, weil nichts bekannt ist.

Ist der Ort selbst nicht erreichbar, bricht der Durchlauf sofort ab und ändert nichts. Jeder Durchlauf ist jederzeit abbrechbar; bereits geschriebene Stapel bleiben gültig.

Es läuft immer nur ein Durchlauf gleichzeitig. Lokale Orte kommen in der Warteschlange vor Freigaben.

### 7.3 Ausschlüsse

Übersprungene Ordner werden gar nicht ins Verzeichnis aufgenommen.

| Art | Standardwerte |
|---|---|
| Ordnername, überall | `node_modules`, `.git`, `.svn`, `.hg`, `.vs`, `.idea`, `__pycache__`, `$RECYCLE.BIN`, `System Volume Information` |
| Voller Pfad, mit Umgebungsvariablen und `*` je Pfadteil | `%WINDIR%`, `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%ProgramData%`, `C:\Users\*\AppData` |
| Dateiname, mit `*` | `~$*`, `Thumbs.db`, `desktop.ini`, `*.tmp` |
| Attribut | Einträge mit dem Attribut `System` |

Versteckte Dateien ohne Systemattribut werden aufgenommen.

**Startmenü** (nachgetragen am 2026-11-14 nach Rückmeldung des Nutzers): Die beiden Startmenü-Ordner `%APPDATA%\Microsoft\Windows\Start Menu` und `%ProgramData%\Microsoft\Windows\Start Menu` werden immer aufgenommen, obwohl sie unter ausgeschlossenen Pfaden liegen. Sonst findet das Programm keine installierten Programme.
- Liegt ein Startmenü-Ordner in einem Ort, gilt für ihn und für jeden Ordner auf dem Weg dorthin keine Ausschlussregel, auch nicht das Systemattribut (`C:\ProgramData\Microsoft` trägt es). Alles andere unter einem sonst ausgeschlossenen Ordner bleibt ausgeschlossen: Aus `AppData` kommen nur `Roaming\Microsoft\Windows\Start Menu` und die Ordner auf dem Weg dorthin ins Verzeichnis. Im Startmenü selbst gelten die Regeln wie überall (`desktop.ini` bleibt draußen).
- Liegt ein Startmenü-Ordner in keinem Ort, wird er einmalig nach der Ersteinrichtung (bei bestehenden Installationen beim nächsten Start) als eigener lokaler Ort hinzugefügt. Entfernt der Nutzer ihn in den Einstellungen, bleibt er entfernt.

### 7.4 Schonung der Freigaben

- Genau ein Thread je Freigabe, nie parallel.
- Nach jedem Verzeichnis eine Pause in der Länge der gerade gemessenen Auflistdauer, höchstens 250 ms. Der Durchlauf belegt damit höchstens die Hälfte einer einzelnen Verbindung und wird von selbst langsamer, wenn der Server langsam antwortet.
- Einstellung „sehr schonend“: Pause = vierfache Auflistdauer, mindestens 20 ms, höchstens 1 s.
- Das Protokoll hält je Durchlauf fest: Zahl der aufgelisteten Verzeichnisse, Zahl der Einträge, Dauer, Summe der Pausen, Zahl der Fehler. Damit lässt sich die Last gegenüber der IT belegen.
- Dateisymbole kommen über `SHGetFileInfo` mit `SHGFI_USEFILEATTRIBUTES`, also allein aus der Dateiendung und ohne Zugriff auf die Datei.

### 7.5 Wann wird durchlaufen

| Ort | Auslöser |
|---|---|
| Lokal | 60 Sekunden nach jedem Programmstart (holt nach, was bei beendetem Programm geschah); danach laufend über `LocalWatcher`; auf Klick |
| Freigabe, Wechseldatenträger | wenn der letzte vollständige Durchlauf an einem früheren Kalendertag war und der Ort erreichbar ist; geprüft 2 Minuten nach Start, danach stündlich und bei Netzwerkwechsel; auf Klick |

Ein abgebrochener Freigaben-Durchlauf wird bei der nächsten Prüfung wiederholt, höchstens dreimal je Kalendertag (`attempts_today`).

### 7.6 Laufende Änderungen auf lokalen Orten

Je lokalem Ort ein `FileSystemWatcher` mit `IncludeSubdirectories = true`, `InternalBufferSize = 64 KB` und `NotifyFilter = FileName | DirectoryName`. Größen- und Datumsänderungen werden bewusst nicht überwacht, weil sie sehr viele Ereignisse erzeugen; sie kommen mit dem Durchlauf nach dem nächsten Start.

- Ereignisse werden je Pfad 300 ms gesammelt und von einem einzelnen Arbeiter abgearbeitet.
- Jedes Ereignis wird als „Pfad abgleichen“ behandelt: Existiert der Pfad, wird er eingetragen oder aktualisiert; existiert er nicht, wird er samt Teilbaum entfernt. Das ist wiederholbar und gegen verlorene oder doppelte Ereignisse unempfindlich.
- Ein neu erschienener Ordner wird als Teilbaum durchlaufen, weil beim Hineinverschieben nur ein Ereignis für den Ordner selbst kommt.
- Umbenennen: Ist der alte Pfad bekannt und der neue noch nicht, wird der Eintrag direkt umgehängt (eine Zeile). Sonst beide Pfade abgleichen.
- Ausschlüsse gelten auch hier.
- Meldet der Watcher einen Pufferüberlauf (`Error`), wird der ganze Ort neu durchlaufen.

Freigaben bekommen nie einen Watcher.

### 7.7 Erreichbarkeit

`Directory.Exists` kann auf einer toten Freigabe über 20 Sekunden hängen. Die Prüfung läuft deshalb immer in einem Hintergrund-Task mit 5 Sekunden Zeitgrenze, nie im Oberflächen-Thread.

Geprüft wird beim Start, bei Netzwerkwechsel (`NetworkChange.NetworkAddressChanged`, entprellt) und beim Öffnen der Suchleiste, wenn die letzte Prüfung über 60 Sekunden zurückliegt.

## 8. Oberfläche

### 8.1 Programmrahmen

- Kein Hauptfenster, `ShutdownMode = OnExplicitShutdown`.
- Nur eine Instanz (benannter Mutex). Ein zweiter Start weckt die laufende Instanz und zeigt die Suchleiste.
- Start mit `--autostart`: still im Hintergrund. Start ohne Parameter: Suchleiste zeigen, beim allerersten Start die Ersteinrichtung.
- Autostart über `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Die Registrierung ist die einzige Wahrheit über den Autostart.
- `app.manifest` mit DPI-Modus PerMonitorV2.
- Unbehandelte Ausnahmen werden protokolliert und als Hinweis im Infobereich gemeldet.

### 8.2 Symbol im Infobereich

Linksklick öffnet die Suchleiste. Menü: Suchen, Jetzt aktualisieren, Einstellungen, Statuszeile (z. B. „1.234.567 Einträge · aktualisiert heute 08:12“), Beenden. In Version 2 kommen „Speicherplatz“ und „Dubletten“ dazu.

### 8.3 Tastenkürzel

`RegisterHotKey` über ein unsichtbares Nachrichtenfenster (`HwndSource`), mit `MOD_NOREPEAT`. Schlägt die Registrierung fehl, weil ein anderes Programm das Kürzel belegt (z. B. PowerToys Run), meldet das Programm es im Infobereich und öffnet die Einstellung zum Ändern.

### 8.4 Suchleiste

```
┌──────────────────────────────────────────────────────────────┐
│ 🔍 angebot müller .pdf                       [Alles ▸ Tab]   │
├──────────────────────────────────────────────────────────────┤
│ 📄 Angebot_Mueller_2026-03.pdf   P:\Kunden\Müller\   12.03.  │
│ 📁 Müller Angebote 2026          P:\Vertrieb\        02.11.  │
│ 📄 angebot-mueller-entwurf.pdf   C:\Users\…\Docs\    28.10.  │
├──────────────────────────────────────────────────────────────┤
│ 3 Treffer · Enter öffnen · Strg+Enter Ordner · Esc schließen │
└──────────────────────────────────────────────────────────────┘
```

- Rahmenlos, immer im Vordergrund, nicht in der Taskleiste, etwa 720 Einheiten breit, mittig im oberen Drittel des Bildschirms, auf dem sich der Mauszeiger befindet.
- Das Fenster wird beim Programmstart einmal erzeugt und danach nur gezeigt und versteckt.
- Es schließt sich bei Esc und bei Fokusverlust. Beim erneuten Öffnen steht die letzte Eingabe markiert im Feld.
- Nach dem Tastenkürzel muss das Eingabefeld sicher den Fokus haben (`Show`, `Activate`, `Focus`). Das ist ein bekannter Stolperstein und steht in der Abnahme.
- Trefferliste virtualisiert, höchstens 100 Zeilen. Je Zeile: Symbol, Name mit hervorgehobenen Fundstellen, Ordnerpfad (in der Mitte gekürzt), Änderungsdatum.
- Treffer von nicht erreichbaren Orten sind ausgegraut und tragen den Zusatz „nicht verbunden“.
- Fußzeile: Trefferzahl und Tastenhinweise, oder der Zustand („Verzeichnis wird geladen“, „Erster Durchlauf läuft: 123.456 Einträge“).

| Taste | Wirkung |
|---|---|
| ↑ ↓ Bild↑ Bild↓ | Auswahl bewegen |
| Enter, Doppelklick | öffnen (`Process.Start` mit `UseShellExecute`) |
| Strg+Enter | im Explorer zeigen (`SHOpenFolderAndSelectItems`) |
| Strg+C | Pfad kopieren, sofern im Eingabefeld kein Text markiert ist |
| Tab, Umschalt+Tab | Modus Alles → Dateien → Ordner → Programme |
| Esc | schließen |
| Rechtsklick | Menü: Öffnen, Im Explorer zeigen, Pfad kopieren |

Existiert eine Datei beim Öffnen nicht mehr, erscheint ein Hinweis in der Fußzeile, und der Eintrag wird aus dem Verzeichnis entfernt. Bei nicht erreichbaren Orten ist nur „Pfad kopieren“ möglich.

Nach erfolgreichem Öffnen merkt sich der `UsageTracker` den Pfad.

### 8.5 Ersteinrichtung und Einstellungen

**Ersteinrichtung** (einmalig, ein Fenster)

- Liste mit Häkchen: Benutzerordner (vorausgewählt), jedes lokale Laufwerk, jedes verbundene Netzlaufwerk mit Buchstabe und UNC-Pfad, dazu „Ordner hinzufügen“ (Ordnerauswahl oder UNC-Pfad eintippen).
- Hinweistext bei Freigaben: Es werden nur Namen gelesen, nichts wird auf dem Server gespeichert, und es ist besser, nur die benötigten Unterordner zu wählen.
- Häkchen „Mit Windows starten“ (vorausgewählt) und Anzeige des Tastenkürzels.
- Verschachtelte Orte sind nicht erlaubt: Enthält ein gewählter Ort einen anderen, bleibt nur der äußere.

**Einstellungen**

| Bereich | Inhalt |
|---|---|
| Orte | Liste mit Zustand, letzter Aktualisierung, Zahl der Einträge; Hinzufügen, Entfernen, Jetzt aktualisieren |
| Ausschlüsse | die drei Listen aus 7.3, bearbeitbar, „Auf Standard zurücksetzen“ |
| Allgemein | Tastenkürzel aufnehmen, Autostart, Sprache (System/Deutsch/English), Update-Prüfung, Schonstufe für Freigaben, „Verzeichnis neu aufbauen“ |
| Info | Version, Lizenz, Link zum Repository, Speicherort der Daten mit „Ordner öffnen“ |

`settings.json` enthält: `schemaVersion`, `hotkey`, `language`, `checkForUpdates`, `networkPoliteness`, die drei Ausschlusslisten, `firstRunCompleted`. Die Orte stehen in der Datenbank.

### 8.6 Sprache

Texte in `Strings.resx` (Englisch) und `Strings.de.resx`. Die Sprache folgt der Windows-Anzeigesprache oder der Einstellung; eine Änderung wirkt nach Neustart. Kein Text steht fest im XAML oder Code.

## 9. Auslieferung

- `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`.
- Zwei Downloads je Version: `FileDetective-<Version>-win-x64-setup.exe` (Inno Setup, installiert pro Benutzer nach `%LOCALAPPDATA%\Programs\FileDetective`, Startmenü-Eintrag, Autostart-Option) und `FileDetective-<Version>-win-x64.zip` mit der einzelnen `.exe`.
- Dazu `SHA256SUMS.txt`.
- `release.yml` baut bei einem Versions-Tag, führt die Tests aus, erstellt die Downloads und das GitHub-Release mit den Notizen aus `CHANGELOG.md`.
- Die Deinstallation lässt `%LOCALAPPDATA%\FileDetective` stehen. Die Anleitung nennt den Ordner.

**Update**: Höchstens einmal täglich fragt das Programm `api.github.com/repos/<Konto>/FileDetective/releases/latest` ab. Es sendet dabei keine Daten über den Nutzer. Bei einer neueren Version erscheint ein Hinweis im Infobereich-Menü und in den Einstellungen. Ein Klick lädt die ZIP-Datei, prüft sie gegen `SHA256SUMS.txt`, benennt die laufende `.exe` in `.old` um, legt die neue ab und startet neu. Fehler (kein Netz, privates Repository, Proxy) werden still protokolliert.

**Anleitung** (`README.md`, Englisch): Was das Tool tut, Downloads, SmartScreen-Hinweis („Weitere Informationen“ → „Trotzdem ausführen“), Bedienung, was auf Freigaben geschieht und was nicht, Speicherort der Daten, bekannte Grenzen, Bauen aus dem Quelltext.

## 10. Etappen für Version 1

Jede Etappe ist für eine eigene Sitzung gedacht. Sie endet mit grünen Tests, einem Eintrag in `CHANGELOG.md`, dem abgehakten Kästchen in Abschnitt 12 und einem Commit. Was nicht unter „Umfang“ steht, gehört nicht in die Etappe.

Anspruch: **hoch** bedeutet, dass Fehler leise Datenverlust im Verzeichnis oder falsche Treffer erzeugen können; dafür ein starkes Modell verwenden. **normal** ist handwerkliche Arbeit nach Vorgabe.

### Etappe 0 – Projektgerüst (normal)

Umfang: `git init`, Lösung mit den drei Projekten, `.editorconfig`, `.gitignore`, `LICENSE` (MIT), `README.md` als Gerüst, `CHANGELOG.md`, `ci.yml` (Bauen und Testen auf `windows-latest`). `Nullable` und `TreatWarningsAsErrors` an. Das Programm startet, zeigt ein Symbol im Infobereich mit „Beenden“ und sonst nichts.

Abnahme: `dotnet build` und `dotnet test` laufen fehlerfrei. Das Programm startet und lässt sich über das Symbol beenden.

### Etappe 1 – Suchkern (hoch)

Umfang: `SearchNormalizer`, `MemoryIndex`, Zerlegen der Eingabe, Trefferbestimmung, Rangfolge. Alles ohne Dateisystem und ohne Datenbank; die Auflösung von Nummer zu vollem Pfad läuft über eine Schnittstelle.

Abnahme durch Tests:
- Suchform: `Müller`, `MUELLER`, zerlegtes `Mu\u0308ller` ergeben dieselben Bytes; `é→e`; `ß→ss`.
- Jeder Punkt aus 6.4 hat mindestens einen Test: Reihenfolge der Wörter egal, Begriff nur im Pfad, Anführungszeichen, Typfilter, Modus, mehr als 8 Begriffe.
- Rangfolge: je Kriterium ein Test, der genau dieses Kriterium entscheidet.
- Gelöschte Einträge erscheinen nicht. Umbenannte Ordner ändern die Pfadtreffer ihrer Kinder.
- Elternordner mit höherer Nummer als das Kind funktioniert.
- Leistungstest mit 1 Mio. erzeugten Einträgen: Suche unter 50 ms (im CI großzügiger, lokal gemessen und im Changelog notiert).
- Gleichzeitiges Suchen und Ändern aus mehreren Threads ohne Fehler.

### Etappe 2 – Datenbank und Speicher (normal)

Umfang: `IndexDatabase` mit Schema und Versionsprüfung, `IndexStore` (Nummernvergabe, Stapel schreiben, Teilbaum löschen, umhängen), Laden in den `MemoryIndex` beim Start, Auflösung von Nummer zu vollem Pfad und Anzeigedaten, `UsageTracker`.

Abnahme durch Tests: Nach Schreiben, Schließen und erneutem Laden liefert die Suche dieselben Treffer. Löschen eines Teilbaums entfernt alle Nachkommen in Datenbank und Speicher. Eine Datenbank mit fremder Schemaversion wird umbenannt und neu angelegt, statt abzustürzen. 1 Mio. Einträge laden in unter 3 Sekunden (lokal gemessen).

### Etappe 3 – Durchlauf (hoch)

Umfang: `IDirectoryLister` mit echter Umsetzung, `ExcludeRules`, `Scanner` mit Abgleich nach 7.2, Pausenregel nach 7.4, Durchlauf-Statistik, Abbruch.

Abnahme durch Tests, teils gegen einen echten Temp-Ordner, teils gegen eine Attrappe:
- Erster Durchlauf erfasst alles außer Ausschlüssen.
- Zweiter Durchlauf nach Anlegen, Löschen, Umbenennen und Größenänderung bringt das Verzeichnis exakt auf den neuen Stand.
- **Attrappe wirft bei einem Verzeichnis eine Ausnahme: dessen Teilbaum bleibt vollständig erhalten.**
- **Attrappe wirft beim Ort selbst: nichts ändert sich.**
- Junction im Temp-Ordner wird eingetragen, aber nicht betreten; eine Schleife hängt nicht.
- Bei einem Ort der Art „Netzwerk“ ruft der Scanner den Lister nie parallel auf und hält die Pausen ein (über eine einschiebbare Uhr geprüft).
- Abbruch mitten im Durchlauf hinterlässt ein gültiges Verzeichnis.

### Etappe 4 – Suchleiste (normal)

Umfang: Infobereich-Symbol mit Menü, Tastenkürzel, Suchleiste nach 8.4 mit allen Tasten, Öffnen, Im Explorer zeigen, Pfad kopieren, Dateisymbole. Als Ort dient vorläufig fest der Benutzerordner, der beim Start einmal durchlaufen wird.

Abnahme von Hand:
- Alt+Leertaste öffnet die Leiste aus jedem Programm heraus, das Eingabefeld hat sofort den Fokus (zehnmal hintereinander prüfen, auch aus einem Vollbild-Browser).
- Tippen zeigt Treffer ohne spürbare Verzögerung.
- Jede Taste aus der Tabelle in 8.4 wirkt wie beschrieben.
- Die Leiste erscheint auf dem Bildschirm mit dem Mauszeiger und ist bei 100 % und 150 % Skalierung scharf.

Ab hier ist das Programm benutzbar.

### Etappe 5 – Orte und Einstellungen (normal)

Umfang: `DriveDiscovery` (lokale Laufwerke, Netzlaufwerke mit UNC-Pfad über `WNetGetConnection`), Ersteinrichtung, Einstellungsfenster nach 8.5, `SettingsService`, Autostart, Einzelinstanz, Regel gegen verschachtelte Orte, Entfernen eines Ortes samt seiner Einträge.

Abnahme: Tests für `SettingsService`, die Verschachtelungsregel und die Platzhalter der Ausschlusspfade. Von Hand: Ersteinrichtung erscheint nur beim ersten Start; ein verbundenes Netzlaufwerk erscheint mit Buchstabe und UNC-Pfad; ein hinzugefügter Ort wird durchlaufen und ist durchsuchbar; ein entfernter Ort verschwindet aus den Treffern.

### Etappe 6 – Aktualität (hoch)

Umfang: `LocalWatcher` nach 7.6, `RefreshScheduler` nach 7.5, `ReachabilityMonitor` nach 7.7, Warteschlange für Durchläufe, „Jetzt aktualisieren“, Zustandsanzeige im Menü und in der Fußzeile, ausgegraute Treffer.

Abnahme durch Tests (Uhr und Erreichbarkeit einschiebbar): Eine Freigabe wird am selben Kalendertag kein zweites Mal automatisch durchlaufen; nach drei abgebrochenen Versuchen ist für den Tag Schluss; „Jetzt aktualisieren“ läuft trotzdem. Watcher-Fälle: Datei anlegen, löschen, umbenennen; Ordner mit Inhalt hineinverschieben; Ordner umbenennen; Ereignis in ausgeschlossenem Ordner; Pufferüberlauf löst Durchlauf aus.

Von Hand: Eine neu angelegte lokale Datei ist nach höchstens 2 Sekunden auffindbar. Bei getrenntem Netzlaufwerk bleiben dessen Treffer ausgegraut sichtbar, und die Suchleiste öffnet sich ohne Verzögerung.

### Etappe 7 – Feinschliff (normal)

Umfang: beide Sprachen vollständig, zuletzt geöffnete Einträge bei leerer Eingabe, Hervorhebung der Fundstellen, `FileLog`, Meldung unbehandelter Fehler, Neunummerierung nach 6.3, „Verzeichnis neu aufbauen“, Programmsymbol.

Abnahme: Kein fester Text in XAML oder Code (Test prüft, dass beide Sprachdateien dieselben Schlüssel haben). Zielwerte aus 6.5 lokal gemessen und im Changelog notiert. Prozessorlast im Leerlauf über 10 Minuten praktisch null.

### Etappe 8 – Veröffentlichung (normal)

Umfang: Veröffentlichungsprofile, Inno-Setup-Skript, `release.yml`, `SHA256SUMS.txt`, `UpdateChecker` mit Update per Klick, englische Anleitung.

Abnahme: Ein Versions-Tag erzeugt ein Release mit beiden Downloads. Installer und einzelne `.exe` laufen auf einem Rechner ohne installierte .NET-Laufzeit und ohne Adminrechte. Update von der Vorversion per Klick funktioniert.

**Firmentest vor dem Öffentlichmachen** (führt der Nutzer durch):
1. Erlaubnis der IT einholen, die Freigabe aufzulisten.
2. Zuerst einen kleinen Unterordner der Freigabe aufnehmen, dann einen großen.
3. Aus dem Protokoll notieren: aufgelistete Verzeichnisse, Einträge, Dauer, Pausenzeit, Fehler.
4. VPN trennen: Treffer bleiben ausgegraut, keine Hänger.
5. Am nächsten Tag prüfen, dass genau ein automatischer Durchlauf stattfand.

## 11. Version 2

Version 2 beginnt erst, wenn Version 1 veröffentlicht ist. Die Abschnitte legen die Richtung fest; die Einzelheiten werden vor Beginn mit dem Nutzer bestätigt.

Geändert am 2026-11-14: Auf Wunsch des Nutzers werden die Etappen 9 und 10 vorgezogen, nach seinem ersten Test von Version 1 und vor der Veröffentlichung. Sie werden wie hier beschrieben umgesetzt. Einzelheiten, die hier nicht festgelegt sind, entscheidet die Umsetzung und hält sie in diesem Abschnitt fest. Der Firmentest bleibt Voraussetzung für das Öffentlichmachen und gilt dann für alles zusammen.

### 11.1 Etappe 9 – Speicherplatz (normal)

Eigenes Fenster, erreichbar über das Infobereich-Menü. Aufklappbare Ordnerliste, sortiert nach Größe, mit den Spalten Name, Größe, Anteil als Balken, Zahl der Dateien, Zahl der Ordner, Änderungsdatum. Darüber je Laufwerk belegter und freier Platz. Zusätzlich eine Liste der 1.000 größten Dateien.

**Woher die Zahlen kommen**

- **Freigaben**: aus dem vorhandenen Verzeichnis. Es entsteht keine zusätzliche Last auf dem Server. Angezeigt wird der Stand des letzten Durchlaufs mit Datum.
- **Lokale Laufwerke**: über einen eigenen vollständigen Durchlauf auf Klick, ohne Ausschlüsse, in eine Struktur im Arbeitsspeicher, die mit dem Schließen des Fensters verworfen wird. Grund: Das Suchverzeichnis lässt `Windows`, `Program Files` und `AppData` aus, und genau dort liegt oft der meiste Platz. Der Durchlauf nutzt denselben `Scanner` mit anderer Ausschlussregel und anderem Ziel.

Ohne Adminrechte sind einige Ordner nicht lesbar. Die Differenz zwischen belegtem Platz laut Laufwerk und der Summe der gelesenen Dateien erscheint als eigene Zeile „Nicht lesbar oder Systembereich“.

**Gesperrte Ordner und Admin-Analyse** (nachgetragen am 2026-11-14 nach Rückmeldung des Nutzers): Die Zeile „Nicht lesbar oder Systembereich“ lässt sich aufklappen und listet die Ordner, die beim Durchlauf nicht aufgelistet werden konnten (z. B. `System Volume Information`, `Program Files\WindowsApps`, `Recovery`). Ein Knopf „Als Administrator analysieren …“ startet nur auf Klick einen eigenen Prozess mit Adminrechten (UAC-Abfrage), der nur das Speicherplatz-Fenster zeigt. Suche, Verzeichnis, Hotkey, Infobereich und Updates laufen in diesem Prozess nicht, und er schreibt nichts in den Datenordner. Wer Adminrechte bekommt, darf dort auch in den Papierkorb verschieben und den Papierkorb leeren; die Sperren für Laufwerkswurzel, Papierkorb, `System Volume Information` und den Windows-Ordner gelten weiter. Ohne Adminrechte (Abfrage abgelehnt oder nicht möglich) bleibt alles wie bisher.

Umsetzung (2026-11-15): Die gesperrten Ordner liest `SpaceSnapshot.GetLockedFolders` aus dem Baum (Ordner mit `IsUnreadable`), sortiert nach Pfad ohne Groß-/Kleinschreibung, höchstens 200 (`LockedFolderList`), dazu eine Zeile „n weitere“; jede Zeile hat „Im Explorer zeigen“ und „Pfad kopieren“, keine Größe und kein „In den Papierkorb“. Der Knopf erscheint nur, wenn der Prozess nicht erhöht läuft (`WindowsElevationProbe`, Rolle Administratoren) und nicht selbst der Speicherplatz-Prozess ist (`StorageAdminLauncher.CanOffer`). Er startet `Environment.ProcessPath` mit `--storage-admin` über `ShellRunAsStarter` (`UseShellExecute`, Verb `runas`); Ablehnung (Fehler 1223) gibt nur einen Hinweis, andere Fehler einen Hinweis und eine Zeile im Protokoll. Die Kommandozeile wertet `StartupOptions` aus (`--storage-admin` hat Vorrang vor allem anderen), was ein Prozess einrichtet, steht in `StartupPlan`. Der Speicherplatz-Prozess nimmt nicht die Einzelinstanz-Sperre, hält aber die Marke `Local\FileDetective.Running`, ruft `FileLog.Initialize` nicht auf und liest `settings.json` nur über einen `SettingsService` im Nur-Lese-Modus (eine beschädigte Datei bleibt liegen, Speichern wirft). Er zeigt im Titel und als Kennzeichen über der Laufwerksliste „Mit Administratorrechten“, nennt den Grund für fehlende Freigaben und ergänzt die Rückfrage vor dem Verschieben um „auch geschützte Dateien werden verschoben“. Er endet mit dem Schließen des Fensters. Das Manifest bleibt `asInvoker`.

Dateien, die nur in der Cloud liegen (Attribute `RecallOnDataAccess` oder `Offline`), werden gekennzeichnet und zählen nicht zur belegten Größe.

Aktionen je Zeile: Im Explorer zeigen, Pfad kopieren, bei lokalen Einträgen „In den Papierkorb“ mit Rückfrage.

**Umsetzung und Entscheidungen** (Etappe 9, 2026-11-14)

- Aufbau: `SpaceModel.cs` (`SpaceCollector`, `SpaceFolder`, `SpaceFile`, `SpaceSnapshot`), `StorageAnalysis.cs` (`LocalSpaceScanner`, `DirectorySpaceReader`, `StorageAnalysis` als Eingang, `SpaceRecycler`, `IRecycleBin`, `IDriveSpaceSource`, `ByteSize`) in `Core`; `StorageWindow` und `SpaceRow` in `App`. Das Fenster öffnet sich über „Speicherplatz …“ im Infobereich-Menü, es gibt höchstens eines.
- Gemeinsame Logik mit dem `Scanner`: Der Plan sagt „derselbe `Scanner`“. Der `Scanner` ist aber um den Abgleich mit dem `IndexStore` herum gebaut (bekannte Kinder, Stapel, Zustand in `roots`). Ihn dafür zu zerlegen hätte das Verhalten des Suchverzeichnisses gefährdet. Der lokale Durchlauf hat deshalb eine eigene, kleine Schleife und nutzt dieselben Bausteine: `IDirectoryLister` (`FileSystemDirectoryLister`), `ILinkDetector` (`ReparseTagLinkDetector`; Links werden eingetragen, nicht betreten) und die Regel für Auflistfehler (`Scanner.IsListingFailure`, dafür `internal` gemacht, sonst unverändert). Er hat keinen Zugang zum `IndexStore` und kann dort nichts schreiben.
- Lokaler Durchlauf: keine Ausschlüsse (auch `System`-Ordner und `$RECYCLE.BIN` werden betreten, soweit lesbar), kein Eintrag in `index.db`, Ergebnis nur im Arbeitsspeicher. Es gibt je Ordner einen Knoten; Dateien werden in ihren Ordner aufsummiert, nur die 1.000 größten bleiben einzeln (begrenzter Haufen). Ein nicht lesbarer Ordner wird gezählt und markiert, der Durchlauf geht weiter. Abbruch verwirft die unvollständigen Zahlen (ein früheres Ergebnis desselben Laufwerks bleibt). Ein Durchlauf gleichzeitig.
- Laufwerke: Es werden lokale (Fest-)Laufwerke und Wechseldatenträger durchlaufen. Netzlaufwerke und UNC-Pfade werden von `StorageAnalysis` abgelehnt, bevor etwas aufgelistet wird, und für sie wird auch kein freier Platz abgefragt (`DriveInfo` würde den Server berühren). Freigaben sind die Orte der Art „Netzwerk“ aus der Datenbank.
- Freigaben: `DirectorySpaceReader` liest die Einträge des Ortes in einem Durchgang über eine eigene Lese-Verbindung (`IndexStore.ReadEntriesOfRoot`) und kennt keinen `IDirectoryLister`. Angezeigt wird das Datum des letzten vollständigen Durchlaufs; war der letzte Durchlauf unvollständig oder ist keiner abgeschlossen, steht das dabei. Das Ergebnis wird im Fenster gemerkt und neu gelesen, wenn ein neuerer Durchlauf fertig ist.
- Größen sind die Dateigrößen laut Verzeichnis, nicht der belegte Platz auf dem Datenträger. Komprimierung, dünn besetzte Dateien und mehrfach benannte Dateien (harte Verknüpfungen) können die Summe über den belegten Platz heben; die Zeile „Nicht lesbar oder Systembereich“ ist dann 0 (nie negativ) und wird auch dann gezeigt.
- „Nicht lesbar oder Systembereich“ = belegter Platz laut Laufwerk (gesamt minus frei, zum Ende des Durchlaufs) minus Summe der gelesenen Dateien. Sie steht als Zeile unter dem Laufwerk, nur bei lokalen Durchläufen.
- Cloud-Dateien (`RecallOnDataAccess` 0x400000 oder `Offline`): zählen nicht zur Größe, nicht zur Liste der größten Dateien (sie belegen keinen Platz), werden aber in der Zahl der Dateien mitgezählt und am Ordner als „n Dateien nur in der Cloud, nicht mitgezählt (Größe)“ gekennzeichnet. Es gibt nur Auflistungen von Ordnern, keine Datei wird je geöffnet.
- Spalten: Anteil = Anteil am übergeordneten Ordner (oberste Zeile: das ganze Laufwerk); Dateien und Ordner zählen rekursiv; Änderungsdatum ist das des Ordners selbst. Die Ordnerliste zeigt Unterordner, die größten zuerst; die Liste der größten Dateien hat die Spalten Name, Ordner, Größe, Geändert.
- Papierkorb: nur bei Ergebnissen eines lokalen Festlaufwerks, nicht bei Freigaben, nicht bei Wechseldatenträgern (ohne Papierkorb wäre das Löschen endgültig), nicht bei Links und nicht beim Laufwerk selbst. Mit `Microsoft.VisualBasic.FileIO.FileSystem` und `RecycleOption.SendToRecycleBin`, nach einer Rückfrage mit „Nein“ als Vorgabe. Danach werden die Zahlen im Fenster angepasst; die Bytes zählen nicht als „nicht lesbar“, denn sie belegen den Platz bis zum Leeren des Papierkorbs.
- Im Explorer zeigen und Pfad kopieren gibt es für jede Zeile (Schaltflächen und Kontextmenü); bei Freigaben öffnet „Im Explorer zeigen“ den Ordner auf dem Server, aber nur auf ausdrücklichen Klick des Nutzers.

**Nachtrag Papierkorb und Dateien im Baum** (nachgetragen am 2026-11-14 nach Rückmeldung des Nutzers)

- Papierkorb: Der Ordner `$Recycle.Bin` direkt in der Wurzel eines lokalen Festlaufwerks (`SpaceFolder.IsRecycleBin`, nur bei lokalen Durchläufen) heißt im Baum „Papierkorb“. Für ihn gibt es statt „In den Papierkorb“ die Aktion „Papierkorb leeren …“ (Knopf und Kontextmenü, nur bei lokalen Festplatten). Geleert wird mit `SHEmptyRecycleBin` für genau dieses Laufwerk, ohne `SHERB_NOCONFIRMATION`: Windows fragt selbst nach und zeigt Fortschritt und Ton. Der Aufruf liegt hinter `IRecycleBinEmptier`. Danach wird `$Recycle.Bin` lokal neu aufgelistet (`StorageAnalysis.ScanFolder`, `SpaceSnapshot.ReplaceFolder`), Größen und Ordner darüber werden angepasst, der freie Platz des Laufwerks wird neu gelesen und die Bytes aus „verschoben, noch nicht leer“ fallen weg. Verneint der Nutzer (Rückgabewert oder unveränderte Größe), bleibt alles. Kann `$Recycle.Bin` ohne Adminrechte nicht gelesen werden, bleibt die Zeile unverändert und nur der freie Platz wird aktualisiert.
- „In den Papierkorb“ ist zusätzlich gesperrt für `$Recycle.Bin`, `System Volume Information` und den Windows-Ordner (`Environment.SpecialFolder.Windows`, wird an `SpaceRecycler` übergeben) samt allem darin, für Ordner und Dateien (`SpaceRecycler.CanRecycle`).
- Dateien im Baum: Ein aufgeklappter Ordner zeigt Unterordner und Dateien gemeinsam nach Größe, größte zuerst, mit Name, Größe, Anteil am Ordner und Datum (Dateien/Ordner bleiben leer). Die Dateien werden erst beim Aufklappen geladen (`StorageAnalysis.LoadFiles`), bis dahin zeigt eine Zeile „Lade …“; ein Fehler erscheint als Zeile. Lokal wird genau dieser Ordner über den `IDirectoryLister` aufgelistet (Hintergrund-Thread, keine Datei wird geöffnet); bei Freigaben kommen die Dateien aus der Datenbank (`IndexStore.GetKnownChildren` über die Eintragsnummer `SpaceFolder.EntryId`), der Lister wird nie gefragt. Pro Ordner höchstens 1.000 Dateien (`FolderFileListing.Limit`), dazu eine Sammelzeile „n weitere Dateien, Summe“. Cloud-Dateien zählen mit Größe 0 und tragen eine Kennzeichnung. Aktionen wie bei den größten Dateien; nach dem Verschieben verschwindet die Zeile, und die Größen aller Ordner darüber werden angepasst.

### 11.2 Etappe 10 – Dubletten (hoch)

**Lokal, mit Inhaltsvergleich**

1. Kandidaten aus der Datenbank: Dateien gleicher Größe ab einer einstellbaren Mindestgröße (Standard 1 MB). Dafür ein Index auf `entries(size)`.
2. Cloud-Dateien (Attribute wie oben) werden **nie gelesen**, weil das einen Download auslösen würde.
3. Harte Verknüpfungen auf dieselbe Datei (gleiche Datenträger- und Dateinummer) gelten nicht als Dubletten.
4. Gestufte Prüfung: erst Prüfsumme über die ersten 64 KB, bei Gleichheit SHA-256 über die ganze Datei.
5. Ergebnisse in einer Tabelle `hashes(entry_id, size, mtime, partial, full)`; ein Eintrag gilt nur, solange Größe und Datum stimmen.
6. Lesen mit niedriger Priorität, mit Fortschrittsanzeige, jederzeit abbrechbar.

**Freigaben, ohne Lesen**

Gruppen aus gleichem Namen (Suchform) und gleicher Größe, deutlich als „mögliche Dubletten, nicht geprüft“ bezeichnet. Keine Löschfunktion, nur „Im Explorer zeigen“.

**Oberfläche**

**Programm- und Datenbankdateien** (nachgetragen am 2026-11-14 nach Rückmeldung des Nutzers): Ein Schalter oben im Fenster, standardmäßig an und gemerkt in `hideTechnicalDuplicates`, blendet Gruppen aus, deren Dateien alle zu Programmen gehören: Endungen `.dll .exe .pdb .sys .ocx .msi .so .pyd .jar .node .db .sqlite .sqlite3 .db-wal .db-shm .mdf .ldf` oder ein Ordner `bin`, `obj`, `publish`, `packages`, `.nuget` im Pfad. Diese Kopien werden dort gebraucht, wo sie liegen. Eine Gruppe mit mindestens einer anderen Datei bleibt sichtbar. Die Zahl der ausgeblendeten Gruppen steht neben dem Schalter. Beim Ausblenden werden Markierungen der Gruppe aufgehoben, damit nie eine unsichtbare Datei in den Papierkorb geht. Die Suche selbst ist davon nicht betroffen.

Gruppen sortiert nach verschwendetem Platz. Je Gruppe die Dateien mit Pfad und Datum und einem Häkchen. Hilfen: „alle außer der neuesten markieren“, „alle außer denen in diesem Ordner markieren“. Mindestens eine Datei je Gruppe bleibt immer unmarkiert. „In den Papierkorb“ zeigt Anzahl und Gesamtgröße, prüft direkt vor dem Verschieben erneut Größe und Datum und nutzt `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` mit `RecycleOption.SendToRecycleBin`.

Abnahme durch Tests: gleiche Größe mit verschiedenem Inhalt ist keine Dublette; harte Verknüpfung ist keine Dublette; Cloud-Attribut verhindert jedes Lesen (Attrappe zählt Lesezugriffe); geänderte Datei macht die gespeicherte Prüfsumme ungültig; es lassen sich nie alle Dateien einer Gruppe markieren; für Orte der Art „Netzwerk“ wird nie eine Datei geöffnet.

**Umsetzung und Entscheidungen** (Etappe 10, 2026-11-14)

- Aufbau: `DuplicateFinder.cs` (`DuplicateFinder`, `DuplicateArea`, `DuplicateStats`, `DuplicateProgress`), `Duplicates.cs` (`DuplicateGroup`, `DuplicateFile`, `DuplicateMarking`, `DuplicateRecycler`) und `LocalFileAccess.cs` (`ILocalFileAccess`, `SystemLocalFileAccess`, `LowPriorityWork`) in `Core`; `DuplicatesWindow` und `DuplicateRows` in `App`. Das Fenster öffnet sich über „Dubletten …“ im Infobereich-Menü, es gibt höchstens eines.
- Datenbank: `hashes`, der Index `ix_entries_size` und der Trigger `tr_entries_delete_hashes` (löscht beim Löschen eines Eintrags dessen Prüfsummen) werden bei jedem Öffnen mit `CREATE … IF NOT EXISTS` angelegt (`IndexDatabase.EnsureExtensions`). Die Schemaversion bleibt 1, damit bestehende Verzeichnisse nicht umbenannt und neu aufgebaut werden. Fehlt der Trigger bei vorhandener Tabelle (eine ältere Version hat `entries` bei der Neunummerierung neu gebaut), werden alle Prüfsummen verworfen, weil die Nummern dann anderen Dateien gehören könnten. Die Neunummerierung (6.3) überträgt die Prüfsummen auf die neuen Nummern und legt Index und Trigger neu an. Geschrieben wird eine Prüfsumme nur, solange der Eintrag existiert, unter derselben Sperre wie jedes Löschen.
- `hashes.mtime` ist die letzte Schreibzeit in UTC-Ticks (100 ns), nicht in Sekunden wie `entries.mtime`, damit eine in derselben Sekunde gleich groß überschriebene Datei ihre alte Prüfsumme verliert. Verglichen wird mit Größe und Datum laut aktueller Auflistung des Elternordners, nicht laut Verzeichnis, weil der Watcher Datumsänderungen nicht meldet. Weicht die Größe vom Verzeichnis ab, wird die Datei übersprungen („seit dem letzten Durchlauf geändert“).
- Prüfsumme der ersten 64 KB: SHA-256 (aus `System.Security.Cryptography`), nicht `System.IO.Hashing` aus 5.1, weil das ein zusätzliches Paket wäre. Bei Dateien bis 64 KB gilt die erste Prüfsumme zugleich für die ganze Datei.
- Reihenfolge der Zugriffe je Datei: Cloud-Attribute laut Verzeichnis → aktuelle Angaben aus der Auflistung des Elternordners (`FindFirstFileEx`, öffnet die Datei nicht) → Cloud-Attribute und Linkart erneut → Datenträger- und Dateinummer (`GetFileInformationByHandleEx` mit `FileIdInfo` über ein Öffnen nur für Attribute) → Lesen. Als „nie öffnen“ gelten zusätzlich zu `RecallOnDataAccess` und `Offline` auch `RecallOnOpen` (0x40000). Dateien mit Analysepunkt, deren Tag ein Namensersatz ist (symbolische Links) oder nicht lesbar ist, werden nicht verfolgt; OneDrive-Dateien, die lokal vorliegen, werden normal verglichen.
- Harte Verknüpfungen: Von mehreren Namen derselben Datei bleibt nur einer in der Gruppe, die übrigen werden gezählt und nicht angezeigt; verschwendet ist Größe × (Zahl verschiedener Dateien − 1).
- Freigaben: Als Freigabe gilt ein Ort der Art „Netzwerk“, jeder UNC-Pfad, jedes Netzlaufwerk und ein Pfad, den `DriveDiscovery` nicht beschreiben kann, unabhängig von der gespeicherten Art. Für sie wird `ILocalFileAccess` nie aufgerufen. Gruppen entstehen über alle Freigaben hinweg aus Suchform des Namens und Größe; lokale Dateien und Freigaben kommen nie in dieselbe Gruppe. Datum ist das aus dem Verzeichnis. „Im Explorer zeigen“ öffnet den Ordner auf dem Server nur auf ausdrücklichen Klick.
- Wechseldatenträger werden verglichen, ihre Dateien lassen sich aber nicht markieren und nicht verschieben (ohne Papierkorb wäre das Löschen endgültig), wie in Etappe 9.
- Markieren: Die Regel „mindestens eine Datei bleibt unmarkiert“ steckt in `DuplicateGroup`; ungeprüfte Gruppen lassen sich gar nicht markieren. „Alle außer der neuesten“ behält die Datei mit der jüngsten Schreibzeit (bei Gleichstand die erste nach Pfad). „Alle außer denen in diesem Ordner“ nimmt den Ordner der ausgewählten Datei samt Unterordnern und wirkt auf alle Gruppen, die dort eine Datei haben; Gruppen ohne Datei dort bleiben unverändert.
- Papierkorb: Vor dem Verschieben wird für jede markierte Datei Größe und Datum (Ticks) erneut aus der Auflistung geprüft; eine abweichende Datei wird übersprungen und gemeldet. Zusätzlich muss mindestens eine unmarkierte Datei der Gruppe noch unverändert vorhanden sein, sonst wird die ganze Gruppe übersprungen. Danach wird der Eintrag über dieselbe Regel wie bei einem nicht gefundenen Treffer (`ExistenceCheck`, dann `IndexStore.DeleteSubtree`) aus dem Verzeichnis entfernt, nur wenn die Nummer noch für diesen Pfad steht; der Watcher würde es ebenfalls bemerken, beides ist wiederholbar.
- Lesen: auf einem eigenen Thread mit niedrigster Priorität und `THREAD_MODE_BACKGROUND_BEGIN` (senkt auch die E/A-Priorität), in Blöcken zu 1 MB, Abbruch zwischen den Blöcken. Bereits berechnete Prüfsummen bleiben beim Abbruch gespeichert. Nach dem Lesen wird geprüft, dass sich Größe und Datum nicht geändert haben.
- Mindestgröße: Einstellung `duplicateMinSize` in `settings.json`, Standard 1 MB, bereinigt auf 1 KB bis 1 TB (0 oder kleiner wird 1 MB). Auswahl im Dubletten-Fenster (64 KB bis 1 GB; ein von Hand eingetragener Wert erscheint zusätzlich).
- Nur in Gruppen, deren Größe mindestens zweimal vorkommt, wird überhaupt eine Datei angefasst; Pfade werden über `DatabaseEntryResolver` aufgelöst.

## 12. Stand

- [x] Etappe 0 – Projektgerüst
- [x] Etappe 1 – Suchkern
- [x] Etappe 2 – Datenbank und Speicher
- [x] Etappe 3 – Durchlauf
- [x] Etappe 4 – Suchleiste
- [x] Etappe 5 – Orte und Einstellungen
- [x] Etappe 6 – Aktualität
- [x] Etappe 7 – Feinschliff
- [x] Etappe 8 – Veröffentlichung
- [ ] Firmentest
- [x] Etappe 9 – Speicherplatz
- [x] Etappe 10 – Dubletten

## 13. Bewusst nicht enthalten

- Suche nach Dateiinhalt oder Bedeutung, OCR, KI-Funktionen
- Tippfehler-Toleranz
- macOS, Linux, ARM64
- zentrale Konfiguration oder Verteilung durch eine IT-Abteilung
- gemeinsames Verzeichnis für mehrere Benutzer
- Code-Signatur
- Kachelbild in der Speicherplatz-Analyse
- direktes Auslesen der Dateitabelle (bräuchte Adminrechte)
