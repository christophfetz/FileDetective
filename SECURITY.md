# Security

## Reporting a problem

Please **do not open a public issue** for a security problem. Report it privately through
[Report a vulnerability](https://github.com/christophfetz/FileDetective/security/advisories/new) on the *Security* tab
of this repository. You will get an answer as soon as possible; this is a spare-time project, so please allow a few days.

Helpful details: the FileDetective version (*Settings > About*), the Windows version and the steps that show the problem.

## Supported versions

Only the latest release gets fixes. FileDetective checks for new versions once a day (if *Check for new versions* is on)
and installs them with one click.

## What FileDetective promises

These rules are part of the design (see `PLAN.md` and `CLAUDE.md`); breaking one of them counts as a security bug:

- On network shares it only lists folders and never opens a file.
- It never writes to the places it searches. Its own data lives only in `%LOCALAPPDATA%\FileDetective`.
- It needs no administrator rights. The only exception is the storage analysis, which can be started as a separate
  elevated process after an explicit click.
- It sends no telemetry. The only connection is the optional daily update check against the GitHub releases of this
  repository, and an update is installed only if its SHA-256 checksum matches `SHA256SUMS.txt`.
- It never deletes files on its own.

The program is not code-signed. Compare downloads with `SHA256SUMS.txt` of the release.
