using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    // FPS trajectory/aim, immediate casts and mobile native reload; CE owns ammo and impact/armour damage.
    internal static partial class CeCompat
    {
        private static Type launchType, shootType, projectileType, ammoType;
        private static PropertyInfo compAmmo, projectileDef, curRounds, magSize, canFire, useAmmo, hasAmmo, hasMagazine;
        private static PropertyInfo currentAmmo, fireModes, fireMode, availableModes;
        private static MethodInfo startReload, toggleFireMode;
        private static FieldInfo shotAngle, shotRotation, lastAngle, lastRotation, exactLastPos, launchedShotSpeed;
        private static PropertyInfo exactPos;
        private static VectorAccess vector2, vector3;
        private static readonly Dictionary<Type, bool> ownedTypes = new Dictionary<Type, bool>();
        private static readonly Dictionary<Verb, Cast> casts = new Dictionary<Verb, Cast>();
        private static ConditionalWeakTable<Thing, Trace> traces = new ConditionalWeakTable<Thing, Trace>();
        private static Pawn hudPawn;
        private static Verb hudVerb;
        private static object hudAmmo;
        private static Job observedReload;
        private static int reloadToil = -1, reloadMaximum;
        private static int lastReloadRequest = -60;
        [ThreadStatic] private static ShotScope scope;
        internal static bool Ready { get; private set; }
        internal static string Status { get; private set; } = "not initialized";
        internal static string LastGate="idle";

        private sealed class Cast { internal Pawn pawn; internal bool managed, held; internal string mode; internal readonly SpecialFireClock clock = new SpecialFireClock(); }
        private sealed class Trace
        {
            internal Map map;
            internal Thing launcher;
            internal Pawn turretOperator;
            internal int lastAirDrawFrame = -1;
            internal bool? explosiveGun;
            internal Thing aerialTarget;
            internal float aerialTurn;
            internal Pawn homing;
            internal V3 pos, previous;
            internal int bounces;
            internal bool completed;
            internal V3 velocity;
            internal Ballistics.ExternalTrace contacts;
            internal StructureFlight structure;
            internal float gravity;
            internal bool nativePhysics, nativeVelocityMotion, nativeRestorePending;
            internal int nativePhysicsMode;
            internal float nativeHeight;
            internal int nativeShift, nativeLevel, nativeUnitsVersion;
            internal int lastTick;
        }
        // CE 1.6 metadata names UnityEngine.CoreModule; Waylith exposes UnityStubs. Do not cast boxed
        // CE vectors to our engine vectors or require assembly identity equality for these value types.
        private sealed class VectorAccess
        {
            private readonly ConstructorInfo constructor;
            private readonly FieldInfo x, y, z;
            internal VectorAccess(Type type, int dimensions)
            {
                if (type.FullName != "UnityEngine.Vector" + dimensions || !type.IsValueType)
                    throw new TypeLoadException("Unexpected CE vector value type: " + type);
                x = FloatField(type, "x"); y = FloatField(type, "y");
                if (dimensions == 3) z = FloatField(type, "z");
                constructor = type.GetConstructor(dimensions == 2 ? new[] { typeof(float), typeof(float) }
                    : new[] { typeof(float), typeof(float), typeof(float) });
                if (constructor == null) throw new MissingMethodException(type.FullName, ".ctor(float...)");
            }
            internal object Make2(Vector2 value) => constructor.Invoke(new object[] { value.x, value.y });
            internal object Make3(Vector3 value) => constructor.Invoke(new object[] { value.x, value.y, value.z });
            internal Vector3 Read3(object value) => new Vector3((float)x.GetValue(value), (float)y.GetValue(value), (float)z.GetValue(value));
        }
        internal sealed class ShotScope
        {
            internal ShotScope previous;
            internal Verb verb;
            internal Pawn pawn;
            internal Thing launcher;
            internal Thing equipment;
            internal Vector2 origin;
            internal float height, elevation, rotation;
            internal V3 direction;
            internal V3 baseDirection, eye;
            internal V3 arcVelocity, arcGoal;
            internal float arcGravity;
            internal bool artillery;
            internal Pawn homing;
            internal int pellet;
            internal bool adjusted, launched, released;
        }

        private static Type Named(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name.StartsWith("CombatExtended", StringComparison.Ordinal))
                {
                    Type type = assembly.GetType("CombatExtended." + name, false);
                    if (type != null) return type;
                }
            return null;
        }
        private static PropertyInfo Property(Type type, string name, Type value = null)
        {
            PropertyInfo property = AccessTools.Property(type, name);
            if (property?.GetGetMethod(true) == null || value != null && property.PropertyType != value)
                throw new MissingMemberException(type.FullName, name);
            return property;
        }
        private static FieldInfo FloatField(Type type, string name)
        {
            FieldInfo field = AccessTools.Field(type, name);
            if (field?.FieldType != typeof(float)) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private static MethodInfo Method(Type type, string name, params Type[] args) =>
            AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
        private static MethodInfo ShapeMethod(Type type, string name, params string[] parameterTypes)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.Name != name) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length) continue;
                bool match = true;
                for (int i = 0; i < parameters.Length; i++) if (parameters[i].ParameterType.FullName != parameterTypes[i]) { match = false; break; }
                if (match) return method;
            }
            throw new MissingMethodException(type.FullName, name + " (required parameter shape)");
        }

        internal static void Install()
        {
            var harmony = new Harmony(Bootstrap.Id + ".ce-native-r1");
            try
            {
                launchType = Named("Verb_LaunchProjectileCE");
                if (launchType == null) { Status = "CE not loaded"; return; }
                shootType = Named("Verb_ShootCE"); projectileType = Named("ProjectileCE"); ammoType = Named("CompAmmoUser");
                if (shootType == null || projectileType == null || ammoType == null
                    || !typeof(Verb).IsAssignableFrom(launchType) || !typeof(Thing).IsAssignableFrom(projectileType))
                    throw new TypeLoadException("CE types do not resolve to the running game's Verb/Thing");
                launchedShotSpeed = FloatField(projectileType, "shotSpeed");
                compAmmo = Property(launchType, "CompAmmo"); projectileDef = Property(launchType, "Projectile", typeof(ThingDef));
                curRounds = Property(ammoType, "CurMagCount", typeof(int)); magSize = Property(ammoType, "MagSize", typeof(int));
                canFire = Property(ammoType, "CanBeFiredNow", typeof(bool)); useAmmo = Property(ammoType, "UseAmmo", typeof(bool));
                hasAmmo = Property(ammoType, "HasAmmo", typeof(bool)); hasMagazine = Property(ammoType, "HasMagazine", typeof(bool));
                currentAmmo = Property(ammoType, "CurrentAmmo"); startReload = Method(ammoType, "TryStartReload", Type.EmptyTypes);
                shotAngle = FloatField(launchType, "shotAngle"); shotRotation = FloatField(launchType, "shotRotation");
                lastAngle = FloatField(launchType, "lastShotAngle"); lastRotation = FloatField(launchType, "lastShotRotation");
                exactPos = Property(projectileType, "ExactPosition");
                vector3 = new VectorAccess(exactPos.PropertyType, 3);
                exactLastPos = AccessTools.Field(projectileType, "LastPos");
                if (exactLastPos?.FieldType != exactPos.PropertyType) throw new MissingFieldException("ProjectileCE.LastPos");
                Type report = Named("ShiftVecReport");
                if (report == null) throw new TypeLoadException("CE ShiftVecReport missing");
                MethodInfo shift = ShapeMethod(launchType, "ShiftTarget", report.FullName, "UnityEngine.Vector3", "System.Single&", "System.Boolean", "System.Boolean");
                MethodInfo launch = ShapeMethod(projectileType, "Launch", "Verse.Thing", "UnityEngine.Vector2", "System.Single", "System.Single", "System.Single", "System.Single", "Verse.Thing", "System.Single");
                vector2 = new VectorAccess(launch.GetParameters()[1].ParameterType, 2);
                InstallNativeMotion(launch, harmony);
                MethodInfo cast = Method(shootType, "TryCastShot", Type.EmptyTypes);
                MethodInfo draw = ShapeMethod(projectileType, "DrawAt", "UnityEngine.Vector3", "System.Boolean");
                fireModes = Property(launchType, "CompFireModes");
                Type modes = Named("CompFireModes");
                if (modes == null) throw new TypeLoadException("CE fire mode type missing");
                fireMode = Property(modes, "CurrentFireMode"); availableModes = Property(modes, "AvailableFireModes");
                toggleFireMode = Method(modes, "ToggleFireMode", Type.EmptyTypes);
                harmony.Patch(cast, prefix: new HarmonyMethod(typeof(CeCompat), nameof(BeforeShot)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(CeCompat), nameof(AfterShot)) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(typeof(CeCompat), nameof(FinalizeShot)));
                harmony.Patch(shift, postfix: new HarmonyMethod(typeof(CeCompat), nameof(AfterShift)) { priority = Priority.Last });
                var launchEntries = new HashSet<MethodInfo> { launch };
                var drawEntries = new HashSet<MethodInfo> { draw };
                Type[] launchArguments = Array.ConvertAll(launch.GetParameters(), p => p.ParameterType);
                Type[] drawArguments = Array.ConvertAll(draw.GetParameters(), p => p.ParameterType);
                foreach (ThingDef definition in DefDatabase<ThingDef>.AllDefsListForReading)
                    for (Type type = definition.thingClass; type != null && type != projectileType && projectileType.IsAssignableFrom(type); type = type.BaseType)
                    {
                        MethodInfo entry = AccessTools.DeclaredMethod(type, "Launch", launchArguments);
                        if (entry != null && !entry.IsAbstract && entry.GetBaseDefinition() == launch.GetBaseDefinition()) launchEntries.Add(entry);
                        MethodInfo drawOverride = AccessTools.DeclaredMethod(type, "DrawAt", drawArguments);
                        if (drawOverride != null && !drawOverride.IsAbstract && drawOverride.GetBaseDefinition() == draw.GetBaseDefinition()) drawEntries.Add(drawOverride);
                    }
                foreach (MethodInfo entry in launchEntries)
                    harmony.Patch(entry, prefix: new HarmonyMethod(typeof(CeCompat), nameof(BeforeLaunch)) { priority = Priority.Last },
                        postfix: new HarmonyMethod(typeof(CeCompat), nameof(AfterLaunch)),
                        finalizer: new HarmonyMethod(typeof(CeCompat), nameof(FinishLaunch)));
                Log.Message("[FpsBridge] CE_LAUNCH_ENTRYPOINTS=" + launchEntries.Count);
                foreach (MethodInfo entry in drawEntries)
                    harmony.Patch(entry, prefix: new HarmonyMethod(typeof(CeCompat), nameof(BeforeDraw)) { priority = Priority.First });
                InstallFpsFlight(harmony);
                var ceImpactFx = AccessTools.Method(AccessTools.TypeByName("ProjectileImpactFX.ImpactFleckThrower"), "ThrowFleck");
                if (ceImpactFx != null)
                    harmony.Patch(ceImpactFx, prefix: new HarmonyMethod(typeof(CeCompat), nameof(KeepNativeImpactFleck)));
                CeMobileReload.Install(harmony);
                InstallExtended(harmony);
                InstallStructure();
                CeVersionFix.Install(harmony); CeAbilityOwnerFix.Install(harmony);
                InstallCePackFlight();
                Ready = true; Status = "FPS trajectory/instant cast/mobile reload; CE ammo/armour/damage";
                Log.Message("[FpsBridge] CE_BRIDGE BOUND; assembly=" + launchType.Assembly.FullName
                    + "; labelGate=none; ammo=CE; damage=CE; flight=FPS; spread=FPS_ONLY; warmup=AFTER_SHOT; movingReload=ON; candidate=CE_R7");
            }
            catch (Exception e)
            {
                harmony.UnpatchAll(harmony.Id); Ready = false; Status = "CE interface unavailable";
                Log.Warning("[FpsBridge] CE_BRIDGE unavailable (CE weapons will not use free virtual ammo): " + e);
            }
        }

        // Reserve CE weapons even if binding fails; never silently send them through virtual-magazine combat.
        internal static bool Owns(Verb verb)
        {
            if (verb == null || verb.verbProps?.IsMeleeAttack == true) return false;
            Type type = verb.GetType();
            if (!ownedTypes.TryGetValue(type, out bool owned))
            {
                for (Type t = type; t != null; t = t.BaseType)
                    if (t.FullName == "CombatExtended.Verb_LaunchProjectileCE") { owned = true; break; }
                ownedTypes[type] = owned;
            }
            if (owned) return true;
            ThingWithComps equipment = verb.EquipmentSource;
            if (equipment != null) foreach (ThingComp comp in equipment.AllComps)
                for (Type t = comp.GetType(); t != null; t = t.BaseType)
                    if (t.FullName == "CombatExtended.CompAmmoUser") return true;
            return false;
        }

        private static bool Supported(Pawn pawn, Verb verb)
        {
            if (!Ready || pawn?.Map == null || verb == null || !launchType.IsInstanceOfType(verb) || verb.EquipmentSource == null) return false;
            ThingDef def = projectileDef.GetValue(verb) as ThingDef;
            return def?.projectile != null && projectileType.IsAssignableFrom(def.thingClass);
        }

        private static bool IsReload(Pawn pawn, Verb verb) => pawn?.CurJob != null && verb?.EquipmentSource != null
            && pawn.jobs?.curDriver != null && pawn.jobs.curDriver.GetType().FullName == "CombatExtended.JobDriver_Reload"
            && ReferenceEquals(pawn.CurJob.targetB.Thing, verb.EquipmentSource);
        internal static bool MobileReload(Pawn pawn) => Ready && IsReload(pawn, pawn?.equipment?.PrimaryEq?.PrimaryVerb);
        internal static bool Reloading => IsReload(hudPawn, hudVerb);
        internal static float ReloadProgress => reloadMaximum > 0 && Reloading
            ? Mathf.Clamp01(1f - (float)Math.Max(0, hudPawn.jobs.curDriver.ticksLeftThisToil) / reloadMaximum) : 0;
        internal static float ReloadPose => Reloading ? Mathf.Sin(ReloadProgress * Mathf.PI) * .65f + .35f : 0;

        internal static void ReloadFrame(Pawn pawn, bool input, bool paused)
        {
            Verb verb = pawn?.equipment?.PrimaryEq?.PrimaryVerb;
            if (!Owns(verb)) { ClearHud(); return; }
            if (!ReferenceEquals(hudVerb, verb)) { observedReload = null; reloadToil = -1; reloadMaximum = 0; lastReloadRequest = -60; }
            hudPawn = pawn; hudVerb = verb;
            if (!Ready || !launchType.IsInstanceOfType(verb)) { hudAmmo = null; return; }
            ObserveCast(pawn, verb, Find.TickManager.TicksGame);
            hudAmmo = compAmmo.GetValue(verb);
            if (hudAmmo == null) return;
            if (Reloading)
            {
                int index = pawn.jobs.curDriver.CurToilIndex;
                if (observedReload != pawn.CurJob || reloadToil != index)
                { observedReload = pawn.CurJob; reloadToil = index; reloadMaximum = 0; }
                // Actual native remaining ticks; no independent timer, fake completion or ammunition writes.
                reloadMaximum = Math.Max(reloadMaximum, pawn.jobs.curDriver.ticksLeftThisToil);
                return;
            }
            observedReload = null; reloadToil = -1; reloadMaximum = 0;
            if (!Supported(pawn, verb)) return;
            int now = Find.TickManager.TicksGame;
            if (CeNativeRules.WantsReload(input, paused, verb.Bursting || pawn.stances.curStance is Stance_Warmup,
                (bool)hasMagazine.GetValue(hudAmmo), (int)curRounds.GetValue(hudAmmo), (int)magSize.GetValue(hudAmmo),
                (bool)useAmmo.GetValue(hudAmmo), (bool)hasAmmo.GetValue(hudAmmo), ControlKeys.Down(ControlAction.Reload), now, lastReloadRequest))
            {
                lastReloadRequest = now; FireControl.CancelInput();
                startReload.Invoke(hudAmmo, null); // CE selects, unloads, reserves and consumes its own inventory ammunition.
            }
        }

        internal static void Frame(Pawn pawn, Verb verb, bool held, bool pressed)
        {
            LastGate="frame";
            if (!Supported(pawn, verb)) {LastGate="unsupported";return;}
            int now = Find.TickManager.TicksGame;
            ObserveCast(pawn, verb, now);
            if (!casts.TryGetValue(verb, out Cast cast)) casts[verb] = cast = new Cast();
            cast.held = held || pressed;
            if (cast.managed && cast.mode == "AutoFire" && verb.Bursting && !cast.held)
            { StopBurst(pawn, verb, cast); return; }
            if (Reloading || !cast.clock.Ready(now, verb.Bursting) || pawn.stances.curStance is Stance_Busy) {LastGate=Reloading?"reloading":verb.Bursting?"burst":"recovery-or-stance";return;}
            object ammo = compAmmo.GetValue(verb);
            if (ammo != null && !(bool)canFire.GetValue(ammo)) {LastGate="ammo";return;}
            object modes = fireModes.GetValue(verb);
            string mode = modes == null ? "SingleFire" : fireMode.GetValue(modes).ToString();
            if (!(CyberSkills.Eye(pawn) ? held || pressed : CeNativeRules.WantsTrigger(mode, held, pressed)) || !verb.Available()) {LastGate="trigger-or-unavailable";return;}
            LocalTargetInfo target = PickTarget(pawn, verb);
            if (!target.IsValid) target = FreeFireTarget(pawn, verb, Ballistics.FallbackTarget(pawn, verb));
            if (!target.IsValid) {LastGate="no-target";return;}
            cast.pawn = pawn; cast.managed = true; cast.mode = mode;
            cast.clock.Begin(verb.verbProps.AdjustedCooldownTicks(verb, pawn));
            int launched = FpsLaunched;
            try
            {
                if (!verb.TryStartCastOn(target, false, true)) { LastGate="native-cast-rejected";cast.managed = false; cast.clock.Failed(); return; }
                CompleteWarmup(pawn, verb, cast);
                if (FpsLaunched > launched) InstantCasts++;
                ObserveCast(pawn, verb, now);
                LastGate=FpsLaunched>launched?"fired":"native-cast-without-projectile";
            }
            catch { cast.managed = false; if (cast.clock.Casting) cast.clock.CompleteFallback(now); throw; }
        }

        private static void CompleteWarmup(Pawn pawn, Verb verb, Cast cast)
        {
            // Reuse the existing FPS special-weapon contract: charge removed warmup to recovery once.
            // CE's optional second aiming stance is transferred too; its native shot still deducts ammo.
            for (int i = 0; i < 4 && pawn.stances.curStance is Stance_Warmup warmup && warmup.verb == verb; i++)
            {
                cast.clock.TransferWarmup(warmup.ticksLeft);
                WarmupTransferred = (int)Math.Min(int.MaxValue,(long)WarmupTransferred + Math.Max(0,warmup.ticksLeft));
                warmup.Interrupt();
                verb.WarmupComplete();
            }
        }
        private static void ObserveCast(Pawn pawn, Verb verb, int now)
        {
            if (!casts.TryGetValue(verb, out Cast cast) || !cast.clock.Casting) return;
            CompleteWarmup(pawn, verb, cast);
            if (!verb.Bursting && !(pawn.stances.curStance is Stance_Warmup w && w.verb == verb))
            {
                if (pawn.stances.curStance is Stance_Cooldown cool && cool.verb == verb) cast.clock.Complete(now, cool.ticksLeft);
                else cast.clock.CompleteFallback(now,CyberSkills.FireFactor(pawn));
            }
        }
        internal static void AdjustCooldown(Verb verb, ref int ticks)
        {
            if (verb != null && casts.TryGetValue(verb, out Cast cast) && cast.clock.Casting)
            {
                if (!verb.Bursting) ticks = cast.clock.Complete(Find.TickManager.TicksGame, ticks,CyberSkills.FireFactor(cast.pawn));
            }
        }

        internal static void CycleMode(Pawn pawn)
        {
            Verb verb = pawn?.equipment?.PrimaryEq?.PrimaryVerb;
            if (!Ready || !launchType.IsInstanceOfType(verb)) return;
            object modes = fireModes.GetValue(verb);
            if (modes == null || verb.Bursting || pawn.stances.curStance is Stance_Warmup) return;
            if (!(availableModes.GetValue(modes) is System.Collections.ICollection available) || available.Count == 0) return;
            toggleFireMode.Invoke(modes, null);
            Messages.Message("CE · " + fireMode.GetValue(modes), MessageTypeDefOf.SilentInput, false);
        }

        internal static bool BeforeBurst(Verb verb, ref LocalTargetInfo target)
        {
            if (!casts.TryGetValue(verb, out Cast cast) || !cast.managed || !Bridge.Active
                || Bridge.ShootingPawn != cast.pawn || cast.pawn.equipment?.PrimaryEq?.PrimaryVerb != verb) return true;
            if (!Bridge.CeControlsAllowed || !Supported(cast.pawn, verb)) { StopBurst(cast.pawn,verb,cast); return false; }
            if (cast.mode == "AutoFire" && !cast.held) { StopBurst(cast.pawn,verb,cast);return false; }
            LocalTargetInfo picked = PickTarget(cast.pawn, verb);
            if (!picked.IsValid) picked = FreeFireTarget(cast.pawn, verb, Ballistics.FallbackTarget(cast.pawn, verb));
            if (!picked.IsValid) return false;
            target = picked; return true;
        }

        internal static bool BeforeShot(Verb __instance, ref bool __result, out ShotScope __state)
        {
            __state = null;
            if (!casts.TryGetValue(__instance, out Cast cast) || !cast.managed || !Bridge.Active
                || Bridge.ShootingPawn != cast.pawn || cast.pawn.equipment?.PrimaryEq?.PrimaryVerb != __instance) return true;
            if (!Bridge.CeControlsAllowed || !Supported(cast.pawn, __instance)) { __result = false; return false; }
            V3 eye = IsGrenade(__instance)?Ballistics.Hand(cast.pawn):Ballistics.Eye(cast.pawn), direction = ControlCamera.ShotDirection(cast.pawn);
            Pawn homing = null;
            if (CyberSkills.Eye(cast.pawn) && !IsExplosive(__instance) && CyberSkills.Target(cast.pawn,__instance,out Vector3 head) is Pawn victim)
            { homing=victim;V3 to=new V3(head.x,head.y,head.z)-eye;if(to.Length>.0001f)direction=to*(1f/to.Length); }
            V3 nativeDirection = WorldVelocityToNative(direction);
            if (!CeNativeRules.TryAngles(nativeDirection.X, nativeDirection.Y, nativeDirection.Z, out float elevation, out float rotation)
                || !float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z)
                || !FloorSpace.InWorld(cast.pawn.Map, eye)) { __result = false; return false; }
            Vector3 nativeEye = WorldPointToNative(cast.pawn.Map, eye, FloorSpace.Band(cast.pawn.Map, cast.pawn.Position));
            __state = new ShotScope { previous = scope, verb = __instance, pawn = cast.pawn, launcher=cast.pawn, equipment = __instance.EquipmentSource,
                origin = new Vector2(nativeEye.x, nativeEye.z), height = Math.Max(.001f, nativeEye.y), elevation = elevation, rotation = rotation,
                direction = direction, baseDirection=direction,eye=eye,homing=homing };
            scope = __state; return true;
        }

        internal static void AfterShift(Verb __instance)
        {
            ShotScope s = scope; if (s?.verb != __instance) {AimAtAircraft(__instance);return;}
            PelletDirection(s);
            // ShotDirection already contains our camera sway/recoil. Do not stack CE random aim on top.
            shotAngle.SetValue(__instance, s.elevation);
            shotRotation.SetValue(__instance, s.rotation); s.adjusted = true;
        }

        private enum LaunchOwner { Native, Vehicle, FirstPerson }

        private struct LaunchState
        {
            internal LaunchOwner Owner;
            internal Thing PreviousCePackLaunch;
            internal V3 VehicleDirection;
            internal bool Dispatcher;
            internal ArcLaunchScope PreviousArc;
        }

        private static void BeforeLaunch(Thing __instance, object[] __args, out LaunchState __state)
        {
            __state = default;
            if (ReferenceEquals(__args[6], __instance) && IsFragment(__instance.def)) return;
            // A derived Launch may delegate to the base eight-argument Launch.
            // The outer claimed entry owns adjustment/adoption exactly once.
            if (ReferenceEquals(cePackLaunch, __instance)) return;
            if (arcLaunch != null && ReferenceEquals(__args[0], arcLaunch.Launcher) && ReferenceEquals(__args[6], arcLaunch.Equipment))
            {
                // The native dispatcher already received the corrected carrier
                // muzzle and basis. Its child arguments contain the native fan.
                __state = ClaimLaunch(__instance, arcLaunch.Owner, __args);
                return;
            }
            VehicleCeSpeed.Launch(__instance,__args);
            if (scope == null && VehicleOrigin(__instance, __args,out V3 vehicleDirection))
            {
                __state = ClaimLaunch(__instance, LaunchOwner.Vehicle, __args);
                __state.VehicleDirection=vehicleDirection;
                return;
            }
            ShotScope s = scope;
            if (s == null || (!ReferenceEquals(__args[0], s.launcher) && !ReferenceEquals(__args[0],s.pawn)) || !ReferenceEquals(__args[6], s.equipment) || __instance.Map != s.launcher.Map) return;
            if (!s.adjusted) throw new InvalidOperationException("CE launch did not pass the bound ShiftTarget method");
            __args[1] = vector2.Make2(s.origin); __args[2]=s.elevation;__args[3]=s.rotation;__args[4] = s.height; s.launched = true;
            __state = ClaimLaunch(__instance, LaunchOwner.FirstPerson, __args);
        }

        private static LaunchState ClaimLaunch(Thing projectile, LaunchOwner owner, object[] arguments)
        {
            var state = new LaunchState { Owner = owner, PreviousCePackLaunch = cePackLaunch };
            cePackLaunch = projectile;
            if (ArcDefinition(projectile.def) != null)
            {
                state.Dispatcher = true; state.PreviousArc = arcLaunch;
                arcLaunch = new ArcLaunchScope { Launcher = arguments[0] as Thing, Equipment = arguments[6] as Thing, Owner = owner };
            }
            return state;
        }

        private static void FinishLaunch(LaunchState __state)
        {
            if (__state.Dispatcher) arcLaunch = __state.PreviousArc;
            if (__state.Owner != LaunchOwner.Native) cePackLaunch = __state.PreviousCePackLaunch;
        }

        private static void AfterLaunch(Thing __instance, LaunchState __state)
        {
            if (__state.Dispatcher) return;
            ArmProximity(__instance);
            if (__state.Owner == LaunchOwner.Native)
            {
                if (!AdoptTurretAirThreat(__instance) && !AdoptProximityFlight(__instance) && !ObserveNativeTurretRound(__instance)) RememberNativeAirThreat(__instance);
                return;
            }
            if (__state.Owner == LaunchOwner.Vehicle) { AdoptVehicle(__instance,__state.VehicleDirection); return; }
            if (__state.Owner == LaunchOwner.FirstPerson && scope?.launched == true && __instance.Map == scope.launcher.Map)
            {
                float speed = ViewRules.Speed(ActualLaunchSpeed(__instance), FpsMod.Settings.speed);
                bool grenade=IsGrenade(scope.verb);
                if(grenade)speed=ThrowSpeed(scope.verb,scope.direction);
                if(scope.artillery)SetArtilleryArc(scope,speed);
                traces.Remove(__instance); traces.Add(__instance, new Trace { map = __instance.Map, launcher = scope.launcher, turretOperator = scope.pawn,
                    velocity = scope.artillery?scope.arcVelocity:grenade?scope.direction * speed:ReadNativeVelocity(__instance)*(speed/ActualLaunchSpeed(__instance)), contacts = new Ballistics.ExternalTrace(scope.launcher, scope.pawn), pos=scope.eye,previous=scope.eye,homing=scope.homing,bounces=grenade?1:0,
                    structure = StructureFor(__instance, scope.equipment?.def.defName, scope.verb.verbProps.burstShotCount),
                    gravity = scope.artillery?scope.arcGravity:scope.homing!=null?0f:grenade?ThrowGravity(scope.verb):ViewRules.Gravity(FpsMod.Settings.gravity),
                    nativePhysics = !grenade && !scope.artillery, nativeVelocityMotion = scope.homing != null,
                    nativeHeight = FloorSpace.Band(__instance.Map, scope.launcher.Position).Height,
                    nativeShift = FloorSpace.Band(__instance.Map, scope.launcher.Position).Shift,
                    nativeLevel = FloorSpace.Band(__instance.Map, scope.launcher.Position).Level, nativeUnitsVersion=1, lastTick = Find.TickManager.TicksGame });
                FpsLaunched++;
                CombatDiagnostics.CeLaunch(__instance, scope.launcher, scope.eye);
            }
        }
        internal static float ActualLaunchSpeed(Thing projectile) => ActualLaunchSpeed(projectile, false);
        internal static float ActualLaunchSpeed(Thing projectile, bool allowStationaryBomb)
        {
            float speed = (float)launchedShotSpeed.GetValue(projectile);
            if (!float.IsFinite(speed) || speed < 0f || (!allowStationaryBomb && speed == 0f))
                throw new InvalidOperationException("CE launch did not produce a finite positive shotSpeed for " + projectile.def.defName);
            // CE Launch writes shotSpeed from its final velocity in cells/second.
            // Retain native charge selection and convert once to the FPS per-tick motor.
            return speed / 60f;
        }
        internal static bool NativeProjectile(ThingDef definition) => Ready && definition?.thingClass != null
            && projectileType.IsAssignableFrom(definition.thingClass);
        internal static void AfterShot(bool __result, ShotScope __state)
        {
            if (__state == null) return;
            try
            {
                if (__result && __state.launched)
                {
                    if(IsGrenade(__state.verb)){WeaponView.Throw();return;}
                    float damage = (projectileDef.GetValue(__state.verb) as ThingDef)?.projectile?.GetDamageAmount(__state.equipment, null) ?? 10f;
                    AimMotion.Kick(__state.pawn, damage); WeaponView.Kick(damage);
                }
            }
            finally { ReleaseScope(__state); }
        }
        private static void ReleaseScope(ShotScope state)
        { if (state == null || state.released) return; if (ReferenceEquals(scope, state)) scope = state.previous; state.released = true; }
        internal static Exception FinalizeShot(Exception __exception, ShotScope __state)
        { ReleaseScope(__state); return __exception; }

        internal static bool BeforeDraw(Thing __instance,ref Vector3 __0)
        {
            if (DrawFragment(__instance)) return false;
            if (!View3D.Active) return true;
            if (!traces.TryGetValue(__instance, out Trace trace))
            {
                if (!ObserveNativeTurretRound(__instance)) RememberNativeAirThreat(__instance);
                return true;
            }
            if (__instance.Map != trace.map) return true;
            try
            {
                if (!Visuals.Primitives(out _, out _)) return true;
                if(trace.completed){__0=new Vector3(trace.pos.X,trace.pos.Y,trace.pos.Z);return true;}
                bool airLaunched = IsAirborne(trace);
                if (DrawAsOrdnance(__instance, trace, airLaunched))
                {
                    if (!airLaunched) DrawTurretTracer(__instance, trace, trace.pos - trace.previous, 2.4f);
                    DrawOrdnance(trace);
                    return false;
                }
                return !DrawRoundTracer(__instance, trace, airLaunched);
            }
            catch (Exception e) { Log.WarningOnce("[FpsBridge] CE tracer unavailable; native drawing retained: " + e.Message, 19271901); return true; }
        }

        private static bool IsAirborne(Trace trace)
            => trace.launcher is Pawn aircraft && AerialFlight.Airborne(aircraft);

        // Airborne rounds can occupy a ProjectZ row outside the current native
        // draw list even though its true 3D position is inside the camera.
        internal static void DrawAirborneRounds(Map map)
        {
            if (!Ready || !View3D.Active || map == null) return;
            foreach (var entry in traces)
            {
                Thing round = entry.Key;
                Trace trace = entry.Value;
                if (trace.completed || trace.map != map || round.Map != map || !round.Spawned
                    || !IsAirborne(trace)) continue;
                IntVec3 cell = FloorSpace.Cell(map, trace.pos);
                if (!cell.InBounds(map) || cell.Fogged(map)) continue;
                if (DrawAsOrdnance(round, trace, true)) DrawOrdnance(trace);
                else DrawRoundTracer(round, trace, true);
            }
        }

        private static bool DrawAsOrdnance(Thing round, Trace trace, bool airLaunched)
        {
            if (!IsExplosiveDef(round.def)) return false;
            return !airLaunched || !(trace.explosiveGun ??= AerialCombat.ExplosiveGunRound((Pawn)trace.launcher, round.def));
        }

        private static bool DrawRoundTracer(Thing round, Trace trace, bool airLaunched)
        {
            if (airLaunched && trace.lastAirDrawFrame == Time.frameCount) return true;
            V3 delta = trace.pos - trace.previous;
            float length = delta.Length;
            if (length < .02f) return false;
            var target = VehicleApi.Get(round, "intendedTarget") is LocalTargetInfo intended ? intended : LocalTargetInfo.Invalid;
            if (!DrawTurretTracer(round, trace, delta, 2.4f))
                Visuals.Tracer(trace.pos, delta, Math.Min(2.4f, length), Vector3.zero, AerialCombat.TracerScale(trace.launcher, target));
            if (airLaunched) trace.lastAirDrawFrame = Time.frameCount;
            FpsDraws++;
            return true;
        }

        private static bool DrawTurretTracer(Thing round, Trace trace, V3 direction, float traveled)
        {
            if (!TurretTracers.IsMount(trace.launcher)) return false;
            var target = VehicleApi.Get(round, "intendedTarget") is LocalTargetInfo intended ? intended : LocalTargetInfo.Invalid;
            return TurretTracers.DrawOwned(round, trace.launcher, VehicleApi.Get(round, "equipmentDef") as ThingDef,
                trace.pos, direction, traveled, AerialCombat.TracerScale(trace.launcher, target), true);
        }

        private static void DrawOrdnance(Trace trace)
        {
            int frame = Time.frameCount;
            if (trace.lastAirDrawFrame == frame) return;
            trace.lastAirDrawFrame = frame;
            Visuals.Ordnance(trace.pos, trace.velocity);
            if (IsAirborne(trace) && (trace.pos - trace.previous).Length > .01f)
                Visuals.AerialOrdnanceTrail(trace.pos, trace.velocity);
            FpsDraws++;
        }

        internal static string MagazineStatus(Pawn pawn)
        {
            Verb verb = pawn?.equipment?.PrimaryEq?.PrimaryVerb;
            if (!Ready || !launchType.IsInstanceOfType(verb)) return "CE · " + Status;
            object ammo = compAmmo.GetValue(verb);
            if (ammo == null) return L.T("CE · 无弹匣组件", "CE · no magazine component");
            string count = (bool)hasMagazine.GetValue(ammo) ? curRounds.GetValue(ammo) + " / " + magSize.GetValue(ammo)
                : L.T("无弹匣", "no magazine");
            string supply = (bool)useAmmo.GetValue(ammo) ? (currentAmmo.GetValue(ammo) as Def)?.LabelCap.ToString() ?? "CE"
                : L.T("弹药消耗关闭", "ammo consumption off");
            string reload = IsReload(pawn, verb) ? L.T(" · 换弹 ", " · reload ") + (Math.Max(0, pawn.jobs.curDriver.ticksLeftThisToil) / 60f).ToString("F1") + "s" : "";
            return "CE " + count + " · " + supply + reload;
        }
        internal static void DrawHud(Pawn pawn)
        {
            Verb verb = pawn?.equipment?.PrimaryEq?.PrimaryVerb;
            string text = Supported(pawn, verb) ? L.T("FPS弹道 · CE弹药/伤害", "FPS ballistics · CE ammo/damage")
                : L.T("CE候选 · 当前武器/位置尚未接入", "CE candidate · weapon/location not supported");
            if (Ready && launchType.IsInstanceOfType(verb))
            { object modes = fireModes.GetValue(verb); if (modes != null) text += " · " + fireMode.GetValue(modes); }
            GameFont font = Text.Font; TextAnchor anchor = Text.Anchor;
            try { Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperCenter;
                Widgets.Label(new Rect(UI.screenWidth*.5f-240, UI.screenHeight*.5f+46, 480, 24), text); }
            finally { Text.Font = font; Text.Anchor = anchor; }
        }

        internal static void Release(Pawn pawn)
        {
            foreach (var pair in casts)
            {
                Cast cast = pair.Value; if (!cast.managed || cast.pawn != pawn) continue;
                cast.managed = false;
                bool bursting = pair.Key.Bursting;
                bool warming = pawn?.stances?.curStance is Stance_Warmup warmup && warmup.verb == pair.Key;
                if (warming) ((Stance_Warmup)pawn.stances.curStance).Interrupt();
                if (bursting || warming) pair.Key.Reset(); // Reset CE's private aiming state as well as Verse burst state.
                if (bursting)
                {
                    int remaining = pawn.stances.curStance is Stance_Cooldown cool && cool.verb == pair.Key ? cool.ticksLeft : 0;
                    int ticks = Math.Max(remaining, Math.Max(1, pair.Key.verbProps.AdjustedCooldownTicks(pair.Key, pawn)));
                    pawn.stances.SetStance(new Stance_Cooldown(ticks, LocalTargetInfo.Invalid, pair.Key));
                }
            }
            // The native reload job and real magazine are left to CE, including after leaving FPS.
            ClearHud();
        }
        internal static void ClearHud()
        { hudPawn = null; hudVerb = null; hudAmmo = null; observedReload = null; reloadMaximum = 0; reloadToil = -1; }
        internal static void Reset()
        { casts.Clear(); traces = new ConditionalWeakTable<Thing, Trace>(); scope = null; ClearHud(); lastReloadRequest = -60; }
    }

    internal static partial class Bridge
    {
        internal static bool CeControlsAllowed => Active && CanDrive() && !TurretControl.Manned && !QuickTend.For(pawn)
            && !ConsumableHands.Using(pawn) && !Find.Targeter.IsTargeting && !WorkControl.Swallow && !Vault.Active
            && pawn.CurJob?.ability == null;
    }
}
