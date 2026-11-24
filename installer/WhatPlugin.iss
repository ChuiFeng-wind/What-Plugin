; WhatPlugin 安装包。由 installer\build.ps1 调用。
; /DTestInstall 生成试装版：不要管理员权限，所有文件都装到 TestRoot，不碰系统目录。

#define AppName "WhatPlugin"
#define AppVersion "1.3.2"
#define Vst3Name "WhatPluginLookup.vst3"
#define LegacyVst3Name "PluginVault Lookup.vst3"
; 1.2.6 及以前名字里带空格：插件、安装目录、快捷方式都叫 "What Plugin…"，升级时清理掉。
#define OldVst3Name "What Plugin Lookup.vst3"
#define OldDirName "What Plugin"
#define MainAppId "{7E3C1B52-9A44-4F0E-8C21-5D6A0F7B91C3}"
#define AchievementsAppId "{4C6F0B9E-2D71-4A8B-9E35-8B1D7F2A6C40}"

#ifdef TestInstall
  #define Vst3Folder TestRoot + "\VST3"
#else
  #define Vst3Folder "{commoncf64}\VST3"
#endif

[Setup]
AppId={{7E3C1B52-9A44-4F0E-8C21-5D6A0F7B91C3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
UninstallDisplayName={#AppName}
; 快捷方式和“应用和功能”用单独的 ico 文件：exe 路径不变时，Windows 会一直用缓存里的旧图标。
UninstallDisplayIcon={app}\WhatPlugin.ico
SetupIconFile=..\src\WhatPlugin.App\app.ico
WizardSmallImageFile=wizard-small-55.bmp,wizard-small-64.bmp,wizard-small-69.bmp,wizard-small-83.bmp,wizard-small-92.bmp,wizard-small-110.bmp
; 装完通知资源管理器刷新图标。
ChangesAssociations=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
; 默认位置由 DefaultAppDir 决定：老用户装在带空格的 "What Plugin" 里时，换到同一位置下的 "WhatPlugin"。
UsePreviousAppDir=no
DefaultDirName={code:DefaultAppDir}
#ifdef TestInstall
PrivilegesRequired=lowest
OutputDir={#TestRoot}
OutputBaseFilename=WhatPlugin-Setup-Test
#else
; VST3 插件要装到系统目录，所以需要管理员权限。
PrivilegesRequired=admin
OutputDir=..\dist
#ifdef WithAchievements
OutputBaseFilename=WhatPlugin-Setup-{#AppVersion}-with-Achievements
#else
OutputBaseFilename=WhatPlugin-Setup-{#AppVersion}
#endif
#endif

[Languages]
Name: "zh"; MessagesFile: "compiler:Default.isl"

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=0
DialogFontName=Microsoft YaHei UI
DialogFontSize=9

[Messages]
SetupAppTitle=安装程序
SetupWindowTitle=安装 %1
UninstallAppTitle=卸载
UninstallAppFullTitle=卸载 %1
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonYesToAll=全部是(&A)
ButtonNo=否(&N)
ButtonNoToAll=全部否(&O)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolderName=新建文件夹
ExitSetupTitle=退出安装
ExitSetupMessage=安装还没有完成。现在退出的话，软件不会被安装。%n%n确定要退出吗？
WelcomeLabel1=欢迎安装 [name]
#ifdef WithAchievements
WelcomeLabel2=这个程序会把 [name/ver] 安装到你的电脑上，已经包含成就包，装好后左侧有“成就”页面。%n%n安装前请先关闭 Cubase、Studio One 等宿主软件。%n%n以前用过 PluginVault 的话，备注和账号等数据会自动带过来，旧数据不会被删除。
#else
WelcomeLabel2=这个程序会把 [name/ver] 安装到你的电脑上。%n%n安装前请先关闭 Cubase、Studio One 等宿主软件。%n%n以前用过 PluginVault 的话，备注和账号等数据会自动带过来，旧数据不会被删除。
#endif
ClickNext=点“下一步”继续，点“取消”退出。
WizardSelectDir=选择安装位置
SelectDirDesc=要把 [name] 安装到哪里？
SelectDirLabel3=[name] 会安装到下面的文件夹。
SelectDirBrowseLabel=点“下一步”继续。要换位置请点“浏览”。
DiskSpaceMBLabel=至少需要 [mb] MB 磁盘空间。
DiskSpaceGBLabel=至少需要 [gb] GB 磁盘空间。
BrowseDialogTitle=选择文件夹
BrowseDialogLabel=在下面的列表中选择一个文件夹，然后点“确定”。
NewFolderName=新建文件夹
WizardSelectTasks=选择附加内容
SelectTasksDesc=还要安装哪些内容？
SelectTasksLabel2=选好后点“下一步”。
WizardReady=准备安装
ReadyLabel1=现在可以开始安装 [name] 了。
ReadyLabel2a=点“安装”开始，要修改请点“上一步”。
ReadyLabel2b=点“安装”开始。
ReadyMemoDir=安装位置：
ReadyMemoTasks=附加内容：
WizardPreparing=正在准备安装
PreparingDesc=正在准备把 [name] 安装到你的电脑上。
ApplicationsFound=下面的程序正在使用要更新的文件。建议让安装程序自动关闭它们。
ApplicationsFound2=下面的程序正在使用要更新的文件。建议让安装程序自动关闭它们。安装完成后会尝试重新打开。
CloseApplications=自动关闭这些程序(&A)
DontCloseApplications=不关闭这些程序(&D)
WizardInstalling=正在安装
InstallingLabel=正在把 [name] 安装到你的电脑上，请稍候。
FinishedHeadingLabel=[name] 安装完成
FinishedLabelNoIcons=[name] 已经安装好了。
FinishedLabel=[name] 已经安装好了，可以从开始菜单打开。
ClickFinish=点“完成”退出。
RunEntryExec=打开 %1
StatusClosingApplications=正在关闭程序...
StatusCreateDirs=正在创建文件夹...
StatusExtractFiles=正在复制文件...
StatusCreateIcons=正在创建快捷方式...
StatusSavingUninstall=正在保存卸载信息...
StatusRunProgram=正在完成安装...
StatusRollback=正在撤销更改...
WinVersionTooLowError=这个软件需要 %1 %2 或更高版本的系统。
ConfirmUninstall=确定要卸载 %1 吗？你的备注、账号等数据会保留在电脑上。
UninstallStatusLabel=正在卸载 %1，请稍候。
UninstalledAll=%1 已经卸载。
UninstalledMost=%1 已经卸载。有些文件没能删除，可以手动删除。
WizardUninstalling=卸载状态
StatusUninstalling=正在卸载 %1...

[Tasks]
Name: "vst3"; Description: "安装 DAW 内查询窗口（VST3 插件 WhatPluginLookup）"
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[InstallDelete]
; 只删除这个软件自己的旧插件。名字和新插件不同但 ID 相同，留着 DAW 里会出现冲突。
Type: filesandordirs; Name: "{#Vst3Folder}\{#LegacyVst3Name}"; Tasks: vst3; Check: InstallVst3
Type: filesandordirs; Name: "{#Vst3Folder}\{#OldVst3Name}"; Tasks: vst3; Check: InstallVst3
; 1.3.2 及以前旧界面（WPF）自己的文件。1.3.3 起 WhatPlugin.exe 启动的是 WhatPlugin.Desktop.dll，这几个不再用到。
Type: files; Name: "{app}\WhatPlugin.dll"
Type: files; Name: "{app}\WhatPlugin.deps.json"
Type: files; Name: "{app}\WhatPlugin.runtimeconfig.json"
#ifndef TestInstall
; 旧名字的快捷方式，指向的旧目录升级后会删掉。
Type: files; Name: "{autoprograms}\{#OldDirName}.lnk"
Type: files; Name: "{autodesktop}\{#OldDirName}.lnk"
#endif

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\WhatPlugin.App\app.ico"; DestDir: "{app}"; DestName: "WhatPlugin.ico"; Flags: ignoreversion
#ifdef WithAchievements
; 带成就的完整版：成就包和主程序一起装，卸载主程序时一起删掉。
Source: "..\dist\achievements\WhatPlugin.Achievements.dll"; DestDir: "{app}\Extensions\Achievements"; Flags: ignoreversion
#else
; 从旧目录搬家时，把单独装的成就包一起带到新目录，不用重装。
Source: "{code:OldAchievementsDll}"; DestDir: "{app}\Extensions\Achievements"; Flags: external skipifsourcedoesntexist ignoreversion; Check: MoveAchievements
#endif
Source: "..\dist\VST3\{#Vst3Name}\*"; DestDir: "{#Vst3Folder}\{#Vst3Name}"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: vst3; Check: InstallVst3

[Icons]
#ifndef TestInstall
Name: "{autoprograms}\WhatPlugin"; Filename: "{app}\WhatPlugin.exe"; IconFilename: "{app}\WhatPlugin.ico"
Name: "{autodesktop}\WhatPlugin"; Filename: "{app}\WhatPlugin.exe"; IconFilename: "{app}\WhatPlugin.ico"; Tasks: desktopicon
#endif

[Run]
#ifndef TestInstall
; 刷新当前用户的图标缓存，桌面和开始菜单换成新图标。
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden nowait skipifdoesntexist runasoriginaluser
; 必须以普通用户身份运行，否则数据会写到管理员账号下。
Filename: "{app}\WhatPlugin.exe"; Description: "打开 WhatPlugin"; Flags: nowait postinstall skipifsilent runasoriginaluser
#endif

[UninstallDelete]
Type: filesandordirs; Name: "{#Vst3Folder}\{#Vst3Name}"
Type: filesandordirs; Name: "{#Vst3Folder}\{#OldVst3Name}"

[Code]
// DAW 打开着、并且加载过 WhatPluginLookup 时，Windows 不允许删除或覆盖这个 .vst3 文件，
// 复制到一半会弹“DeleteFile failed; code 5. 拒绝访问”。安装程序自带的“关闭正在使用的程序”
// 只检查 .exe/.dll，不认 .vst3，所以开始复制之前自己检查一次。
var
  SkipVst3: Boolean;
  PrevDir: String;   // 上次安装的位置，第一次安装为空
  MoveFrom: String;  // 要搬走的旧目录（带空格的 "What Plugin"），不用搬时为空

function InstallVst3(): Boolean;
begin
  Result := not SkipVst3;
end;

// 能以独占方式打开写入，说明没有程序在用它。文件不存在也算没在用。
function FileInUse(const Path: String): Boolean;
var
  Stream: TFileStream;
begin
  Result := False;
  if not FileExists(Path) then
    exit;
  try
    Stream := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive);
    Stream.Free;
  except
    Result := True;
  end;
end;

function Vst3InUse(): Boolean;
begin
  Result := FileInUse(ExpandConstant('{#Vst3Folder}\{#Vst3Name}\Contents\x86_64-win\{#Vst3Name}'))
    or FileInUse(ExpandConstant('{#Vst3Folder}\{#OldVst3Name}\Contents\x86_64-win\{#OldVst3Name}'))
    or FileInUse(ExpandConstant('{#Vst3Folder}\{#LegacyVst3Name}\Contents\x86_64-win\{#LegacyVst3Name}'));
end;

// 某个软件登记的安装位置，从它的卸载信息里读。
function InstalledDir(const AppId: String): String;
var
  Key: String;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + AppId + '_is1';
#ifdef TestInstall
  if not RegQueryStringValue(HKCU, Key, 'InstallLocation', Result) then
    Result := '';
#else
  if not RegQueryStringValue(HKLM64, Key, 'InstallLocation', Result) then
    if not RegQueryStringValue(HKLM, Key, 'InstallLocation', Result) then
      Result := '';
#endif
  Result := RemoveBackslashUnlessRoot(Result);
end;

// 只认 1.2.6 及以前的默认目录：文件夹名正好是 "What Plugin"，里面有主程序。
function IsOldDir(const Dir: String): Boolean;
begin
  Result := (Dir <> '') and SameText(ExtractFileName(Dir), '{#OldDirName}') and FileExists(Dir + '\WhatPlugin.exe');
end;

function IsUnder(const Path, Dir: String): Boolean;
begin
  Result := (Path <> '') and (Pos(Lowercase(AddBackslash(Dir)), Lowercase(AddBackslash(Path))) = 1);
end;

function NewAppDir(): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
end;

function InitializeSetup(): Boolean;
begin
  PrevDir := InstalledDir('{#MainAppId}');
  Result := True;
end;

function DefaultAppDir(Param: String): String;
begin
  if IsOldDir(PrevDir) then
    Result := AddBackslash(ExtractFileDir(PrevDir)) + '{#AppName}'
  else if PrevDir <> '' then
    Result := PrevDir
  else
#ifdef TestInstall
    Result := '{#TestRoot}\{#AppName}';
#else
    Result := ExpandConstant('{autopf}\{#AppName}');
#endif
end;

function OldAchievementsDll(Param: String): String;
begin
  Result := MoveFrom + '\Extensions\Achievements\WhatPlugin.Achievements.dll';
end;

function MoveAchievements(): Boolean;
begin
  Result := (MoveFrom <> '') and FileExists(OldAchievementsDll(''));
end;

// 旧目录里单独装的成就包：文件已经带到新目录，删掉它在“应用和功能”里的旧登记。
procedure ForgetOldAchievements();
var
  Key: String;
begin
  if not IsUnder(InstalledDir('{#AchievementsAppId}'), MoveFrom) then
    exit;
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AchievementsAppId}_is1';
#ifdef TestInstall
  RegDeleteKeyIncludingSubkeys(HKCU, Key);
#else
  RegDeleteKeyIncludingSubkeys(HKLM64, Key);
  RegDeleteKeyIncludingSubkeys(HKLM, Key);
#endif
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    MoveFrom := '';
    if IsOldDir(PrevDir) and not SameText(PrevDir, NewAppDir()) then
      MoveFrom := PrevDir;
  end
  else if (CurStep = ssPostInstall) and (MoveFrom <> '') then
  begin
    ForgetOldAchievements();
    // 再确认一次，只删旧的 "What Plugin" 程序目录。数据在 %LOCALAPPDATA%\WhatPlugin，不在这里。
    if IsOldDir(MoveFrom) then
      DelTree(MoveFrom, True, True, True);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Answer: Integer;
begin
  Result := '';
  SkipVst3 := False;
  // 搬家前旧目录里的主程序必须关掉，否则旧文件删不掉。
  if IsOldDir(PrevDir) and not SameText(PrevDir, NewAppDir()) then
    while FileInUse(PrevDir + '\WhatPlugin.exe') do
      if SuppressibleMsgBox('WhatPlugin 正在运行，请先关闭它，然后点“重试”。' + #13#10 + #13#10 +
           '这个版本把安装位置从“' + PrevDir + '”换成了不带空格的“' + NewAppDir() + '”，' +
           '要先关闭旧版本才能搬过去。你的数据不受影响。',
           mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
      begin
        Result := '安装已取消。关闭 WhatPlugin 后重新运行安装程序即可。';
        exit;
      end;
  if not WizardIsTaskSelected('vst3') then
    exit;
  while Vst3InUse() do
  begin
    Answer := SuppressibleMsgBox(
      'DAW 内查询插件 WhatPluginLookup 正在被宿主软件使用（比如开着的 Cubase、Studio One），暂时不能更新。' + #13#10 + #13#10 +
      '请先保存工程、关闭宿主软件，然后：' + #13#10 +
      '  点“重试”：重新检查，继续安装；' + #13#10 +
      '  点“忽略”：这次不更新查询插件，其余照常安装（查询插件保留旧版本）；' + #13#10 +
      '  点“中止”：退出安装。',
      mbError, MB_ABORTRETRYIGNORE, IDIGNORE);
    if Answer = IDIGNORE then
    begin
      SkipVst3 := True;
      exit;
    end;
    if Answer = IDABORT then
    begin
      Result := '安装已取消。关闭宿主软件后重新运行安装程序即可。';
      exit;
    end;
  end;
end;
