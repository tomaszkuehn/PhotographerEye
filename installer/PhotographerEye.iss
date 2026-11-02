; PhotographerEye installer.
; Build (Win10/11): iscc installer\PhotographerEye.iss
; Build (Win7):     iscc installer\PhotographerEye.iss /DWin7
; Expects publish output in dist (Win10/11, net8) or dist-win7 (net6, no GPU)
; produced by installer\build.ps1. Everything is packed into the installer,
; nothing is downloaded during installation.

#define AppName "PhotographerEye"
#define AppVersion "1.00"
#define AppId "{5540f35b-a8c9-4f5a-8093-641f77030569}"
#define RootDir RemoveBackslash(AddBackslash(SourcePath) + "..")

#ifndef Win7
#define DistDir "dist"
#define SetupName AppName + "-Setup-Win10-11-" + AppVersion
#else
#define DistDir "dist-win7"
#define SetupName AppName + "-Setup-Win7-" + AppVersion
#endif

[Setup]
AppId={{#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Tomasz Kuehn
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
OutputDir={#RootDir}\{#DistDir}\installer
OutputBaseFilename={#SetupName}
SetupIconFile={#RootDir}\Assets\icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed={#ifndef Win7}x64compatible{#else}x64{#endif}
ArchitecturesInstallIn64BitMode={#ifndef Win7}x64compatible{#else}x64{#endif}
UninstallDisplayIcon={app}\{#AppName}.exe
ChangesAssociations=yes
#ifndef Win7
MinVersion=10.0.14393
#else
MinVersion=6.1sp1
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#RootDir}\installer\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "{#RootDir}\{#DistDir}\PhotographerEye.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RootDir}\{#DistDir}\plugins\*"; DestDir: "{app}\plugins"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#RootDir}\{#DistDir}\thirdparty\*"; DestDir: "{app}\thirdparty"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: filesandordirs; Name: "{app}\thirdparty"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppName}.exe"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppName}.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Registry]
Root: HKLM; Subkey: "Software\Classes\.heic"; ValueType: string; ValueName: ""; ValueData: "PhotographerEye.heic"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\PhotographerEye.heic"; ValueType: string; ValueName: ""; ValueData: "{#AppName} Image"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\PhotographerEye.heic\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\PhotographerEye.exe,0"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\PhotographerEye.heic\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PhotographerEye.exe"" ""%1"""; Flags: uninsdeletekey

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Instalowanie bibliotek Visual C++..."; Check: NeedVCRedist
Filename: "{app}\PhotographerEye.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function NeedVCRedist: Boolean;
var
  Value: Cardinal;
begin
  Result := not (RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Value) and (Value = 1));
end;