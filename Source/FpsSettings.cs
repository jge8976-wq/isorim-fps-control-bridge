using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public sealed class FpsSettings : ModSettings
    {
        public float gravity = 1f, speed = 1f, tracerVisibility = .4f;
        public float colonyAircraftSpeed = .6f, airToGroundTracer = 1.5f, groundToAirTracer = 1.5f;
        public float aircraftAgility = .6f;
        public float antiAirLead = 1f, missileTurn = 1f, shootingSkillInfluence = 1f;
        public float manualTurretDispersion = .6f;
        public bool showAircraftRanges = true;
        public bool showAntiAirLead = true, showTurretSphere = true, ceFreeFire = true, ceAiBurstTracking = true;
        public bool reinforceCrackedPlasteel;
        public bool turretTracers = true;
        public int turretAmmoRadius = 2;
        public float turretAutoReloadFraction = .2f;
        public int explosionQuality = 2;
        public float lookSensitivityMultiplier = LookSensitivityRules.Default, aimSensitivityMultiplier = LookSensitivityRules.Default;
        public bool showHints = true, showPawnHud = true, showMinimap = true, showIff = true;
        public bool showAbilityBar = true, abilityNumberKeys = true;
        public bool keywordWeaponModels = true;
        public bool firstPersonWeaponOutlines;
        public bool readableHeldWeapons=true;
        public bool descentAnimation = true, combatMusic = true, musicStopWithSkills = false;
        public bool descentProfiles = true, descentLandingShake = true, combatMusicDuckAmbient = true;
        public bool firstPersonGuideShown;
        public float descentSeconds = 1.5f, combatMusicVolume = .75f, combatMusicSeconds = 60f, combatMusicOffset = 0f;
        public string combatMusicPath = CombatMusicPaths.Default;
        public float minimapSize = 210f;
        public float cooldownMultiplier = CooldownRules.Default;
        // 0.6.0 obstacle armour for penetrating rounds (fraction; shown as %).
        public float penetrationArmorWood = .10f, penetrationArmorCover = .15f, penetrationArmorStoneChunk = .20f, penetrationArmorSlag = .25f,
            penetrationArmorMetalWall = .30f, penetrationArmorStoneWall = .35f, penetrationArmorNaturalRock = .40f, penetrationArmorSlab = .35f;
        public bool penetrationDiagnostics;
        // Routine diagnostic log lines (each wall-top step, stair probes, upstairs sound): off by default, since every log
        // line on this engine carries a stack trace and costs frame time.
        public bool verboseLog;

        internal ObstacleArmor ObstacleArmor => new ObstacleArmor
        {
            Wood = penetrationArmorWood, Cover = penetrationArmorCover, StoneChunk = penetrationArmorStoneChunk, Slag = penetrationArmorSlag,
            MetalWall = penetrationArmorMetalWall, StoneWall = penetrationArmorStoneWall, NaturalRock = penetrationArmorNaturalRock, Slab = penetrationArmorSlab
        }.Normalized();

        public void ResetPenetration()
        {
            ObstacleArmor d = ObstacleArmor.Default;
            penetrationArmorWood = d.Wood; penetrationArmorCover = d.Cover; penetrationArmorStoneChunk = d.StoneChunk; penetrationArmorSlag = d.Slag;
            penetrationArmorMetalWall = d.MetalWall; penetrationArmorStoneWall = d.StoneWall; penetrationArmorNaturalRock = d.NaturalRock; penetrationArmorSlab = d.Slab;
        }

        public void Normalize()
        {
            lookSensitivityMultiplier = LookSensitivityRules.Normalize(lookSensitivityMultiplier);
            aimSensitivityMultiplier = LookSensitivityRules.Normalize(aimSensitivityMultiplier);
            descentSeconds = ViewRules.Safe(descentSeconds, .5f, 2f, 1.5f);
            combatMusicVolume = ViewRules.Safe(combatMusicVolume, 0f, 1f, .75f);
            // Migrate old shorter windows and skill-end fading to the requested fixed-minute policy.
            combatMusicSeconds = 60f; musicStopWithSkills = false;
            combatMusicOffset = ViewRules.Safe(combatMusicOffset, 0f, 600f, 0f);
            if (string.IsNullOrWhiteSpace(combatMusicPath)) combatMusicPath = CombatMusicPaths.Default;
            gravity = ViewRules.Safe(gravity, 0f, 10f, 1f);
            speed = ViewRules.Safe(speed, .25f, 4f, 1f);
            tracerVisibility = ViewRules.Safe(tracerVisibility, .1f, 3f, .4f);
            colonyAircraftSpeed = ViewRules.Safe(colonyAircraftSpeed, .2f, 1.25f, .6f);
            aircraftAgility = ViewRules.Safe(aircraftAgility, .2f, 1f, .6f);
            airToGroundTracer = ViewRules.Safe(airToGroundTracer, .5f, 3f, 1.5f);
            groundToAirTracer = ViewRules.Safe(groundToAirTracer, .5f, 3f, 1.5f);
            antiAirLead = ViewRules.Safe(antiAirLead, 0f, 1f, 1f);
            turretAmmoRadius = Mathf.Clamp(turretAmmoRadius, 1, 8);
            turretAutoReloadFraction = ViewRules.Safe(turretAutoReloadFraction, 0f, .5f, .2f);
            missileTurn = ViewRules.Safe(missileTurn, .35f, 1.5f, 1f);
            shootingSkillInfluence = ViewRules.Safe(shootingSkillInfluence, 0f, 1f, 1f);
            manualTurretDispersion = ViewRules.Safe(manualTurretDispersion, 0f, 2f, .6f);
            explosionQuality = Mathf.Clamp(explosionQuality, 0, 3);
            minimapSize = ViewRules.Safe(minimapSize, 160f, 300f, 210f);
            cooldownMultiplier = CooldownRules.Normalize(cooldownMultiplier);
            ObstacleArmor n = ObstacleArmor;
            penetrationArmorWood = n.Wood; penetrationArmorCover = n.Cover; penetrationArmorStoneChunk = n.StoneChunk; penetrationArmorSlag = n.Slag;
            penetrationArmorMetalWall = n.MetalWall; penetrationArmorStoneWall = n.StoneWall; penetrationArmorNaturalRock = n.NaturalRock; penetrationArmorSlab = n.Slab;
        }

        public void ResetPresentation()
        {
            descentAnimation = descentProfiles = descentLandingShake = combatMusic = combatMusicDuckAmbient = true;
            musicStopWithSkills = false;
            descentSeconds = 1.5f; combatMusicVolume = .75f; combatMusicSeconds = 60f; combatMusicOffset = 0;
            combatMusicPath = CombatMusicPaths.Default;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref lookSensitivityMultiplier, "fpsLookSensitivityMultiplier", LookSensitivityRules.Default);
            Scribe_Values.Look(ref aimSensitivityMultiplier, "fpsAimSensitivityMultiplier", LookSensitivityRules.Default);
            Scribe_Values.Look(ref gravity, "bulletGravityMultiplier", 1f);
            Scribe_Values.Look(ref speed, "bulletSpeedMultiplier", 1f);
            Scribe_Values.Look(ref tracerVisibility, "tracerSize081", .4f);   // 0.8.1: new key, so the old 1.6 default does not carry over
            Scribe_Values.Look(ref colonyAircraftSpeed, "colonyAircraftSpeedR2", .6f);
            Scribe_Values.Look(ref aircraftAgility, "aircraftAgility", .6f);
            if (Scribe.mode == LoadSaveMode.LoadingVars && Scribe.loader.curXmlParent["colonyAircraftSpeedR2"] == null)
            {
                float previous = .4f;
                Scribe_Values.Look(ref previous, "colonyAircraftSpeed", .4f);
                if (Mathf.Abs(previous - .4f) > .001f) colonyAircraftSpeed = previous;
            }
            Scribe_Values.Look(ref airToGroundTracer, "airToGroundTracer", 1.5f);
            Scribe_Values.Look(ref groundToAirTracer, "groundToAirTracer", 1.5f);
            Scribe_Values.Look(ref antiAirLead, "antiAirLead", 1f);
            Scribe_Values.Look(ref missileTurn, "missileTurn", 1f);
            Scribe_Values.Look(ref shootingSkillInfluence, "shootingSkillInfluence", 1f);
            Scribe_Values.Look(ref manualTurretDispersion, "manualTurretDispersion", .6f);
            Scribe_Values.Look(ref showAntiAirLead, "showAntiAirLead", true);
            Scribe_Values.Look(ref showTurretSphere, "showTurretSphere", true);
            Scribe_Values.Look(ref showAircraftRanges, "showAircraftRanges", true);
            Scribe_Values.Look(ref turretTracers, "turretTracers", true);
            Scribe_Values.Look(ref turretAmmoRadius, "turretAmmoRadius", 2);
            Scribe_Values.Look(ref turretAutoReloadFraction, "turretAutoReloadFraction", .2f);
            Scribe_Values.Look(ref ceFreeFire, "ceFreeFire", true);
            Scribe_Values.Look(ref ceAiBurstTracking, "ceAiBurstTracking", true);
            Scribe_Values.Look(ref reinforceCrackedPlasteel, "reinforceCrackedPlasteel", false);
            Scribe_Values.Look(ref explosionQuality, "explosionQuality3D", 2);
            Scribe_Values.Look(ref showHints, "showHints", true);
            Scribe_Values.Look(ref showPawnHud, "showPawnHud", true);
            Scribe_Values.Look(ref showMinimap, "showMinimap", true);
            Scribe_Values.Look(ref showIff, "showIff", true);
            Scribe_Values.Look(ref showAbilityBar, "showAbilityBar", true);
            Scribe_Values.Look(ref abilityNumberKeys, "abilityNumberKeys", true);
            Scribe_Values.Look(ref keywordWeaponModels, "keywordWeaponModels", true);
            Scribe_Values.Look(ref firstPersonWeaponOutlines, "firstPersonWeaponOutlines", false);
            Scribe_Values.Look(ref readableHeldWeapons, "readableHeldWeapons", true);
            Scribe_Values.Look(ref descentAnimation, "descentAnimation", true);
            Scribe_Values.Look(ref descentSeconds, "descentSeconds", 1.5f);
            Scribe_Values.Look(ref descentProfiles, "descentProfiles", true);
            Scribe_Values.Look(ref descentLandingShake, "descentLandingShake", true);
            Scribe_Values.Look(ref combatMusic, "combatMusic", true);
            Scribe_Values.Look(ref musicStopWithSkills, "musicStopWithSkills", false);
            Scribe_Values.Look(ref combatMusicVolume, "combatMusicVolume", .75f);
            Scribe_Values.Look(ref combatMusicSeconds, "combatMusicSeconds", 60f);
            Scribe_Values.Look(ref combatMusicOffset, "combatMusicOffset", 0f);
            Scribe_Values.Look(ref combatMusicPath, "combatMusicPath", CombatMusicPaths.Default);
            Scribe_Values.Look(ref combatMusicDuckAmbient, "combatMusicDuckAmbient", true);
            Scribe_Values.Look(ref firstPersonGuideShown, "firstPersonGuideShown", false);
            Scribe_Values.Look(ref minimapSize, "minimapSize", 210f);
            Scribe_Values.Look(ref cooldownMultiplier, "controlledPawnCooldownMultiplier", CooldownRules.Default);
            ObstacleArmor d = ObstacleArmor.Default;
            Scribe_Values.Look(ref penetrationArmorWood, "penetrationArmorWood", d.Wood);
            Scribe_Values.Look(ref penetrationArmorCover, "penetrationArmorCover", d.Cover);
            Scribe_Values.Look(ref penetrationArmorStoneChunk, "penetrationArmorStoneChunk", d.StoneChunk);
            Scribe_Values.Look(ref penetrationArmorSlag, "penetrationArmorSlag", d.Slag);
            Scribe_Values.Look(ref penetrationArmorMetalWall, "penetrationArmorMetalWall", d.MetalWall);
            Scribe_Values.Look(ref penetrationArmorStoneWall, "penetrationArmorStoneWall", d.StoneWall);
            Scribe_Values.Look(ref penetrationArmorNaturalRock, "penetrationArmorNaturalRock", d.NaturalRock);
            Scribe_Values.Look(ref penetrationArmorSlab, "penetrationArmorSlab", d.Slab);
            Scribe_Values.Look(ref penetrationDiagnostics, "penetrationDiagnostics", false);
            Scribe_Values.Look(ref verboseLog, "verboseLog", false);
            Normalize();
        }
    }

    public sealed class FpsMod : Mod
    {
        private Vector2 scroll;
        private bool keyBindings;
        public static FpsSettings Settings { get; private set; } = new FpsSettings();
        public FpsMod(ModContentPack content) : base(content) { Settings = GetSettings<FpsSettings>(); Settings.Normalize(); CombatMusicManager.Configure(content.RootDir); }
        public override string SettingsCategory() => "ISORIM FPS Control Bridge";
        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (Bootstrap.StartupFailure is string failure)
            {
                var notice = new Listing_Standard();
                notice.Begin(inRect);
                notice.Label(L.T("第一人称控制启动失败。请核对前置依赖，并在重启后查看 Player.log 中的 CONTROL_R4。",
                    "First-person controls failed to start. Check the prerequisites and look for CONTROL_R4 in Player.log after restarting."));
                notice.Label(failure);
                notice.End();
                return;
            }
            float tabWidth = (inRect.width - 12f) * .5f;
            if (Widgets.ButtonText(new Rect(inRect.x, inRect.y, tabWidth, 34), L.T("常规设置", "General settings"))) { keyBindings = false; scroll = Vector2.zero; }
            if (Widgets.ButtonText(new Rect(inRect.x + tabWidth + 12, inRect.y, tabWidth, 34), L.T("按键设置", "Key bindings"))) { keyBindings = true; scroll = Vector2.zero; }
            inRect.yMin += 46f;
            if (keyBindings)
            {
                Rect keysContent = new Rect(0, 0, inRect.width - 22f, 750f + ControlKeys.Actions.Length * 36f);
                Widgets.BeginScrollView(inRect, ref scroll, keysContent);
                ControlKeys.DrawSettings(keysContent);
                Widgets.EndScrollView(); return;
            }
            Rect content = new Rect(0f, 0f, inRect.width - 22f, 3450f);
            Widgets.BeginScrollView(inRect, ref scroll, content);
            var list = new Listing_Standard();
            list.Begin(content);
            list.Label(L.T("弹道设置（仅接管人物发射的普通子弹；下一发起生效）", "Ballistics (ordinary rounds fired by the possessed pawn; from the next shot)"));
            list.Label(L.T("重力系数：", "Gravity: ") + Settings.gravity.ToString("0.00") + L.T(" ×  （0 = 无下坠，1 = 原 R4）", " ×  (0 = no drop, 1 = original R4)"));
            Settings.gravity = list.Slider(Settings.gravity, 0f, 10f);
            list.Label(L.T("子弹飞行速度：", "Bullet speed: ") + Settings.speed.ToString("0.00") + L.T(" × 分档默认速度", " × tier default"));
            list.Label(L.T("默认：栓动/狙击 100，突击/轻机枪 75，冲锋枪/手枪 55（游戏速度单位）。其他武器沿用自身速度。", "Defaults: bolt-action/sniper 100, assault/LMG 75, SMG/pistol 55 (game speed units). Other weapons keep their own speed."));
            Settings.speed = list.Slider(Settings.speed, .25f, 4f);
            list.CheckboxLabeled(L.T("进入控制时播放降临动画", "Camera descent when entering control"), ref Settings.descentAnimation);
            list.Label(L.T("降临时长：", "Descent duration: ") + Settings.descentSeconds.ToString("0.0") + "s");
            Settings.descentSeconds = list.Slider(Settings.descentSeconds, .5f, 2f);
            list.CheckboxLabeled(L.T("按目标类型调整降临节奏", "Adjust descent for the controlled target type"), ref Settings.descentProfiles);
            list.CheckboxLabeled(L.T("降临末尾轻微落地震动", "Subtle landing motion at the end of descent"), ref Settings.descentLandingShake);
            list.Label(L.T("Esc 取消并返回原视角；再按一次视角切换键可跳过动画。", "Esc cancels and restores the previous view; press the view-cycle key again to skip."));
            list.CheckboxLabeled(L.T("斯安威斯坦／死神之眼战斗音乐", "Combat music for Sandevistan / Death Eye"), ref Settings.combatMusic);
            list.Label(L.T("技能音乐固定播放一分钟；技能结束不中断，期间重复释放不会重播或延长。", "Skill music plays for one minute. Ending a skill does not interrupt it; repeat abilities neither restart nor extend it."));
            list.CheckboxLabeled(L.T("战斗音乐期间降低环境音（保留枪声）", "Lower ambient sound during combat music (keep weapon effects)"), ref Settings.combatMusicDuckAmbient);
            list.Label(L.T("战斗音乐音量：", "Combat music level: ") + Settings.combatMusicVolume.ToString("0.00"));
            Settings.combatMusicVolume = list.Slider(Settings.combatMusicVolume, 0f, 1f);
            if (CombatMusicPaths.IsBundled(Content.RootDir, Settings.combatMusicPath))
                list.Label(L.T("内置曲段：已跳过原曲前20秒，播放20–80秒；无需再次设置偏移。", "Bundled excerpt: original seconds 20-80, already trimmed; no additional seek is applied."));
            else
            {
                list.Label(L.T("自定义音乐开始位置：", "Custom music start offset: ") + Settings.combatMusicOffset.ToString("0.0") + "s");
                Settings.combatMusicOffset = list.Slider(Settings.combatMusicOffset, 0f, 30f);
            }
            list.Label(L.T("音乐路径（相对 Sounds 的路径，或本地音频完整路径）：", "Music path (relative to Sounds, or a full local audio path):"));
            Settings.combatMusicPath = list.TextEntry(Settings.combatMusicPath);
            list.Label(L.T("当前为本地导入的战斗音乐；原背景音乐淡出，枪声等音效保留。", "Uses the locally imported combat track; background music fades while effects remain."));
            if (list.ButtonText(L.T("动画与音乐恢复默认", "Reset animation and music"))) Settings.ResetPresentation();
            list.Gap();
            list.Label(L.T("曳光弹大小：", "Tracer size: ") + Settings.tracerVisibility.ToString("0.00") + " ×");
            Settings.tracerVisibility = list.Slider(Settings.tracerVisibility, .1f, 3f);
            list.Gap();
            list.Label(L.T("空战与防空", "Air combat and anti-air"));
            Slider(L.T("殖民地地图最大空速", "Maximum airspeed on colony maps"), ref Settings.colonyAircraftSpeed, .2f, 1.25f);
            Slider(L.T("飞行操纵灵活性（玩家与 AI）", "Aircraft handling (player and AI)"), ref Settings.aircraftAgility, .2f, 1f);
            Slider(L.T("空对地曳光大小", "Air-to-ground tracer size"), ref Settings.airToGroundTracer, .5f, 3f);
            Slider(L.T("地对空曳光大小", "Ground-to-air tracer size"), ref Settings.groundToAirTracer, .5f, 3f);
            Slider(L.T("自动防空提前量", "Automatic anti-air lead"), ref Settings.antiAirLead, 0f, 1f);
            Slider(L.T("制导导弹转向速率", "Guided missile turn rate"), ref Settings.missileTurn, .35f, 1.5f);
            Slider(L.T("射击技能对晃动与后坐的影响", "Shooting skill influence on sway and recoil"), ref Settings.shootingSkillInfluence, 0f, 1f);
            list.Label(L.T("手操直射炮塔散布", "Manual direct-fire turret dispersion") + " " + Settings.manualTurretDispersion.ToString("0.00") + "°");
            Settings.manualTurretDispersion = list.Slider(Settings.manualTurretDispersion, 0f, 2f);
            list.CheckboxLabeled(L.T("通用炮塔 3D 发光弹道", "Universal glowing 3D turret tracers"), ref Settings.turretTracers);
            list.Label(L.T("CE 第一人称炮塔取弹范围 ", "CE first-person turret ammo range ") + Settings.turretAmmoRadius + L.T(" 格", " cells"));
            Settings.turretAmmoRadius = Mathf.RoundToInt(list.Slider(Settings.turretAmmoRadius, 1f, 8f));
            list.Label(L.T("CE 第一人称炮塔自动补弹阈值 ", "CE first-person turret auto-reload threshold ") + Settings.turretAutoReloadFraction.ToString("P0"));
            Settings.turretAutoReloadFraction = list.Slider(Settings.turretAutoReloadFraction, 0f, .5f);
            list.CheckboxLabeled(L.T("手操防空显示提前量准星", "Show lead marker for operated anti-air guns"), ref Settings.showAntiAirLead);
            list.CheckboxLabeled(L.T("选中炮塔时显示球形范围", "Show a selected turret's spherical range"), ref Settings.showTurretSphere);
            list.CheckboxLabeled(L.T("选中飞行器时显示机载武器的立体射程", "Show 3D weapon ranges for a selected aircraft"), ref Settings.showAircraftRanges);
            list.CheckboxLabeled(L.T("允许 CE 普通弹朝空处开火", "Allow CE direct-fire rounds to fire into empty space"), ref Settings.ceFreeFire);
            list.CheckboxLabeled(L.T("CE 地面 AI 机枪逐发跟瞄", "Refresh CE ground AI machine-gun aim each shot"), ref Settings.ceAiBurstTracking);
            list.CheckboxLabeled(L.T("破损玻璃钢楼板抵抗爆炸坍塌", "Protect cracked plasteel slabs against blast collapse"), ref Settings.reinforceCrackedPlasteel);
            string explosionQuality = Settings.explosionQuality switch
            {
                0 => L.T("关", "Off"), 1 => L.T("低", "Low"),
                3 => L.T("高", "High"), _ => L.T("均衡", "Balanced")
            };
            if (list.ButtonText(L.T("立体爆炸效果 · ", "3D explosions · ") + explosionQuality))
                Settings.explosionQuality = (Settings.explosionQuality + 1) % 4;
            if (list.ButtonText(L.T("空战选项恢复默认", "Reset air combat options")))
            {
                Settings.colonyAircraftSpeed = .6f;
                Settings.aircraftAgility = .6f;
                Settings.airToGroundTracer = Settings.groundToAirTracer = 1.5f;
                Settings.antiAirLead = 1f;
                Settings.turretTracers = true;
                Settings.turretAmmoRadius = 2;
                Settings.turretAutoReloadFraction = .2f;
                Settings.missileTurn = Settings.shootingSkillInfluence = 1f;
                Settings.manualTurretDispersion = .6f;
                Settings.showAircraftRanges = Settings.showAntiAirLead = Settings.showTurretSphere = Settings.ceFreeFire = Settings.ceAiBurstTracking = true;
                Settings.reinforceCrackedPlasteel = false;
                Settings.explosionQuality = 2;
            }
            list.Gap();
            list.Label(L.T("全局冷却系数（仅本 Mod 接管的人物，三个控制视角均生效）", "Global cooldown factor (pawns possessed by this mod, in all three views)"));
            list.Label(L.T("冷却时间：", "Cooldown: ") + Settings.cooldownMultiplier.ToString("0.00") + L.T(" ×  （0.5 = 时间减半，1 = 默认，2 = 时间加倍）", " ×  (0.5 = half the time, 1 = default, 2 = double)"));
            Settings.cooldownMultiplier = list.Slider(Settings.cooldownMultiplier, CooldownRules.Minimum, CooldownRules.Maximum);
            list.Label(L.T("影响单发射击间隔、热量恢复、手雷与近战恢复（包括挥空）。不改变连发间隔、其他人物或炮塔。已开始的动作冷却不重置；热量恢复采用当前系数。", "Affects single-shot intervals, heat recovery, grenade and melee recovery (misses included). Burst intervals, other pawns and turrets are unchanged. Cooldowns already running are not reset; heat recovery uses the current factor."));
            if (list.ButtonText(L.T("冷却系数恢复 1.00 ×", "Reset cooldown factor to 1.00 ×"))) Settings.cooldownMultiplier = CooldownRules.Default;
            list.Gap();
            list.Label(CeCompat.Ready
                ? L.T("CE 贯穿与建筑护甲（锐利穿甲，毫米均质钢当量；下一发起生效）", "CE penetration and structure armor (sharp, mm RHA; from the next shot)")
                : L.T("贯穿与障碍护甲（接管人物发射的普通子弹；下一发起生效）", "Penetration and obstacle armor (ordinary rounds fired by the possessed pawn; from the next shot)"));
            list.Label(L.T("子弹穿甲严格大于障碍护甲时，第一个障碍一定能穿过；之后还要额度够才能继续穿。连发武器没有额外额度（只穿一个），单发枪 1 点，电荷／脉冲 2 点；金属墙、石墙、天然岩层、楼板每个消耗 2 点，其他 1 点。爆炸弹等特殊弹头不穿透。", "A round whose armor penetration is strictly greater than an obstacle's armor always gets through the first obstacle; further ones need budget. Burst guns have none (one obstacle), single-shot guns 1 point, charge / pulse 2; metal walls, stone walls, natural rock and floor slabs cost 2 each, others 1. Explosive and other special rounds do not penetrate."));
            Armor(L.T("木质建筑、木门", "Wooden buildings, wooden doors"), ref Settings.penetrationArmorWood);
            Armor(L.T("普通掩体、其他普通建筑", "Ordinary cover, other ordinary buildings"), ref Settings.penetrationArmorCover);
            Armor(L.T("石块堆", "Stone chunks"), ref Settings.penetrationArmorStoneChunk);
            Armor(L.T("钢渣块堆", "Steel slag chunks"), ref Settings.penetrationArmorSlag);
            Armor(L.T("金属墙、关闭的金属门", "Metal walls, closed metal doors"), ref Settings.penetrationArmorMetalWall);
            Armor(L.T("石墙、关闭的石门", "Stone walls, closed stone doors"), ref Settings.penetrationArmorStoneWall);
            Armor(L.T("天然岩层", "Natural rock"), ref Settings.penetrationArmorNaturalRock);
            Armor(L.T("架空实体楼板、实体屋顶", "Raised solid floor slabs, solid roofs"), ref Settings.penetrationArmorSlab);
            if (list.ButtonText(L.T("贯穿障碍护甲恢复默认", "Reset obstacle armor to defaults"))) Settings.ResetPenetration();
            list.CheckboxLabeled(L.T("贯穿诊断日志（测试用，限频）", "Penetration diagnostic log (testing, rate-limited)"), ref Settings.penetrationDiagnostics);
            list.CheckboxLabeled(L.T("详细诊断日志（排查问题用，会降低帧率）", "Verbose diagnostic log (for troubleshooting; costs frame rate)"), ref Settings.verboseLog);
            list.Gap();
            list.CheckboxLabeled(L.T("显示操作提示与命中提示", "Show control hints and hit messages"), ref Settings.showHints);
            list.CheckboxLabeled(L.T("第一人称手持模型显示深色描边", "Dark outlines on first-person held models"), ref Settings.firstPersonWeaponOutlines);
            list.CheckboxLabeled(L.T("第一人称黑铁材质细节提亮", "Readable black-iron first-person materials"), ref Settings.readableHeldWeapons);
            list.CheckboxLabeled(L.T("显示左下角人物与装备面板", "Show the pawn and gear panel (bottom left)"), ref Settings.showPawnHud);
            list.CheckboxLabeled(L.T("显示小地图（北朝上，箭头表示人物朝向）", "Show the minimap (north up, arrow = facing)"), ref Settings.showMinimap);
            list.CheckboxLabeled(L.T("敌友识别标记（红=敌，绿=己方，蓝=友方/访客，黄点=中立）", "Friend-or-foe marks (red = hostile, green = own, blue = friendly/guest, yellow dot = neutral)"), ref Settings.showIff);
            list.CheckboxLabeled(L.T("显示主动能力快捷栏（0 翻页）", "Show the ability bar (0 for the next page)"), ref Settings.showAbilityBar);
            list.CheckboxLabeled(L.T("能力键使用 1–9（取消勾选则使用 Z/X/C/V）", "Ability keys 1–9 (unticked: Z/X/C/V)"), ref Settings.abilityNumberKeys);
            list.Label(L.T("第一人称专属技能固定显示：斯安威斯坦默认 F6，死神之眼默认 F7；在本页上方的“按键设置”或游戏键盘设置中改键，不随其他技能翻页。", "First-person skills stay in a fixed row: Sandevistan defaults to F6, Death Eye to F7. Rebind in the Key bindings tab above or the game's keyboard settings; other abilities do not move them."));
            list.Label(L.T("接管时能力键优先于原调速等快捷键。普通目标能力朝准心施放；世界目标或双目标能力使用原版选择器。原版施放条件、消耗与冷却保留。", "While possessing, ability keys take priority over the game's speed and other shortcuts. Ordinary targeted abilities are cast at the crosshair; world-targeted or two-target abilities use the game's own selector. The game's casting conditions, costs and cooldowns are kept."));
            list.Label(L.T("小地图大小：", "Minimap size: ") + Settings.minimapSize.ToString("0"));
            Settings.minimapSize = list.Slider(Settings.minimapSize, 160f, 300f);
            list.Label(L.T("准心、热量条和手雷落点预览保留。手雷、AI 与炮塔不受上面的子弹参数影响。", "The crosshair, heat bar and grenade landing preview stay. Grenades, the AI and turrets are not affected by the round settings above."));
            list.Gap();
            list.Label(L.T("前侧键：第一人称 → 肩后跟随 → 俯视控制 → 退出。WASD 移动，Q / 长按 E 探头，轻按 E 交互，Tab 换武器，I 背包。", "Mouse forward button: first person → over the shoulder → overhead → leave. WASD move, Q / hold E lean, tap E interact, Tab weapon, I inventory."));
            if (list.ButtonText(L.T("恢复默认设置", "Reset to defaults")))
            { Settings.ResetPenetration(); Settings.ResetPresentation(); Settings.penetrationDiagnostics = false; Settings.verboseLog = false; Settings.gravity = Settings.speed = 1f; Settings.cooldownMultiplier = CooldownRules.Default; Settings.tracerVisibility = .4f; Settings.showHints = Settings.showPawnHud = Settings.showMinimap = Settings.showAbilityBar = Settings.abilityNumberKeys = Settings.showIff = true; Settings.minimapSize = 210f; }
            list.End();
            Widgets.EndScrollView();

            void Slider(string label, ref float value, float min, float max)
            {
                list.Label(label + " " + value.ToString("0.00") + " ×");
                value = list.Slider(value, min, max);
            }

            void Armor(string label, ref float value)
            {
                list.Label(label + L.T("：", ": ") + (value * 100f).ToString("0") + (CeCompat.Ready ? " mm RHA" : "%"));
                value = Mathf.Round(list.Slider(value, ObstacleArmor.Minimum, ObstacleArmor.Maximum) * 100f) / 100f;
            }
            Settings.Normalize();
        }
    }
}
