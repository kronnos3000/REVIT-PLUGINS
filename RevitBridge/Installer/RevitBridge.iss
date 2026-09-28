; RevitBridge.iss — Inno Setup 6 script for the CCorp RevitBridge Revit add-in installer.
;
; Build with:   iscc /DMyAppVersion=1.3.0 RevitBridge.iss
; Expects staged payloads at ..\dist\<year>\  (produced by ..\Build-All.ps1):
;   RevitBridge.addin + RevitBridge\*.dll (incl. Microsoft.CodeAnalysis*.dll) + RevitBridge\Resources\*.png
; Years 2025 and 2027 only; years whose
; %APPDATA%\Autodesk\Revit\Addins\<year> folder is missing are disabled in the UI.
; Ships only the add-in. The Python MCP server (RevitBridge\mcp) is set up by hand.

#ifndef MyAppVersion
  #define MyAppVersion "1.3.0"
#endif

#define MyAppName      "CCorp RevitBridge"
#define MyAppPublisher "Construction Corps"
#define MyAppId        "{{9049392B-2F6C-4A4D-9A47-F96214063D98}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={userappdata}\CCorp\RevitBridge
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist\installer
OutputBaseFilename=RevitBridge-Setup-{#MyAppVersion}
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
Source: "..\dist\2025\RevitBridge.addin";   DestDir: "{code:AddinDir|2025}"; Check: ShouldInstall2025; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2025\RevitBridge\*";       DestDir: "{code:AddinDir|2025}\RevitBridge"; Check: ShouldInstall2025; Flags: ignoreversion skipifsourcedoesntexist recursesubdirs createallsubdirs

Source: "..\dist\2027\RevitBridge.addin";   DestDir: "{code:AddinDir|2027}"; Check: ShouldInstall2027; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\2027\RevitBridge\*";       DestDir: "{code:AddinDir|2027}\RevitBridge"; Check: ShouldInstall2027; Flags: ignoreversion skipifsourcedoesntexist recursesubdirs createallsubdirs

[UninstallDelete]
Type: filesandordirs; Name: "{code:AddinDir|2025}\RevitBridge"
Type: filesandordirs; Name: "{code:AddinDir|2027}\RevitBridge"

[Code]
var
  YearPage: TInputOptionWizardPage;
  YearAvailable: array[0..1] of Boolean;

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
  years: array[0..1] of String;
  caption: String;
begin
  years[0] := '2025';
  years[1] := '2027';

  YearPage := CreateInputOptionPage(wpWelcome,
    'Choose target Revit year(s)',
    'Select which Revit installations should receive RevitBridge',
    'Only Revit years that are installed on this machine are enabled.',
    False, False);

  for i := 0 to 1 do
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
    for i := 0 to 1 do
      if YearAvailable[i] and YearPage.Values[i] then
        anySelected := True;
    if not anySelected then
    begin
      MsgBox('Select at least one Revit year to install into.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function ShouldInstall2025(): Boolean; begin Result := YearAvailable[0] and YearPage.Values[0]; end;
function ShouldInstall2027(): Boolean; begin Result := YearAvailable[1] and YearPage.Values[1]; end;
