using System;
using IsoRim;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Bridge
    {
        private static Pawn pawn;
        private static Game game;
        private static object avatar, previousMode, appliedMode;
        private static bool ownsAvatar, ending, originalCursorVisible, originalDrafted;
        private static CursorLockMode originalCursorLock;
        private static float yaw;
        private static int inputFrame = -1;
        private static readonly CapturePolicy policy = new CapturePolicy();
        internal static bool Active => pawn != null && !ending;
        // The pawn whose shots use first-person ballistics (any shot while the session is live, input focus or not).
        internal static Pawn ShootingPawn => Active ? pawn : null;

        // Re-entry after a save (autosave included): the session is closed before serialization and reopened after.
        private sealed class Resume
        {
            public Pawn pawn; public Game game; public float yaw, pitch; public bool drafted, originalDrafted, wantsCapture, ownsAvatar;
            public int frame; public ControlView view;
        }
        private static Resume resume;
        private static bool Ours(object instance) => Active && ReferenceEquals(instance, avatar);

        public static void AfterToggle()
        {
            Guard(() => {
                if (!Bootstrap.Ready || !FirstPerson.On) return;
                Pawn chosen = FirstPerson.Pawn;
                if (Active && chosen == pawn) return;
                End("switch", true);
                if (!CanPossess(chosen)) { Messages.Message(L.T("请选择可控制的己方人物。", "Select a colonist or mech of yours that can be controlled."), MessageTypeDefOf.RejectInput, false); FirstPerson.Leave(null); return; }
                object oldAvatar = Bindings.Avatar;
                if (oldAvatar != null && Bindings.PawnOf(oldAvatar) != chosen)
                {
                    Messages.Message(L.T("请先退出 Perspective Shift 当前接管，再按鼠标前侧键。", "Leave Perspective Shift's current possession first, then press the mouse forward button."), MessageTypeDefOf.RejectInput, false);
                    FirstPerson.Leave(null);
                    return;
                }
                var turretWork = TurretControl.CurrentWork(chosen);
                originalCursorLock = Cursor.lockState;
                originalCursorVisible = Cursor.visible;
                originalDrafted = chosen.Drafted;
                previousMode = Bindings.ModeField.GetValue(null);
                appliedMode = null;
                ownsAvatar = oldAvatar == null;
                // All adapter work can fail; retain an ownership record before changing upstream state.
                pawn = chosen; game = Current.Game;
                ResetMovementRecovery();
                if (ownsAvatar)
                {
                    if (previousMode.ToString() == "Director")
                    {
                        appliedMode = Enum.Parse(Bindings.ModeField.FieldType, "Dynamic");
                        Bindings.ModeField.SetValue(null, appliedMode);
                    }
                    TurretControl.BindAvatar(chosen, turretWork != null);
                }
                avatar = Bindings.Avatar;
                if (Bindings.PawnOf(avatar) != chosen) throw new InvalidOperationException("SetAvatar did not bind selected pawn");
                yaw = chosen.Rotation.AsAngle;
                Resume back = resume != null && resume.pawn == chosen && resume.game == Current.Game ? resume : null;
                resume = null;
                if (back != null)
                {
                    yaw = back.yaw;
                    originalDrafted = back.originalDrafted;
                    if (chosen.drafter != null && chosen.Drafted != back.drafted) chosen.drafter.Drafted = back.drafted;
                }
                StabilizeHeading();
                if (back != null) FirstPerson.Look(0f, back.pitch - FirstPerson.Pitch);
                policy.Enter(back == null || back.wantsCapture); inputFrame = -1;
                // Postures are a person's; a mech gets its own link and move instead.
                if (!MechControl.IsMech(chosen)) Stances.Begin(chosen);
                MechControl.Begin(chosen);
                DubsMinimap.Begin();
                WorkControl.Begin(chosen);
                InventoryPanel.Reset();
                LeanControl.Begin(chosen);
                AimMotion.Reset();
                ControlCamera.Set(back?.view ?? ControlView.FirstPerson);
                if (back != null) FirstPerson.Look(0f, back.pitch - FirstPerson.Pitch);
                Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), false);
                // The pawn's inspect pane (bottom left) from selecting it before entering: not part of the view.
                Find.Selector.ClearSelection();
                if (Find.MainTabsRoot.OpenTab == MainButtonDefOf.Inspect) Find.MainTabsRoot.EscapeCurrentTab(false);
                WeaponCycle.Reset(); AbilityBar.Reset();
                if (turretWork != null) TurretControl.TakeCurrentWork(chosen, turretWork);
                else if (back == null && !VehicleControl.Descending(chosen)) FirstPersonTransition.Begin(chosen);
                TickInput();
                BeginControlDiagnostics();
                Log.Message("[FpsBridge] ENTER pawn=" + chosen.thingIDNumber + "; ownsAvatar=" + ownsAvatar);
            });
        }

        private static Pawn leavingPawn;
        public static void BeforeLeave() { leavingPawn = FirstPerson.Pawn; Guard(() => End("IsoRim leave", true)); }
        public static void AfterLeave()
        {
            Pawn was = leavingPawn;
            leavingPawn = null;
            try { FloorSpace.KeepCameraOn(was); }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Camera floor jump after leaving failed: " + e, 19271707); }
        }

        public static void BeforeFirstPerson()
        {
            Guard(() => {
                if (!Active) return;
                if (!ValidSession()) { EndAndLeave("invalid pawn/map/session"); return; }
                TickInput();
                StabilizeHeading();
            });
        }

        public static void AfterFirstPerson() { Guard(() => { if (Active) { StabilizeHeading(); FaceView(); } }); }
        public static bool AllowOriginalLook() => !Active && !RemoteTurret.On && !VehicleControl.On;
        public static bool AllowAvatarCamera(object __instance) => !Ours(__instance);
        public static bool AllowAvatarRotation(object __instance) => !Ours(__instance);

        public static void AfterMoveInput(object __instance, ref Vector3 ___moveInput)
        {
            if (!Ours(__instance)) return;
            // Riding a ladder/elevator: no input, so the ride is not cut short (it is ProjectZ's own step).
            if (!CanDrive() || QuickTend.For(pawn) || TurretControl.Manned || MechControl.Locked || ProjectZStairs.Riding(pawn) || Vault.Active) { ___moveInput = Vector3.zero; return; }
            Vector3 intent = new Vector3((ControlKeys.Held(ControlAction.Right) ? 1f : 0f) - (ControlKeys.Held(ControlAction.Left) ? 1f : 0f), 0f,
                (ControlKeys.Held(ControlAction.Forward) ? 1f : 0f) - (ControlKeys.Held(ControlAction.Backward) ? 1f : 0f));
            if (intent.sqrMagnitude > 1f) intent.Normalize();
            var v = MovementIntent.Rotate(intent.x, intent.z, yaw);
            ___moveInput = new Vector3(v.x, 0f, v.z);
            // 0.7.0: the bridge's own toggles decide running (Perspective Shift's sprint, boosted) and never its sneak.
            if (Stances.Enabled)
            {
                Bindings.SprintField.SetValue(__instance, !MountedControl.For(pawn) && Stances.State.MovementSprint && ___moveInput.sqrMagnitude > 0.01f);
                Bindings.WalkField.SetValue(__instance, false);
            }
        }

        public static bool BeforePhysics(object __instance)
        {
            if (!Ours(__instance)) return true;
            if (MountedControl.Flying || TurretControl.Manned || VehicleControl.Descending(pawn)) return false;
            if (QuickTend.For(pawn)) { Bindings.ClearMovement(__instance); return false; }
            // On a wall/roof top: our own walking instead of Perspective Shift's, which would push the pawn off.
            if (Surface.For(pawn)) { bool drive = CanDrive(); Guard(() => Surface.Physics(pawn, avatar, drive)); return false; }
            if (CanDrive()) return true;
            Bindings.ClearMovement(__instance);
            return false;
        }

        public static void AfterPhysics(object __instance) { if (Ours(__instance)) Guard(FaceView); }
        public static bool BeforeProcessMovement(object __instance)
        {
            if (!Ours(__instance)) return true;
            if (InteractionWheel.Blocking) { Bindings.ClearMovement(__instance); return false; }
            if (QuickTend.For(pawn)) { Bindings.ClearMovement(__instance); return false; }
            if (!CanDrive() || pawn.CurJob?.ability != null) return true;
            bool stepped = false;
            // Off an upper floor's edge straight onto a wall/roof top at the same height.
            Guard(() => stepped = Surface.TryWalkFromFloor(pawn, avatar, (Vector3)Bindings.MoveField.GetValue(avatar)));
            if (stepped) return false;
            Guard(() => stepped = ProjectZStairs.TryStep(pawn, avatar, (Vector3)Bindings.MoveField.GetValue(avatar)));
            return !stepped;
        }
        public static void AfterControlsFrozen(ref bool __result) { if (Active && !CanDrive()) __result = true; }

        public static void BeforeAvatarClear()
        {
            Guard(() => { if (Active) { End("Perspective Shift leave", false); FirstPerson.Leave(null); } });
        }

        public static void FrameBegin() { Guard(() => {
            FirstPersonFloors.Sync();
            if (PauseChord.Frame()) return;
            CombatMusicManager.Frame();
            FirstPersonTransition.Observe2D();
            VehicleControl.VehicleDiagnosticsFrame();
            if (FirstPersonTransition.Frame()) { if(avatar!=null) Bindings.ClearMovement(avatar); return; }
            Knockback.Frame();
            if (VehicleControl.On) { VehicleControl.Frame(); return; }
            if (RemoteTurret.On) { RemoteTurret.Frame(); WeaponLight.Frame(); return; }
            ResumeAfterSave();
            HandleSideButton();
            HandleBackButton();
            if (Active) { if (!ValidSession()) EndAndLeave("frame validation"); else TickInput(); }
            Surface.Frame(ShootingPawn);
            WeaponLight.Frame();
        }); }
        public static void FrameEnd()
        {
            Guard(() =>
            {
                FirstPersonFloors.Sync();
                MovementRecoveryFrame();
                MovementRenderFrame();
                if (VehicleControl.On) VehicleControl.ApplyCursor();
                else if (Active)
                {
                    if (TurretControl.Current is CeTurret ce)
                        ce.MaintainReload(CanDrive() && (ControlKeys.Held(ControlAction.Fire) || ControlKeys.Down(ControlAction.Fire)));
                    CombatFrame();
                    DrawFrame();
                    ApplyCursor();
                }
                ControlDiagnosticsFrame();
            });
        }
        public static void GameDisposing() { Guard(() => { FirstPersonFloors.Release(); FirstPersonTransition.Forget(); CombatMusicManager.Release(); VehicleControl.End(); RemoteTurret.End(null); resume = null; recall = null; if (Active) EndAndLeave("game dispose"); Surface.Release(); RoofWear.Reset(); FogPeek.Reset(); CyberSkills.End(); Ballistics.Reset(); FireControl.Reset(); SpecialWeapons.Reset(); CeCompat.Reset(); MeleeReach.Reset(); Knockback.Reset(); }); }

        // Use PS's normal cleanup before serialization so saved lord/draft references are restored too.
        // A borrowed PS avatar belongs to the user and is preserved by End.
        public static void BeforeWorldSave()
        {
            if (Scribe.mode == LoadSaveMode.Saving) InteractionWheel.Reset();
            if (Scribe.mode == LoadSaveMode.Saving) WeaponLight.Release();
            if (Scribe.mode == LoadSaveMode.Saving && VehicleControl.On) VehicleControl.End();
            if (Scribe.mode == LoadSaveMode.Saving && RemoteTurret.On) RemoteTurret.End(null);
            if (Scribe.mode != LoadSaveMode.Saving || !Active) return;
            Guard(() => {
                Resume r = Snapshot();
                EndAndLeave("save game");
                // Only a session the bridge itself opened is reopened; a borrowed PS avatar stays the user's.
                resume = r.ownsAvatar ? r : null;
            });
        }

        // After the save's long event: reopen the same session (view, draft state and mouse capture as before).
        private static void ResumeAfterSave()
        {
            if (resume == null) return;
            if (Current.Game != resume.game || Current.ProgramState != ProgramState.Playing || Time.frameCount - resume.frame > 3600)
            { resume = null; return; }
            if (LongEventHandler.AnyEventNowOrWaiting || Time.frameCount - resume.frame < 2 || Active || FirstPerson.On) return;
            Resume r = resume;
            resume = null;
            if (Reenter(r)) Log.Message("[FpsBridge] RESUMED after save pawn=" + r.pawn.thingIDNumber);
        }

        private static Resume Snapshot() => new Resume { pawn = pawn, game = game, yaw = yaw, pitch = FirstPerson.Pitch, drafted = pawn.Drafted,
            originalDrafted = originalDrafted, wantsCapture = policy.WantsCapture, ownsAvatar = ownsAvatar, frame = Time.frameCount, view = ControlCamera.Mode };

        // Reopens a recorded session (view, heading, draft and capture as recorded); AfterToggle consumes the record.
        private static bool Reenter(Resume r)
        {
            Pawn p = r.pawn;
            if (!CanPossess(p) || Bindings.Avatar != null || FirstPerson.On) return false;
            resume = r;
            try
            {
                Find.Selector.ClearSelection();
                Find.Selector.Select(p, false, false);
                FirstPerson.Toggle();
            }
            finally { resume = null; }
            return Active;
        }

        public static void AfterAvatarGUI()
        {
            if (FirstPersonTransition.Active) return;
            Guard(() => {
                if (!Active) return;
                ApplyCursor();
                if (Event.current == null || Event.current.type != EventType.Repaint) return;
                if (InventoryPanel.Blocking) return;
                Condition.DrawOverlay(pawn);   // wounds and suppression at the view's edges, under everything else
                IffMarkers.Draw(pawn);   // under the rest of the HUD
                HitMarker.Draw();
                MeleeFx.DrawLabels();   // body parts struck in melee
                TargetMark.Draw();
                CyberSkills.DrawHud(pawn);   // Sandevistan / Death Eye edges and lock mark
                if (policy.Captured && TurretControl.Manned) TurretControl.DrawHud(pawn);
                else if (policy.Captured)
                {
                    float x = UI.screenWidth * 0.5f, y = UI.screenHeight * 0.5f;
                    if (!WorkControl.Auto) TurretControl.DrawHud(pawn);   // "E 操纵" prompt
                    Pocket.DrawHud(pawn);
                    ConsumableHands.DrawHud();
                    ObjectDurability.DrawHud(pawn);
                    if (!WorkControl.Auto && !TurretControl.Manned) MountInteraction.DrawHud(pawn);
                    if (!WorkControl.Auto && !TurretControl.Manned) VehicleControl.DrawPrompt(pawn);
                    if (!WorkControl.Auto && !TurretControl.Manned) Social.DrawHud(pawn);
                    string ride = WorkControl.Auto ? null : ProjectZStairs.Prompt(pawn);
                    if (ride != null)
                    {
                        GameFont f1 = Text.Font; TextAnchor a1 = Text.Anchor; Color c1 = GUI.color;
                        Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperCenter; GUI.color = new Color(.6f, .9f, 1f);
                        Widgets.Label(new Rect(x - 200, y + 46, 400, 24), ControlHints.For(ride));
                        Text.Font = f1; Text.Anchor = a1; GUI.color = c1;
                    }
                    Color tint = pawn.stances.curStance is Stance_Busy ? new Color(1f, .7f, .25f) : Color.white;
                    // Iron sights replace the crosshair; a dot stays for weapons without a sight in the model.
                    bool scopeDrawn = ScopeHud.Draw();
                    if (!scopeDrawn && WeaponView.SimpleReticle)
                    {
                        // Simple scope crosshair, centred on the exact same ray as the shot.
                        Widgets.DrawBoxSolid(new Rect(x - 46, y - 1, 93, 3), Color.black);
                        Widgets.DrawBoxSolid(new Rect(x - 1, y - 46, 3, 93), Color.black);
                        Widgets.DrawBoxSolid(new Rect(x - 45, y, 91, 1), Color.white);
                        Widgets.DrawBoxSolid(new Rect(x, y - 45, 1, 91), Color.white);
                    }
                    else if (!scopeDrawn && !WeaponView.Aiming)
                    {
                        Widgets.DrawBoxSolid(new Rect(x - 5, y, 11, 1), tint);
                        Widgets.DrawBoxSolid(new Rect(x, y - 5, 1, 11), tint);
                    }
                    FireControl.DrawHud(pawn, WeaponView.Aiming);
                    if (FpsMod.Settings.showHints && HitboxView.Held)
                    {
                        GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
                        Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperCenter; GUI.color = new Color(1f, 1f, 1f, .8f);
                        Widgets.Label(new Rect(x - 300, 60, 600, 22), L.T("按住 Alt 查看 · 轻按切换 · 红 头部 · 黄 躯干 · 蓝 腿部 · 青 乘员席",
                            "Hold Alt to view · Tap to toggle · Red head · Yellow torso · Blue legs · Cyan crew"));
                        Text.Font = f0; Text.Anchor = a0; GUI.color = c0;
                    }
                    if (FpsMod.Settings.showHints && Ballistics.LastHit != null && Time.unscaledTime < Ballistics.LastHitUntil)
                    {
                        GameFont font = Text.Font; TextAnchor anchor = Text.Anchor; Color color = GUI.color;
                        Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperCenter; GUI.color = new Color(1f, .45f, .35f);
                        Widgets.Label(new Rect(x - 160, y + 14, 320, 22), Ballistics.LastHit);
                        Text.Font = font; Text.Anchor = anchor; GUI.color = color;
                    }
                }
                MountedControl.DrawHud(pawn);
                WorkControl.DrawHud(pawn);
                if (MechControl.IsMech(pawn)) MechControl.DrawHud(pawn, CaptureStatus());
                else if (FpsMod.Settings.showPawnHud) PawnHud.Draw(pawn, CaptureStatus());
                if (!DubsMinimap.Active) PawnMinimap.Draw(pawn);
                AbilityBar.Draw(pawn);
                diagHudRepaints++;
                ReportHudRendered();
            });
        }

        private static bool CanPossess(Pawn p) => p != null && p.Spawned && p.Map == Find.CurrentMap && p.Faction == Faction.OfPlayer
            && (p.RaceProps.Humanlike || MechControl.CanPossess(p)) && !p.Dead && !p.Downed && !p.InMentalState;

        private static bool ValidSession() => Current.Game == game && Current.ProgramState == ProgramState.Playing && FirstPerson.On
            && FirstPerson.Pawn == pawn && View3D.Active && CanPossess(pawn) && ReferenceEquals(Bindings.Avatar, avatar);

        private static bool ContextAllows() => LookAllows() && !Find.TickManager.Paused;

        // Turning the view is allowed while paused; movement, firing and weapon switching are not (ContextAllows).
        private static bool LookAllows() => Active && Application.isFocused && !LongEventHandler.ShouldWaitForEvent
            && Find.TickManager != null && !WorldRendererUtility.WorldSelected && !View3D.ControlsLocked
            && GUIUtility.keyboardControl == 0 && Find.WindowStack != null && !Find.WindowStack.WindowsPreventCameraMotion
            && !Find.WindowStack.AnyWindowAbsorbingAllInput && !Find.WindowStack.AnySearchWidgetFocused
            && !MainTabBlocks && !InventoryPanel.Blocking && !SelectedInteractions.Blocking
            && Find.DesignatorManager?.SelectedDesignator == null && Find.Targeter?.IsTargeting != true;

        internal static bool MainTabBlocks => Find.MainTabsRoot?.OpenTab != null && Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect;

        // During auto work the game's AI drives the body; the view still turns.
        private static bool CanDrive() => !FirstPersonTransition.Active && !InteractionWheel.Blocking && Active && policy.Captured && ContextAllows() && !WorkControl.Auto && !MountInteraction.Mounting(pawn) && !VehicleControl.Descending(pawn);

        private static bool CanOpenWheel() => Active && !FirstPersonTransition.Active && policy.Captured && LookAllows()
            && !WorkControl.Auto && !TurretControl.Manned && !MountedControl.For(pawn) && !ProjectZStairs.Riding(pawn) && !Vault.Active
            && !MountInteraction.Mounting(pawn) && pawn.CurJob?.ability == null
            && (!Stances.For(pawn) || (!Stances.State.Airborne && !Stances.State.Falling && !Stances.State.Sliding));
        internal static void WheelGuiInput()
        {
            if (!Active || FirstPersonTransition.Active) return;
            Guard(() => {
                if (InteractionWheel.Frame(pawn, CanOpenWheel()))
                { Bindings.ClearMovement(avatar); FireControl.CancelInput(); ApplyCursor(); }
            });
        }

        private static void TickInput()
        {
            if (FirstPersonTransition.Active) { if (avatar != null) Bindings.ClearMovement(avatar); return; }
            if (inputFrame == Time.frameCount) return;
            inputFrame = Time.frameCount;
            if (VehicleControl.Descending(pawn))
            {
                policy.Step(LookAllows(), ControlKeys.Down(ControlAction.Capture), Input.GetKeyDown(KeyCode.Escape));
                Bindings.ClearMovement(avatar);
                ApplyCursor();
                if (policy.Captured) GameSpeedKeys();
                if (policy.Captured && !policy.SkipMouse)
                {
                    float scale = FpsLook.Scale(1f, false);
                    FirstPerson.Look(Input.GetAxisRaw("Mouse X") * scale,
                        Input.GetAxisRaw("Mouse Y") * scale * (IsoRimMod.Settings.invertY ? 1f : -1f));
                    SyncView();
                }
                return;
            }
            if (!InteractionWheel.Blocking && !WorkControl.Auto && !WorkControl.Swallow) InventoryPanel.Frame(pawn);
            bool context = LookAllows();
            if (InteractionWheel.Frame(pawn, CanOpenWheel()))
            {
                Bindings.ClearMovement(avatar); FireControl.CancelInput();
                LeanControl.ReadKeys(false);
                bool menuPaused = Find.TickManager.Paused;
                QuickTend.Frame(pawn, false); ConsumableHands.Frame(pawn, false); Magazines.Frame(pawn, false, menuPaused);
                Condition.Frame(pawn, false, menuPaused); CyberSkills.Frame(pawn, menuPaused);
                GameSpeedKeys(); ApplyCursor(); return;
            }
            bool escape = Input.GetKeyDown(KeyCode.Escape);
            policy.Step(context, ControlKeys.Down(ControlAction.Capture), escape);
            ApplyCursor();
            HoldViewAfterEscape(escape);
            bool skillPress = CanDrive() && SkillHotkeys.Pressed(pawn);
            string cut = MechControl.Frame(pawn, CanDrive() && !WorkControl.Swallow && !skillPress, Find.TickManager.Paused);
            if (cut != null) { EndAndLeave("mech signal lost"); Messages.Message(cut, MessageTypeDefOf.NegativeEvent, false); return; }
            WorkControl.Frame(pawn, policy.Captured && context);
            bool actions = CanDrive() && !WorkControl.Swallow && !skillPress;
            MountedControl.Frame(pawn, avatar, actions && !TurretControl.Manned, Find.TickManager.Paused);
            QuickTend.Frame(pawn, actions);
            ConsumableHands.Frame(pawn, actions && !Find.TickManager.Paused);
            Magazines.Frame(pawn, actions, Find.TickManager.Paused);
            bool tapE = LeanControl.ReadKeys(CanDrive() && !WorkControl.Swallow && !TurretControl.Manned && !skillPress);
            Stances.Frame(pawn, avatar, CanDrive() && !QuickTend.For(pawn) && !WorkControl.Swallow && !TurretControl.Manned && !skillPress, Find.TickManager.Paused);
            Condition.Frame(pawn, ((Vector3)Bindings.MoveField.GetValue(avatar)).sqrMagnitude > .01f, Find.TickManager.Paused);
            if (!policy.Captured) { Bindings.ClearMovement(avatar); return; }
            GameSpeedKeys();
            TurretOptics.Frame(policy.Captured && context);
            CyberSkills.Frame(pawn, Find.TickManager.Paused);   // skill timers and stamina
            HitboxView.Frame();
            TargetMark.Frame(pawn);
            ProjectZStairs.RideFrame(pawn);
            bool paused = Find.TickManager.Paused;
            if (!paused && !WorkControl.Auto && !WorkControl.Swallow && !skillPress)
            {
                // On foot E is resolved on release: a short press interacts, a held press only leans.
                // A turret keeps its immediate Q/E network controls.
                bool wasManned = TurretControl.Manned;
                // On a ladder/elevator end E rides it (ProjectZ); a turret under the crosshair comes next.
                // 0.9.0: then, with no work within reach, the item under the crosshair into the pack.
                bool interact = wasManned ? ControlKeys.Down(ControlAction.InteractLeanRight) : tapE;
                if (interact && !wasManned && VehicleControl.TryBoard(pawn)) return;
                if (interact && !wasManned && MountInteraction.TryMount(pawn)) return;
                if (interact && (wasManned || !ProjectZStairs.TryRide(pawn)) && !TurretControl.Toggle(pawn)
                    && !wasManned && !TurretControl.Manned) Pocket.TryPickUp(pawn);
                if (!wasManned && !TurretControl.Manned && ControlKeys.Down(ControlAction.UseItem)) Pocket.Use(pawn);
                if (wasManned && TurretControl.Manned)
                {
                    // 0.9.0: B on a direct-fire turret: single / three-round / auto (heat trigger).
                    if (!Artillery.On && ControlKeys.Down(ControlAction.FireMode))
                    {if(TurretControl.Current is CeTurret ce)ce.FireMode=ce.FireMode==Interop.TurretFireMode.Auto?Interop.TurretFireMode.Single:ce.FireMode+1;else (TurretControl.Current as VanillaTurret)?.CycleHeatMode();}
                    if (ControlKeys.Down(ControlAction.LeanLeft)) TurretControl.Switch(pawn, -1);
                    if (ControlKeys.Down(ControlAction.ManualWork)) TurretControl.Leave(L.T("离开炮台", "Left the turret"));
                    if (!Artillery.On && TurretControl.Current is CeTurret && ControlKeys.Down(ControlAction.Reload))
                        TurretControl.ReloadKey(pawn);
                }
                if (Artillery.On)
                {
                    if (ControlKeys.Down(ControlAction.NextWeapon)) Artillery.ToggleView();
                    if (ControlKeys.Down(ControlAction.Reload)) TurretControl.ReloadKey(pawn);
                    if (ControlKeys.Down(ControlAction.FireMode)) Artillery.CycleFireMode();
                }
                if (!wasManned && !TurretControl.Manned)
                {
                    if (ControlKeys.Down(ControlAction.ManualWork)) WorkControl.Manual(pawn);
                    else WorkControl.HoldFrame(pawn);
                    Social.Frame(pawn);   // T: greet, hug, head pat, chat
                    if (ControlKeys.Down(ControlAction.NextWeapon)) WeaponCycle.Request(pawn, 1);
                    if (ControlKeys.Down(ControlAction.FireMode)) FireControl.CycleMode(pawn);
                }
                WeaponCycle.Update(pawn);
            }
            else Bindings.ClearMovement(avatar);
            try
            {
                if (policy.SkipMouse || InteractionWheel.SkipMouse || Time.frameCount <= escapeUntil) return;
                // Waylith UnityStubs.GetAxis returns the engine's relative mouse delta; no deltaTime factor.
                float dx = Input.GetAxisRaw("Mouse X"), dy = Input.GetAxisRaw("Mouse Y");
                if (!float.IsFinite(dx) || !float.IsFinite(dy)) return;
                float sens = FpsLook.Scale(TurretControl.Manned ? TurretOptics.LookScale : WeaponView.LookScale,
                    TurretControl.Manned ? TurretOptics.Scoped : WeaponView.Aiming || WeaponView.ScopeLatched);
                if (Artillery.On) Artillery.Wheel(Input.mouseScrollDelta.y);
                else if (Social.MenuOpen) Social.Wheel(Input.mouseScrollDelta.y);
                else if (!TurretControl.Manned && !WeaponView.ScopeLatched && !VehicleControl.Wheel(pawn, Input.mouseScrollDelta.y)) Pocket.Wheel(pawn, Input.mouseScrollDelta.y);   // nearby vehicle seats take wheel priority
                if (!Artillery.Look(dx * sens, dy * sens)) FirstPerson.Look(dx * sens, dy * sens * (IsoRimMod.Settings.invertY ? 1f : -1f));
                if (TurretControl.Manned) TurretControl.ClampLook(pawn, paused);
                else AimMotion.Frame(pawn, ((Vector3)Bindings.MoveField.GetValue(avatar)).sqrMagnitude > 0.01f, paused);
                ControlCamera.ClampLook();
                CyberSkills.AimAssist(pawn, paused);   // Death Eye: RMB onto a head
                SyncView();
            }
            finally { LeanControl.Frame(pawn); }   // one pose update, including on mouse-warp suppression frames
        }

        // Esc (closing a pawn's tab or the inspect pane) drops the mouse lock for a frame; relocking it can hand the
        // view a jump. For a few frames after Esc the mouse is not read and the view is held where it was.
        private const int EscapeHoldFrames = 6;
        private static int escapeUntil = -1;
        private static float escapeYaw, escapePitch;
        private static ControlView escapeMode;

        private static void HoldViewAfterEscape(bool escape)
        {
            if (escape)
            {
                escapeUntil = Time.frameCount + EscapeHoldFrames;
                escapeYaw = FirstPerson.Yaw; escapePitch = FirstPerson.Pitch; escapeMode = ControlCamera.Mode;
                return;
            }
            if (Time.frameCount > escapeUntil) return;
            if (ControlCamera.Mode != escapeMode) ControlCamera.Set(escapeMode);
            float dYaw = Mathf.DeltaAngle(FirstPerson.Yaw, escapeYaw), dPitch = escapePitch - FirstPerson.Pitch;
            if (Mathf.Abs(dYaw) > .01f || Mathf.Abs(dPitch) > .01f)
            {
                FirstPerson.Look(dYaw, dPitch);
                SyncView();
            }
        }

        internal static bool AltHeld => ControlKeys.Held(ControlAction.Overlay);

        // Alt + 1/2/3/4: game speed 1x/2x/3x/ultrafast while possessed (plain 1-9 are the ability keys). Ultrafast
        // follows the game's own rule (developer mode); otherwise 4 is the fastest normal speed.
        internal static void GameSpeedKeys()
        {
            if (!AltHeld || Find.TickManager == null) return;
            if (Input.GetKeyDown(KeyCode.Alpha5)) FogPeek.Toggle();   // test aid: fog not drawn (nothing revealed)
            TimeSpeed? speed = Input.GetKeyDown(KeyCode.Alpha1) ? TimeSpeed.Normal : Input.GetKeyDown(KeyCode.Alpha2) ? TimeSpeed.Fast
                : Input.GetKeyDown(KeyCode.Alpha3) ? TimeSpeed.Superfast : Input.GetKeyDown(KeyCode.Alpha4) ? (Prefs.DevMode ? TimeSpeed.Ultrafast : TimeSpeed.Superfast) : (TimeSpeed?)null;
            if (speed.HasValue) Find.TickManager.CurTimeSpeed = speed.Value;
        }

        // After anything turned IsoRim's view (mouse, sway, a shot's kick): fold it into the body heading.
        internal static void SyncView()
        {
            if (!Active) return;
            yaw = FirstPerson.Yaw;
            StabilizeHeading();
        }

        private static void StabilizeHeading()
        {
            Bindings.SetHeading(yaw);
            Bindings.SetLookYaw(0f);
            Bindings.SetHeadingReady(true);
        }

        private static void FaceView()
        {
            if (!Active || pawn.Dead || !pawn.Spawned || WorkControl.OwnsRotation(pawn) || TurretControl.Manned) return;
            pawn.Rotation = Rot4.FromAngleFlat(yaw);
            Bindings.AimField.SetValue(avatar, yaw);
        }

        private static void ApplyCursor()
        {
            bool capture = policy.Captured && LookAllows() && !InteractionWheel.Blocking;
            CursorLockMode target = capture ? CursorLockMode.Locked : CursorLockMode.None;
            if (Cursor.lockState != target) Cursor.lockState = target;
            Cursor.visible = !capture;
        }

        private static void EndAndLeave(string reason) { End(reason, true); FirstPerson.Leave(null); }

        internal static bool ReleaseForVehicle()
        {
            if (!Active || !ownsAvatar) return false;
            recall = resume = null;
            EndAndLeave("board vehicle");
            return Bindings.Avatar == null && !FirstPerson.On;
        }
        internal static void ReturnFromVehicle(Pawn p, float? heading = null)
        {
            if (!CanPossess(p) || Active || FirstPerson.On || Bindings.Avatar != null) return;
            Find.Selector.ClearSelection(); Find.Selector.Select(p, false, false); FirstPerson.Toggle();
            if (Active && heading.HasValue)
            {
                FirstPerson.Look(Mathf.DeltaAngle(FirstPerson.Yaw, heading.Value), 12f - FirstPerson.Pitch);
                SyncView();
            }
        }

        private static void End(string reason, bool clearOwnedAvatar)
        {
            InteractionWheel.Reset();
            FirstPersonTransition.Reset();
            if (!Active) return;
            EndControlDiagnostics(reason);
            ending = true;
            try
            {
                try { CeCompat.Release(pawn); SpecialWeapons.Release(pawn); }
                catch (Exception e) { Log.Warning("[FpsBridge] Special weapon release failed: " + e.Message); }
                try { AimMotion.Release(); } catch { }
                try { WeaponView.Release(); Melee.Reset(); } catch { }
                Bindings.ClearMovement(avatar);
                object currentAvatar = Bindings.Avatar;
                bool owned = ownsAvatar && (ReferenceEquals(currentAvatar, avatar) || (avatar == null && Bindings.PawnOf(currentAvatar) == pawn));
                if (clearOwnedAvatar && owned)
                {
                    Bindings.ClearAvatar();
                    if (Current.Game == game && pawn.Spawned && !pawn.Dead && !pawn.Downed && pawn.drafter != null) pawn.drafter.Drafted = originalDrafted;
                }
                if (appliedMode != null && (owned || currentAvatar == null) && Equals(Bindings.ModeField.GetValue(null), appliedMode))
                    Bindings.ModeField.SetValue(null, previousMode);
            }
            finally
            {
                WeaponLight.Release();
                InventoryPanel.Reset();
                LeanControl.Reset();
                bool returnTurretWork = Current.Game == game && reason != "exception";
                pawn = null; game = null; avatar = null; ownsAvatar = false;
                ResetMovementRecovery();
                Stances.End();
                MechControl.Reset();
                ProjectZStairs.ResetRide();
                QuickTend.Reset(); ConsumableHands.Reset(); ModelSurfaces.Reset(); Magazines.End(); MountedControl.Reset();
                WorkControl.Reset();
                TurretControl.Release(returnTurretWork);
                previousMode = appliedMode = null; policy.Exit(); inputFrame = -1;
                WeaponCycle.Reset(); AbilityBar.Reset(); AimPicker.Reset();
                ControlCamera.Reset();
                PawnMinimap.Reset();
                DubsMinimap.End();
                IffMarkers.Reset();
                Social.Reset();
                HitMarker.Reset();
                Condition.Reset();
                CyberSkills.End();   // the world back to its own speed
                TargetMark.Reset();
                Cursor.lockState = originalCursorLock;
                Cursor.visible = originalCursorVisible;
                ending = false;
                Log.Message("[FpsBridge] EXIT reason=" + reason);
            }
        }

        private static void Guard(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                try { End("exception", true); FirstPerson.Leave(null); }
                catch { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                Log.ErrorOnce("[FpsBridge] Control stopped: " + e, 19271701);
            }
        }
    }
}
