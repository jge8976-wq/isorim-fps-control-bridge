using System;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // 0.7.0 pre-development: crouch (LeftCtrl), prone (Z), sprint (LeftShift) and jump (Space) for the controlled pawn.
    // What changes: the eye (first person, the shoulder camera and the shot origin follow it), the pawn's 3D figure in
    // the external views, its own hit volumes, its speed (through Perspective Shift's movement, scaled for one call),
    // hand sway and the held-weapon pose while sprinting. Jumping is a visual/eye/hit-volume arc: the pawn does not
    // leave its cell or cross obstacles. The game's AI hit chances against the pawn are unchanged.
    // Installed under its own Harmony id: if a hook does not match, postures are off and control keeps working.
    internal static class Stances
    {
        private static readonly StanceState state = new StanceState();
        private static Pawn owner;
        private static FieldInfo psSettings, psMoveMultiplier, psSpeedCap;
        private static object movingSettings;
        private static float savedMultiplier, savedCap;
        private static bool speedApplied;
        private static float lastStep;

        public static bool Enabled { get; private set; }
        public static bool SpeedEnabled { get; private set; }
        public static StanceState State => state;
        public static bool For(Pawn pawn) => Enabled && pawn != null && pawn == owner;
        internal static float BodyScale(Pawn pawn) => pawn?.RaceProps?.Humanlike == true ? PawnPoseRules.Scale(pawn.def.defName) : 1f;
        internal static float StandingEye(Pawn pawn) => Mathf.Clamp(IsoRimMod.Settings.fpEyeHeight,.3f,3f)*BodyScale(pawn);

        public static void Install(Harmony harmony)
        {
            var t = typeof(Stances);
            MethodInfo eye = AccessTools.PropertyGetter(typeof(FirstPerson), "Eye") ?? throw new MissingMethodException("IsoRim.FirstPerson.Eye");
            harmony.Patch(eye, postfix: new HarmonyMethod(t, nameof(AfterEye)) { priority = Priority.Last });
            MethodInfo muzzle = AccessTools.Method(typeof(Projectiles), nameof(Projectiles.MuzzleHeight), new[] { typeof(Thing) });
            if (muzzle != null) harmony.Patch(muzzle, postfix: new HarmonyMethod(t, nameof(AfterMuzzleHeight)));
            MethodInfo figure = AccessTools.DeclaredMethod(typeof(BodyAnims), "Apply", new[] { typeof(Pawn), typeof(Vector3), typeof(float), typeof(float), typeof(Matrix4x4) });
            if (figure != null) harmony.Patch(figure, postfix: new HarmonyMethod(t, nameof(AfterFigurePose)) { priority = Priority.Last });
            else Log.Warning("[FpsBridge] IsoRim.BodyAnims.Apply not found: the figure does not show postures (eye, speed and hit volumes still do).");
            try
            {
                Type mod = AccessTools.TypeByName("PerspectiveShift.PerspectiveShiftMod");
                psSettings = AccessTools.Field(mod, "settings");
                Type settings = psSettings?.FieldType;
                psMoveMultiplier = AccessTools.Field(settings, "moveSpeedMultiplier");
                psSpeedCap = AccessTools.Field(settings, "playerMoveSpeedCap");
                if (psSettings == null || !psSettings.IsStatic || psMoveMultiplier?.FieldType != typeof(float) || psSpeedCap?.FieldType != typeof(float))
                    throw new MissingFieldException("PerspectiveShiftSettings.moveSpeedMultiplier/playerMoveSpeedCap");
                harmony.Patch(Bindings.ProcessMovement, prefix: new HarmonyMethod(t, nameof(BeforeMovement)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(t, nameof(AfterMovement)) { priority = Priority.Last });
                SpeedEnabled = true;
            }
            catch (Exception e) { SpeedEnabled = false; Log.Warning("[FpsBridge] Posture speed unavailable (Perspective Shift settings changed): " + e.Message); }
            Enabled = true;
        }

        // ---- session

        public static void Begin(Pawn pawn) { owner = pawn; state.SetCapacity(CapacityOf(pawn)); state.Reset(); lastStep = Time.unscaledTime; }

        // The stamina bar's size from the pawn's Melee and Shooting skills (StanceState.CapacityFor).
        private static float CapacityOf(Pawn pawn) => StanceState.CapacityFor(pawn.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 5, pawn.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0);
        private static int capacityFrame;
        // A pawn on a wall/roof top stays there (Surface.Frame watches it until the next session or the game moves it).
        public static void End() { Vault.Cancel(); owner = null; state.Reset(); }

        // Keys this module reads while the view is captured (vanilla bindings on them are held off).
        public static bool Owns(KeyCode key) => Enabled && (ControlKeys.Matches(ControlAction.Crouch, key)
            || ControlKeys.Matches(ControlAction.Sprint, key) || ControlKeys.Matches(ControlAction.Jump, key)
            || (ControlKeys.Matches(ControlAction.Prone, key) && !AbilityBar.Owns(key)));

        // Once per frame while the session is live: posture keys when the pawn can move, the arc and height always.
        public static void Frame(Pawn pawn, object avatar, bool canMove, bool paused)
        {
            if (!Enabled || pawn != owner) return;
            float now = Time.unscaledTime, dt = Mathf.Clamp(now - lastStep, 0f, .1f);
            lastStep = now;
            if (Time.frameCount - capacityFrame > 120) { capacityFrame = Time.frameCount; state.SetCapacity(CapacityOf(pawn)); }   // skills grow
            bool moving = avatar != null && ((Vector3)Bindings.MoveField.GetValue(avatar)).sqrMagnitude > .01f;
            if (canMove && !paused && !Vault.Active && !MountedControl.For(pawn))
            {
                if (ControlKeys.Down(ControlAction.Crouch))
                {
                    if(ModelSurfaces.DropKey(pawn,now)) { state.EndJump(); return; }
                    bool wasSprint = state.Sprint;
                    state.PressCrouch(moving);
                    if (state.Sliding && wasSprint) pawn.skills?.Learn(SkillDefOf.Melee, 40f);   // 0.9.0: sliding trains Melee
                }
                if (ControlKeys.Down(ControlAction.Prone) && !AbilityBar.Owns(ControlKeys.PressedKey(ControlAction.Prone))) state.PressProne();
                if (ControlKeys.Down(ControlAction.Sprint)) state.PressSprint();
                if (ControlKeys.Down(ControlAction.Jump)) Jump(pawn, avatar);
            }
            if (MountedControl.For(pawn)) state.CancelSprint();
            if (state.Sprint && pawn.needs?.rest != null && pawn.needs.rest.CurLevelPercentage < .01f)
            {
                state.CancelSprint();
                Messages.Message(L.T("太累了，跑不动。", "Too tired to run."), MessageTypeDefOf.RejectInput, false);
            }
            float speed = paused ? 0f : (Find.TickManager?.TickRateMultiplier ?? 1f) * CyberSkills.PlayerTime;   // own pace in a slowed world
            Vault.Step(pawn, dt * speed, state);
            // 0.9.0: running, jumping, climbing and sliding wear the body twice as fast (one extra normal fall of rest
            // and food on top of the game's own).
            if (dt * speed > 0f && (state.Exerting(moving) || Vault.Active || Melee.Whirling(pawn))) Wear(pawn, dt * speed * 60f);
            bool wasExhausted = state.Exhausted;
            state.Step(dt * speed, moving);
            if (state.Exhausted && !wasExhausted) Messages.Message(L.T("气喘吁吁，需要歇一会儿。", "Out of breath, need a rest."), MessageTypeDefOf.RejectInput, false);
        }

        private static void Wear(Pawn pawn, float ticks)
        {
            Need_Rest rest = pawn.needs?.rest;
            Need_Food food = pawn.needs?.food;
            if (rest != null) rest.CurLevel = Mathf.Max(0f, rest.CurLevel - rest.RestFallPerTick * ticks);
            if (food != null) food.CurLevel = Mathf.Max(0f, food.CurLevel - food.FoodFallPerTick * ticks);
        }

        // Space: next to an upper floor's open edge (heading toward it) the pawn jumps down; otherwise a jump in place.
        private static void Jump(Pawn pawn, object avatar)
        {
            if (avatar != null && ModelSurfaces.Ratkin(pawn) && !state.Falling
                && Vault.TryModel(pawn,avatar,Quaternion.Euler(0,FirstPerson.Yaw,0)*Vector3.forward,state)) return;
            // 0.8.1: Space again in the air at a wall: catch the top and vault (Vault.cs).
            if (state.Airborne && avatar != null && Vault.TryMantle(pawn, avatar, Quaternion.Euler(0f, FirstPerson.Yaw, 0f) * Vector3.forward, state)) return;
            Vector3 view = Quaternion.Euler(0f, FirstPerson.Yaw, 0f) * Vector3.forward;
            // A low top in front (sandbag-high over the feet): one Space jumps straight onto it.
            if (state.Posture == Posture.Stand && !state.Airborne && !state.Falling && avatar != null && Vault.TryMantle(pawn, avatar, view, state, lowOnly: true)) return;
            if (Surface.For(pawn)) { state.PressJump(); return; }   // on a top: a plain jump (Space again climbs higher)
            if (state.Posture == Posture.Stand && !state.Airborne && !state.Falling && avatar != null)
            {
                Vector3 move = (Vector3)Bindings.MoveField.GetValue(avatar);
                Vector3 heading = move.sqrMagnitude > .01f ? move : Quaternion.Euler(0f, FirstPerson.Yaw, 0f) * Vector3.forward;
                // 0.9.0: low cover in front: hop over it rather than jump.
                if (Vault.TryHop(pawn, avatar, Quaternion.Euler(0f, FirstPerson.Yaw, 0f) * Vector3.forward, state)) return;
                // At an upper floor's edge: onto a wall/roof top just below it, else down to the floor below.
                if (Surface.TryFromFloor(pawn, avatar, heading)) return;
                if (ProjectZStairs.TryJumpDown(pawn, avatar, heading, out float drop)) { state.StartFall(drop); return; }
            }
            state.PressJump();
        }

        public static string Label
        {
            get
            {
                if (!Enabled || owner == null) return null;
                if (state.Falling) return L.T("下落", "Falling");
                if (state.Airborne) return L.T("跳跃", "Jumping");
                if (state.Sprint) return L.T("奔跑", "Sprinting");
                return state.Posture == Posture.Crouch ? L.T("蹲下", "Crouching") : state.Posture == Posture.Prone ? L.T("趴下", "Prone") : null;
            }
        }

        // ---- hooks

        // The eye follows the body: lowered by the posture's height and raised by the jump arc. Everything that reads
        // the eye (first-person camera, shoulder camera, the shot's origin, the held weapon) follows.
        public static void AfterMuzzleHeight(Thing launcher, ref float __result)
        {
            if (!(launcher is Pawn pawn)) return;
            if (For(pawn) && BodyScale(pawn)<1f)
                __result = PawnPoseRules.Eye(Mathf.Clamp(IsoRimMod.Settings.fpEyeHeight,.3f,3f),BodyScale(pawn),state.Height,state.Lift,Surface.HeightOf(pawn)) - .08f;
            __result += MountedControl.Lift(pawn) + Knockback.Lift(pawn);
        }

        public static void AfterEye(ref Vector3 __result)
        {
            if (!Enabled || owner == null || FirstPerson.Pawn != owner) return;
            float standing = Mathf.Clamp(IsoRimMod.Settings.fpEyeHeight, .3f, 3f);
            float scale = BodyScale(owner);
            __result.y += PawnPoseRules.Eye(standing,scale,state.Height,state.Lift,Surface.HeightOf(owner)) - standing;
            __result.y += MountedControl.Lift(owner) + Knockback.Lift(owner);
            __result += Vault.EyeOffset;   // 0.8.1: climbing a wall
            __result += Condition.EyeOffset*scale;
            __result += Social.EyeOffset*scale;
            __result += LeanControl.EyeOffset*scale;
        }

        // The pawn's 3D figure (external views): squatting and leaning when crouched, lying forward when prone, raised
        // by the jump arc, leaning into a sprint. Figure space before its size: x right, y up, z forward, feet at 0.
        public static void AfterFigurePose(Pawn pawn, float yaw, float size, ref Matrix4x4 __result)
        {
            float mountedLift = MountedControl.Lift(pawn) + Knockback.Lift(pawn);
            if (mountedLift > 0f) __result = Matrix4x4.Translate(new Vector3(0f, mountedLift / Mathf.Max(.1f, size), 0f)) * __result;
            if (!For(pawn))
            {
                // A pawn left standing on a top after its session.
                float top = Surface.HeightOf(pawn);
                if (top > 0f) __result = Matrix4x4.Translate(new Vector3(0f, top / Mathf.Max(.1f, size), 0f)) * __result;
                return;
            }
            float h = state.Height;
            float prone = Mathf.Clamp01((StanceState.CrouchHeight - h) / (StanceState.CrouchHeight - StanceState.ProneHeight));
            float crouch = Mathf.Clamp01((1f - h) / (1f - StanceState.CrouchHeight)) * (1f - prone);
            float lean = 12f * crouch + (state.Sprint ? 8f : 0f) + 84f * prone;
            float squash = Mathf.Lerp(1f, .72f, crouch);
            float lift = (state.Lift + .1f * prone + Surface.HeightOf(pawn)) / Mathf.Max(.1f, size);
            if (lean >= .01f || squash <= .999f || lift >= 1e-4f)
            {
                Matrix4x4 extra = Matrix4x4.Translate(new Vector3(0f, lift, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(lean, Vector3.right))
                    * Matrix4x4.Scale(new Vector3(1f, squash, 1f));
                __result = extra * __result;
            }
            LeanControl.Figure(pawn, yaw, size, ref __result);
            // The whirlwind: the whole figure turns (seen from the shoulder or overhead views).
            if (Melee.Whirling(pawn)) __result = Matrix4x4.Rotate(Quaternion.AngleAxis(Melee.WhirlAngle, Vector3.up)) * __result;
        }

        // Perspective Shift computes the speed from its settings: for our pawn's one call they carry the posture's
        // factor (the cap rises with a sprint so the boost is not clipped), then go straight back.
        public static void BeforeMovement(object __instance)
        {
            speedApplied = false;
            if (!SpeedEnabled || owner == null || Bindings.PawnOf(__instance) != owner) return;
            float factor = (MountedControl.For(owner) ? MountedControl.SpeedFactor : state.SpeedFactor) * CyberSkills.PlayerTime;   // Sandevistan: normal pace in the slowed world
            if (Math.Abs(factor - 1f) < 1e-4f) return;
            movingSettings = psSettings.GetValue(null);
            if (movingSettings == null) return;
            savedMultiplier = (float)psMoveMultiplier.GetValue(movingSettings);
            savedCap = (float)psSpeedCap.GetValue(movingSettings);
            psMoveMultiplier.SetValue(movingSettings, savedMultiplier * factor);
            if (factor > 1f) psSpeedCap.SetValue(movingSettings, savedCap * factor);
            speedApplied = true;
        }

        public static Exception AfterMovement(Exception __exception)
        {
            if (speedApplied && movingSettings != null)
            {
                psMoveMultiplier.SetValue(movingSettings, savedMultiplier);
                psSpeedCap.SetValue(movingSettings, savedCap);
            }
            speedApplied = false; movingSettings = null;
            return __exception;
        }
    }
}
