using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Perspective Shift remains an external dependency. No upstream source is copied.
    internal static class Bindings
    {
        public static Type AvatarType, StateType;
        public static FieldInfo AvatarField, PawnField, MoveField, SprintField, WalkField, AimField, ModeField;
        public static Action<Pawn, bool> SetAvatar;
        public static Action ClearAvatar;
        public static Action<float> SetHeading, SetLookYaw;
        public static Action<bool> SetHeadingReady;
        public static MethodInfo AvatarCamera, AvatarInput, AvatarPhysics, AvatarRotate, AvatarMouseRotate, AvatarRender;
        public static MethodInfo ControlsFrozen, StateOnGUI, StateClear;
        public static MethodInfo ProcessMovement, CombatStance, Firing, HoldFire, CursorReticle, SelectorClick, RunAndGun;
        public static Func<Pawn, bool> UpstreamRunAndGun;
        public static Func<Thing, float> PickHeight;
        public static FieldInfo LeanField;
        public static FieldInfo LeanSmoothedField;
        public static FieldInfo PhysicsPosition;

        public static object Avatar => AvatarField.GetValue(null);
        public static Pawn PawnOf(object avatar) => avatar == null ? null : (Pawn)PawnField.GetValue(avatar);

        public static void Initialize()
        {
            AvatarType = AccessTools.TypeByName("PerspectiveShift.Avatar") ?? throw new MissingMemberException("PerspectiveShift.Avatar");
            StateType = AccessTools.TypeByName("PerspectiveShift.State") ?? throw new MissingMemberException("PerspectiveShift.State");
            AvatarField = Field(StateType, "Avatar", AvatarType, true);
            PawnField = Field(AvatarType, "pawn", typeof(Pawn), false);
            MoveField = Field(AvatarType, "moveInput", typeof(Vector3), false);
            PhysicsPosition = Field(AvatarType, "physicsPosition", typeof(Vector3?), false);
            SprintField = Field(AvatarType, "isSprinting", typeof(bool), false);
            WalkField = Field(AvatarType, "isWalking", typeof(bool), false);
            AimField = Field(AvatarType, "aimAngle", typeof(float), false);
            ModeField = AccessTools.Field(StateType, "CurrentMode") ?? throw new MissingFieldException("State.CurrentMode");
            if (!ModeField.IsStatic || !ModeField.FieldType.IsEnum || !Enum.IsDefined(ModeField.FieldType, "Dynamic"))
                throw new MissingMemberException("State.CurrentMode/Dynamic");
            SetAvatar = (Action<Pawn, bool>)Method(StateType, "SetAvatar", true, typeof(Pawn), typeof(bool)).CreateDelegate(typeof(Action<Pawn, bool>));
            StateClear = Method(StateType, "ClearAvatar", true);
            ClearAvatar = (Action)StateClear.CreateDelegate(typeof(Action));
            AvatarCamera = Method(AvatarType, "UpdateCamera", false);
            AvatarInput = Method(AvatarType, "UpdateInput", false);
            AvatarPhysics = Method(AvatarType, "UpdatePhysics", false);
            AvatarRender = Method(AvatarType, "RenderPawn", false);
            AvatarRotate = Method(AvatarType, "UpdateRotation", false, typeof(Vector3));
            AvatarMouseRotate = Method(AvatarType, "RotateTowardsMouse", false);
            ControlsFrozen = AccessTools.PropertyGetter(StateType, "ControlsFrozen") ?? throw new MissingMethodException("State.ControlsFrozen");
            if (ControlsFrozen.ReturnType != typeof(bool) || !ControlsFrozen.IsStatic) throw new MissingMethodException("State.ControlsFrozen signature");
            StateOnGUI = Method(StateType, "OnGUI", true);
            ProcessMovement = Method(AvatarType, "ProcessMovement", false);
            CombatStance = Method(AvatarType, "HandleCombatStance", false);
            Firing = Method(AvatarType, "HandleFiring", false);
            HoldFire = Method(AvatarType, "HandleHoldToFire", false, typeof(bool), typeof(bool));
            CursorReticle = Method(AvatarType, "UpdateCursorAndReticle", false, typeof(bool), typeof(bool));
            SelectorClick = AccessTools.DeclaredMethod(AvatarType, "HandleSelectorClick", Type.EmptyTypes);
            if (SelectorClick == null || SelectorClick.ReturnType != typeof(bool)) throw new MissingMethodException("Avatar.HandleSelectorClick");
            Type compat = AccessTools.TypeByName("PerspectiveShift.ModCompatibility");
            RunAndGun = AccessTools.DeclaredMethod(compat, "IsRunAndGunActiveFor", new[] { typeof(Pawn) });
            UpstreamRunAndGun = (Func<Pawn, bool>)RunAndGun.CreateDelegate(typeof(Func<Pawn, bool>));
            PickHeight = (Func<Thing, float>)AccessTools.DeclaredMethod(typeof(Interaction), "PickHeight", new[] { typeof(Thing) }).CreateDelegate(typeof(Func<Thing, float>));
            LeanField = Field(AvatarType, "LeanTarget", typeof(Vector3), false);
            LeanSmoothedField = Field(AvatarType, "LeanSmoothed", typeof(Vector3), false);
            SetHeading = Setter<float>(Field(typeof(FirstPerson), "heading", typeof(float), true));
            SetLookYaw = Setter<float>(Field(typeof(FirstPerson), "lookYaw", typeof(float), true));
            SetHeadingReady = Setter<bool>(Field(typeof(FirstPerson), "headingSet", typeof(bool), true));
        }

        private static FieldInfo Field(Type type, string name, Type fieldType, bool isStatic)
        {
            FieldInfo f = AccessTools.DeclaredField(type, name);
            if (f == null || f.FieldType != fieldType || f.IsStatic != isStatic || f.IsInitOnly)
                throw new MissingFieldException(type.FullName, name + " (signature)");
            return f;
        }

        internal static MethodInfo Method(Type type, string name, bool isStatic, params Type[] args)
        {
            MethodInfo m = AccessTools.DeclaredMethod(type, name, args);
            if (m == null || m.IsStatic != isStatic || m.ReturnType != typeof(void) || m.IsAbstract || m.GetMethodBody() == null)
                throw new MissingMethodException(type.FullName, name + " (signature/body)");
            return m;
        }

        private static Action<T> Setter<T>(FieldInfo field)
        {
            var dm = new DynamicMethod("FpsBridge_Set_" + field.Name, typeof(void), new[] { typeof(T) }, typeof(Bindings).Module, true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Stsfld, field); il.Emit(OpCodes.Ret);
            return (Action<T>)dm.CreateDelegate(typeof(Action<T>));
        }

        public static void ClearMovement(object avatar)
        {
            if (avatar == null) return;
            MoveField.SetValue(avatar, Vector3.zero);
            SprintField.SetValue(avatar, false);
            WalkField.SetValue(avatar, false);
        }
    }
}
