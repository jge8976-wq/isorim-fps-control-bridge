# ISORIM FPS Control Bridge (CE & Vanilla)

Version **0.8.18-held-sprite-r1** · [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3824599915)

The FPS release adds original-texture held models and a shared optic for guns without dedicated 3D assets. The current Steam Workshop release is 0.8.17.

缺专用模型的武器用原贴图加厚显示，右键统一使用通用瞄具。工坊当前为 0.8.17。

The GitHub Releases page provides an installable ZIP. Extract its mod folder into RimWorld's Mods directory. See Mod/About/About.xml for requirements.

GitHub Releases 提供可安装压缩包。解压后把模组文件夹放进 RimWorld 的 Mods 目录。

## Build the FPS bridge

```powershell
dotnet build Source/HSK.FpsBridge.csproj -c Release "-p:RimWorldDir=<SteamLibrary>/steamapps/common/RimWorld" "-p:WorkshopDir=<SteamLibrary>/steamapps/workshop/content/294100"
```
