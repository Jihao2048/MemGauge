; 内存挂件（MemGauge）安装包脚本
; 用 Inno Setup 7 编译：ISCC.exe MemGauge.iss
; 生成物：installer\Output\MemGauge-<版本>-setup.exe

#define AppName "内存挂件"
#define AppNameEn "MemGauge"
#define AppVersion "1.0.0"
#define AppPublisher "Jihao2048"
#define AppUrl "https://github.com/Jihao2048/MemGauge"
#define AppExe "MemGauge.exe"

[Setup]
AppId={{8F3C2A54-6B1E-4E0A-9C7D-2A5E4B0C1D73}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppNameEn}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename={#AppNameEn}-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 装在当前用户下，不需要管理员权限（程序本身也是普通权限运行）
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "autostart"; Description: "开机时自动启动（当前用户）"; GroupDescription: "启动选项："
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

; 老版本或便携版可能留了一个同名快捷方式，先删掉再建，避免被锁住沿用旧目标
[InstallDelete]
Type: files; Name: "{autoprograms}\{#AppName}.lnk"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "内存占用环 + 一键释放"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "MemGauge"; ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "立即运行 {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// 安装前先关掉正在运行的挂件：它常驻托盘且不响应 WM_CLOSE，直接 taskkill 最省事
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
  Result := '';
end;
