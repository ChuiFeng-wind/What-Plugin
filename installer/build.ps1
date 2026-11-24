# 从源码生成 dist\app、dist\VST3 和安装包。用 PowerShell 7 运行：pwsh installer\build.ps1
# -TestRoot 另外生成一个不需要管理员权限的试装版，放在那个文件夹里。
# 1.3.3 起 Windows 版也用跨平台界面（WhatPlugin.Desktop）。-Wpf 打旧界面（WhatPlugin.App），只留一个版本备用。
param(
    [string]$CMake = 'F:\DevTools\cmake\cmake-4.4.4-windows-x86_64\bin\cmake.exe',
    [string]$VsInstance = 'F:/DevTools/VSBuildTools',
    [string]$Vst3Sdk = 'F:\DevTools\vst3sdk',
    [string]$Iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    [string]$TestRoot = '',
    [switch]$Wpf
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# 扫描器和查询插件都只用 VST3 SDK（MIT）和它带的 VSTGUI（BSD）。
# CMake 里反斜杠是转义符，传给它的路径用正斜杠。
$sdk = $Vst3Sdk.Replace('\', '/')
& $CMake -S scanner -B build\scanner -G 'Visual Studio 18 2026' -A x64 "-DCMAKE_GENERATOR_INSTANCE=$VsInstance" "-DVST3_SDK=$sdk" | Out-Null
& $CMake -S plugin -B build\plugin -G 'Visual Studio 18 2026' -A x64 "-DCMAKE_GENERATOR_INSTANCE=$VsInstance" "-Dvst3sdk_SOURCE_DIR=$sdk" | Out-Null
& $CMake --build build\scanner --config Release --parallel | Out-Null
# 编译后会自动跑 SDK 的验证工具，不通过时这一步报错。
& $CMake --build build\plugin --config Release --target WhatPluginLookup --parallel | Out-Null

dotnet test tests\WhatPlugin.Tests -nologo --verbosity quiet
dotnet test tests\WhatPlugin.Desktop.Tests -nologo --verbosity quiet

Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force dist\app, dist\VST3 | Out-Null
$ui = if ($Wpf) { 'WhatPlugin.App' } else { 'WhatPlugin.Desktop' }
foreach ($project in $ui, 'WhatPlugin.Cli') {
    dotnet publish "src\$project" -c Release -r win-x64 --self-contained true -o dist\app -nologo --verbosity quiet `
        -p:SatelliteResourceLanguages=en -p:DebugType=none
}
# 程序名保持 WhatPlugin.exe：快捷方式、安装包、查询插件和用户习惯都认这个名字，数据库和设置也还在原来的位置。
# 程序集名不改（界面资源地址 avares://WhatPlugin.Desktop/ 用的是它），只改启动程序的文件名：
# 启动程序里记的是 WhatPlugin.Desktop.dll，改名不影响启动。
if (-not $Wpf) { Rename-Item dist\app\WhatPlugin.Desktop.exe WhatPlugin.exe }
# -Force：插件文件夹里的 desktop.ini 是隐藏文件，不加时会漏掉（它让文件夹显示插件图标）。
Copy-Item 'build\plugin\VST3\Release\WhatPluginLookup.vst3' dist\VST3 -Recurse -Force

# 成就包单独打包，只要它自己的 dll：WhatPlugin.Core、Avalonia 和图标库都用主程序里的那份。
# 1.3.3 起成就包是 Avalonia 界面，旧界面（-Wpf）加载不了，会提示版本不匹配。
# 成就包不在公开仓库里，没有它的源码时只打不带成就的安装包。
$withAchievements = Test-Path src\WhatPlugin.Achievements\WhatPlugin.Achievements.csproj
if ($withAchievements) {
    dotnet build src\WhatPlugin.Achievements -c Release -o build\achievements-release -nologo --verbosity quiet -p:DebugType=none
    New-Item -ItemType Directory -Force dist\achievements | Out-Null
    Copy-Item build\achievements-release\WhatPlugin.Achievements.dll dist\achievements
}

# 本软件和第三方的授权文本
$licenses = New-Item -ItemType Directory -Force dist\app\licenses
Copy-Item installer\THIRD-PARTY-NOTICES.txt $licenses
Copy-Item LICENSE (Join-Path $licenses 'WhatPlugin-GPL-3.0.txt')
$thirdparty = Join-Path $Vst3Sdk 'vstgui4\vstgui\thirdparty'
$files = @{
    'VST3-SDK.txt'       = Join-Path $Vst3Sdk 'LICENSE.txt'
    'VSTGUI.txt'         = Join-Path $Vst3Sdk 'vstgui4\LICENSE'
    'miniz.txt'          = Join-Path $thirdparty 'miniz\LICENSE'
    'RapidJSON.txt'      = Join-Path $thirdparty 'rapidjson\license.txt'
    # SDK 里的 expat 没带授权文件，用仓库里保存的 MIT 文本。
    'expat.txt'          = 'installer\licenses\expat.txt'
    # SQLitePCLRaw 的包里不带授权文件，它是 Apache-2.0。
    'Apache-2.0.txt'     = 'installer\licenses\Apache-2.0.txt'
    'dotnet.txt'         = 'C:\Program Files\dotnet\LICENSE.txt'
    'dotnet-notices.txt' = 'C:\Program Files\dotnet\ThirdPartyNotices.txt'
}
foreach ($name in $files.Keys) { Copy-Item -LiteralPath $files[$name] (Join-Path $licenses $name) }

# 两个完整安装包：不带成就的，和带成就的；成就包也单独打一份，给已经装了不带成就版的人补装。
& $Iscc /Q installer\WhatPlugin.iss
if ($withAchievements) {
    & $Iscc /Q /DWithAchievements installer\WhatPlugin.iss
    & $Iscc /Q installer\WhatPlugin.Achievements.iss
}
if ($TestRoot) {
    & $Iscc /Q /DTestInstall "/DTestRoot=$TestRoot" installer\WhatPlugin.iss
    if ($withAchievements) { & $Iscc /Q /DTestInstall "/DTestRoot=$TestRoot" installer\WhatPlugin.Achievements.iss }
}
Get-ChildItem dist -File | ForEach-Object { '{0}  {1:N0} MB' -f $_.Name, ($_.Length / 1MB) }
