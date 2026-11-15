# Contributing

Thank you for your interest! Bug reports and ideas are welcome as
[issues](https://github.com/christophfetz/FileDetective/issues/new/choose), in English or German.

## Before you write code

Please open an issue first and describe what you want to change. FileDetective is kept small on purpose, and some
features are left out deliberately (see section 13 of `PLAN.md`). A short agreement first saves you work.

## Rules the code must keep

`CLAUDE.md` lists them; the most important ones:

1. On network shares only list folders, never open a file.
2. Never write to the places that are searched. Own data only below `%LOCALAPPDATA%\FileDetective`.
3. Read shares with exactly one thread and with pauses, automatically at most once a day.
4. A failed listing never removes entries.
5. No administrator rights (except the storage analysis on explicit click), no telemetry.
6. Only dependencies with a permissive license; new packages only after agreement.
7. The program never deletes files on its own.

## How to work

- Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download):

  ```
  dotnet build
  dotnet test
  dotnet run --project src/FileDetective.App
  ```

- Set the environment variable `FILEDETECTIVE_DATA_DIR` to a test folder so your own index is never touched.
- Logic without user interface belongs in `FileDetective.Core` and gets tests. File system, clock and network are used
  through interfaces, so tests can simulate errors and time.
- Code and names in English. Every text of the user interface goes into both `Strings.resx` and `Strings.de.resx`, never
  directly into XAML or code.
- The build must stay free of warnings and all tests must pass. Add a line to `CHANGELOG.md`.

By contributing you agree that your contribution is published under the [MIT license](LICENSE).
