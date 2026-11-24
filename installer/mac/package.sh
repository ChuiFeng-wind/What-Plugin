#!/bin/bash
# 把跨平台界面打成 WhatPlugin.app，再装进 .dmg。只能在 Mac 上运行（要用 sips、iconutil、codesign、hdiutil）。
#
# 用法：installer/mac/package.sh <osx-arm64|osx-x64> <输出文件夹>
# 前提：扫描器已经编译好（build/scanner/WhatPluginScanner_artefacts/Release/WhatPluginScanner），
#       发布时会自动带进 .app；DAW 内查询插件已经编译好（build/plugin/VST3/Release/WhatPluginLookup.vst3，
#       可用环境变量 LOOKUP_PLUGIN 指定别的位置）；环境变量 VST3_SDK 指向 VST3 SDK，从里面取它的授权文本。
#
# 没有 Apple 开发者证书，只做临时签名（ad-hoc）。用户第一次打开要在“隐私与安全性”里允许，见 打开说明.txt。
set -euo pipefail

rid=${1:?要给出 osx-arm64 或 osx-x64}
out=${2:?要给出输出文件夹}
case "$rid" in
    osx-arm64) arch=arm64 ;;
    osx-x64) arch=x64 ;;
    *) echo "不认识的平台：$rid" >&2; exit 2 ;;
esac
: "${VST3_SDK:?要设置 VST3_SDK}"

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/WhatPlugin.Desktop/WhatPlugin.Desktop.csproj" | head -1)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
app="$work/dmg/WhatPlugin.app"
mkdir -p "$out" "$app/Contents/MacOS" "$app/Contents/Resources"

echo "== 发布 ${rid}（版本 ${version}）"
dotnet publish "$root/src/WhatPlugin.Desktop" -c Release -r "$rid" --self-contained true -o "$work/publish" -nologo
test -x "$work/publish/WhatPlugin.Desktop"
test -x "$work/publish/WhatPluginScanner" || { echo "发布结果里没有扫描器，先编译 scanner" >&2; exit 1; }
# 命令行工具放在主程序旁边：DAW 里的查询插件调用它读数据。
dotnet publish "$root/src/WhatPlugin.Cli" -c Release -r "$rid" --self-contained true -o "$work/publish" -nologo
test -x "$work/publish/WhatPlugin.Cli"
cp -R "$work/publish/." "$app/Contents/MacOS/"

sed "s/__VERSION__/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"

echo "== 图标"
iconset="$work/WhatPlugin.iconset"
mkdir -p "$iconset"
# 原图是 256 像素，不放大到 512，免得发虚。
for size in 16 32 128 256; do
    sips -z $size $size "$here/AppIcon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
done
for size in 16 32 128; do
    double=$((size * 2))
    sips -z $double $double "$here/AppIcon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/WhatPlugin.icns"

echo "== 授权文本"
licenses="$app/Contents/Resources/licenses"
mkdir -p "$licenses"
nuget=${NUGET_PACKAGES:-$HOME/.nuget/packages}
# 取还原下来的那个版本（CI 上每个包只还原项目引用的那一个版本）。
package() { ls -d "$nuget/$1"/*/ | tail -1; }
dotnet_root=${DOTNET_ROOT:-$(dirname "$(command -v dotnet)")}
cp "$here/../THIRD-PARTY-NOTICES.txt" "$licenses/"
cp "$root/LICENSE" "$licenses/WhatPlugin-GPL-3.0.txt"
cp "$here/../licenses/expat.txt" "$here/../licenses/Apache-2.0.txt" "$here/../licenses/MIT.txt" "$licenses/"
cp "$VST3_SDK/LICENSE.txt" "$licenses/VST3-SDK.txt"
cp "$VST3_SDK/vstgui4/LICENSE" "$licenses/VSTGUI.txt"
cp "$VST3_SDK/vstgui4/vstgui/thirdparty/miniz/LICENSE" "$licenses/miniz.txt"
cp "$VST3_SDK/vstgui4/vstgui/thirdparty/rapidjson/license.txt" "$licenses/RapidJSON.txt"
cp "$(package skiasharp.nativeassets.macos)LICENSE.txt" "$licenses/SkiaSharp.txt"
cp "$(package skiasharp.nativeassets.macos)THIRD-PARTY-NOTICES.txt" "$licenses/SkiaSharp-third-party.txt"
cp "$(package harfbuzzsharp.nativeassets.macos)LICENSE.txt" "$licenses/HarfBuzzSharp.txt"
cp "$(package harfbuzzsharp.nativeassets.macos)THIRD-PARTY-NOTICES.txt" "$licenses/HarfBuzzSharp-third-party.txt"
cp "$dotnet_root/LICENSE.txt" "$licenses/dotnet.txt"
cp "$dotnet_root/ThirdPartyNotices.txt" "$licenses/dotnet-notices.txt"

echo "== DAW 内查询插件"
lookup=${LOOKUP_PLUGIN:-$root/build/plugin/VST3/Release/WhatPluginLookup.vst3}
test -d "$lookup" || { echo "没有找到查询插件 $lookup，先编译 plugin" >&2; exit 1; }
binary="$lookup/Contents/MacOS/WhatPluginLookup"
lipo -archs "$binary" | grep -q arm64
lipo -archs "$binary" | grep -q x86_64
# 用户在设置里点“安装”时，从这里复制到 ~/Library/Audio/Plug-Ins/VST3。
cp -R "$lookup" "$app/Contents/Resources/"
codesign --force --deep --sign - "$app/Contents/Resources/WhatPluginLookup.vst3"

echo "== 签名"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict --verbose=2 "$app"

echo "== 打包 .dmg"
cp "$here/打开说明.txt" "$work/dmg/"
ln -s /Applications "$work/dmg/Applications"
dmg="$out/WhatPlugin-$version-mac-$arch.dmg"
rm -f "$dmg"
hdiutil create -volname "WhatPlugin $version" -srcfolder "$work/dmg" -fs HFS+ -format UDZO -ov "$dmg" >/dev/null
hdiutil verify "$dmg" >/dev/null
echo "已生成 $dmg"
