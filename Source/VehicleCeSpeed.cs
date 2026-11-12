using System;
using System.Linq;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleCeSpeed
    {
        [ThreadStatic] private static object firing;
        private static readonly Dictionary<Type, (MethodInfo method, Type vector, FieldInfo x)> chargeBrackets = new Dictionary<Type, (MethodInfo, Type, FieldInfo)>();
        internal static object Current => firing;
        internal static V3 Aim(Pawn vehicle,object gun,ThingDef projectile,V3 origin)
            => AimAt(vehicle,gun,projectile,origin,(LocalTargetInfo)VehicleApi.Get(gun,"targetInfo"));
        internal static V3 AimAt(Pawn vehicle,object gun,ThingDef projectile,V3 origin,LocalTargetInfo info,bool leadAircraft=false)
        {
            V3 point;
            V3 motion = default;
            if(leadAircraft && info.Thing is Pawn aircraft && AerialFlight.TryGet(aircraft,out var flight) && flight.Airborne)
            {
                point = VehicleModels.V(AerialTargetLock.Point(aircraft));
                motion = flight.Motor.Velocity * (1f / 60f);
            }
            else if(info.Thing is Pawn target)
            {
                var parts=new List<HitVolume>();BodyRegions.AddVolumes(target,parts);
                var torso=parts.FirstOrDefault(x=>x.Region==HitRegion.Torso);
                point=parts.Any(x=>x.Region==HitRegion.Torso)?(torso.Kind==VolumeKind.Capsule?(torso.A+torso.B)*.5f:torso.A):FloorSpace.Spot(target)+new V3(0,.7f,0);
            }
            else if(info.Thing!=null)point=FloorSpace.Spot(info.Thing)+new V3(0,.7f,0);
            else {var band=FloorSpace.Band(vehicle.Map,info.Cell);point=new V3(info.Cell.x+.5f,band.Height+.7f,info.Cell.z-band.Shift+.5f);}
            float speed=ViewRules.Speed(VehicleWeapons.NativeSpeed(gun,projectile),FpsMod.Settings.speed);
            bool nativeCe = CeCompat.NativeProjectile(projectile);
            V3 AimPoint(V3 aim) => nativeCe
                ? CeCompat.NativeAim(vehicle.Map, FloorSpace.Band(vehicle.Map, vehicle.Position), projectile, origin, aim, speed)
                : VanillaAim(aim - origin, speed);

            V3 direction = AimPoint(point);
            if (!motion.Finite || motion.Length < .0001f) return direction;
            // The motor reports cells/second and the projectile advances once per game tick.
            // Refine the intercept using the horizontal speed at the solved elevation.
            V3 intercept = point;
            for (int i = 0; i < 4; i++)
            {
                V3 velocity = nativeCe ? CeCompat.NativeShotVelocity(direction, speed) : direction * speed;
                float horizontalSpeed = new Vector2(velocity.X, velocity.Z).magnitude;
                if (horizontalSpeed < .001f || !float.IsFinite(horizontalSpeed)) break;
                V3 displacement = intercept - origin;
                float ticks = new Vector2(displacement.X, displacement.Z).magnitude / horizontalSpeed;
                if (ticks > 120f) break;
                V3 next = point + motion * ticks;
                V3 nextDirection = AimPoint(next);
                if (!nextDirection.Finite || nextDirection.Length < .001f) break;
                direction = nextDirection;
                if ((next - intercept).Length < .01f) break;
                intercept = next;
            }
            return direction;
        }

        private static V3 VanillaAim(V3 d,float speed)
        {
            float horizontal=MathF.Sqrt(d.X*d.X+d.Z*d.Z);
            float angle=MathF.Atan2(d.Y,Math.Max(.01f,horizontal)),gravity=ViewRules.Gravity(FpsMod.Settings.gravity);
            for(int i=0;i<6;i++){float time=horizontal/Math.Max(.01f,speed*MathF.Cos(angle));angle=MathF.Atan2(d.Y+.5f*gravity*time*(time+1),Math.Max(.01f,horizontal));}
            return HitMath.Direction(MathF.Atan2(d.X,d.Z)*Mathf.Rad2Deg,-angle*Mathf.Rad2Deg);
        }
        internal static void Install(Harmony harmony)
        {
            var method=AccessTools.DeclaredMethod(VehicleApi.TurretType,"FireTurretCE");
            if(method==null)return;
            harmony.Patch(method,prefix:new HarmonyMethod(typeof(VehicleCeSpeed),nameof(BeforeFire)),
                finalizer:new HarmonyMethod(typeof(VehicleCeSpeed),nameof(AfterFire)));
        }
        public static void BeforeFire(object __instance,out object __state){__state=firing;firing=__instance;}
        public static void AfterFire(object __state){firing=__state;}
        internal static float Charges(object gun)
        {
            var vehicle=VehicleApi.Get(gun,"vehicle") as Pawn;
            var comp=vehicle?.AllComps.FirstOrDefault(c=>c.GetType().Name=="CompCharges");
            if(comp==null)return 0;
            var values=VehicleApi.List(comp.props,"chargeSpeeds");float maximum=0;
            foreach(var value in values)maximum=Math.Max(maximum,Convert.ToSingle(value));
            return maximum;
        }
        private static float ExplicitCeSpeed(object gun)
        {
            var def=VehicleApi.Get(gun,"def") as Def;
            var extension=def?.modExtensions?.FirstOrDefault(e=>e.GetType().Name=="CETurretDataDefModExtension");
            var field=extension==null?null:AccessTools.Field(extension.GetType(),"speed");
            return field==null?0:Convert.ToSingle(field.GetValue(extension));
        }
        internal static float ConfiguredSpeed(object gun, ThingDef projectile)
        {
            float explicitSpeed = ExplicitCeSpeed(gun);
            float authoredSpeed = VehicleApi.Float(VehicleApi.Get(gun, "def"), "projectileSpeed");
            float projectileSpeed = projectile?.projectile?.speed ?? 0f;
            float speed = explicitSpeed > 0f ? explicitSpeed : authoredSpeed > 0f ? authoredSpeed : projectileSpeed;
            var vehicle = VehicleApi.Get(gun, "vehicle") as Pawn;
            var charges = vehicle?.AllComps.FirstOrDefault(c => c.GetType().Name == "CompCharges");
            // Match CE_Utility.LaunchProjectileCE's real charge bracket. The
            // existing direct-fire explicit-speed correction in Launch remains authoritative.
            if (charges != null && (projectile?.projectile?.flyOverhead == true || explicitSpeed <= 0f))
            {
                LocalTargetInfo target = (LocalTargetInfo)VehicleApi.Get(gun, "targetInfo");
                if (target.IsValid)
                {
                    if (!chargeBrackets.TryGetValue(charges.GetType(), out var bracket))
                    {
                        MethodInfo method = charges.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault(m =>
                        {
                            ParameterInfo[] p = m.GetParameters();
                            return m.Name == "GetChargeBracket" && m.ReturnType == typeof(bool) && p.Length == 4
                                && p[0].ParameterType == typeof(float) && p[1].ParameterType == typeof(float) && p[2].ParameterType == typeof(float)
                                && p[3].ParameterType.IsByRef && p[3].ParameterType.GetElementType().FullName == "UnityEngine.Vector2";
                        });
                        if (method == null) throw new MissingMethodException("CE CompCharges.GetChargeBracket(float,float,float,out Vector2)");
                        Type vector = method.GetParameters()[3].ParameterType.GetElementType();
                        bracket = (method, vector, AccessTools.Field(vector, "x"));
                        chargeBrackets.Add(charges.GetType(), bracket);
                    }
                    // CE chooses this bracket before our launch-origin prefix. Its
                    // input height is FireTurretCE's authored CE shotHeight.
                    var extension = (VehicleApi.Get(gun, "def") as Def)?.modExtensions?.FirstOrDefault(e => e.GetType().Name == "CETurretDataDefModExtension");
                    float height = extension == null ? 1f : Convert.ToSingle(VehicleApi.Get(extension, "shotHeight"));
                    float gravity = Convert.ToSingle(VehicleApi.Get(projectile.projectile, "GravityPerWidth"));
                    object[] args = { (target.Cell - vehicle.Position).LengthHorizontal, height, gravity, Activator.CreateInstance(bracket.vector) };
                    if ((bool)bracket.method.Invoke(charges, args)) speed = Convert.ToSingle(bracket.x.GetValue(args[3]));
                }
            }
            // The native ProjectileCE launch enforces the projectile's authored minimum.
            return Math.Max(speed, projectileSpeed);
        }
        internal static void Launch(Thing projectile,object[] args)
        {
            if(firing==null||args.Length<6||VehicleApi.Get(firing,"vehicle") as Thing!=args[0] as Thing)return;
            float speed=Convert.ToSingle(args[5]);
            float configured=ExplicitCeSpeed(firing);
            // CE's vehicle-wide charge component also affects co-mounted direct-fire guns.
            // Their explicit CE speed must survive a mortar's charge selection.
            if(!projectile.def.projectile.flyOverhead&&configured>0&&Charges(firing)>0)args[5]=configured;
            else if(speed<=0&&Charges(firing)>0)args[5]=Charges(firing);
        }
    }
}
