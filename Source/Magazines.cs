using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Optional per-weapon override for other mods: <li Class="HSK.FpsBridge.MagazineExtension">...</li>.
    public sealed class MagazineExtension : DefModExtension
    {
        public int capacity = 30;
        public float reloadSeconds = 2.5f;
        public bool perRound;
        public bool disabled;
    }
    public sealed class MagazineRecord : IExposable
    {
        public string key;
        public int rounds;
        public int capacity;
        internal MagazineState State;
        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving && State != null) rounds = State.Rounds;
            Scribe_Values.Look(ref key, "weapon"); Scribe_Values.Look(ref rounds, "rounds", 0);
            Scribe_Values.Look(ref capacity, "capacity", 0);
        }
    }
    public sealed class MagazineGame : GameComponent
    {
        private List<MagazineRecord> records = new List<MagazineRecord>();
        private readonly Dictionary<string, MagazineRecord> byKey = new Dictionary<string, MagazineRecord>();
        public MagazineGame(Game game) { }
        internal MagazineState Get(string key, int capacity)
        {
            if (!byKey.TryGetValue(key, out MagazineRecord record))
            {
                record = records.Find(r => r.key == key);
                if (record == null) { record = new MagazineRecord { key = key, rounds = capacity, capacity = capacity }; records.Add(record); }
                byKey[key] = record;
            }
            if (record.State == null) record.State = new MagazineState(Math.Clamp(record.rounds, 0, capacity));
            return record.State;
        }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref records, "fpsVirtualMagazines", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) { records ??= new List<MagazineRecord>(); byKey.Clear(); }
        }
        internal MagazineState CorrectedCrossbow(string key,int capacity)
        {
            MagazineState state=Get(key,capacity);MagazineRecord record=byKey[key];
            // R10 saved this repeating crossbow with an inferred capacity of one. Repair that legacy state once.
            if(record.capacity==0 && capacity>=10 && state.Rounds<=1)
            {state.Cancel();state.Rounds=capacity;record.rounds=capacity;}
            record.capacity=capacity;return state;
        }
    }
    internal static class Magazines
    {
        private static MagazineState current;
        private static Verb currentVerb;
        private static MagazineSpec currentSpec;
        private static int lastTick = -1;
        private static float flashUntil;
        [ThreadStatic] private static Dictionary<Verb, int> shotDepth;
        public sealed class ShotAttempt { internal bool owned, root, released; internal Verb verb; }
        private static readonly Dictionary<ThingDef, MagazineSpec> specs = new Dictionary<ThingDef, MagazineSpec>();
        public static void Install(Harmony harmony)
        {
            // Patch the actual shot implementations, including custom beams and launchers. A failed cast consumes
            // no round. The original burst wrapper still installs its original cooldown when a magazine runs dry.
            var methods = new HashSet<MethodInfo>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                if (def.Verbs != null) foreach (VerbProperties v in def.Verbs)
                {
                    if (v.IsMeleeAttack || v.verbClass == null) continue;
                    MethodInfo method = AccessTools.Method(v.verbClass, "TryCastShot", Type.EmptyTypes);
                    if (method != null) method = AccessTools.DeclaredMethod(method.DeclaringType, method.Name, Type.EmptyTypes);
                    if (method != null && !method.IsAbstract && method.ReturnType == typeof(bool)) methods.Add(method);
                }
            foreach (MethodInfo method in methods)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(Magazines), nameof(BeforeShot)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(Magazines), nameof(AfterShot)) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(typeof(Magazines), nameof(FinalizeShot)));
            Log.Message("[FpsBridge] VIRTUAL_MAGAZINES READY; nativeShotHooks=" + methods.Count + "; supplies=none; cooldown=unchanged");
        }
        private static bool Applies(Pawn pawn, Verb verb) => pawn != null && verb != null && !verb.verbProps.IsMeleeAttack
            && !CeCompat.Owns(verb)
            && verb.EquipmentSource != null && verb.EquipmentSource.def.IsRangedWeapon
            // Native spell/resource sequences must not be cut short by an inferred thirty-round gun magazine.
            && (!SpecialWeapons.Uses(verb) || verb.EquipmentSource.def.GetModExtension<MagazineExtension>() != null)
            && verb.EquipmentSource.def.GetModExtension<MagazineExtension>()?.disabled != true;
        private static MagazineSpec Spec(Verb verb)
        {
            ThingDef def = verb.EquipmentSource.def;
            if (specs.TryGetValue(def, out MagazineSpec spec)) return spec;
            MagazineExtension custom = def.GetModExtension<MagazineExtension>();
            if (custom != null) spec = new MagazineSpec(custom.capacity, custom.reloadSeconds, custom.perRound);
            else
            {
                string identity = def.defName + "|" + def.graphicData?.texPath + "|" + string.Join("|", def.weaponTags ?? new List<string>())
                    + "|" + verb.verbProps.verbClass?.Name + "|" + verb.verbProps.defaultProjectile?.defName;
                spec = MagazineRules.Classify(identity, verb.verbProps.burstShotCount, verb.verbProps.range, Ballistics.IsGrenade(verb));
                // Read compatible weapon metadata without requiring a CE assembly or copying its implementation.
                if (def.comps != null) foreach (CompProperties props in def.comps)
                {
                    Type type = props.GetType();
                    FieldInfo cap = AccessTools.Field(type, "magazineSize"), time = AccessTools.Field(type, "reloadTime");
                    if (cap?.GetValue(props) is int count && count > 0 && time?.GetValue(props) is float seconds && seconds > 0f)
                    { spec = new MagazineSpec(count, seconds, AccessTools.Field(type, "reloadOneAtATime")?.GetValue(props) is true); break; }
                }
            }
            specs[def] = spec; return spec;
        }
        private static MagazineState State(Pawn pawn, Verb verb, out MagazineSpec spec)
        {
            spec = Spec(verb);
            MagazineGame store=Current.Game.GetComponent<MagazineGame>();string key=verb.EquipmentSource.GetUniqueLoadID();
            return verb.EquipmentSource.def.defName=="RK_AutoCrossBow" ? store.CorrectedCrossbow(key,spec.Capacity) : store.Get(key,spec.Capacity);
        }
        public static bool Ready(Pawn pawn, Verb verb)
        {
            if (!Applies(pawn, verb)) return true;
            MagazineState state = State(pawn, verb, out _);
            if (state.Reloading || state.Rounds == 0) { flashUntil = Time.unscaledTime + 1f; return false; }
            return true;
        }
        public static void Shot(Pawn pawn, Verb verb)
        { if (Applies(pawn, verb)) State(pawn, verb, out _).Consume(); }
        public static bool BeforeShot(Verb __instance, ref bool __result, out ShotAttempt __state)
        {
            Pawn pawn = __instance.Caster as Pawn;
            __state = new ShotAttempt { verb = __instance, owned = Bridge.Active && pawn == Bridge.ShootingPawn && !TurretControl.Manned && Applies(pawn, __instance) };
            if (!__state.owned) return true;
            shotDepth ??= new Dictionary<Verb, int>();
            shotDepth.TryGetValue(__instance, out int depth);
            __state.root = depth == 0;
            shotDepth[__instance] = depth + 1;
            if (!__state.root || Ready(pawn, __instance)) return true;
            __result = false; return false;
        }
        public static void AfterShot(Verb __instance, bool __result, ShotAttempt __state)
        {
            if (__state?.owned != true) return;
            if (__state.root && __result) Shot(__instance.CasterPawn, __instance);
            ReleaseShot(__state);
        }
        private static void ReleaseShot(ShotAttempt state)
        {
            if (state?.owned != true || state.released) return;
            if (shotDepth.TryGetValue(state.verb, out int depth) && depth > 1) shotDepth[state.verb] = depth - 1;
            else shotDepth.Remove(state.verb);
            state.released = true;
        }
        public static Exception FinalizeShot(Exception __exception, ShotAttempt __state) { ReleaseShot(__state); return __exception; }
        public static bool Reloading => CeCompat.Reloading || current?.Reloading == true;
        public static float ReloadPose => CeCompat.Reloading ? CeCompat.ReloadPose : current?.Reloading == true ? Mathf.Sin(current.Progress(currentSpec) * Mathf.PI) * .65f + .35f : 0f;
        public static void CancelReload() { current?.Cancel(); }
        public static void End() { CancelReload(); current = null; currentVerb = null; lastTick = -1; }
        public static void Frame(Pawn pawn, bool input, bool paused)
        {
            Verb verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (CeCompat.Owns(verb))
            {
                // No CE state in fpsVirtualMagazines; no duplicate timer or inventory consumption.
                current?.Cancel(); current = null; currentVerb = null; lastTick = -1;
                CeCompat.ReloadFrame(pawn, input, paused); return;
            }
            CeCompat.ClearHud();
            if (!Applies(pawn, verb)) { End(); return; }
            MagazineState state = State(pawn, verb, out MagazineSpec spec);
            int now = Find.TickManager.TicksGame;
            if (state != current) { CancelReload(); current = state; currentVerb = verb; lastTick = now; }
            currentSpec = spec;
            int elapsed = lastTick < 0 ? 0 : Math.Max(0, now - lastTick); lastTick = now;
            if (!paused && input && !QuickTend.For(pawn) && !ConsumableHands.Using(pawn) && !TurretControl.Manned)
            {
                float hands = pawn.health?.capacities.GetLevel(PawnCapacityDefOf.Manipulation) ?? 1f;
                current.Advance(elapsed / 60f * Mathf.Clamp(hands, .2f, 1f) * CyberSkills.PlayerTime, spec);
                // Use the same reload action when the held weapon is empty, even without an R press.
                // Start rejects an already active reload, so empty frames cannot reset its progress.
                if ((state.Rounds == 0 || ControlKeys.Down(ControlAction.Reload)) && !verb.Bursting && !(pawn.stances.curStance is Stance_Warmup))
                {
                    if (state.Start(spec))
                    {
                        FireControl.CancelInput();
                        Messages.Message(L.T("换弹：", "Reloading: ") + verb.EquipmentSource.LabelShortCap, MessageTypeDefOf.SilentInput, false);
                    }
                }
                // Tube-fed guns can interrupt after a shell was inserted. Magazine changes finish as one action.
                if (state.Reloading && spec.PerRound && state.Rounds > 0 && ControlKeys.Down(ControlAction.Fire)) state.Cancel();
            }
            else if (!paused) CancelReload();
        }
        public static string Status(Pawn pawn)
        {
            Verb verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (CeCompat.Owns(verb)) return CeCompat.MagazineStatus(pawn);
            if (!Applies(pawn, verb)) return null;
            MagazineState state = State(pawn, verb, out MagazineSpec spec);
            return L.T("弹匣 ", "Magazine ") + state.Rounds + " / " + spec.Capacity + (state.Reloading
                ? L.T(" · 换弹 ", " · reload ") + state.Remaining(spec).ToString("F1") + "s"
                : " · " + ControlKeys.Label(ControlAction.Reload) + L.T(" 换弹", " reload"));
        }
        // Read-only lower-left HUD, above the existing pawn panel. Reload ring fills clockwise to a full turn.
        public static void DrawHud(Pawn pawn)
        {
            if (Bridge.MainTabBlocks || InventoryPanel.Blocking || TurretControl.Manned) return;
            string text = Status(pawn); if (text == null) return;
            float y = Math.Max(12, UI.screenHeight - 423);
            GameFont font = Text.Font; TextAnchor anchor = Text.Anchor; Color color = GUI.color;
            try
            {
                Widgets.DrawBoxSolid(new Rect(12, y, 354, 35), new Color(.035f, .045f, .06f, .9f));
                Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = Time.unscaledTime < flashUntil ? new Color(1f, .6f, .25f) : Color.white;
                Widgets.Label(new Rect(46, y + 2, 316, 31), text);
                Vector2 center = new Vector2(29, y + 17);
                float fill = CeCompat.Reloading ? CeCompat.ReloadProgress : current?.Reloading == true ? current.Progress(currentSpec) : 1f;
                for (int i = 0; i < 40; i++)
                {
                    float a = i * Mathf.PI * 2f / 40f - Mathf.PI / 2f, b = (i + 1) * Mathf.PI * 2f / 40f - Mathf.PI / 2f;
                    Widgets.DrawLine(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 11f, center + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * 11f,
                        i / 40f < fill ? new Color(.4f, .9f, 1f) : new Color(.25f, .3f, .35f), 2f);
                }
            }
            finally { Text.Font = font; Text.Anchor = anchor; GUI.color = color; }
        }
    }
}
