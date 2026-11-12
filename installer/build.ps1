# 从源码生成 dist\app、dist\VST3 和安装包。用 PowerShell 7 运行：pwsh installer\build.ps1
# -TestRoot 另外生成一个不需要管理员权限的试装版，放在那个文件夹里。
param(
    [string]$CMake = 'F:\DevTools\cmake\cmake-4.4.4-windows-x86_64\bin\cmake.exe',
    [string]$VsInstance = 'F:/DevTools/VSBuildTools',
    [string]$Juce = 'F:\DevTools\JUCE',
    [string]$Iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    [string]$TestRoot = ''
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

foreach ($target in 'scanner', 'plugin') {
    & $CMake -S $target -B "build\$target" -G 'Visual Studio 18 2026' -A x64 "-DCMAKE_GENERATOR_INSTANCE=$VsInstance" | Out-Null
}
& $CMake --build build\scanner --config Release --parallel | Out-Null
& $CMake --build build\plugin --config Release --target WhatPluginLookup_VST3 --parallel | Out-Null

dotnet test tests\WhatPlugin.Tests -nologo --verbosity quiet

Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force dist\app, dist\VST3 | Out-Null
foreach ($project in 'WhatPlugin.App', 'WhatPlugin.Cli') {
    dotnet publish "src\$project" -c Release -r win-x64 --self-contained true -o dist\app -nologo --verbosity quiet `
        -p:SatelliteResourceLanguages=en -p:DebugType=none
}
Copy-Item 'build\plugin\WhatPluginLookup_artefacts\Release\VST3\What Plugin Lookup.vst3' dist\VST3 -Recurse

# 成就包单独打包，只要它自己的 dll：WhatPlugin.Core 用主程序里的那份。
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
$modules = "$Juce\modules"
$files = @{
    'JUCE.txt'         = "$Juce\LICENSE.md"
    'VST3-SDK.txt'     = "$modules\juce_audio_processors_headless\format_types\VST3_SDK\LICENSE.txt"
    'zlib.txt'         = "$modules\juce_core\zip\zlib\LICENSE"
    'libpng.txt'       = "$modules\juce_graphics\image_formats\pnglib\LICENSE"
    'HarfBuzz.txt'     = "$modules\juce_graphics\fonts\harfbuzz\COPYING"
    'SheenBidi.txt'    = "$modules\juce_graphics\unicode\sheenbidi\LICENSE"
    'LunaSVG.txt'      = "$modules\juce_graphics\drawables\lunasvg\LICENSE"
    'PlutoVG.txt'      = "$modules\juce_graphics\drawables\lunasvg\plutovg\LICENSE"
    'FLAC.txt'         = "$modules\juce_audio_formats\codecs\flac\Flac Licence.txt"
    'OggVorbis.txt'    = "$modules\juce_audio_formats\codecs\oggvorbis\Ogg Vorbis Licence.txt"
    'dotnet.txt'       = 'C:\Program Files\dotnet\LICENSE.txt'
    'dotnet-notices.txt' = 'C:\Program Files\dotnet\ThirdPartyNotices.txt'
}
foreach ($name in $files.Keys) { Copy-Item -LiteralPath $files[$name] (Join-Path $licenses $name) }
# SQLitePCLRaw 的包里不带授权文件，它是 Apache-2.0，和 SheenBidi 的文本相同。
Copy-Item -LiteralPath $files['SheenBidi.txt'] (Join-Path $licenses 'Apache-2.0.txt')

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
