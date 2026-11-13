# Changelog

## Unreleased

- Stage 0: project skeleton with solution (Core, App, Tests), tray icon with an "Exit" menu (English and German), CI workflow, MIT license.
- Stage 1: search core in `FileDetective.Core` without file system or database: `SearchNormalizer` (search form of names), `SearchQuery` (input parsing: quotes, `\`/`/` splitting, type filters, max. 8 terms, mode), `MemoryIndex` (16-byte entries in 65,536-entry blocks, 16 MiB name segments, `ReaderWriterLockSlim`, parallel matching with inherited folder masks, bounded best lists per thread, deterministic ranking, cancellation), `SearchEngine` with `IEntryResolver` (id to full path) and `IUsageLookup` (entries opened through the tool). Placeholder class removed.
- Measured (stage 1, Release build, AMD Ryzen 7 8745HS, 8 cores / 16 logical processors): search over 1 million generated entries, median per input, 4 threads: 5–14 ms for typical inputs, 14–30 ms for the worst case (single letter `e`, every entry is a hit); all 16 logical processors: 4–11 ms. Target under 50 ms on 4 cores is met. Debug builds are about 2–5x slower.