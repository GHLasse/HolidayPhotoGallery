; Windows installer for Holiday Photo Gallery (Inno Setup 6).
;
; Installs for the current user only, so no administrator rights are needed: the app goes to
; %LocalAppData%\Programs\HolidayPhotoGallery, gets a Start menu entry (and a desktop icon if
; wanted), and shows up in Settings > Apps > Installed apps, where it can be uninstalled.
;
; Build (see installer/README.md):
;   iscc /DAppVersion=1.2.3 /DSourceDir=<app build folder> /DOutputDir=<folder> installer\HolidayPhotoGallery.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\src\PhotoGallery.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "Output"
#endif

#define AppName "Holiday Photo Gallery"
#define AppExe "HolidayPhotoGallery.exe"

[Setup]
; Never change the AppId: Windows uses it to find an earlier install to update or uninstall.
AppId={{93cb5568-05f6-4c7a-8d4e-0d102a878f21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Lasse
AppPublisherURL=https://github.com/GHLasse/HolidayPhotoGallery
AppSupportURL=https://github.com/GHLasse/HolidayPhotoGallery/issues
AppUpdatesURL=https://github.com/GHLasse/HolidayPhotoGallery/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\HolidayPhotoGallery
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; Per-user install; "/ALLUSERS" on the command line still allows an install for everyone.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809, the oldest version the app supports.
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=HolidayPhotoGallery-Setup-x64
SetupIconFile=..\src\PhotoGallery.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; An open app is closed before its files are replaced and started again afterwards.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.RemoveData=Also remove your settings, the photo index and the downloaded tagging and face models?%n%nYour photos and everything written into them are not touched either way.
de.RemoveData=Auch die Einstellungen, den Fotoindex und die heruntergeladenen Modelle für Schlagwörter und Gesichter entfernen?%n%nDeine Fotos und alles, was in ihnen gespeichert ist, bleiben in jedem Fall erhalten.
es.RemoveData=¿Eliminar también la configuración, el índice de fotos y los modelos descargados de etiquetas y caras?%n%nTus fotos y todo lo guardado en ellas no se tocan en ningún caso.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Lets Windows (Run dialog, "Open with" lists) find the app by its exe name.
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Settings, index, logs and downloaded models live in %LocalAppData%\HolidayPhotoGallery, apart
// from the program folder. They stay unless the user asks to remove them, so a reinstall keeps
// the library set up. Silent uninstalls keep them too.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\HolidayPhotoGallery');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
