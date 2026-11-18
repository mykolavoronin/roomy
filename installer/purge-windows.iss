; Purge for Windows installer (Inno Setup 6). Per-user install, no admin prompt.
#ifndef AppVersion
  #define AppVersion "0.1.4"
#endif

[Setup]
AppId={{6B0F3B2E-6C1A-4C7E-9E3B-52A1F0D7C6A4}
AppName=Purge for Windows
AppVersion={#AppVersion}
AppPublisher=Mykola Voronin
AppSupportURL=https://github.com/mykolavoronin/purge-windows/issues
DefaultDirName={localappdata}\Programs\PurgeWindows
DefaultGroupName=Purge for Windows
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=PurgeWindows-Setup
SetupIconFile=..\src\PurgeWindows\Assets\purgewindows.ico
UninstallDisplayIcon={app}\PurgeWindows.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\publish\PurgeWindows.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Purge for Windows"; Filename: "{app}\PurgeWindows.exe"
Name: "{autodesktop}\Purge for Windows"; Filename: "{app}\PurgeWindows.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PurgeWindows.exe"; Description: "Launch Purge for Windows"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\PurgeWindows.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "PurgeWindowsBackground"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "PurgeWindows"; Flags: dontcreatekey uninsdeletevalue
; purge-windows:// links open the app. Route handling (scan, clean-safe,
; uninstall-review) arrives with the port phases; today the app just opens.
Root: HKCU; Subkey: "Software\Classes\purge-windows"; ValueType: string; ValueName: ""; ValueData: "URL:Purge for Windows"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\purge-windows"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\purge-windows\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PurgeWindows.exe"" ""%1"""; Flags: uninsdeletekey
