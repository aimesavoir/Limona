#ifndef AppVersion
  #define AppVersion "1.0.0.0"
#endif

#define AppName "Limona"
#define AppPublisher "MECATROTEC"
#ifndef PublishDir
  #define PublishDir "..\src\Limona\bin\x64\Release\net9.0-windows10.0.19041.0\win-x64\publish"
#endif

[Setup]
AppId={{1FC5E05B-7319-4C5F-9B9A-87C65D0205CB}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=mailto:baroudi.inc@gmail.com
DefaultDirName={localappdata}\Programs\Limona
DefaultGroupName=Limona
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\setup
OutputBaseFilename=Limona-Setup-{#AppVersion}
SetupIconFile=..\src\Limona\Assets\app.ico
UninstallDisplayIcon={app}\Limona.exe
UninstallDisplayName=Limona
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Limona Setup
VersionInfoProductName=Limona
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\Limona\Limona"; Filename: "{app}\Limona.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Limona.exe"
Name: "{autodesktop}\Limona"; Filename: "{app}\Limona.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Limona.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Limona"; ValueData: """{app}\Limona.exe"""; Flags: uninsdeletevalue

[Code]
var
  AppLanguagePage: TInputOptionWizardPage;
  ExistingInstall: Boolean;
  ExistingInstallDir: string;
  ExistingUninstallPath: string;
  InstallActionPage: TInputOptionWizardPage;
  LanguagePageAfter: Integer;

function InitializeSetup: Boolean;
begin
  ExistingInstallDir := ExpandConstant('{localappdata}\Programs\Limona');
  ExistingInstall := RegQueryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{1FC5E05B-7319-4C5F-9B9A-87C65D0205CB}_is1',
    'InstallLocation',
    ExistingInstallDir);

  if ExistingInstallDir = '' then
    ExistingInstallDir := ExpandConstant('{localappdata}\Programs\Limona');

  ExistingUninstallPath := AddBackslash(ExistingInstallDir) + 'unins000.exe';
  ExistingInstall := ExistingInstall or
    FileExists(ExistingUninstallPath) or
    FileExists(AddBackslash(ExistingInstallDir) + 'Limona.exe');

  Result := True;
end;

procedure InitializeWizard;
begin
  LanguagePageAfter := wpSelectDir;

  if ExistingInstall then
  begin
    InstallActionPage := CreateInputOptionPage(
      wpSelectDir,
      'Existing Limona installation found',
      'Choose how to proceed',
      'Repair replaces the installed program files and keeps your settings and data. Reinstall removes the existing program first, then installs it again. Your settings and data are kept either way.',
      True,
      False);
    InstallActionPage.Add('Repair the existing installation');
    InstallActionPage.Add('Uninstall and reinstall Limona');
    InstallActionPage.SelectedValueIndex := 0;
    LanguagePageAfter := InstallActionPage.ID;
  end;

  AppLanguagePage := CreateInputOptionPage(
    LanguagePageAfter,
    'Program language',
    'Choose the language Limona will use',
    'English is selected by default. You can change the language later in Limona settings.',
    True,
    False);
  AppLanguagePage.Add('English');
  AppLanguagePage.Add('Français');
  AppLanguagePage.Add('العربية');
  AppLanguagePage.Add('Español');
  AppLanguagePage.SelectedValueIndex := 0;
end;

function SelectedAppLanguage: string;
begin
  case AppLanguagePage.SelectedValueIndex of
    1: Result := 'fr';
    2: Result := 'ar';
    3: Result := 'es';
  else
    Result := 'en';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  if not Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/F /T /IM Limona.exe',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result := 'Could not stop Limona. Close the program and try again.';
    Exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 128) then
  begin
    Result := 'Could not fully stop Limona (error ' + IntToStr(ResultCode) +
      '). Close the program and try again.';
    Exit;
  end;

  if ExistingInstall and (InstallActionPage.SelectedValueIndex = 1) then
  begin
    if not FileExists(ExistingUninstallPath) then
    begin
      Result := 'The existing uninstaller could not be found. Choose Repair or remove Limona from Windows Settings first.';
      Exit;
    end;

    if not Exec(
      ExistingUninstallPath,
      '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
      ExistingInstallDir,
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
    begin
      Result := 'Could not start the existing Limona uninstaller.';
      Exit;
    end;

    if ResultCode <> 0 then
    begin
      Result := 'The existing Limona installation could not be removed (error ' +
        IntToStr(ResultCode) + ').';
      Exit;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not Exec(
      ExpandConstant('{app}\Limona.exe'),
      '--set-language ' + SelectedAppLanguage,
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
      RaiseException('Could not save the selected Limona language.');

    if ResultCode <> 0 then
      RaiseException('Could not save the selected Limona language (error ' +
        IntToStr(ResultCode) + ').');
  end;

  if CurStep = ssDone then
  begin
    if not Exec(
      ExpandConstant('{app}\Limona.exe'),
      '',
      ExpandConstant('{app}'),
      SW_SHOWNORMAL,
      ewNoWait,
      ResultCode) then
      RaiseException('Could not launch Limona.');
  end;
end;
