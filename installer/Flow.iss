; Flow-Setup-x64: per-user installer for Flow (Inno Setup 6, https://jrsoftware.org/isinfo.php).
; Build with tools\build-installer.ps1, which passes AppVersion and SourceDir (the publish folder).
;
; Per-user on purpose: no administrator prompt to install or update, and updates can run silently:
;   Flow-Setup-x64-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS

#ifndef AppVersion
  #error AppVersion is not defined (use tools\build-installer.ps1)
#endif
#ifndef SourceDir
  #error SourceDir is not defined (use tools\build-installer.ps1)
#endif

[Setup]
; Never change AppId: it is how Windows recognises later versions as updates of this app.
AppId={{17AE5013-F095-4B33-A1B0-78D5E057B158}
AppName=Flow
AppVersion={#AppVersion}
AppVerName=Flow {#AppVersion}
AppPublisher=Joseph Martinez
AppPublisherURL=https://github.com/leansalesman/Flow
AppSupportURL=https://github.com/leansalesman/Flow/issues
AppUpdatesURL=https://github.com/leansalesman/Flow/releases
AppCopyright=© 2026 Joseph Martinez
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Joseph Martinez
VersionInfoDescription=Flow Setup
VersionInfoProductName=Flow
; %LOCALAPPDATA%\Programs\Flow
DefaultDirName={autopf}\Flow
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
SetupIconFile=..\src\Flow\Assets\Flow.ico
UninstallDisplayIcon={app}\Flow.exe
UninstallDisplayName=Flow
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
; Close a running Flow (and its librespot) before replacing files.
CloseApplications=yes
CloseApplicationsFilter=Flow.exe,librespot.exe
RestartApplications=no
OutputDir=..\installer\Output
OutputBaseFilename=Flow-Setup-x64-{#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "fileassoc"; Description: "Add Flow to ""Open with"" for audio files"; GroupDescription: "Audio files:"

[Files]
Source: "{#SourceDir}\Flow.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\librespot.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Flow"; Filename: "{app}\Flow.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Flow"; Filename: "{app}\Flow.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Flow registers "Open with" itself, the same way its Settings toggle does.
Filename: "{app}\Flow.exe"; Parameters: "--register-associations"; Tasks: fileassoc; Flags: runhidden waituntilterminated
Filename: "{app}\Flow.exe"; Description: "Open Flow"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Flow.exe"; Parameters: "--unregister-associations"; Flags: runhidden waituntilterminated; RunOnceId: "FlowUnregisterAssociations"

[Code]
// Uninstall keeps the library, settings and Spotify sign-in (%LOCALAPPDATA%\Flow) unless the user asks to remove them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Flow');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('Also remove your Flow library, settings and Spotify sign-in?' + #13#10 + #13#10 +
                'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
