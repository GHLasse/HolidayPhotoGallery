; Windows installer for Holiday Photo Gallery (Inno Setup 6).
;
; Installs for the current user only, so no administrator rights are needed: the app goes to
; %LocalAppData%\Programs\HolidayPhotoGallery, gets a Start menu entry (and a desktop icon if
; wanted), and shows up in Settings > Apps > Installed apps, where it can be uninstalled.
;
; The wizard tells a little holiday story in the colours of the app icon: plan the trip (pick the
; folder on a map), pack (a suitcase lists what gets installed), fly there (the plane follows the real
; install progress), and arrive on an island. The pictures are layers from installer/art (made by
; render-art.mjs, converted with png2bmp.py), stacked and animated by the [Code] section below.
; Needs Inno Setup 6.7 or later.
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
DisableWelcomePage=no
DisableDirPage=no
DisableReadyPage=no
DirExistsWarning=no
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
WizardSizePercent=145,145
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
en.WelcomeTitle=Welcome to Holiday Photo Gallery
de.WelcomeTitle=Willkommen bei Holiday Photo Gallery
es.WelcomeTitle=Te damos la bienvenida a Holiday Photo Gallery
en.WelcomeText=Your trips, sorted, rated and on the map. Setup puts the app on this PC just for you. It takes about a minute and needs no administrator rights.
de.WelcomeText=Deine Reisen, sortiert, bewertet und auf der Karte. Das Setup installiert die App nur für dich auf diesem PC. Es dauert etwa eine Minute und braucht keine Administratorrechte.
es.WelcomeText=Tus viajes, ordenados, valorados y en el mapa. El instalador pone la aplicación en este PC solo para ti. Tarda alrededor de un minuto y no necesita permisos de administrador.
en.MapTitle=Where should we go?
de.MapTitle=Wohin soll die Reise gehen?
es.MapTitle=¿Adónde vamos?
en.MapText=The usual place is fine for almost everyone. Your photos stay where they are; only the app goes here.
de.MapText=Der vorgeschlagene Ort passt für fast alle. Deine Fotos bleiben, wo sie sind; nur die App kommt hierher.
es.MapText=El lugar habitual sirve para casi todo el mundo. Tus fotos se quedan donde están; aquí solo va la aplicación.
en.Folder=Folder
de.Folder=Ordner
es.Folder=Carpeta
en.DesktopShortcut=Also put a shortcut on the desktop
de.DesktopShortcut=Auch eine Verknüpfung auf dem Desktop anlegen
es.DesktopShortcut=Crear también un acceso directo en el escritorio
en.PackTitle=All packed?
de.PackTitle=Alles gepackt?
es.PackTitle=¿Está todo en la maleta?
en.PackText=Here’s what goes in the suitcase. Click Install when you’re ready, or Back to change something.
de.PackText=Das kommt in den Koffer. Klicke auf Installieren, wenn du so weit bist, oder auf Zurück, um etwas zu ändern.
es.PackText=Esto es lo que va en la maleta. Haz clic en Instalar cuando estés listo, o en Atrás para cambiar algo.
en.PackFolder=App folder
de.PackFolder=App-Ordner
es.PackFolder=Carpeta de la aplicación
en.PackStartMenu=Start menu entry
de.PackStartMenu=Eintrag im Startmenü
es.PackStartMenu=Entrada en el menú Inicio
en.PackDesktop=Desktop shortcut
de.PackDesktop=Verknüpfung auf dem Desktop
es.PackDesktop=Acceso directo en el escritorio
en.Yes=Yes
de.Yes=Ja
es.Yes=Sí
en.No=No
de.No=Nein
es.No=No
en.FlyTitle=Taking you to your destination…
de.FlyTitle=Wir bringen dich an dein Ziel …
es.FlyTitle=Te llevamos a tu destino…
en.DoneTitle=You’re all set!
de.DoneTitle=Alles bereit!
es.DoneTitle=¡Todo listo!
en.DoneText=Holiday Photo Gallery is installed. You’ll find it in the Start menu, and you can remove it any time from Settings › Apps.
de.DoneText=Holiday Photo Gallery ist installiert. Du findest die App im Startmenü und kannst sie jederzeit unter Einstellungen › Apps entfernen.
es.DoneText=Holiday Photo Gallery está instalado. Lo encontrarás en el menú Inicio y puedes quitarlo cuando quieras desde Configuración › Aplicaciones.
en.OpenNow=Open Holiday Photo Gallery now
de.OpenNow=Holiday Photo Gallery jetzt öffnen
es.OpenNow=Abrir Holiday Photo Gallery ahora

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The wizard's pictures, only unpacked to Setup's temporary folder.
Source: "art\bmp\*.bmp"; DestDir: "{tmp}\art"; Flags: dontcopy
Source: "art\png\ready-check.png"; DestDir: "{tmp}\art"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Check: WantDesktopIcon

