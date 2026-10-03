; ============================================================
;  易客服 Windows 客户端 —— 安装包脚本（Inno Setup 6）
;
;  打包流程（也可以直接跑根目录的 build-setup.ps1 一键完成）：
;    1) dotnet publish src\Windowsyikefu.App -c Release -o dist
;    2) ISCC.exe installer\Windowsyikefu.iss
;
;  产物：installer\kefu_setup.exe
; ============================================================

#define AppName "易客服"
#define AppExeName "Windowsyikefu.exe"
#define DistDir "..\dist"

; 版本号由 build-setup.ps1 从 csproj 里读出来传进来（ISCC /DAppVersion=x.y.z）；
; 没传就用手写的默认值，单独编译本脚本时也能跑。
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; AppId 固定不变：以后发布新版时，安装程序会自动覆盖升级而不是装两份
AppId={{7C4F1E92-3B5D-4A18-9E27-2F6B8D41C503}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
; 让安装包右键属性里也能看到版本号
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} 安装程序
VersionInfoCompany={#AppName}
DefaultDirName={autopf}\Windowsyikefu
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\installer
OutputBaseFilename=kefu_setup
SetupIconFile=..\src\Windowsyikefu.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 64 位系统专用，装到 Program Files
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; 安装前自动关掉正在运行的旧版本，避免文件被占用
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
; 英文语言包 + 中文文案（Inno 官方没带简体中文语言文件，这里直接覆盖向导文字）
SetupAppTitle=安装 {#AppName} {#AppVersion}
SetupWindowTitle=安装 {#AppName} {#AppVersion}
WelcomeLabel1=欢迎安装 {#AppName}
WelcomeLabel2=安装程序将在您的电脑上安装 {#AppName} Windows 客户端，并创建开始菜单和桌面快捷方式。%n%n点击「下一步」继续。
SelectDirLabel3=安装程序会把 {#AppName} 安装到下面的文件夹。
SelectDirBrowseLabel=点击「浏览」可以换一个文件夹；点击「下一步」继续。
SelectTasksLabel2=请选择需要一并完成的附加任务，然后点击「下一步」。
ReadyLabel1=一切就绪，可以开始安装了。
ReadyLabel2a=点击「安装」开始安装；想改设置就点「上一步」。
InstallingLabel=正在安装 {#AppName}，请稍候…
FinishedHeadingLabel={#AppName} 安装完成
FinishedLabel=安装程序已完成 {#AppName} 的安装。{enter}现在可以启动它，登录您的客服账号开始使用。
FinishedLabelNoIcons=安装程序已完成 {#AppName} 的安装。
ClickFinish=点击「完成」结束安装。
ButtonNext=下一步(&N) >
ButtonBack=< 上一步(&B)
ButtonInstall=安装(&I)
ButtonCancel=取消
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)…
ButtonYes=是(&Y)
ButtonNo=否(&N)
ConfirmUninstall=确定要卸载 {#AppName} 吗？%n%n（您的账号配置会保留在用户目录里，重新安装后仍然可用。）
UninstallAppFullTitle=卸载 {#AppName}
UninstallStatusLabel=正在卸载 {#AppName}，请稍候…
UninstalledAll={#AppName} 已成功卸载。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "{#DistDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 注册 easykefu:// 协议：这样网站后台的绑定页点「打开桌面端」能直接唤起客户端
Root: HKA; Subkey: "Software\Classes\easykefu"; ValueType: string; ValueName: ""; ValueData: "URL:easykefu Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\easykefu"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\easykefu\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKA; Subkey: "Software\Classes\easykefu\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清理安装目录里的残留，用户配置（%AppData%\Windowsyikefu）保留，重装后还能用
Type: filesandordirs; Name: "{app}\logs"
