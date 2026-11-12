# ISORIM FPS Control Bridge (CE & Vanilla)

Version **0.8.22-vehicle-recovery-r1** · [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3824599915)

A claimed damaged vehicle now enters your faction’s repair list and can be repaired through Vehicle Framework. When a hostile pilot is incapacitated, a helicopter attempts a vertical landing at its current position or a nearby clear spot. Fixed-wing aircraft at or below 15 cells above the surface glide and roll to a stop if the route is clear. Higher aircraft crash and leave a recoverable wreck. Intact aircraft can be claimed after stopping and wrecks after repair. Update both ISORIM FPS Control Bridge and ISORIM 3D Strike Framework for these changes.

认领受损载具后，车辆会加入我方维修名单，可按 Vehicle Framework 的正常流程维修。敌方飞行员失能后，直升机尝试原地垂直降落，原位受阻时寻找附近安全落点。离地 15 格及以内的固定翼在路线安全时滑翔接地并滑跑停稳，高空固定翼会坠毁并留下可修复残骸。完整飞机停稳后可认领，残骸修好后可认领。请同时更新 ISORIM 第一人称控制桥和 ISORIM 立体打击框架。

## Recovery screenshots / 认领与迫降实机图

- [Badger repair / 认领后维修 Badger](Screenshots/01-badger-repair.png)
- [Mosquito emergency landing / Mosquito 就地降落](Screenshots/02-mosquito-landing.png)
- [Warbird glide approach / Warbird 滑翔进近](Screenshots/03-warbird-glide.png)
- [Recoverable Warbird wreck / 可修复的 Warbird 残骸](Screenshots/04-warbird-wreck.png)

Hold Alt to view crew spaces on Vehicle Framework vehicles within 40 cells in 3D. Calibrated models use their measured positions. Other models use the vehicle’s native role hitboxes or an estimate based on the hull and seats. When no crew member can act, a hostile vehicle becomes unowned and ejects surviving occupants. Airborne ejections use the Strike Framework’s parachute and crash sequence.

按住 Alt 查看三维距离 40 格以内的 Vehicle Framework 载具乘员位置。已有实测坐标的车型使用实测值，其余车型使用原生席位格或按车体与席位估算。敌方载具所有乘员失能时变为无主，并弹出仍在舱内的幸存者。空中弹出沿用立体打击框架的跳伞和坠机流程。

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