[Registry]
; Lets Windows (Run dialog, "Open with" lists) find the app by its exe name.
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:OpenNow}"; Flags: nowait postinstall skipifsilent

[Code]
// ---------------------------------------------------------------------------------------------
// The wizard's pictures. Each page shows a scene stacked from the layers in installer/art (drawn
// at twice their size), redrawn about 30 times a second by a timer, and scaled down smoothly to
// the size of the picture on the page.
// ---------------------------------------------------------------------------------------------

const
  SRCCOPY = $00CC0020;
  HALFTONE = 4;
  PER_PIXEL_ALPHA = $01000000;
  INK = $003D2114;        // #14213D
  BODY_INK = $005C473D;   // #3D475C
  MUTED_INK = $0078635A;  // #5A6378
  PAPER = $00F6FBFF;      // #FFFBF6

function BitBlt(DestDC: Longint; X, Y, Width, Height: Integer; SrcDC: Longint; XSrc, YSrc: Integer; Rop: DWORD): BOOL;
  external 'BitBlt@gdi32.dll stdcall';
function StretchBlt(DestDC: Longint; X, Y, Width, Height: Integer; SrcDC: Longint; XSrc, YSrc, SrcWidth, SrcHeight: Integer; Rop: DWORD): BOOL;
  external 'StretchBlt@gdi32.dll stdcall';
function SetStretchBltMode(DC: Longint; Mode: Integer): Integer;
  external 'SetStretchBltMode@gdi32.dll stdcall';
function SetBrushOrgEx(DC: Longint; X, Y: Integer; Previous: Longint): BOOL;
  external 'SetBrushOrgEx@gdi32.dll stdcall';
function AlphaBlend(DestDC: Longint; X, Y, Width, Height: Integer; SrcDC: Longint; XSrc, YSrc, SrcWidth, SrcHeight: Integer; Blend: DWORD): BOOL;
  external 'AlphaBlend@msimg32.dll stdcall';
function SetTimer(Wnd: Longint; IDEvent, Elapse: Longint; TimerFunc: Longword): Longword;
  external 'SetTimer@user32.dll stdcall';
function KillTimer(Wnd: Longint; IDEvent: Longword): BOOL;
  external 'KillTimer@user32.dll stdcall';
function TickCount: DWORD;
  external 'GetTickCount@kernel32.dll stdcall';

var
  Timer: Longword;
  PageStart: DWORD;
  WelcomeSky, WelcomeSea, WelcomePlane: TBitmap;
  MapBack, MapFlat, MapPin: TBitmap;
  MapPulse: array [0..15] of TBitmap;
  Suitcase: array [0..3] of TBitmap;
  FlySky, FlySun, FlyTrail, FlySea: TBitmap;
  FlyPlane: array [0..23] of TBitmap;
  IslandSky, IslandSea, IslandSand: TBitmap;
  IslandRipple: array [0..23] of TBitmap;
  IslandPalm: array [0..12] of TBitmap;
  Black, White, Shadow, Strip, PanelBuffer, FlyBuffer: TBitmap;
  WelcomeImage, MapImage, SuitcaseImage, FlyImage, IslandImage: TBitmapImage;
  PackFolderValue, PackDesktopValue: TNewStaticText;
  DesktopCheck: TNewCheckBox;
  ShownTicks: Integer;
  FlyTarget, FlyShown: Double;
  RunListX, RunListY, RunListW: Integer;

function TwoDigits(N: Integer): String;
begin
  Result := IntToStr(N);
  if N < 10 then
    Result := '0' + Result;
end;

