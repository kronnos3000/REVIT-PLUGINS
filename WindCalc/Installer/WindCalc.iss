; WindCalc.iss — Inno Setup 6 script for the WindCalc Revit plugin installer.
;
; Build with:   iscc /DMyAppVersion=1.0.0 WindCalc.iss
; Expects staged payloads at ..\dist\<year>\  (produced by ..\Build-All.ps1).
; Years without a dist folder are skipped; years whose
; %APPDATA%\Autodesk\Revit\Addins\<year> folder is missing are disabled in the UI.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName      "Wind Calculator"
#define MyAppPublisher "Construction Corps"
#define MyAppId        "{{B7E2C1F9-4A3D-4C58-9F21-7A6E2D9B3C44}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={userappdata}\CCorp\WindCalc
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist\installer
OutputBaseFilename=WindCalc-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayName={#MyAppName} {#MyAppVersion}
CloseApplications=force
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\dist\2024\WindCalc.dll";        DestDir: "{code:AddinDir|2024}"; Check: ShouldInstall2024; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2024\WindCalc.addin";      DestDir: "{code:AddinDir|2024}"; Check: ShouldInstall2024; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2024\Newtonsoft.Json.dll"; DestDir: "{code:AddinDir|2024}"; Check: ShouldInstall2024; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2024\Resources\*";         DestDir: "{code:AddinDir|2024}\Resources"; Check: ShouldInstall2024; Flags: ignoreversion skipifsourcedoesntexist recursesubdirs createallsubdirs

Source: "..\dist\2025\WindCalc.dll";        DestDir: "{code:AddinDir|2025}"; Check: ShouldInstall2025; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2025\WindCalc.addin";      DestDir: "{code:AddinDir|2025}"; Check: ShouldInstall2025; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2025\Newtonsoft.Json.dll"; DestDir: "{code:AddinDir|2025}"; Check: ShouldInstall2025; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2025\Resources\*";         DestDir: "{code:AddinDir|2025}\Resources"; Check: ShouldInstall2025; Flags: ignoreversion skipifsourcedoesntexist recursesubdirs createallsubdirs

Source: "..\dist\2026\WindCalc.dll";        DestDir: "{code:AddinDir|2026}"; Check: ShouldInstall2026; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2026\WindCalc.addin";      DestDir: "{code:AddinDir|2026}"; Check: ShouldInstall2026; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2026\Newtonsoft.Json.dll"; DestDir: "{code:AddinDir|2026}"; Check: ShouldInstall2026; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2026\Resources\*";         DestDir: "{code:AddinDir|2026}\Resources"; Check: ShouldInstall2026; Flags: ignoreversion skipifsourcedoesntexist recursesubdirs createallsubdirs

Source: "..\dist\2027\WindCalc.dll";        DestDir: "{code:AddinDir|2027}"; Check: ShouldInstall2027; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2027\WindCalc.addin";      DestDir: "{code:AddinDir|2027}"; Check: ShouldInstall2027; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2027\Newtonsoft.Json.dll"; DestDir: "{code:AddinDir|2027}"; Check: ShouldInstall2027; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2027\Resources\*";         DestDir: "{code:AddinDir|2027}\Resources"; Check: ShouldInstall2027; Flags: ignoreversion skipifsourcedoesntexist recursesubdirs createallsubdirs

[UninstallDelete]
Type: filesandordirs; Name: "{code:AddinDir|2024}\Resources"
Type: filesandordirs; Name: "{code:AddinDir|2025}\Resources"
Type: filesandordirs; Name: "{code:AddinDir|2026}\Resources"
Type: filesandordirs; Name: "{code:AddinDir|2027}\Resources"

[Code]
var
  YearPage: TInputOptionWizardPage;
  YearAvailable: array[0..3] of Boolean;

function AddinDir(Param: String): String;
begin
  Result := ExpandConstant('{userappdata}') + '\Autodesk\Revit\Addins\' + Param;
end;

function RevitIsRunning(): Boolean;
var
  ResultCode: Integer;
  Output: AnsiString;
  TempFile: String;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\revitcheck.txt');
  if Exec(ExpandConstant('{cmd}'),
          '/C tasklist /FI "IMAGENAME eq Revit.exe" /NH > "' + TempFile + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringFromFile(TempFile, Output) then
      if Pos('Revit.exe', Output) > 0 then
        Result := True;
    DeleteFile(TempFile);
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if RevitIsRunning() then
  begin
    MsgBox('Revit is currently running. Please close all Revit sessions and run setup again.',
           mbError, MB_OK);
    Result := False;
  end;
end;

procedure InitializeWizard();
var
  i: Integer;
  years: array[0..3] of String;
  caption: String;
begin
  years[0] := '2024';
  years[1] := '2025';
  years[2] := '2026';
  years[3] := '2027';

  YearPage := CreateInputOptionPage(wpWelcome,
    'Choose target Revit year(s)',
    'Select which Revit installations should receive WindCalc',
    'Only Revit years that are installed on this machine are enabled. You can install into multiple years at once.',
    False, False);

  for i := 0 to 3 do
  begin
    YearAvailable[i] := DirExists(AddinDir(years[i]));
    caption := 'Revit ' + years[i];
    if not YearAvailable[i] then
      caption := caption + '  (not installed)';
    YearPage.Add(caption);
    YearPage.CheckListBox.ItemEnabled[i] := YearAvailable[i];
    YearPage.Values[i] := YearAvailable[i];
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  i: Integer;
  anySelected: Boolean;
begin
  Result := True;
  if CurPageID = YearPage.ID then
  begin
    anySelected := False;
    for i := 0 to 3 do
      if YearAvailable[i] and YearPage.Values[i] then
        anySelected := True;
    if not anySelected then
    begin
      MsgBox('Select at least one Revit year to install into.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function ShouldInstall2024(): Boolean; begin Result := YearAvailable[0] and YearPage.Values[0]; end;
function ShouldInstall2025(): Boolean; begin Result := YearAvailable[1] and YearPage.Values[1]; end;
function ShouldInstall2026(): Boolean; begin Result := YearAvailable[2] and YearPage.Values[2]; end;
function ShouldInstall2027(): Boolean; begin Result := YearAvailable[3] and YearPage.Values[3]; end;
