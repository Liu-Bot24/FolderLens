#ifndef AppSource
  #error AppSource is required
#endif
#ifndef OutputRoot
  #error OutputRoot is required
#endif
#ifndef BuildId
  #error BuildId is required
#endif
#ifndef SourceFileList
  #error SourceFileList is required; use Package.ps1 to generate the verified file list
#endif
[Setup]
AppId={{9945B15D-3D41-4535-A101-C2C017DC36F2}
AppName=FolderLens
SetupIconFile=..\src\FolderLens.App\Assets\FolderLens.ico
AppVersion=0.1.0
AppVerName=FolderLens {#BuildId}
DefaultDirName={localappdata}\Programs\FolderLens
DefaultGroupName=FolderLens
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputRoot}
OutputBaseFilename=FolderLens-{#BuildId}-win-x64-setup-unsigned
Compression=zip/6
SolidCompression=no
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayName=FolderLens
UninstallDisplayIcon={app}\versions\{#BuildId}\FolderLens.App.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
#include SourceFileList
[Icons]
Name: "{autoprograms}\FolderLens"; Filename: "{app}\versions\{#BuildId}\FolderLens.App.exe"; WorkingDir: "{app}\versions\{#BuildId}"
Name: "{autodesktop}\FolderLens"; Filename: "{app}\versions\{#BuildId}\FolderLens.App.exe"; WorkingDir: "{app}\versions\{#BuildId}"; Tasks: desktopicon
[Code]
const
  FileAttributeReparsePoint = $00000400;
  InvalidFileAttributes = $FFFFFFFF;
var
  RemoveAppData: Boolean;
function GetFileAttributesW(lpFileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function IsUnlinkedTree(const Directory: String): Boolean;
var
  Attr: LongWord;
  FindRec: TFindRec;
  Child: String;
begin
  Result := False;
  Attr := GetFileAttributesW(Directory);
  if Attr = InvalidFileAttributes then exit;
  if (Attr and FileAttributeReparsePoint) <> 0 then exit;
  if FindFirst(Directory + '\*', FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          if (FindRec.Attributes and FileAttributeReparsePoint) <> 0 then exit;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
            Child := Directory + '\' + FindRec.Name;
            if not IsUnlinkedTree(Child) then exit;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end else exit;
  Result := True;
end;
function InitializeUninstall(): Boolean;
begin
  RemoveAppData := False;
  if not UninstallSilent then
    RemoveAppData := MsgBox('Also delete FolderLens settings, index and cache? Source folders are never deleted. Choose No to retain application data.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and RemoveAppData then begin
    DataDir := ExpandConstant('{localappdata}\FolderLens');
    if DirExists(DataDir) then begin
      if ((GetFileAttributesW(ExpandConstant('{localappdata}')) and FileAttributeReparsePoint) = 0) and IsUnlinkedTree(DataDir) then begin
        if not DelTree(DataDir, True, True, True) then
          MsgBox('Some application data could not be removed. It was retained for manual review.', mbInformation, MB_OK);
      end else
        MsgBox('Application data contains a link or cannot be safely inspected. It was retained.', mbInformation, MB_OK);
    end;
  end;
end;
