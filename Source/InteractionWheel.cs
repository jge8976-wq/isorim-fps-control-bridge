using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class InteractionWheel
    {
        private static readonly WheelGesture gesture = new WheelGesture();
        private static Pawn owner;
        private static Thing target;
        private static IntVec3 clickedCell;
        private static List<InteractionOption> options;
        private static Action pendingCommand;
        private static WheelWindow window;
        private static KeyCode pressedKey;
        private static int page, closedUntil = -1;
        private static bool waitForRelease;
        private static int scrollFrame = -1;
        private static int sampledFrame = -1;
        private static int skipMouseUntil = -1;
        private static bool wasBlocking;
        private static Vector2 openingPointer;
        private static int openedFrame;
        private static bool selectionArmed;
        internal static bool SkipMouse => Time.frameCount <= skipMouseUntil;
        internal static bool Enabled { get; private set; }
        internal static bool Open => window != null;
        internal static bool Blocking => Open || waitForRelease || Time.frameCount <= closedUntil;
        internal static bool Owns(Window value) => value != null && ReferenceEquals(window, value);
        internal static void Install(Harmony harmony)
        {
            NativeInteractions.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)),
                prefix: First(nameof(BeforeUIRoot)));
            foreach (string name in new[] { "SelectorOnGUI", "HandleMapClicks", "SelectorOnGUI_BeforeMainTabs" })
                harmony.Patch(AccessTools.Method(typeof(Selector), name), prefix: First(nameof(BeforeSelector)));
            harmony.Patch(Bindings.StateOnGUI, prefix: First(nameof(BeforePerspectiveGUI)));
            Enabled = true;
            Log.Message("[FpsBridge] INTERACTION_WHEEL READY; hold=0.30; tap=mark; nativeJobs=ON");
        }
        private static HarmonyMethod First(string name) => new HarmonyMethod(typeof(InteractionWheel), name) { priority = Priority.First };
        public static void BeforeUIRoot()
        {
            Bridge.WheelGuiInput();
            if (Open && options == null) BuildOptions();
            Action execute = pendingCommand;
            pendingCommand = null;
            execute?.Invoke();
            if (!Bridge.Active || !Blocking) return;
            if (Find.Selector?.dragBox != null) Find.Selector.dragBox.active = false;
            Event e = Event.current;
            if (e != null && (e.isMouse || e.type == EventType.ScrollWheel
                || (e.isKey && e.keyCode == KeyCode.Escape))) e.Use();
        }
        public static bool BeforeSelector(Selector __instance)
        {
            if (!Bridge.Active || !Blocking) return true;
            if (__instance.dragBox != null) __instance.dragBox.active = false;
            return false;
        }
        public static bool BeforePerspectiveGUI() => !Bridge.Active || !Blocking;

        // Called before gameplay input dispatch. A modal keeps capture intent, but owns mouse and body input.
        internal static bool Frame(Pawn actor, bool canOpen)
        {
            if (!Enabled)
            {
                if (canOpen && ControlKeys.Down(ControlAction.MarkTarget)) TargetMark.Toggle(actor);
                return false;
            }
            if (owner != null && owner != actor) Reset();
            if (waitForRelease && !AnyHeld()) waitForRelease = false;
            if (!Blocking && wasBlocking) { skipMouseUntil = Time.frameCount + 2; wasBlocking = false; }
            bool baseContext = actor?.Spawned == true && !actor.Dead && !actor.Downed && Application.isFocused
                && Current.ProgramState == ProgramState.Playing && !LongEventHandler.ShouldWaitForEvent
                && Find.CurrentMap == actor.Map && !ControlKeys.Editing && !InventoryPanel.Blocking && !Bridge.MainTabBlocks
                && Find.Targeter?.IsTargeting != true && !TurretControl.Manned && !VehicleControl.On && !RemoteTurret.On
                && !MountedControl.For(actor) && !ProjectZStairs.Riding(actor) && !Vault.Active;
            bool otherWindow = Find.WindowStack == null || Find.WindowStack.Windows.Any(w => !Owns(w)
                && (w.preventCameraMotion || w.absorbInputAroundWindow));
            if (!baseContext || otherWindow)
            {
                if (Open || gesture.Pending) Cancel();
                return Blocking;
            }
            if (!Open && Blocking) return true;
            // Update and the early OnGUI prefix share the same polled input. Never dispatch a tap twice.
            if (sampledFrame == Time.frameCount) return Blocking;
            sampledFrame = Time.frameCount;
            bool allowed = Open || canOpen;
            bool immediate = false;
            KeyCode key = KeyCode.None;
            if (!gesture.Pending && allowed)
            {
                key = ControlKeys.PressedKey(ControlAction.InteractionMenu);
                immediate = key != KeyCode.None;
                if (key == KeyCode.None) key = ControlKeys.PressedKey(ControlAction.MarkTarget);
                if (key != KeyCode.None) { pressedKey = key; owner = actor; }
            }
            bool cancelNow = Input.GetKeyDown(KeyCode.Escape)
                || (Open && Input.GetMouseButtonDown(1) && pressedKey != KeyCode.Mouse1);
            WheelGestureEvent result = gesture.Step(Time.unscaledTime, key != KeyCode.None,
                ControlKeys.RawHeld(pressedKey), allowed, cancelNow, immediate);
            if (result == WheelGestureEvent.Tap) { TargetMark.Toggle(actor); owner = null; pressedKey = KeyCode.None; }
            else if (result == WheelGestureEvent.Open) Show(actor);
            else if (result == WheelGestureEvent.Confirm) Finish(true);
            else if (result == WheelGestureEvent.Cancel) Cancel();
            if (Open && Input.GetMouseButtonDown(1) && pressedKey != KeyCode.Mouse1) Cancel();
            if (Open && Input.GetMouseButtonDown(0) && pressedKey != KeyCode.Mouse0) Finish(true);
            if (Open && scrollFrame != Time.frameCount && Mathf.Abs(Input.mouseScrollDelta.y) > .01f
                && options?.Count > InteractionWheelRules.PageSize)
            {
                scrollFrame = Time.frameCount;
                int pages = (options.Count + InteractionWheelRules.PageSize - 1) / InteractionWheelRules.PageSize;
                page = (page + (Input.mouseScrollDelta.y < 0 ? 1 : pages - 1)) % pages;
            }
            return Blocking;
        }

        private static bool AnyHeld() => ControlKeys.RawHeld(pressedKey) || Input.GetKey(KeyCode.Escape)
            || ControlKeys.Held(ControlAction.Fire) || ControlKeys.Held(ControlAction.Aim)
            || ControlKeys.Held(ControlAction.Jump) || ControlKeys.Held(ControlAction.InteractLeanRight)
            || ControlKeys.Held(ControlAction.MarkTarget) || ControlKeys.Held(ControlAction.InteractionMenu)
            || ControlKeys.Held(ControlAction.ManualWork) || ControlKeys.Held(ControlAction.Reload)
            || ControlKeys.Held(ControlAction.UseItem) || ControlKeys.Held(ControlAction.QuickTend)
            || ControlKeys.Held(ControlAction.Inventory) || ControlKeys.Held(ControlAction.NextWeapon)
            || ControlKeys.Held(ControlAction.Social) || ControlKeys.Held(ControlAction.AutoWork)
            || ControlKeys.Held(ControlAction.Forward) || ControlKeys.Held(ControlAction.Backward)
            || ControlKeys.Held(ControlAction.Left) || ControlKeys.Held(ControlAction.Right);

        private static void Show(Pawn actor)
        {
            LocalTargetInfo picked = InteractionActions.Pick(actor);
            target = picked.HasThing ? picked.Thing : picked.IsValid ? null : actor;
            clickedCell = picked.IsValid ? picked.Cell : actor.Position;
            options = null;
            if (Event.current != null) BuildOptions();
            page = 0;
            FireControl.CancelInput();
            LeanControl.ReadKeys(false);
            StopNativeAttack(actor);
            Stances.State.CancelSprint();
            window = new WheelWindow();
            Find.WindowStack.Add(window);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            openingPointer = UI.MousePositionOnUIInverted; openedFrame = Time.frameCount; selectionArmed = false;
        }

        private static void BuildOptions() => options = InteractionActions.Build(owner, target, clickedCell);
        internal static void StopNativeAttack(Pawn actor)
        {
            Verse.Verb verb = actor?.equipment?.PrimaryEq?.PrimaryVerb;
            if (verb?.Bursting == true)
            {
                int cooldown = Math.Max(1, verb.verbProps.AdjustedCooldownTicks(verb, actor));
                LocalTargetInfo aim = verb.CurrentTarget;
                verb.Reset();
                actor.stances?.SetStance(new Stance_Cooldown(cooldown, aim, verb));
            }
            else if (actor?.stances?.curStance is Stance_Warmup) actor.stances.CancelBusyStanceSoft();
        }

        private static float Radius => Mathf.Min(176f, Mathf.Min(UI.screenWidth * .27f, UI.screenHeight * .27f));
        private static Vector2 Center => new Vector2(UI.screenWidth * .5f, UI.screenHeight * .5f);
        private static int Count => Math.Min(InteractionWheelRules.PageSize, Math.Max(0, (options?.Count ?? 0) - page * InteractionWheelRules.PageSize));
        private static int Selected
        {
            get
            {
                Vector2 pointer = UI.MousePositionOnUIInverted;
                // Unlocking the OS cursor can restore its old absolute position. A held press alone never selects.
                if (!selectionArmed)
                {
                    if (Time.frameCount <= openedFrame + 1) { openingPointer = pointer; return -1; }
                    if ((pointer - openingPointer).sqrMagnitude < 25f) return -1;
                    selectionArmed = true;
                }
                Vector2 delta = pointer - Center;
                return InteractionWheelRules.Pick(delta.x, delta.y, Count, Radius * .30f, Radius * 1.3f);
            }
        }
        private static string Disabled(InteractionOption option)
        {
            try
            {
                string reason = InteractionActions.NativeTargetInvalid(owner, target, clickedCell);
                if (!string.IsNullOrEmpty(reason)) return reason;
                return option.Validate != null ? option.Validate() : option.DisabledReason;
            }
            catch (Exception e)
            {
                Log.WarningOnce("[FpsBridge] Interaction validation unavailable: " + option.Id + ": " + e, 19272851);
                return L.T("该操作的检查暂不可用。", "This action's checks are temporarily unavailable.");
            }
        }
        private static void Finish(bool execute)
        {
            int index = Selected;
            InteractionOption chosen = execute && index >= 0 && options != null ? options[page * InteractionWheelRules.PageSize + index] : null;
            string reason = chosen == null ? null : Disabled(chosen);
            CloseWindow();
            if (chosen == null) return;
            if (Event.current == null) pendingCommand = () => Execute(chosen, reason);
            else Execute(chosen, reason);
        }

        private static void Execute(InteractionOption chosen, string reason)
        {
            try
            {
                if (!string.IsNullOrEmpty(reason)) Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                else if (chosen.Execute?.Invoke() != true)
                    Messages.Message(L.T("这项操作现在无法执行。", "This action cannot be performed now."), MessageTypeDefOf.RejectInput, false);
            }
            catch (Exception e)
            {
                Log.Warning("[FpsBridge] Interaction failed safely: " + chosen.Id + ": " + e);
                Messages.Message(L.T("操作未能完成，请重试。", "The action could not be completed. Please try again."), MessageTypeDefOf.RejectInput, false);
            }
        }
        internal static void Cancel() { gesture.Reset(); CloseWindow(); }
        private static void CloseWindow()
        {
            WheelWindow current = window; window = null;
            if (current != null) current.Close(false);
            closedUntil = Time.frameCount + 3;
            waitForRelease = true;
            wasBlocking = true;
            gesture.Reset();
            // owner/target references are intentionally retained until a chosen closure has completed.
            options = null; target = null;
        }
        internal static void Reset()
        {
            Cancel(); owner = null; pressedKey = KeyCode.None;
            pendingCommand = null;
            waitForRelease = wasBlocking = false; closedUntil = skipMouseUntil = -1;
            sampledFrame = -1;
        }

        private sealed class WheelWindow : Window
        {
            protected override float Margin => 0f;
            public override Vector2 InitialSize => new Vector2(UI.screenWidth, UI.screenHeight);
            public WheelWindow()
            {
                layer = WindowLayer.Dialog; doWindowBackground = false; drawShadow = false;
                doCloseX = doCloseButton = false; closeOnAccept = closeOnCancel = closeOnClickedOutside = false;
                absorbInputAroundWindow = true; preventCameraMotion = true; forcePause = false;
            }
            protected override void SetInitialSizeAndPosition() => windowRect = new Rect(0, 0, UI.screenWidth, UI.screenHeight);
            public override void PostClose()
            {
                base.PostClose();
                if (ReferenceEquals(window, this)) { window = null; gesture.Reset(); closedUntil = Time.frameCount + 3; waitForRelease = wasBlocking = true; }
            }
            public override void DoWindowContents(Rect rect)
            {
                Event e = Event.current;
                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { Cancel(); e.Use(); return; }
                if (e.type != EventType.Repaint) return;
                Color colour = GUI.color; GameFont font = Text.Font; TextAnchor anchor = Text.Anchor; bool wrap = Text.WordWrap;
                try
                {
                    GUI.color = Color.white;
                    Widgets.DrawBoxSolid(rect, new Color(.015f, .025f, .035f, .62f));
                    Vector2 center = Center; float radius = Radius; int count = Count, selected = Selected;
                    for (int i = 0; i < count; i++)
                    {
                        InteractionOption option = options[page * InteractionWheelRules.PageSize + i];
                        string disabled = Disabled(option);
                        bool enabled = string.IsNullOrEmpty(disabled), hover = selected == i;
                        Color fill = hover ? enabled ? new Color(.12f, .50f, .57f, .95f) : new Color(.42f, .19f, .19f, .95f)
                            : new Color(.075f, .12f, .16f, .95f);
                        float step = Mathf.PI * 2f / Math.Max(1, count);
                        int strips = Math.Max(14, 80 / Math.Max(1, count));
                        for (int n = 0; n < strips; n++)
                        {
                            float a = -Mathf.PI / 2 + i * step - step * .47f + (n + .5f) * step * .94f / strips;
                            Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                            Widgets.DrawLine(center + dir * radius * .33f, center + dir * radius,
                                fill, radius * step * .94f / strips + 1.5f);
                        }
                        float middle = -Mathf.PI / 2 + i * step;
                        Vector2 label = center + new Vector2(Mathf.Cos(middle), Mathf.Sin(middle)) * radius * .70f;
                        Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleCenter; Text.WordWrap = true;
                        GUI.color = enabled ? Color.white : new Color(.60f, .64f, .67f);
                        Widgets.Label(new Rect(label.x - 62, label.y - 32, 124, 64), option.Label);
                    }
                    GUI.color = new Color(.76f, .9f, .94f); Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(new Rect(center.x - 240, center.y - radius - 65, 480, 35), target?.LabelShortCap ?? L.T("地面交互", "Interact with cell"));
                    GUI.color = Color.white; Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(center.x - radius * .29f, center.y - 24, radius * .58f, 48), L.T("取消", "Cancel"));
                    string hint = L.T("移动鼠标选择 · 松开执行 · 中心 / Esc / 右键取消", "Move to select · release to act · center / Esc / RMB cancels");
                    Widgets.Label(new Rect(center.x - 330, center.y + radius + 20, 660, 30), hint);
                    if (count == 0) Widgets.Label(new Rect(center.x - 260, center.y + radius + 50, 520, 50), L.T("没有可执行的交互。", "No interactions available."));
                    else if (selected >= 0)
                    {
                        InteractionOption option = options[page * InteractionWheelRules.PageSize + selected];
                        string reason = Disabled(option);
                        Widgets.Label(new Rect(center.x - 350, center.y + radius + 48, 700, 36), option.Label);
                        if (!string.IsNullOrEmpty(reason))
                        { GUI.color = new Color(1f, .65f, .52f); Widgets.Label(new Rect(center.x - 350, center.y + radius + 83, 700, 44), reason); }
                    }
                    if (options?.Count > InteractionWheelRules.PageSize)
                    { GUI.color = Color.white; Widgets.Label(new Rect(center.x - 150, center.y - radius - 92, 300, 25), (page + 1) + " / " + ((options.Count + 7) / 8) + L.T(" · 滚轮翻页", " · wheel to change page")); }
                }
                finally { GUI.color = colour; Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wrap; }
            }
        }
    }
}
