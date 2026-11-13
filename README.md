# ISORIM FPS Control Bridge (CE & Vanilla)

Version **0.8.26-combat-hud-r2** · [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3824599915)

新增第一人称紧凑武器卡、多武器挂载条和默认 K 键展开的座舱状态图。装填时显示真实进度，小地图显示爆炸武器预计落点。修复敌机重复进入地面待命任务。敌方 AI 和玩家共用默认 60% 的飞行灵活性。火焰损伤部件，泡沫可扑灭载具上的火焰。寻路连通性每批只更新一次受影响的邻格。请同时更新两个 ISORIM 模组。

Adds compact first-person weapon cards, an aircraft and vehicle weapon rail, and a cockpit status panel bound to K by default. Reload bars appear during loading. The minimaps mark projected explosive impacts. Enemy aircraft keep native jobs without re-entering ground job loops. Air handling defaults to 60% for both player and AI. Fire drains component durability and can be extinguished with foam. Path connectivity now updates each affected neighbor once per batch. Update both ISORIM mods together.

GitHub Releases provides an installable ZIP. Extract its mod folder into RimWorld’s Mods directory.

## In-game screenshots / 实机截图

- [Compact weapon card / 紧凑武器卡](Screenshots/armed-hud.png)
- [Weapon rail and cockpit status / 挂载条与座舱状态](Screenshots/cockpit-hud.png)
- [Infantry first-person weapon card / 人物第一人称武器卡](Screenshots/infantry-hud.png)
- [Mortar reloading in first person / 第一人称迫击炮装填](Screenshots/turret-reload-hud.png)

## Build the FPS bridge

```powershell
dotnet build Source/HSK.FpsBridge.csproj -c Release "-p:RimWorldDir=<SteamLibrary>/steamapps/common/RimWorld" "-p:WorkshopDir=<SteamLibrary>/steamapps/workshop/content/294100"
```
