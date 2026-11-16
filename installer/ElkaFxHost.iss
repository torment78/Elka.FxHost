; Elka FX Host installer: the app and its virtual ASIO driver are one product.
;
; - Installs the app and ElkaFxHost.Asio.dll, and registers the driver silently
;   (HKLM\SOFTWARE\ASIO\Elka FX Host). The uninstaller unregisters it again.
; - Asks which Matrix edition is in use and sets the driver channel count:
;     Matrix = 32, Matrix Coconut = 64, Custom = 8..128 in banks of 8.
; - Silent installs: /CHANNELS=32|64|<multiple of 8 up to 128> (default: keep the current
;   setting, otherwise 64).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef RepoRoot
  #define RepoRoot ".."
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\ElkaFxHost\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "ElkaFxHostSetup"
#endif

#define AppExe "ElkaFxHost.exe"
#define DriverDll "ElkaFxHost.Asio.dll"
#define AsioSettingsKey "Software\ElkaSoft\FxHost\Asio"

#if Ver < EncodeVer(6, 6, 0)
  #error Inno Setup 6.6 or later is required for the dark installer
#endif

[Setup]
; New AppId: Elka FX Host installs side by side with Elka FX Host.
AppId={{F0F07B05-AB22-4FE8-867E-71604337526D}
AppName=Elka FX Host
AppVersion={#AppVersion}
AppPublisher=ElkaSoft
AppPublisherURL=https://github.com/torment78/Elka.FxHost
AppSupportURL=https://github.com/torment78/Elka.FxHost/issues
AppUpdatesURL=https://github.com/torment78/Elka.FxHost/releases
DefaultDirName={autopf}\ElkaSoft\FX Host
DefaultGroupName=ElkaSoft\Elka FX Host
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#RepoRoot}\src\app-wpf\Assets\VoicemeeterDelay.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Elka FX Host
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dark polar includetitlebar
WizardSizePercent=120,120
DisableWelcomePage=no
WizardImageFile={#RepoRoot}\installer\Assets\FxHostWizard.png
WizardSmallImageFile={#RepoRoot}\src\app-wpf\Assets\VoicemeeterDelayIconPreview.png
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Registering an ASIO driver writes HKLM, so setup always needs administrator rights.
PrivilegesRequired=admin
; The installing user's HKCU gets the channel count on purpose; HKLM holds the default for others.
UsedUserAreasWarning=no
CloseApplications=yes
RestartApplications=no
#ifdef SignedUninstallerOutputDir
; Bulk signing prepares this exact uninstaller before packaging the final setup.
SignedUninstaller=yes
SignedUninstallerDir={#SignedUninstallerOutputDir}
#else
#ifdef SignInstaller
SignTool=ElkaVoiceMeeterFxHost
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#RepoRoot}\installer\Assets\ElkaSoft.png"; Flags: dontcopy
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "{#DriverDll},MatrixLinkLoopback.exe,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; The driver is loaded by Matrix, so it may be in use during an upgrade: replace it on
; reboot in that case. regserver registers it as the ASIO device "Elka FX Host" and
; unregisters it on uninstall.
Source: "{#SourceDir}\{#DriverDll}"; DestDir: "{app}"; Flags: ignoreversion regserver restartreplace uninsrestartdelete 64bit

[InstallDelete]
; Before 0.1.2 the program was Elka.VoiceMeeterFxHost.App.exe with ElkaVoiceMeeterFxHost.Native.dll.
Type: files; Name: "{app}\Elka.VoiceMeeterFxHost.App.exe"
Type: files; Name: "{app}\Elka.VoiceMeeterFxHost.App.dll"
Type: files; Name: "{app}\Elka.VoiceMeeterFxHost.App.deps.json"
Type: files; Name: "{app}\Elka.VoiceMeeterFxHost.App.runtimeconfig.json"
Type: files; Name: "{app}\ElkaVoiceMeeterFxHost.Native.dll"

[Registry]
; Driver channel count. The per-user value is what the driver and the app read first;
; the machine value is the default for other users on this PC.
Root: HKLM64; Subkey: "{#AsioSettingsKey}"; ValueType: dword; ValueName: "ChannelCount"; ValueData: "{code:SelectedChannelCountText}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "{#AsioSettingsKey}"; ValueType: dword; ValueName: "ChannelCount"; ValueData: "{code:SelectedChannelCountText}"; Flags: uninsdeletevalue

[Icons]
Name: "{autoprograms}\ElkaSoft\Elka FX Host"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\Elka FX Host"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[UninstallRun]
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""ElkaFxHost"" /f"; Flags: runhidden; RunOnceId: "RemoveElkaFxHostAutoStart"

[Run]
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{app}\{#AppExe}"""; StatusMsg: "Disabling Windows power throttling for Elka FX Host..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableFxHostPowerThrottling
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{code:MatrixExecutablePath|VBAudioMatrix_x64.exe}"""; StatusMsg: "Disabling Windows power throttling for VB-Audio Matrix (64-bit)..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableMatrixExecutable('VBAudioMatrix_x64.exe')
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{code:MatrixExecutablePath|VBAudioMatrix.exe}"""; StatusMsg: "Disabling Windows power throttling for VB-Audio Matrix (32-bit)..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableMatrixExecutable('VBAudioMatrix.exe')
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{code:MatrixExecutablePath|VBAudioMatrixCoconut_x64.exe}"""; StatusMsg: "Disabling Windows power throttling for VB-Audio Matrix Coconut (64-bit)..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableMatrixExecutable('VBAudioMatrixCoconut_x64.exe')
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{code:MatrixExecutablePath|VBAudioMatrixCoconut.exe}"""; StatusMsg: "Disabling Windows power throttling for VB-Audio Matrix Coconut (32-bit)..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableMatrixExecutable('VBAudioMatrixCoconut.exe')
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Elka FX Host}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  MatrixChannels = 32;
  CoconutChannels = 64;
  MaxChannels = 128;
  ChannelStep = 8;

var
  ChannelPage: TWizardPage;
  MatrixRadio, CoconutRadio, CustomRadio: TNewRadioButton;
  CustomCombo: TNewComboBox;
  PerformanceOptionsPage: TInputOptionWizardPage;
  MatrixEditionPage: TInputOptionWizardPage;
  MatrixEditionDefaulted: Boolean;
  WelcomeLogo, FinishedLogo: TBitmapImage;

function SanitizeChannelCount(Value: Integer): Integer;
begin
  if Value <= 0 then
    Value := CoconutChannels;
  Value := ((Value + ChannelStep - 1) div ChannelStep) * ChannelStep;
  if Value < ChannelStep then
    Value := ChannelStep;
  if Value > MaxChannels then
    Value := MaxChannels;
  Result := Value;
end;

function LastBankLetter(Channels: Integer): string;
begin
  Result := Chr(Ord('A') + (Channels div ChannelStep) - 1);
end;

function ExistingChannelCount: Integer;
var
  Value: Cardinal;
begin
  Result := 0;
  if RegQueryDWordValue(HKCU, '{#AsioSettingsKey}', 'ChannelCount', Value) then
    Result := Value
  else if RegQueryDWordValue(HKLM64, '{#AsioSettingsKey}', 'ChannelCount', Value) then
    Result := Value;
end;

function InitialChannelCount: Integer;
var
  Requested: Integer;
begin
  Requested := StrToIntDef(ExpandConstant('{param:CHANNELS|0}'), 0);
  if Requested <= 0 then
    Requested := ExistingChannelCount;
  Result := SanitizeChannelCount(Requested);
end;

function SelectedChannelCount: Integer;
begin
  if MatrixRadio.Checked then
    Result := MatrixChannels
  else if CoconutRadio.Checked then
    Result := CoconutChannels
  else
    Result := (CustomCombo.ItemIndex + 1) * ChannelStep;
  Result := SanitizeChannelCount(Result);
end;

function SelectedChannelCountText(const Param: string): string;
begin
  Result := IntToStr(SelectedChannelCount);
end;

procedure ChannelChoiceClick(Sender: TObject);
begin
  CustomCombo.Enabled := CustomRadio.Checked;
end;

function AddChoice(const Caption: string; Top: Integer): TNewRadioButton;
begin
  Result := TNewRadioButton.Create(ChannelPage);
  Result.Parent := ChannelPage.Surface;
  Result.Caption := Caption;
  Result.Left := 0;
  Result.Top := Top;
  Result.Width := ChannelPage.SurfaceWidth;
  Result.Height := ScaleY(22);
  Result.OnClick := @ChannelChoiceClick;
end;

procedure CreateChannelPage;
var
  Channels, Initial: Integer;
  Note: TNewStaticText;
begin
  ChannelPage := CreateCustomPage(wpSelectTasks, 'Matrix edition',
    'How many channels should the Elka FX Host ASIO driver show in Matrix?');

  MatrixRadio := AddChoice('VB-Audio Matrix: 32 channels (FX In / FX Out A1 to D8)', 0);
  CoconutRadio := AddChoice('VB-Audio Matrix Coconut: 64 channels (A1 to H8)', ScaleY(30));
  CustomRadio := AddChoice('Custom:', ScaleY(60));

  CustomCombo := TNewComboBox.Create(ChannelPage);
  CustomCombo.Parent := ChannelPage.Surface;
  CustomCombo.Style := csDropDownList;
  CustomCombo.Left := ScaleX(18);
  CustomCombo.Top := ScaleY(88);
  CustomCombo.Width := ScaleX(260);
  Channels := ChannelStep;
  while Channels <= MaxChannels do
  begin
    CustomCombo.Items.Add(IntToStr(Channels) + ' channels (A1 to ' + LastBankLetter(Channels) + '8)');
    Channels := Channels + ChannelStep;
  end;

  Note := TNewStaticText.Create(ChannelPage);
  Note.Parent := ChannelPage.Surface;
  Note.Top := ScaleY(132);
  Note.Width := ChannelPage.SurfaceWidth;
  Note.WordWrap := True;
  Note.Caption :=
    'Channels come in banks of 8. Matrix sends audio into "FX In" and receives the processed ' +
    'audio back on "FX Out".' + #13#10#13#10 +
    'You can change this later in FX Host: ASIO Link > ASIO channels.';

  Initial := InitialChannelCount;
  CustomCombo.ItemIndex := (Initial div ChannelStep) - 1;
  if Initial = MatrixChannels then
    MatrixRadio.Checked := True
  else if Initial = CoconutChannels then
    CoconutRadio.Checked := True
  else
    CustomRadio.Checked := True;
  ChannelChoiceClick(nil);
end;

procedure AddBrandLogo(var Logo: TBitmapImage; ParentPage: TNewNotebookPage; LeftEdge: Integer);
begin
  Logo := TBitmapImage.Create(WizardForm);
  Logo.Parent := ParentPage;
  Logo.SetBounds(LeftEdge, ParentPage.Height - ScaleY(120), ScaleX(108), ScaleY(108));
  Logo.Stretch := True;
  Logo.PngImage.LoadFromFile(ExpandConstant('{tmp}\ElkaSoft.png'));
end;

procedure InitializeBranding;
begin
  ExtractTemporaryFile('ElkaSoft.png');
  WizardForm.WelcomeLabel1.Caption := 'Elka FX Host';
  WizardForm.WelcomeLabel1.Height := ScaleY(34);
  WizardForm.WelcomeLabel2.Top := WizardForm.WelcomeLabel1.Top + WizardForm.WelcomeLabel1.Height + ScaleY(12);
  WizardForm.WelcomeLabel2.Caption :=
    'VST hosting and routing behind its own virtual ASIO driver, for VB-Audio Matrix.' + #13#10#13#10 +
    'Version {#AppVersion}  /  Windows x64' + #13#10#13#10 +
    'Setup installs Elka FX Host and the "Elka FX Host" ASIO driver.' + #13#10#13#10 +
    'Close FX Host, and stop the Elka FX Host device in Matrix, before continuing.';
  WizardForm.WelcomeLabel2.Height := ScaleY(176);
  AddBrandLogo(WelcomeLogo, WizardForm.WelcomePage, WizardForm.WelcomeLabel2.Left);
  AddBrandLogo(FinishedLogo, WizardForm.FinishedPage, WizardForm.FinishedLabel.Left);
end;

procedure InitializeWizard;
begin
  InitializeBranding;
  CreateChannelPage;

  PerformanceOptionsPage := CreateInputOptionPage(
    ChannelPage.ID,
    'Performance Options',
    'Disable Windows power throttling',
    'Setup already has administrator approval. Select the applications that Windows should keep at full performance.',
    False,
    False);
  PerformanceOptionsPage.Add('Elka FX Host (recommended)');
  PerformanceOptionsPage.Values[0] := True;

  // Which Matrix is used: Windows power throttling is turned off for that one too.
  MatrixEditionPage := CreateInputOptionPage(
    PerformanceOptionsPage.ID,
    'Performance Options',
    'Are you using VB-Audio Matrix or VB-Audio Matrix Coconut?',
    'Setup turns off Windows power throttling for the one you use, so Windows keeps it at full performance next to FX Host.',
    True,
    False);
  MatrixEditionPage.Add('VB-Audio Matrix');
  MatrixEditionPage.Add('VB-Audio Matrix Coconut');
  MatrixEditionPage.Add('Neither / leave Matrix unchanged');
  MatrixEditionPage.SelectedValueIndex := 2;
end;

function MatrixFolder: string;
begin
  Result := ExpandConstant('{commonpf32}\VB\VBAudioMatrix');
  if not DirExists(Result) then
    Result := ExpandConstant('{commonpf64}\VB\VBAudioMatrix');
end;

function MatrixExecutablePath(Param: string): string;
begin
  Result := AddBackslash(MatrixFolder) + Param;
end;

function MatrixEditionInstalled(Coconut: Boolean): Boolean;
begin
  if Coconut then
    Result := FileExists(MatrixExecutablePath('VBAudioMatrixCoconut_x64.exe')) or
      FileExists(MatrixExecutablePath('VBAudioMatrixCoconut.exe'))
  else
    Result := FileExists(MatrixExecutablePath('VBAudioMatrix_x64.exe')) or
      FileExists(MatrixExecutablePath('VBAudioMatrix.exe'));
end;

// First visit: preselect the edition picked on the channel page (32 = Matrix, 64 = Coconut),
// otherwise whichever one is installed. The user can change it.
procedure DefaultMatrixEdition;
begin
  if MatrixEditionDefaulted then
    exit;
  MatrixEditionDefaulted := True;
  if CoconutRadio.Checked and MatrixEditionInstalled(True) then
    MatrixEditionPage.SelectedValueIndex := 1
  else if MatrixRadio.Checked and MatrixEditionInstalled(False) then
    MatrixEditionPage.SelectedValueIndex := 0
  else if MatrixEditionInstalled(True) then
    MatrixEditionPage.SelectedValueIndex := 1
  else if MatrixEditionInstalled(False) then
    MatrixEditionPage.SelectedValueIndex := 0
  else
    MatrixEditionPage.SelectedValueIndex := 2;
end;

function ShouldDisableMatrixExecutable(const ExecutableName: string): Boolean;
var
  IsCoconut: Boolean;
begin
  IsCoconut := Pos('Coconut', ExecutableName) > 0;
  Result := FileExists(MatrixExecutablePath(ExecutableName)) and
    (((MatrixEditionPage.SelectedValueIndex = 0) and not IsCoconut) or
     ((MatrixEditionPage.SelectedValueIndex = 1) and IsCoconut));
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Channels: Integer;
begin
  if CurPageID = MatrixEditionPage.ID then
    DefaultMatrixEdition;
  if CurPageID = wpFinished then
  begin
    Channels := SelectedChannelCount;
    WizardForm.FinishedHeadingLabel.Caption := 'FX Host is ready';
    WizardForm.FinishedLabel.Caption :=
      'Elka FX Host has been installed.' + #13#10#13#10 +
      'In Matrix, add the ASIO device "Elka FX Host" (' + IntToStr(Channels) +
      ' channels, FX In / FX Out A1 to ' + LastBankLetter(Channels) + '8).' + #13#10#13#10 +
      'Click Finish to close Setup.';
    WizardForm.FinishedLabel.Height := ScaleY(130);
    WizardForm.RunList.Top := WizardForm.FinishedLabel.Top + ScaleY(142);
    WizardForm.RunList.Height := ScaleY(40);
  end;
end;

function ShouldDisableFxHostPowerThrottling: Boolean;
begin
  Result := PerformanceOptionsPage.Values[0];
end;