function Layer(const Name: String): TBitmap;
begin
  Result := TBitmap.Create;
  Result.LoadFromFile(ExpandConstant('{tmp}\art\' + Name + '.bmp'));
end;

function Blank(Width, Height: Integer): TBitmap;
begin
  Result := TBitmap.Create;
  Result.Width := Width;
  Result.Height := Height;
end;

function Solid(Color: TColor): TBitmap;
var
  R: TRect;
begin
  Result := Blank(4, 4);
  Result.Canvas.Brush.Color := Color;
  R.Left := 0;
  R.Top := 0;
  R.Right := 4;
  R.Bottom := 4;
  Result.Canvas.FillRect(R);
end;

procedure LoadLayers;
var
  I: Integer;
  R: TRect;
begin
  ExtractTemporaryFiles('{tmp}\art\*');
  WelcomeSky := Layer('welcome-sky');
  WelcomeSea := Layer('welcome-sea');
  WelcomePlane := Layer('welcome-plane');
  MapBack := Layer('map-bg');
  MapFlat := Layer('map-flat');
  MapPin := Layer('map-pin');
  for I := 0 to 15 do
    MapPulse[I] := Layer('map-pulse-' + TwoDigits(I));
  for I := 0 to 3 do
    Suitcase[I] := Layer('ready-' + IntToStr(I));
  FlySky := Layer('fly-sky');
  FlySun := Layer('fly-sun');
  FlyTrail := Layer('fly-trail');
  FlySea := Layer('fly-sea');
  for I := 0 to 23 do
    FlyPlane[I] := Layer('fly-plane-' + TwoDigits(2 * I));
  IslandSky := Layer('island-sky');
  IslandSea := Layer('island-sea');
  IslandSand := Layer('island-sand');
  for I := 0 to 23 do
    IslandRipple[I] := Layer('island-ripple-' + TwoDigits(I));
  for I := 0 to 12 do
    IslandPalm[I] := Layer('island-palm-' + TwoDigits(I));
  Black := Solid($000000);
  White := Solid($FFFFFF);
  Shadow := Blank(4, 520);  // #DF8E6E: the warm background, a shade darker
  Shadow.Canvas.Brush.Color := $6E8EDF;
  R.Left := 0;
  R.Top := 0;
  R.Right := 4;
  R.Bottom := 520;
  Shadow.Canvas.FillRect(R);
  Strip := Blank(152, 520);
  PanelBuffer := Blank(560, 1016);
  FlyBuffer := Blank(1436, 680);
end;

procedure Copy(Dest: TBitmap; X, Y: Integer; Src: TBitmap);
begin
  BitBlt(Dest.Canvas.Handle, X, Y, Src.Width, Src.Height, Src.Canvas.Handle, 0, 0, SRCCOPY);
end;

// Draws Src (Width x Height) onto the slanted panel from (X0, Y0) to (X1, Y1), one column at a time.
procedure Slant(Dest: TBitmap; Src: TBitmap; X0, Y0, X1, Y1: Double);
var
  C, W: Integer;
begin
  W := Round(X1 * 2) - Round(X0 * 2);
  for C := 0 to W - 1 do
    BitBlt(Dest.Canvas.Handle, Round(X0 * 2) + C, Round(Y0 * 2 + (Y1 - Y0) * 2 * C / W), 1, Src.Height,
      Src.Canvas.Handle, C * Src.Width div W, 0, SRCCOPY);
end;

procedure Blend(Dest: TBitmap; X, Y: Integer; Src: TBitmap; Opacity: Integer);
begin
  AlphaBlend(Dest.Canvas.Handle, X, Y, Src.Width, Src.Height, Src.Canvas.Handle, 0, 0, Src.Width, Src.Height,
    PER_PIXEL_ALPHA or (Opacity shl 16));
end;

// A sea strip whose waves repeat every 240 px, scrolled 240 px every 9 s so the water keeps flowing.
procedure Sea(Dest: TBitmap; Y: Integer; Src: TBitmap; Width, Elapsed: Integer);
begin
  BitBlt(Dest.Canvas.Handle, 0, Y, Width, Src.Height, Src.Canvas.Handle, (Elapsed mod 9000) * 240 div 9000, 0, SRCCOPY);
end;

// Shows a scene in a picture on the page: scaled to fill it, cutting off what does not fit.
procedure Present(Image: TBitmapImage; Src: TBitmap);
var
  DC: Longint;
  W, H, CropW, CropH: Integer;
begin
  W := Image.Width;
  H := Image.Height;
  if (Image.Bitmap.Width <> W) or (Image.Bitmap.Height <> H) then
  begin
    Image.Bitmap.Width := W;
    Image.Bitmap.Height := H;
  end;
  if W * Src.Height > H * Src.Width then
  begin
    CropW := Src.Width;
    CropH := Src.Width * H div W;
  end else
  begin
    CropH := Src.Height;
    CropW := Src.Height * W div H;
  end;
  DC := Image.Bitmap.Canvas.Handle;
  SetStretchBltMode(DC, HALFTONE);
  SetBrushOrgEx(DC, 0, 0, 0);
  StretchBlt(DC, 0, 0, W, H, Src.Canvas.Handle, (Src.Width - CropW) div 2, (Src.Height - CropH) div 2, CropW, CropH, SRCCOPY);
  Image.Invalidate;
end;

// 1 · Welcome: the plane bobs above the sunset, the sea flows.
procedure DrawWelcome(T: Integer);
var
  Bob: Integer;
begin
  Copy(PanelBuffer, 0, 0, WelcomeSky);
  Sea(PanelBuffer, 700, WelcomeSea, 560, T);
  Bob := Round(6 * (1 - Cos(2 * Pi * T / 4000)));
  Blend(PanelBuffer, 424 - 64, 248 - 64 - Bob, WelcomePlane, 255);
  Present(WelcomeImage, PanelBuffer);
end;

// 2 · Map: the paper map folds in and out every 6 s; its three panels, the pin and the pulse follow.
procedure DrawMap(T: Integer);
var
  K, W, A, P: Double;
  Xs, Ys: array [0..3] of Double;
  I, PinX, PinY, Lift, Opacity, Shade: Integer;
  Tint: TBitmap;
begin
  K := (1 - Cos(2 * Pi * T / 6000)) / 2;
  W := 114 - 14 * K;
  A := 7 + 7 * K;
  Xs[0] := 140 - W;
  Xs[1] := 140 - W / 3;
  Xs[2] := 140 + W / 3;
  Xs[3] := 140 + W;
  Ys[0] := 131 + A;
  Ys[1] := 131 - A;
  Ys[2] := 131 + A;
  Ys[3] := 131 - A;
  Copy(PanelBuffer, 0, 0, MapBack);
  for I := 0 to 2 do
    Slant(PanelBuffer, Shadow, Xs[I] + 4, Ys[I] + 8, Xs[I + 1] + 4, Ys[I + 1] + 8);
  for I := 0 to 2 do
  begin
    BitBlt(Strip.Canvas.Handle, 0, 0, 152, 520, MapFlat.Canvas.Handle, 152 * I, 0, SRCCOPY);
    // The middle panel turns away from the light, the outer ones towards it.
    if I = 1 then
      Tint := Black
    else
      Tint := White;
    Shade := Round(255 * (0.05 + 0.1 * K));
    AlphaBlend(Strip.Canvas.Handle, 0, 0, 152, 520, Tint.Canvas.Handle, 0, 0, 4, 4, Shade shl 16);
    Slant(PanelBuffer, Strip, Xs[I], Ys[I], Xs[I + 1], Ys[I + 1]);
  end;
  // The pin stands at (200, 244) of the flat map, on the right panel.
  PinX := Round((Xs[2] + (Xs[3] - Xs[2]) * 22 / 76) * 2);
  PinY := Round((Ys[2] + 113 + (Ys[3] - Ys[2]) * 22 / 76) * 2);
  Lift := 0;
  Opacity := 255;
  if T < 1200 then
  begin
    // It drops in during the first 1.2 s and bounces once.
    P := T / 1200.0;
    if P < 0.6 then
      Lift := -Round(140 * (1 - P / 0.6) * (1 - P / 0.6))
    else
      Lift := -Round(12 * Sin(Pi * (P - 0.6) / 0.4));
    if P < 0.3 then
      Opacity := Round(255 * P / 0.3);
  end else
    Blend(PanelBuffer, PinX - 72, PinY - 32, MapPulse[((T - 1200) div 150) mod 16], 255);
  Blend(PanelBuffer, PinX - 52, PinY - 100 + Lift, MapPin, Opacity);
  Present(MapImage, PanelBuffer);
end;

// 3 · Packing: the luggage tag gets its three ticks, one after another.
procedure DrawSuitcase(T: Integer);
var
  N: Integer;
begin
  N := 0;
  if T >= 400 then
    N := (T - 400) div 500 + 1;
  if N > 3 then
    N := 3;
  if N <> ShownTicks then
  begin
    ShownTicks := N;
    Present(SuitcaseImage, Suitcase[N]);
  end;
end;

// 4 · Installing: the plane flies along its curve as far as the installation has got, leaving a
// contrail behind it, while the sun rises and the sea flows.
procedure DrawFlight(T: Integer);
var
  P, X, Y, Slope, Angle: Double;
  Frame, TrailWidth: Integer;
begin
  FlyShown := FlyShown + (FlyTarget - FlyShown) * 0.06;
  P := FlyShown;
  Copy(FlyBuffer, 0, 0, FlySky);
  Blend(FlyBuffer, 1040 - 190, Round((250 + 46 * (1 - P)) * 2) - 190, FlySun, 255);
  // The curve: from (-30, 250) through (330, 60) to (690, 150).
  X := (1 - P) * (1 - P) * -30 + 2 * (1 - P) * P * 330 + P * P * 690;
  Y := (1 - P) * (1 - P) * 250 + 2 * (1 - P) * P * 60 + P * P * 150;
  TrailWidth := Round(X * 2);
  if TrailWidth > 1436 then
    TrailWidth := 1436;
  if TrailWidth > 0 then
    AlphaBlend(FlyBuffer.Canvas.Handle, 0, 0, TrailWidth, 500, FlyTrail.Canvas.Handle, 0, 0, TrailWidth, 500, PER_PIXEL_ALPHA or (255 shl 16));
  Sea(FlyBuffer, 500, FlySea, 1436, T);
  // Heading in degrees (the curve always moves 720 px to the right), one plane frame per 2°.
  Slope := (2 * (1 - P) * -190 + 2 * P * 90) / 720;
  Angle := (Slope - Slope * Slope * Slope / 3 + Slope * Slope * Slope * Slope * Slope / 5) * 180 / Pi;
  Frame := Round((Angle + 30) / 2);
  if Frame < 0 then
    Frame := 0;
  if Frame > 23 then
    Frame := 23;
  Blend(FlyBuffer, Round(X * 2) - 52, Round(Y * 2) - 52, FlyPlane[Frame], 255);
  Present(FlyImage, FlyBuffer);
end;

// 5 · Finished: the island, with flowing water, spreading ripples and a gently swaying palm.
procedure DrawIsland(T: Integer);
begin
  Copy(PanelBuffer, 0, 0, IslandSky);
  Sea(PanelBuffer, 600, IslandSea, 560, T);
  Blend(PanelBuffer, 0, 712, IslandRipple[(T div 167) mod 24], 255);
  Blend(PanelBuffer, 0, 600, IslandSand, 255);
  Blend(PanelBuffer, 240, 480, IslandPalm[Round(6 + 6 * Sin(2 * Pi * T / 5000))], 255);
  Present(IslandImage, PanelBuffer);
end;

procedure OnTimer(Wnd, Msg, IDEvent, Time: Longword);
var
  T: Integer;
begin
  T := TickCount - PageStart;
  case WizardForm.CurPageID of
    wpWelcome: DrawWelcome(T);
    wpSelectDir: DrawMap(T);
    wpReady: DrawSuitcase(T);
    wpInstalling: DrawFlight(T);
    wpFinished: DrawIsland(T);
  end;
end;

// ---------------------------------------------------------------------------------------------
// Page layout: a picture on the left (on top for the flight), text and controls beside it.
// ---------------------------------------------------------------------------------------------

function MakeImage(Parent: TWinControl; X, Y, W, H: Integer): TBitmapImage;
begin
  Result := TBitmapImage.Create(WizardForm);
  Result.Parent := Parent;
  Result.SetBounds(X, Y, W, H);
end;

function MakeText(Parent: TWinControl; const Text: String; X, Y, W, Size: Integer; Bold: Boolean; Color: TColor): TNewStaticText;
begin
  Result := TNewStaticText.Create(WizardForm);
  Result.Parent := Parent;
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.ShowAccelChar := False;
  Result.SetBounds(X, Y, W, ScaleY(20));
  Result.Font.Size := Size;
  if Bold then
    Result.Font.Style := [fsBold];
  Result.Font.Color := Color;
  Result.Caption := Text;
  Result.AdjustHeight;
end;

// A page's heading and text; returns where the next control goes.
function MakeIntro(Parent: TWinControl; const Title, Text: String; X, Y, W: Integer): Integer;
var
  Lbl: TNewStaticText;
begin
  Lbl := MakeText(Parent, Title, X, Y, W, 18, True, INK);
  Y := Y + Lbl.Height + ScaleY(10);
  Lbl := MakeText(Parent, Text, X, Y, W, 10, False, BODY_INK);
  Result := Y + Lbl.Height + ScaleY(24);
end;

procedure AddPackRow(X, Y, W: Integer; const Title: String; var Value: TNewStaticText);
var
  Check: TBitmapImage;
  Lbl: TNewStaticText;
begin
  Check := MakeImage(WizardForm.ReadyPage, X, Y + ScaleY(1), ScaleX(20), ScaleY(20));
  Check.Stretch := True;
  Check.PngImage.LoadFromFile(ExpandConstant('{tmp}\art\ready-check.png'));
  Lbl := MakeText(WizardForm.ReadyPage, Title, X + ScaleX(32), Y, W - ScaleX(32), 10, True, INK);
  Value := MakeText(WizardForm.ReadyPage, '', X + ScaleX(32), Y + Lbl.Height + ScaleY(1), W - ScaleX(32), 9, False, MUTED_INK);
end;

procedure InitializeWizard;
var
  PageW, PageH, PanelW, X, Y, W, ButtonW, FlyH: Integer;
  Lbl, Unused: TNewStaticText;
begin
  LoadLayers;
  WizardForm.Color := PAPER;
  WizardForm.WelcomePage.Color := PAPER;
  WizardForm.FinishedPage.Color := PAPER;
  WizardForm.InnerPage.Color := PAPER;
  WizardForm.SelectDirPage.Color := PAPER;
  WizardForm.ReadyPage.Color := PAPER;
  WizardForm.InstallingPage.Color := PAPER;
  // Inner pages lose their header and use the whole area above the buttons, like the outer ones.
  WizardForm.MainPanel.Visible := False;
  WizardForm.Bevel1.Visible := False;
  PageW := WizardForm.InnerPage.ClientWidth;
  PageH := WizardForm.InnerPage.ClientHeight;
  WizardForm.InnerNotebook.SetBounds(0, 0, PageW, PageH);
  PanelW := PageH * 280 div 508;
  X := PanelW + ScaleX(32);
  W := PageW - X - ScaleX(32);

  // 1 · Welcome
  WizardForm.WizardBitmapImage.Visible := False;
  WizardForm.WelcomeLabel1.Visible := False;
  WizardForm.WelcomeLabel2.Visible := False;
  WelcomeImage := MakeImage(WizardForm.WelcomePage, 0, 0, PanelW, PageH);
  MakeIntro(WizardForm.WelcomePage, CustomMessage('WelcomeTitle'), CustomMessage('WelcomeText'), X, ScaleY(40), W);

  // 2 · Where should we go?
  WizardForm.SelectDirBitmapImage.Visible := False;
  WizardForm.SelectDirLabel.Visible := False;
  WizardForm.SelectDirBrowseLabel.Visible := False;
  MapImage := MakeImage(WizardForm.SelectDirPage, 0, 0, PanelW, PageH);
  Y := MakeIntro(WizardForm.SelectDirPage, CustomMessage('MapTitle'), CustomMessage('MapText'), X, ScaleY(40), W);
  Lbl := MakeText(WizardForm.SelectDirPage, CustomMessage('Folder'), X, Y, W, 9, True, INK);
  Y := Y + Lbl.Height + ScaleY(6);
  ButtonW := WizardForm.DirBrowseButton.Width;
  WizardForm.DirEdit.SetBounds(X, Y, W - ButtonW - ScaleX(8), WizardForm.DirEdit.Height);
  WizardForm.DirBrowseButton.SetBounds(X + W - ButtonW, Y - (WizardForm.DirBrowseButton.Height - WizardForm.DirEdit.Height) div 2,
    ButtonW, WizardForm.DirBrowseButton.Height);
  Y := Y + WizardForm.DirBrowseButton.Height + ScaleY(6);
  WizardForm.DiskSpaceLabel.SetBounds(X, Y, W, WizardForm.DiskSpaceLabel.Height);
  WizardForm.DiskSpaceLabel.Font.Color := MUTED_INK;
  Y := Y + WizardForm.DiskSpaceLabel.Height + ScaleY(20);
  DesktopCheck := TNewCheckBox.Create(WizardForm);
  DesktopCheck.Parent := WizardForm.SelectDirPage;
  DesktopCheck.SetBounds(X, Y, W, ScaleY(22));
  DesktopCheck.Caption := CustomMessage('DesktopShortcut');

  // 3 · All packed?
  WizardForm.ReadyLabel.Visible := False;
  WizardForm.ReadyMemo.Visible := False;
  SuitcaseImage := MakeImage(WizardForm.ReadyPage, 0, 0, PanelW, PageH);
  Y := MakeIntro(WizardForm.ReadyPage, CustomMessage('PackTitle'), CustomMessage('PackText'), X, ScaleY(40), W);
  AddPackRow(X, Y, W, CustomMessage('PackFolder'), PackFolderValue);
  Y := Y + ScaleY(58);
  AddPackRow(X, Y, W, CustomMessage('PackStartMenu'), Unused);
  Unused.Caption := '{#AppName}';
  Y := Y + ScaleY(46);
  AddPackRow(X, Y, W, CustomMessage('PackDesktop'), PackDesktopValue);

  // 4 · Taking you to your destination: the flight on top, progress below.
  FlyH := PageW * 340 div 718;
  if FlyH > PageH - ScaleY(130) then
    FlyH := PageH - ScaleY(130);
  FlyImage := MakeImage(WizardForm.InstallingPage, 0, 0, PageW, FlyH);
  Y := FlyH + ScaleY(18);
  Lbl := MakeText(WizardForm.InstallingPage, CustomMessage('FlyTitle'), ScaleX(32), Y, PageW - ScaleX(64), 14, True, INK);
  Y := Y + Lbl.Height + ScaleY(6);
  WizardForm.StatusLabel.SetBounds(ScaleX(32), Y, PageW - ScaleX(64), WizardForm.StatusLabel.Height);
  WizardForm.StatusLabel.Font.Color := MUTED_INK;
  Y := Y + WizardForm.StatusLabel.Height + ScaleY(2);
  WizardForm.FilenameLabel.SetBounds(ScaleX(32), Y, PageW - ScaleX(64), WizardForm.FilenameLabel.Height);
  WizardForm.FilenameLabel.Font.Color := MUTED_INK;
  Y := Y + WizardForm.FilenameLabel.Height + ScaleY(8);
  WizardForm.ProgressGauge.SetBounds(ScaleX(32), Y, PageW - ScaleX(64), WizardForm.ProgressGauge.Height);

  // 5 · You're all set!
  WizardForm.WizardBitmapImage2.Visible := False;
  WizardForm.FinishedHeadingLabel.Visible := False;
  WizardForm.FinishedLabel.Visible := False;
  IslandImage := MakeImage(WizardForm.FinishedPage, 0, 0, PanelW, PageH);
  Y := MakeIntro(WizardForm.FinishedPage, CustomMessage('DoneTitle'), CustomMessage('DoneText'), X, ScaleY(44), W);
  RunListX := X;
  RunListY := Y;
  RunListW := W;

  if not WizardSilent then
    Timer := SetTimer(0, 0, 33, CreateCallback(@OnTimer));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  PageStart := TickCount;
  ShownTicks := -1;
  if CurPageID = wpReady then
  begin
    // Setup shows its own summary again on every visit.
    WizardForm.ReadyMemo.Visible := False;
    PackFolderValue.Caption := WizardDirValue;
    PackFolderValue.AdjustHeight;
    if DesktopCheck.Checked then
      PackDesktopValue.Caption := CustomMessage('Yes')
    else
      PackDesktopValue.Caption := CustomMessage('No');
  end;
  if CurPageID = wpInstalling then
  begin
    FlyTarget := 0;
    FlyShown := 0;
  end;
  if CurPageID = wpFinished then
    // Setup places the run list under its own (hidden) text when the page opens.
    WizardForm.RunList.SetBounds(RunListX, RunListY, RunListW, ScaleY(28));
  if not WizardSilent then
    OnTimer(0, 0, 0, 0);
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if MaxProgress > 0 then
    FlyTarget := CurProgress * 1.0 / MaxProgress;
end;

function WantDesktopIcon: Boolean;
begin
  Result := (DesktopCheck <> nil) and DesktopCheck.Checked;
end;

procedure DeinitializeSetup;
begin
  if Timer <> 0 then
    KillTimer(0, Timer);
end;

// ---------------------------------------------------------------------------------------------
// Uninstall
// ---------------------------------------------------------------------------------------------

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
