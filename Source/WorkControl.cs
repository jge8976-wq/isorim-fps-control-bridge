using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    // 0.8.0 work while possessed: H hands the pawn to the game's own work AI (the camera stays), any key except view,
    // capture and game-speed keys takes it back; F starts the nearest work within two cells by hand.
    // Perspective Shift keeps a possessed pawn idle (a placeholder wait from JobGiver_Orders, several job givers off,
    // every work type at priority 3); during auto work those blocks are lifted for our pawn only.
    internal static class WorkControl
    {
        public const float Range = 2.9f;   // two cells, diagonals included
        private static Pawn owner;
        private static bool auto, wasDrafted, dispatching;
        private static readonly HashSet<Job> nativeOrders = new();
        private static readonly FieldInfo nativeTargetCaster = AccessTools.Field(typeof(Targeter), "caster");
        private static int quietFrame = -1;
        private static int truePriority;
        private static KeyBindingDef[] speedKeys;
        public static bool Enabled { get; private set; }
        public static Job ManualJob { get; private set; }

        public static bool Auto => auto && owner != null && owner == Bridge.ShootingPawn;
        // The frame H started or a key ended auto work: that key does nothing else.
        public static bool Swallow => quietFrame == Time.frameCount;
        private static bool AllowsAi(Pawn pawn) => Auto && pawn == owner && !pawn.Drafted;

        public static void Install(Harmony harmony)
        {
            Type orders = typeof(JobGiver_Orders);
            harmony.Patch(AccessTools.Method(orders, "TryGiveJob") ?? throw new MissingMethodException("JobGiver_Orders.TryGiveJob"),
                postfix: HM(nameof(AfterOrders), Priority.Last));
            harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob),
                new[] { typeof(Job), typeof(JobTag?), typeof(bool) }), postfix: HM(nameof(AfterNativeOrder), Priority.Last));
            // Perspective Shift's own blocking prefixes (several job givers, material delivery) are patched themselves:
            // for our pawn during auto work they answer "run the original". A later prefix cannot undo a skip here.
            harmony.Patch(AccessTools.Method("PerspectiveShift.JobGivers_DisableForAvatar_Patch:Prefix")
                ?? throw new MissingMethodException("PerspectiveShift.JobGivers_DisableForAvatar_Patch.Prefix"), prefix: HM(nameof(ReopenGiver), Priority.First));
            harmony.Patch(AccessTools.Method("PerspectiveShift.WorkGivers_ConstructDeliver_DisableForAvatar_Patch:Prefix")
                ?? throw new MissingMethodException("PerspectiveShift.WorkGivers_ConstructDeliver_DisableForAvatar_Patch.Prefix"), prefix: HM(nameof(ReopenDelivery), Priority.First));
            MethodInfo priority = AccessTools.Method(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.GetPriority));
            harmony.Patch(priority, postfix: HM(nameof(KeepPriority), Priority.First));
            harmony.Patch(priority, postfix: HM(nameof(RestorePriority), Priority.Last));
            speedKeys = new[] { KeyBindingDefOf.TogglePause, KeyBindingDefOf.TimeSpeed_Normal, KeyBindingDefOf.TimeSpeed_Fast,
                KeyBindingDefOf.TimeSpeed_Superfast, KeyBindingDefOf.TimeSpeed_Ultrafast, KeyBindingDefOf.TimeSpeed_Slower, KeyBindingDefOf.TimeSpeed_Faster };
            // Perspective Shift ends some jobs (repair, build, mine, harvest...) unless LMB is held. In first person LMB
            // fires; holding F stands in for that click, and auto work needs no click at all.
            Type psMod = AccessTools.TypeByName("PerspectiveShift.PerspectiveShiftMod");
            psSettings = psMod == null ? null : AccessTools.Field(psMod, "settings");
            heldClick = psSettings == null ? null : AccessTools.Field(psSettings.FieldType, "requireHeldClickForJobs");
            if (heldClick != null && heldClick.FieldType == typeof(bool))
                harmony.Patch(Bindings.AvatarPhysics, prefix: HM(nameof(BeforeAvatarInput), Priority.First), finalizer: HM(nameof(AfterAvatarInput), Priority.Last));
            else Log.Warning("[FpsBridge] Perspective Shift held-click setting not found: F works only for jobs that need no held click.");
            Enabled = true;
        }

        private static FieldInfo psSettings, heldClick;
        private static float nextHoldDispatch;

        // PS tests requireHeldClickForJobs in UpdatePhysics BEFORE calling UpdateInput. Wrap the whole physics
        // update, with per-call state and a finalizer so an exception cannot leave the shared setting changed.
        public static void BeforeAvatarInput(object __instance, out bool? __state)
        {
            __state = null;
            Pawn pawn = owner;
            if (pawn == null || pawn != Bridge.ShootingPawn || Bindings.PawnOf(__instance) != pawn
                || (!Auto && !IsManual(pawn) && !QuickTend.For(pawn) && !ConsumableHands.Using(pawn) && !ControlKeys.Held(ControlAction.ManualWork))) return;
            object s = psSettings.GetValue(null);
            if (s == null) return;
            __state = (bool)heldClick.GetValue(s);
            heldClick.SetValue(s, false);
        }
        public static Exception AfterAvatarInput(bool? __state, Exception __exception)
        {
            if (!__state.HasValue) return __exception;
            object s = psSettings.GetValue(null);
            if (s != null) heldClick.SetValue(s, __state.Value);
            return __exception;
        }

        // While F is held and the pawn is idle, the next job within reach starts by itself (no message when none).
        public static void HoldFrame(Pawn pawn)
        {
            if (!Enabled || pawn != owner || auto || !ControlKeys.Held(ControlAction.ManualWork) || ControlKeys.Down(ControlAction.ManualWork) || Time.unscaledTime < nextHoldDispatch) return;
            Job cur = pawn.CurJob;
            if (cur != null && cur.def != JobDefOf.Wait && cur.def != JobDefOf.Wait_Combat) return;
            nextHoldDispatch = Time.unscaledTime + .4f;
            Manual(pawn, true);
        }

        private static HarmonyMethod HM(string name, int priority) => new HarmonyMethod(typeof(WorkControl), name) { priority = priority };

        // ---- Perspective Shift's idle blocks, lifted for our pawn

        public static void AfterOrders(Pawn pawn, ref Job __result)
        {
            if (__result != null && AllowsAi(pawn) && __result.def == JobDefOf.Wait && __result.expiryInterval == 60) __result = null;
        }
        // __0 is the pawn argument of Perspective Shift's prefix; __result is that prefix's own answer (true = run the
        // job giver as usual). Skipping it leaves the job giver untouched.
        public static bool ReopenGiver(Pawn __0, ref bool __result)
        { if (!AllowsAi(__0)) return true; __result = true; return false; }
        public static bool ReopenDelivery(Pawn __0, ref bool __result)
        { if (!AllowsAi(__0) && !(dispatching && __0 == owner)) return true; __result = true; return false; }
        public static void KeepPriority(int __result) => truePriority = __result;
        public static void RestorePriority(Pawn_WorkSettings __instance, ref int __result, Pawn ___pawn)
        { if (AllowsAi(___pawn)) __result = truePriority; }

        public static bool IsSpeedBinding(KeyBindingDef def) => speedKeys != null && Array.IndexOf(speedKeys, def) >= 0;

        // ---- session

        public static void Begin(Pawn pawn) { Reset(); owner = pawn; }
        public static void Reset()
        {
            bool was = auto;
            auto = false; ManualJob = null; nativeOrders.Clear(); dispatching = false;
            if (was) owner?.workSettings?.Notify_UseWorkPrioritiesChanged();
            owner = null;
        }

        // Once per input frame while the view is captured (also while paused).
        public static void Frame(Pawn pawn, bool context)
        {
            if (!Enabled || !context || pawn != owner) return;
            if (ManualJob != null && pawn.CurJob != ManualJob && !pawn.jobs.jobQueue.Contains(ManualJob)) ManualJob = null;
            nativeOrders.RemoveWhere(job => pawn.CurJob != job && !pawn.jobs.jobQueue.Contains(job));
            if ((ManualJob != null || nativeOrders.Count > 0) && (ControlKeys.Held(ControlAction.Forward) || ControlKeys.Held(ControlAction.Backward)
                || ControlKeys.Held(ControlAction.Left) || ControlKeys.Held(ControlAction.Right)))
            {
                Job cancelled = ManualJob;
                bool interrupt = IsManual(pawn);
                ManualJob = null;
                pawn.jobs.jobQueue.RemoveAll(pawn, j => j == cancelled || nativeOrders.Contains(j));
                nativeOrders.Clear();
                if (interrupt && pawn.CurJob?.def.forceCompleteBeforeNextJob != true && pawn.jobs.IsCurrentJobPlayerInterruptible())
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            if (!auto)
            {
                if (ControlKeys.Down(ControlAction.AutoWork)) StartAuto(pawn);
                return;
            }
            if (Input.anyKeyDown && !PassiveKeyDown()) StopAuto(pawn);
        }

        // Keys that never take control back: the capture toggle, Esc (menus), and game speed / pause.
        private static bool PassiveKeyDown()
        {
            if (ControlKeys.Down(ControlAction.Capture) || ControlKeys.Down(ControlAction.ViewCycle)
                || ControlKeys.Down(ControlAction.ExitRecall) || Input.GetKeyDown(KeyCode.Escape)) return true;
            // Alt + 1-4 is the first-person game speed.
            if (ControlKeys.Held(ControlAction.Overlay)) return true;
            foreach (KeyBindingDef def in speedKeys)
                if (def != null && KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(def, out var keys)
                    && (Down(keys.keyBindingA) || Down(keys.keyBindingB))) return true;
            return false;
        }
        private static bool Down(KeyCode k) => ControlKeys.RawDown(k);

        private static void StartAuto(Pawn pawn)
        {
            quietFrame = Time.frameCount;
            if (pawn.workSettings == null || !pawn.workSettings.EverWork)
            { Messages.Message(pawn.LabelShort + L.T(" 不能工作。", " cannot work."), MessageTypeDefOf.RejectInput, false); return; }
            wasDrafted = pawn.Drafted;
            if (pawn.drafter != null && pawn.Drafted) pawn.drafter.Drafted = false;
            auto = true;
            Stances.State.CancelSprint();
            pawn.workSettings.Notify_UseWorkPrioritiesChanged();
            // Only the idle placeholder is dropped; a job already running (e.g. from F) carries on.
            if (pawn.CurJob == null || pawn.CurJob.def == JobDefOf.Wait || pawn.CurJob.def == JobDefOf.Wait_Combat)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            Messages.Message(L.T("自动工作：交给小人自己安排 · 按任意键接管", "Auto-work: the pawn decides · press any key to take over"), MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] AUTO_WORK on pawn=" + pawn.thingIDNumber);
        }

        private static void StopAuto(Pawn pawn)
        {
            quietFrame = Time.frameCount;
            auto = false;
            pawn.workSettings?.Notify_UseWorkPrioritiesChanged();
            if (pawn.CurJob != null && pawn.CurJob.def != JobDefOf.Wait && pawn.CurJob.def.playerInterruptible)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            pawn.jobs.ClearQueuedJobs();
            if (pawn.carryTracker?.CarriedThing != null) pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
            if (wasDrafted && pawn.drafter != null && !pawn.Downed) pawn.drafter.Drafted = true;
            Messages.Message(L.T("已接管控制", "Control taken back"), MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] AUTO_WORK off pawn=" + pawn.thingIDNumber);
        }

        // ---- F: nearest work within two cells, the one under the crosshair first

        // probe: only whether any work (hauling aside) is within reach; nothing is started or said.
        public static bool Manual(Pawn pawn, bool quiet = false, bool probe = false)
        {
            if (!Enabled || pawn != owner || auto) return false;
            if (!probe) quietFrame = Time.frameCount;
            if (pawn.workSettings == null || !pawn.workSettings.EverWork || pawn.Map == null)
            { if (!probe) Messages.Message(pawn.LabelShort + L.T(" 不能工作。", " cannot work."), MessageTypeDefOf.RejectInput, false); return false; }
            Map map = pawn.Map;
            V3 aim = ControlCamera.AimPoint(pawn);
            IntVec3 aimed = FloorSpace.Cell(map, aim + HitMath.Direction(IsoRim.FirstPerson.Yaw, IsoRim.FirstPerson.Pitch) * .05f);
            var cells = new List<IntVec3>();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(pawn.Position, Range, true))
                if (c.InBounds(map)) cells.Add(c);
            Job best = null; WorkGiver bestGiver = null; Thing bestThing = null; float bestScore = float.MaxValue;
            var things = new List<Thing>();
            dispatching = true;
            try
            {
                List<WorkGiver> givers = pawn.workSettings.WorkGiversInOrderNormal;
                for (int g = 0; g < givers.Count; g++)
                {
                    if (!(givers[g] is WorkGiver_Scanner s) || s.ShouldSkip(pawn, true)) continue;
                    if (probe && s.def.workType == WorkTypeDefOf.Hauling) continue;   // carrying things off is not "work here" for E
                    HashSet<Thing> globalThings = null; HashSet<IntVec3> globalCells = null;
                    foreach (IntVec3 c in cells)
                    {
                        float score = Score(pawn, c, aimed, g);
                        if (score >= bestScore) continue;
                        if (s.def.scanThings)
                        {
                            things.Clear(); things.AddRange(c.GetThingList(map));
                            foreach (Thing t in things)
                            {
                                if (!s.PotentialWorkThingRequest.Accepts(t))
                                {
                                    globalThings ??= new HashSet<Thing>(s.PotentialWorkThingsGlobal(pawn) ?? Array.Empty<Thing>());
                                    if (!globalThings.Contains(t)) continue;
                                }
                                if (!pawn.CanReach(t, s.PathEndMode, Danger.Deadly) || !s.HasJobOnThing(pawn, t, true)) continue;
                                Job job = s.JobOnThing(pawn, t, true);
                                if (job == null) continue;
                                best = job; bestGiver = s; bestThing = t; bestScore = score;
                                break;
                            }
                            if (bestScore <= score) continue;
                        }
                        if (s.def.scanCells)
                        {
                            globalCells ??= new HashSet<IntVec3>(s.PotentialWorkCellsGlobal(pawn) ?? Array.Empty<IntVec3>());
                            if (!globalCells.Contains(c) || !pawn.CanReach(c, s.PathEndMode, Danger.Deadly) || !s.HasJobOnCell(pawn, c, true)) continue;
                            Job job = s.JobOnCell(pawn, c, true);
                            if (job == null) continue;
                            best = job; bestGiver = s; bestThing = null; bestScore = score;
                        }
                    }
                }
            }
            finally { dispatching = false; }
            if (best == null) { if (!quiet && !probe) Messages.Message(L.T("附近两格内没有可执行的工作。", "No work to do within two cells."), MessageTypeDefOf.RejectInput, false); return false; }
            if (probe) return true;
            best.workGiverDef = bestGiver.def;
            if (!StartExplicit(pawn, best, L.T("F：", "F: ") + bestGiver.def.gerund + (bestThing != null ? " " + bestThing.LabelShort : ""))) return false;
            Log.Message("[FpsBridge] MANUAL_WORK giver=" + bestGiver.def.defName + "; job=" + best.def.defName + "; target=" + (bestThing?.ThingID ?? best.targetA.Cell.ToString()));
            return true;
        }

        // The crosshair's cell beats every other; then distance; then the pawn's work order.
        private static float Score(Pawn pawn, IntVec3 c, IntVec3 aimed, int giverIndex) =>
            (c == aimed ? 0f : 1000f) + (c - pawn.Position).LengthHorizontalSquared * 10f + giverIndex * .01f;

        // The manual job turns the body itself; the view must not override it.
        internal static bool IsManual(Pawn pawn) => pawn != null && pawn == owner && pawn.CurJob is Job job
            && (job == ManualJob || nativeOrders.Contains(job));
        public static bool OwnsRotation(Pawn pawn) => Auto || QuickTend.For(pawn) || IsManual(pawn);

        // A single ordered-job route for explicit wheel targets and the legacy F command. No held button needed.
        internal static string NativeInvalid(Pawn pawn)
        {
            if (!Enabled || pawn == null || pawn != owner || pawn != Bridge.ShootingPawn)
                return L.T("该人物当前不受 FPS 控制。", "This pawn is not under FPS control.");
            if (!pawn.Spawned || pawn.Dead || pawn.Downed || pawn.InMentalState || pawn.jobs == null)
                return L.T("该人物现在无法行动。", "This pawn cannot act now.");
            return null;
        }

        internal static string ExplicitInvalid(Pawn pawn)
        {
            string reason = NativeInvalid(pawn);
            if (reason != null) return reason;
            if (TurretControl.Manned || MountedControl.For(pawn) || Vault.Active
                || pawn.CurJob?.ability != null || pawn.CurJob?.def.playerInterruptible == false
                || pawn.CurJob?.def.forceCompleteBeforeNextJob == true)
                return L.T("请先完成当前动作或离开载具、炮台。", "Finish the current action or leave the mount/turret first.");
            return null;
        }

        // Let the original command assign jobs and handle its own reservations, UI and prerequisites.
        internal static bool RunNative(Pawn pawn, Action action)
        {
            if (NativeInvalid(pawn) != null || action == null) return false;
            Job previous = pawn.CurJob;
            bool oldDispatch = dispatching;
            dispatching = true;
            try { action(); }
            finally { dispatching = oldDispatch; }
            if (owner != pawn || Bridge.ShootingPawn != pawn) return true;
            if (pawn.CurJob is Job current && current != previous && !current.def.isIdle) AdoptNativeOrder(current);
            if (Find.Targeter is { IsTargeting: true } targeting && !Find.Selector.IsSelected(pawn)
                && (targeting.targetingSource?.Caster == pawn || ReferenceEquals(nativeTargetCaster?.GetValue(targeting), pawn)))
                Find.Selector.Select(pawn, false, false);
            return true;
        }

        // Native confirmations, submenus and targeters may give their order later. Capture the accepted
        // order at the common job tracker entry point for our pawn only, including queued orders.
        public static void AfterNativeOrder(Pawn ___pawn, Job job, bool __result)
        {
            if (!__result || ___pawn != owner || ___pawn != Bridge.ShootingPawn || job == null || job == ManualJob) return;
            if (___pawn.CurJob == job || ___pawn.jobs.jobQueue.Contains(job)) AdoptNativeOrder(job);
            else if (___pawn.CurJob is Job current && current.JobIsSameAs(___pawn, job)) AdoptNativeOrder(current);
        }

        private static void AdoptNativeOrder(Job job)
        {
            if (job == ManualJob || !nativeOrders.Add(job)) return;
            quietFrame = Time.frameCount;
            Magazines.CancelReload(); FireControl.CancelInput(); Stances.State.CancelSprint();
        }

        internal static bool StartExplicit(Pawn pawn, Job job, string label)
        {
            string reason = ExplicitInvalid(pawn);
            if (reason != null || job == null)
            {
                Messages.Message(reason ?? L.T("这项工作现在无法开始。", "This work cannot start now."), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            bool drafted = pawn.Drafted, oldAuto = auto;
            Job oldManual = ManualJob;
            bool accepted = false;
            try
            {
                auto = false;
                if (oldAuto) pawn.workSettings?.Notify_UseWorkPrioritiesChanged();
                // Native dispatch can accept an equivalent job without installing the new Job object.
                if (pawn.CurJob != null && pawn.CurJob.JobIsSameAs(pawn, job))
                {
                    ManualJob = pawn.CurJob; accepted = true;
                    Magazines.CancelReload(); FireControl.CancelInput(); Stances.State.CancelSprint();
                    return true;
                }
                if (job.workGiverDef != null && !job.workGiverDef.canBeDoneWhileDrafted && pawn.drafter != null && pawn.Drafted)
                    pawn.drafter.Drafted = false;
                job.playerForced = true;
                ManualJob = job;
                accepted = pawn.jobs.TryTakeOrderedJob(job, job.workGiverDef?.tagToGive ?? JobTag.Misc);
                if (!accepted) return false;
                Magazines.CancelReload(); FireControl.CancelInput(); Stances.State.CancelSprint();
                Messages.Message(label + L.T(" · 移动即中止", " · moving stops it"), MessageTypeDefOf.SilentInput, false);
                return true;
            }
            finally
            {
                if (!accepted)
                {
                    ManualJob = oldManual; auto = oldAuto;
                    if (oldAuto) pawn.workSettings?.Notify_UseWorkPrioritiesChanged();
                    if (drafted && pawn.drafter != null && !pawn.Downed) pawn.drafter.Drafted = true;
                }
            }
        }

        public static void DrawHud(Pawn pawn)
        {
            if (!Auto && !OwnsRotation(pawn)) return;
            string report = null;
            try { report = pawn.jobs?.curDriver?.GetReport()?.CapitalizeFirst(); } catch { }
            string text = (Auto ? L.T("自动工作中", "Auto-working") : L.T("手动工作", "Manual work")) + (string.IsNullOrEmpty(report) ? "" : L.T("：", ": ") + report)
                + (Auto ? L.T(" · 按任意键接管", " · any key to take over") : L.T(" · 移动即中止", " · moving stops it"));
            GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperCenter;
            float x = UI.screenWidth * .5f;
            GUI.color = new Color(0f, 0f, 0f, .55f);
            Widgets.DrawBoxSolid(new Rect(x - 260, 88, 520, 28), GUI.color);
            GUI.color = Auto ? new Color(.55f, 1f, .6f) : new Color(1f, .9f, .55f);
            Widgets.Label(new Rect(x - 255, 92, 510, 24), text);
            Text.Font = f0; Text.Anchor = a0; GUI.color = c0;
        }
    }
}
