# ISORIM FPS Control Bridge (CE & Vanilla)

Version **0.8.23-air-raid-feedback-r1** · [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3824599915)

飞机受击火花显示在真实三维接触点。直射防空弹跳过该发武器的操作员。配合立体打击框架可使用新的坠机楼板处理、空袭警报、5.56 毫米轻机枪平衡和编队飞行调整。

Aircraft impact flashes use the actual 3D contact point. Direct anti-air fire skips the operator of that shot. Pair with the Strike Framework for deck-aware crashes, the new siren, light 5.56 mm weapon tuning and formation flight updates.

GitHub Releases provides an installable ZIP. Extract its mod folder into RimWorld’s Mods directory.

## In-game screenshots / 实机截图

- [Four flights at 12, 18, 24, and 30 cells / 12、18、24、30 格错层进场](Screenshots/01-formation-12-18-24-30.png)
- [Fixed-wing strafing run / 固定翼俯冲扫射](Screenshots/02-strafing.png)
- [Wreck on the second-floor plasteel deck / 残骸停在二楼玻璃钢楼板](Screenshots/03-wreck-upper-floor.png)

## Build the FPS bridge

```powershell
dotnet build Source/HSK.FpsBridge.csproj -c Release "-p:RimWorldDir=<SteamLibrary>/steamapps/common/RimWorld" "-p:WorkshopDir=<SteamLibrary>/steamapps/workshop/content/294100"
```
