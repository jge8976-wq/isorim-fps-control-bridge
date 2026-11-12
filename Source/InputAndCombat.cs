using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Bridge
    {
        private static int toggleFrame = -1, combatFrame = -1, filterFrame = -1;
        private static bool filterQE;
        private static KeyBindingDef isoToggle;
        private static bool isoToggleLooked;
        private static ThingDef reportedWeapon;
        private static Type reportedVerb;

        private static void HandleSideButton()
        {
            if (!Bootstrap.Ready || toggleFrame == Time.frameCount || !Application.isFocused
                || Current.ProgramState != ProgramState.Playing || LongEventHandler.ShouldWaitForEvent
                || VehicleControl.Descending(ShootingPawn) || !ControlKeys.Down(ControlAction.ViewCycle)) return;
            if (InteractionWheel.Open) InteractionWheel.Cancel();
            if (GUIUtility.keyboardControl != 0 || Find.WindowStack.AnyWindowAbsorbingAllInput) return;
            toggleFrame = Time.frameCount;
            // Three views keep the same possession; the fourth step releases it, even while paused.
            if (Active)
            {
                // On a turret the front button steps back off it onto the pawn's own view.
                if (TurretControl.Manned) { TurretControl.Leave(L.T("离开炮台", "Left the turret")); return; }
                ControlView? next = ViewRules.Next(ControlCamera.Mode);
                if (next.HasValue) ControlCamera.Set(next.Value); else FirstPerson.Leave(null);
                return;
            }
            if (GUIUtility.keyboardControl != 0 || Find.WindowStack.WindowsPreventCameraMotion
                || Find.WindowStack.AnyWindowAbsorbingAllInput || MainTabBlocks) return;
            if (Find.Selector.NumSelected == 0 && Bindings.Avatar != null)
            {
                Pawn current = Bindings.PawnOf(Bindings.Avatar);
                if (CanPossess(current)) Find.Selector.Select(current);
            }
            if (!VehicleControl.TrySelected()) FirstPerson.Toggle();
        }

        internal static bool HasMapInputContext => Current.ProgramState == ProgramState.Playing
            && Find.UIRoot is UIRoot_Play && Find.CurrentMap != null;

        public static bool FilterKeyBinding(KeyBindingDef __instance, ref bool __result)
        {
            // Vanilla and other mods also poll these global getters in the entry UI.
            if (!HasMapInputContext) return true;
            if (__instance.defName == "TogglePause" && PauseChord.IsDown)
            { __result = false; return false; }
            if(AerialOrders.Consumes(__instance)){__result=false;return false;}
            // Keep PS's separate action/state shortcuts out of our three pawn views, including remapped keys.
            // Our directional postfix reads FPS KeyPrefs directly; PS's own IsDown values are not needed here.
            if (Active && IsPerspectiveKey(__instance)) { __result = false; return false; }
            if (Active && InteractionWheel.Blocking && !WorkControl.IsSpeedBinding(__instance)
                && (__instance == KeyBindingDefOf.Cancel || __instance.defName.StartsWith("PS_", StringComparison.Ordinal)
                    || (KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(__instance, out var wheelKeys)
                        && (Owned(wheelKeys.keyBindingA) || Owned(wheelKeys.keyBindingB)))))
            { __result = false; return false; }
            // The skill dispatch reads both live KeyPrefs slots directly; getters cannot launch a second route.
            if (ControlKeys.IsBinding(__instance)) { __result = false; return false; }
            // Perspective Shift's old Q/E tabs must not race our tap/hold input (including while I's tab is open).
            if (Active && (__instance.defName == "PS_OpenGearTab" || __instance.defName == "PS_NeedsTab"))
            { __result = false; return false; }
            if (Active && InventoryPanel.Blocking && KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(__instance, out var inventoryKeys)
                && (ControlKeys.Matches(ControlAction.Inventory, inventoryKeys.keyBindingA) || ControlKeys.Matches(ControlAction.Inventory, inventoryKeys.keyBindingB)))
            { __result = false; return false; }
            if ((RemoteTurret.Captured || VehicleControl.Captured) && KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(__instance, out var remoteKeys)
                && (VehicleControl.Captured ? Owned(remoteKeys.keyBindingA) || Owned(remoteKeys.keyBindingB) : RemoteOwned(remoteKeys.keyBindingA) || RemoteOwned(remoteKeys.keyBindingB)))
            { __result = false; return false; }
            // UnityStubs' KeyBindingDef.JustPressed does not read mouse buttons. Our frame hook
            // owns Mouse4; suppress the old F5 route to avoid two toggles in one frame.
            // This getter runs for every key binding queried each frame: compare by reference and evaluate the
            // capture context once per frame.
            if (!isoToggleLooked) { isoToggle = DefDatabase<KeyBindingDef>.GetNamedSilentFail("R3D_FirstPerson"); isoToggleLooked = true; }
            if (ReferenceEquals(__instance, isoToggle)) { __result = false; return false; }
            if (filterFrame != Time.frameCount) { filterFrame = Time.frameCount; filterQE = Active && policy.Captured && LookAllows(); }
            // During auto work pause and game speed reach the game (Space, 1-4 are posture/ability keys otherwise).
            if (!filterQE || (WorkControl.Auto && WorkControl.IsSpeedBinding(__instance))
                || !KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(__instance, out var keys)) return true;
            if (Owned(keys.keyBindingA) || Owned(keys.keyBindingB))
            { __result = false; return false; }
            return true;
        }

        private static bool RemoteOwned(KeyCode k) => ControlKeys.Owns(k, remote: true) || k == KeyCode.LeftAlt || k == KeyCode.RightAlt;

        // Q/E lean/interact (or turret network), Tab weapons, I inventory; the remaining session controls follow.
        private static bool Owned(KeyCode k) => ControlKeys.Owns(k) || AbilityBar.Owns(k)
            || k == KeyCode.LeftAlt || k == KeyCode.RightAlt; // keep the vanilla Alt cell inspector out of FPS

        private static int backFrame = -1;
        private static Resume recall;

        // Back side button (Mouse3): from any possessed view straight to the command view; pressed again there, back
        // into the same pawn and view.
        private static void HandleBackButton()
        {
            if (!Bootstrap.Ready || backFrame == Time.frameCount || !Application.isFocused
                || Current.ProgramState != ProgramState.Playing || LongEventHandler.ShouldWaitForEvent
                || !ControlKeys.Down(ControlAction.ExitRecall)) return;
            if (InteractionWheel.Open) InteractionWheel.Cancel();
            if (GUIUtility.keyboardControl != 0 || Find.WindowStack.AnyWindowAbsorbingAllInput) return;
            backFrame = Time.frameCount;
            if (Active)
            {
                // A borrowed Perspective Shift avatar stays the user's; only our own session is recorded for the way back.
                recall = ownsAvatar ? Snapshot() : null;
                EndAndLeave("back button");
                return;
            }
            if (recall == null || FirstPerson.On) return;
            if (recall.game != Current.Game) { recall = null; return; }
            if (GUIUtility.keyboardControl != 0 || Find.WindowStack.WindowsPreventCameraMotion
                || Find.WindowStack.AnyWindowAbsorbingAllInput || MainTabBlocks) return;
            Resume r = recall;
            recall = null;
            if (!Reenter(r)) Messages.Message(L.T("无法回到上一个视角：该人物现在不可控制。", "Cannot return to the previous view: that pawn cannot be controlled now."), MessageTypeDefOf.RejectInput, false);
        }

        public static bool BeforeWindowAdd(Window window)
        {
            if (FirstPerson.On && window is LudeonTK.EditWindow_Log) return false;
            if (InteractionWheel.Open && !InteractionWheel.Owns(window)) InteractionWheel.Cancel();
            return true;
        }

        // The scope owns wheel input; do not also change the command camera's zoom underneath it.
        public static bool BeforeCameraGUI()
        {
            if (Active && InteractionWheel.Blocking && Event.current != null
                && (Event.current.isMouse || Event.current.type == EventType.ScrollWheel))
            { Event.current.Use(); return false; }
            if ((RemoteTurret.Captured || VehicleControl.Captured) && Event.current != null
                && (Event.current.isMouse || Event.current.type == EventType.ScrollWheel))
            { Event.current.Use(); return false; }
            // (0.9.0: in first person the wheel also chooses from the pack)
            if (Active && policy.Captured && LookAllows() && (Artillery.On || TurretControl.Manned || !ControlCamera.External)
                && Event.current?.type == EventType.ScrollWheel)
            { Event.current.Use(); return false; }
            return true;
        }

        public static void AfterWindowRemoved(Window window, bool __result)
        {
            if (!__result || !Active || Find.WindowStack.WindowsPreventCameraMotion
                || Find.WindowStack.AnyWindowAbsorbingAllInput || Find.WindowStack.AnySearchWidgetFocused) return;
            // A closed animal/gear tab must not retain a dead text control's keyboard focus.
            GUIUtility.keyboardControl = 0;
            GUI.FocusControl(null);
        }

        private static string CaptureStatus()
        {
            if (InteractionWheel.Open) return L.T("交互轮盘 · 松开执行 · Esc 取消", "Interaction wheel · release to act · Esc to cancel");
            if (!policy.WantsCapture) return L.T("鼠标已释放 · ~ 恢复", "Mouse released · ~ to recapture");
            if (Find.TickManager?.Paused == true) return L.T("游戏暂停 · 可转视角，继续后可移动和射击", "Paused · you can look around; move and shoot after resuming");
            if (!Application.isFocused) return L.T("窗口失去焦点", "Window not focused");
            if (!ContextAllows()) return L.T("面板 / 输入框 · 关闭后自动恢复", "Panel / text box open · control returns when it closes");
            if (WorkControl.Auto) return L.T("自动工作中 · 任意键接管 · 后侧键回上帝视角", "Auto-working · any key to take over · back button to the god view");
            if (Artillery.On) return L.T("炮兵模式 · Q/E 切换炮台 · F / 前侧键离开 · 后侧键回上帝视角", "Artillery · Q/E switch turret · F / forward button to leave · back button to the god view");
            if (TurretControl.Manned) return L.T("操纵炮台 · 左键开火 · Q/E 切换炮台 · F / 前侧键离开 · 后侧键回上帝视角", "Manning a turret · LMB fire · Q/E switch turret · F / forward button to leave · back button to the god view");
            string posture = Stances.Label;
            return policy.Captured ? ControlCamera.Label + (posture != null ? " · " + posture : "") + (ControlCamera.Mode == ControlView.Overhead ? L.T(" · 前侧键退出控制", " · forward button to leave") : L.T(" · 前侧键下一视角", " · forward button for the next view")) : L.T("正在恢复控制", "Restoring control");
        }

        // Replace only PS's two movement checks, not its external-mod capability flag.
        // In particular, never route into RunAndGun stance constructors when that mod is absent.
        public static IEnumerable<CodeInstruction> WalkingFire(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(Bindings.RunAndGun))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(Bridge), nameof(CanWalkDuringFire));
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("Expected one PS movement run-and-gun check, found " + replaced);
        }

        public static bool CanWalkDuringFire(Pawn candidate) =>
            (Active && candidate == pawn && pawn.CurJob?.ability == null
                && pawn.equipment?.PrimaryEq?.PrimaryVerb is Verb verb && !verb.verbProps.IsMeleeAttack)
            || Bindings.UpstreamRunAndGun(candidate);

        public static bool AllowCombatStance(object __instance) => !Ours(__instance);
        public static bool AllowReticle(object __instance) => !Ours(__instance);
        // FPS already draws a reticle-based interaction prompt. The old PS hint uses an orthographic mouse cell,
        // which can lie outside the map when looking through the first-person camera.
        public static bool AllowEquipHint(object __instance) => !VehicleControl.On && !Ours(__instance);
        public static bool AllowHoldFire(object __instance) => !Ours(__instance);
        // PS's own firing is replaced: the frame input (CombatFrame) owns every attack of our pawn.
        public static bool BeforeFiring(object __instance) => !Ours(__instance);

        public static bool BeforeSelectorClick(object __instance, ref bool __result)
        {
            if (Active && InteractionWheel.Blocking) { Event.current?.Use(); __result = true; return false; }
            if (VehicleControl.OwnsMapInput) { __result = true; return false; }
            if (!Ours(__instance) || !CanDrive() || Find.Targeter.IsTargeting) return true;
            // LMB fires and RMB aims (guns) or slashes (blades): neither may reach PS's click handling (RMB = end job).
            if (Event.current?.type != EventType.MouseDown || (Event.current.button != 0 && Event.current.button != 1)) return true;
            Event.current.Use(); __result = true;
            return false; // The frame input owns this click, independent of the hidden cursor position.
        }

        private static Verb CurrentVerb()
        {
            Verb verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (verb == null && pawn.VerbTracker != null)
                foreach (Verb candidate in pawn.VerbTracker.AllVerbs)
                    if (candidate is Verb_MeleeAttack && candidate.Available()) { verb = candidate; break; }
            return verb;
        }

        // R4 dispatch, once per frame: rounds by FireControl (modes, heat), hand grenades thrown along the view, melee
        // thrust/slash, anything else (launchers, sweeping beams, abilities' verbs) by the game's own cast.
        private static void CombatFrame()
        {
            if (FirstPersonTransition.Active) return;
            if (combatFrame == Time.frameCount) return;
            combatFrame = Time.frameCount;
            if (!CanDrive() || QuickTend.For(pawn) || ConsumableHands.Using(pawn) || Magazines.Reloading || Find.Targeter.IsTargeting || WorkControl.Swallow || Vault.Active) { FireControl.CancelInput(); return; }
            if (TurretControl.Manned) { TurretControl.Frame(pawn, ControlKeys.Held(ControlAction.Fire), ControlKeys.Down(ControlAction.Fire)); return; }
            MechControl.Combat(pawn, ControlKeys.Held(ControlAction.Aim));
            if (!AltHeld && AbilityBar.Frame(pawn)) { FireControl.CancelInput(); return; }
            Bindings.LeanField.SetValue(avatar, Vector3.zero);
            Bindings.LeanSmoothedField.SetValue(avatar, Vector3.zero);
            if (pawn.stances.stunner.Stunned || pawn.CurJob?.ability != null || WeaponCycle.Pending
                || pawn.WorkTagIsDisabled(WorkTags.Violent)) { FireControl.CancelInput(); return; }
            bool held = ControlKeys.Held(ControlAction.Fire), pressed = ControlKeys.Down(ControlAction.Fire), slash = ControlKeys.Down(ControlAction.Aim);
            if (held || pressed || ControlKeys.Held(ControlAction.Aim)) Stances.State.CancelSprint();   // attacking or aiming ends a sprint
            Verb verb = CurrentVerb();
            if (verb == null) return;
            ReportWeapon(verb);
            if (verb.verbProps.IsMeleeAttack) { if (verb.Available()) Melee.Frame(pawn, verb, held, slash, ControlKeys.Held(ControlAction.Aim)); return; }
            if (pawn.WorkTagIsDisabled(WorkTags.Shooting)) return;
            // CE verbs are not Verse.Verb_LaunchProjectile. They must retain the actual CE shot/ammo sequence.
            if (CeCompat.Owns(verb)) { CeCompat.Frame(pawn, verb, held, pressed); return; }
            if (SpecialWeapons.Uses(verb)) { SpecialWeapons.Frame(pawn, verb, held, pressed); return; }
            if (!verb.Available()) return;
            if (verb is Verb_LaunchProjectile launch)
            {
                if (Ballistics.IsRound(verb)) { FireControl.Frame(pawn, launch, held, pressed); return; }
                if (Ballistics.IsGrenade(verb)) { Grenades.Frame(pawn, launch, pressed); return; }
            }
            if (held && Magazines.Ready(pawn, verb)) NativeFire(verb);
        }

        // Record the real definition and execution route once per equipment change for third-party weapon reports.
        // A translated display name alone cannot identify a custom beam, projectile or ability implementation.
        private static void ReportWeapon(Verb verb)
        {
            ThingDef def = verb.EquipmentSource?.def ?? pawn.def;
            Type type = verb.GetType();
            if (reportedWeapon == def && reportedVerb == type) return;
            reportedWeapon = def; reportedVerb = type;
            ThingDef projectile = (verb as Verb_LaunchProjectile)?.Projectile;
            string route = CeCompat.Owns(verb) ? "CE_NATIVE_CANDIDATE" : verb.verbProps.IsMeleeAttack ? "MELEE" : SpecialWeapons.Uses(verb) ? "SPECIAL_NATIVE_MOBILE" : Ballistics.IsBeam(verb) && Ballistics.Enabled ? "3D_BEAM" : Ballistics.IsRound(verb) ? "3D_ROUND"
                : Ballistics.IsGrenade(verb) ? "3D_GRENADE" : "NATIVE";
            Log.Message("[FpsBridge] WEAPON_ROUTE label=" + def.label + "; def=" + def.defName + "; mod=" + def.modContentPack?.Name
                + "; verb=" + type.FullName + "; projectile=" + projectile?.defName + "; class=" + projectile?.thingClass?.FullName + "; route=" + route);
        }

        private static void NativeFire(Verb verb)
        {
            if (pawn.stances.curStance is Stance_Busy) return;
            LocalTargetInfo target = AimPicker.Target(pawn, verb);
            // Nothing under the crosshair within reach (sky, far ground): any visible cell along the view keeps the
            // game's range and sight checks satisfied.
            if (!target.IsValid) target = Ballistics.FallbackTarget(pawn, verb);
            if (target.IsValid && verb.CanHitTarget(target)) verb.TryStartCastOn(target, false, true);
        }

        // Drawing in the 3D view (also while paused): the weapon in hand, Alt hit volumes, the grenade's arc.
        // A drawing failure never ends the session.
        private static void DrawFrame()
        {
            if (FirstPersonTransition.Active) return;
            try
            {
                bool moving = ((Vector3)Bindings.MoveField.GetValue(avatar)).sqrMagnitude > 0.01f;
                bool aim = !InteractionWheel.Blocking && policy.Captured && LookAllows() && ControlKeys.Held(ControlAction.Aim);
                if (!ControlCamera.External) WeaponView.Frame(pawn, aim, moving, !InteractionWheel.Blocking && policy.Captured && LookAllows());
                AimMotion.SwayScale = (WeaponView.SteadySingleAim ? .2f : 1f - 0.55f * WeaponView.AimBlend) * (Stances.For(pawn) ? Stances.State.SwayFactor : 1f) * MechControl.SwayFactor(pawn) * Condition.SwayFactor(pawn);
                if (!View3D.Active) return;
                Ballistics.DrawBeams(pawn.Map);
                MeleeFx.Draw();   // the blade's trail
                MountedControl.Draw(pawn);
                if (!ControlCamera.External) { if (ConsumableHands.Busy) ConsumableHands.Draw(); else SocialHands.Draw(); }
                if (Melee.Whirling(pawn)) MeleeFx.Whirl(FloorSpace.Spot(pawn), Ballistics.Eye(pawn), ControlCamera.AttackPitch(pawn), Melee.WhirlAngle, Melee.WhirlRadius(pawn));
                Artillery.Draw3D(pawn);
                if (policy.Captured && HitboxView.Held) HitboxView.Draw(pawn);
                if (!TurretControl.Manned && pawn.equipment?.PrimaryEq?.PrimaryVerb is Verb_LaunchProjectile g && Ballistics.IsGrenade(g)) Grenades.Preview(pawn, g);
                if(!TurretControl.Manned&&CeCompat.Owns(pawn.equipment?.PrimaryEq?.PrimaryVerb))CeCompat.Preview(pawn,pawn.equipment.PrimaryEq.PrimaryVerb);
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] First-person drawing failed (control continues): " + e, 19271706); }
        }

        // Native bursts (launchers and other verbs the bridge does not fire itself) follow the view between shots.
        public static bool BeforeBurstShot(Verb __instance, ref LocalTargetInfo ___currentTarget)
        {
            if (CeCompat.Owns(__instance)) return CeCompat.BeforeBurst(__instance, ref ___currentTarget);
            if (Active && InteractionWheel.Blocking && __instance.CasterPawn == pawn)
            { InteractionWheel.StopNativeAttack(pawn); return false; }
            // A turret operated in person: its bursts follow the crosshair too.
            if (TurretControl.Current?.OwnsVerb(__instance) == true)
            {
                VanillaTurret.ShapeBurst(__instance);
                if (TurretControl.Target.IsValid) ___currentTarget = TurretControl.Target;
                return true;
            }
            if (!CanDrive() || __instance != pawn.equipment?.PrimaryEq?.PrimaryVerb
                || __instance.verbProps.IsMeleeAttack) return true;
            LocalTargetInfo target = LocalTargetInfo.Invalid;
            Guard(() => {
                target = AimPicker.Target(pawn, __instance);
                if (!target.IsValid) target = Ballistics.FallbackTarget(pawn, __instance);
            });
            if (target.IsValid) ___currentTarget = target;
            return true;
        }
    }
}
