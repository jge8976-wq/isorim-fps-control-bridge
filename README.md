# ISORIM FPS Control Bridge (CE & Vanilla)

Version **0.8.19-formation-raids-r1** · [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3824599915)

Reduces collision work between separated aircraft. Includes original-texture held models and a shared optic for weapons without a dedicated first-person model.

相距较远的飞行器跳过精确碰撞检测。缺少专用模型的武器使用原贴图加厚手持外形和通用瞄具。

The GitHub Releases page provides an installable ZIP. Extract its mod folder into RimWorld's Mods directory. See Mod/About/About.xml for requirements.

GitHub Releases 提供可安装压缩包。解压后把模组文件夹放进 RimWorld 的 Mods 目录。

## Build the FPS bridge

```powershell
dotnet build Source/HSK.FpsBridge.csproj -c Release "-p:RimWorldDir=<SteamLibrary>/steamapps/common/RimWorld" "-p:WorkshopDir=<SteamLibrary>/steamapps/workshop/content/294100"
```
