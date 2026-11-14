; FileDetective - installer (Inno Setup 6)
;
; Per-user installation without administrator rights into %LOCALAPPDATA%\Programs\FileDetective. The folder stays
; writable for the user, so the in-app update (download ZIP, check, replace the exe) works for an installed copy, too.
;
; Call (see .github/workflows/release.yml):
;   ISCC.exe /DAppVersion=0.1.0 /DAppVersionNumeric=0.1.0 /DSourceDir=<folder with the exe> /DOutputDir=<target> installer\FileDetective.iss
;
; SourceDir holds the same files as the ZIP: FileDetective.exe, LICENSE, README.md, CHANGELOG.md.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\package"
#endif
; Optional: /DRepoUrl=https://github.com/<account>/FileDetective adds the links to the "Programs and Features" entry.
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppName "FileDetective"
#define AppExeName "FileDetective.exe"

[Setup]
; Fixed AppId - never change it, or a later setup no longer recognizes this installation.
AppId={{9D4B7E52-3C1A-4F08-A6E5-2B7D1C93F4A8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Christoph Fetz
AppCopyright=Copyright (c) 2026 Christoph Fetz, MIT License
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductName={#AppName}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
#ifdef RepoUrl
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
#endif
; Per user, no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004 and later
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\FileDetective.App\Assets\FileDetective.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The running program holds the mutex below under a fixed name (SingleInstanceGuard.RunningMarkerName). Setup and the
; uninstaller see it and ask to close the program. If it runs in another Windows session, the Restart Manager closes it.
AppMutex=Local\FileDetective.Running
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
german.AutostartGroup=Autostart:
english.AutostartGroup=Startup:
german.AutostartTask=FileDetective mit Windows starten
english.AutostartTask=Start FileDetective with Windows

[Tasks]
; Selected by default. The value written below is the one the program itself uses for "Start with Windows".
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:AutostartGroup}"

[Files]
; Same content as FileDetective-<version>-win-x64.zip
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[Registry]
; Exactly like AutostartManager: HKCU\...\Run, value "FileDetective" = "<exe>" --autostart. The program treats the
; registry as the only truth about the autostart, so it shows the setting as switched on.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FileDetective"; ValueData: """{app}\{#AppExeName}"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Left behind by the in-app update (the former exe is renamed to .old, the new one is unpacked as .new first).
Type: files; Name: "{app}\{#AppExeName}.old"
Type: files; Name: "{app}\{#AppExeName}.new"

; The data folder %LOCALAPPDATA%\FileDetective (directory, settings, log) is kept on uninstall on purpose.

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'FileDetective';

{ The user may have switched the autostart on later in the program's settings (not through this setup). Then the
  value is not covered by uninsdeletevalue. Remove it here if it points to the exe that is being uninstalled. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, RunKey, RunValue, Command) then
    begin
      if Pos(Lowercase(ExpandConstant('{app}\{#AppExeName}')), Lowercase(Command)) > 0 then
        RegDeleteValue(HKCU, RunKey, RunValue);
    end;
  end;
end;
