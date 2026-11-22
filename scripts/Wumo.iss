[Setup]
AppId={{074404EB-F383-4361-B23C-70E364F57CBB}
AppName=Wumo
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
SetupArchitecture=x64
ArchitecturesAllowed=x64os
MinVersion=10.0
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Wumo
DefaultGroupName=Wumo
DisableProgramGroupPage=yes
SetupIconFile=..\src\PixelThumbs.App\pixelthumbs.ico
UninstallDisplayIcon={app}\PixelThumbs.exe
OutputBaseFilename=Wumo-{#AppVersion}-Setup-x64
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.RuntimeFailed=Не удалось установить .NET Desktop Runtime 10 (x64). Код ошибки: %1.
english.RuntimeFailed=Could not install .NET Desktop Runtime 10 (x64). Error code: %1.
russian.UnregisterFailed=Не удалось отключить интеграцию Wumo с Проводником. Закройте Wumo в трее и повторите удаление.
english.UnregisterFailed=Could not remove Wumo's Explorer integration. Close Wumo in the system tray and retry.

[Files]
Source: "{#DesktopRuntime}"; DestName: "windowsdesktop-runtime-x64.exe"; Flags: dontcopy nocompression
Source: "{#PackageDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs solidbreak

[Icons]
Name: "{group}\Wumo"; Filename: "{app}\PixelThumbs.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\PixelThumbs.exe"; Description: "{cm:LaunchProgram,Wumo}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[Code]
var
  RuntimeNeedsRestart: Boolean;

function HasNet10Framework(const Framework: String): Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  if not RegGetValueNames(HKLM32,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\' + Framework, Versions) then
    Exit;
  for Index := 0 to GetArrayLength(Versions) - 1 do
    if (Copy(Versions[Index], 1, 5) = '10.0.') and
       (StrToIntDef(Copy(Versions[Index], 6, Length(Versions[Index])), -1) >= 0) then
    begin
      Result := True;
      Exit;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RuntimeFile: String;
  ExitCode: Integer;
begin
  Result := '';
  if HasNet10Framework('Microsoft.WindowsDesktop.App') and HasNet10Framework('Microsoft.NETCore.App') then
  begin
    Log('Microsoft .NET 10 Desktop Runtime x64 is already installed.');
    Exit;
  end;

  ExtractTemporaryFile('windowsdesktop-runtime-x64.exe');
  RuntimeFile := ExpandConstant('{tmp}\windowsdesktop-runtime-x64.exe');
  if not ShellExec('runas', RuntimeFile, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ExitCode)])
  else if (ExitCode = 3010) or (ExitCode = 1641) then
    RuntimeNeedsRestart := True
  else if ExitCode <> 0 then
    Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ExitCode)]);
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeNeedsRestart;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppExe: String;
  ExitCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  AppExe := ExpandConstant('{app}\PixelThumbs.exe');
  if FileExists(AppExe) then
    if not Exec(AppExe, '--unregister-installed', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) or
       (ExitCode <> 0) then
    begin
      SuppressibleMsgBox(CustomMessage('UnregisterFailed'), mbError, MB_OK, IDOK);
      Abort;
    end;
end;
