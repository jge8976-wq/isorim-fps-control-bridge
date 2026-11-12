# ISORIM FPS Control Bridge (CE & Vanilla)

Version **0.8.20-minimap-crew-r1** · [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3824599915)

The built-in and Dub’s Mint minimaps now rotate with the 3D camera. The compass marks north in cyan and Dub’s click positions follow the rotated map. Hold Alt to view cyan crew hit volumes. On foot, tap Alt to toggle them. Includes crew hit volumes for the Warbird and Badger.

内置小地图和 Dub’s 小地图随 3D 镜头转向。指南针用青色标出北方，Dub’s 的点选位置随地图旋转换算。第一人称按住 Alt 查看青色乘员受击体积，步兵视角也可轻按切换。本版包含 Warbird 和 Badger 的乘员位置。

The GitHub Releases page provides an installable ZIP. Extract its mod folder into RimWorld’s Mods directory. See Mod/About/About.xml for requirements.

GitHub Releases 提供可安装压缩包。解压后把模组文件夹放进 RimWorld 的 Mods 目录。

## In-game screenshots / 实机截图

| Dub’s · North / 朝北 | Dub’s · East / 朝东 |
|---|---|
| ![Dub’s facing north](Screenshots/0820-dubs-north.png) | ![Dub’s facing east](Screenshots/0820-dubs-east.png) |

[Built-in minimap / 内置小地图](Screenshots/0820-built-in-minimap.png) · [Badger crew volumes / Badger 乘员体积](Screenshots/0820-alt-crew-volumes.png) · [Warbird pilot volume / Warbird 飞行员体积](Screenshots/0820-alt-warbird.png)

## Build the FPS bridge

```powershell
dotnet build Source/HSK.FpsBridge.csproj -c Release "-p:RimWorldDir=<SteamLibrary>/steamapps/common/RimWorld" "-p:WorkshopDir=<SteamLibrary>/steamapps/workshop/content/294100"
```
