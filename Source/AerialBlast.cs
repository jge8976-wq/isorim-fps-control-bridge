using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;
namespace HSK.FpsBridge
{
    // Keep native damage/armour, but reject the 2D projection of an airborne hull.
    internal static class AerialBlast
    {
        internal sealed class Point { internal Map map; internal V3 at; internal Pawn directlyDamaged; }
        static readonly ConditionalWeakTable<Thing,Point> points=new();
        [ThreadStatic] static Point active;
        internal static int Rejected;
        internal static void Remember(Thing thing,Map map,V3 at)
        { if(thing==null||map==null||!at.Finite)return;points.Remove(thing);points.Add(thing,new Point{map=map,at=at}); }
        internal static void DirectVictim(Thing projectile,Pawn vehicle)
        {
            if(points.TryGetValue(projectile,out var point))point.directlyDamaged=vehicle;
        }
        public static void Enter(Thing __instance,out Point __state)
        {__state=active;if(points.TryGetValue(__instance,out var p))active=p;}
        public static Exception Exit(Exception __exception,Point __state){active=__state;return __exception;}
        public static void Start(Explosion __instance)
        {
            if(active!=null&&active.map==__instance.Map)
            {
                Remember(__instance,active.map,active.at);
                points.GetOrCreateValue(__instance).directlyDamaged=active.directlyDamaged;
            }
        }
        public static void StartEffects(Explosion __instance) => ExplosionEffects.NativeStart(__instance);
        internal static V3 Origin(Explosion explosion)
        {
            if(points.TryGetValue(explosion,out var p)&&p.map==explosion.Map)return p.at;
            FloorBand band=FloorSpace.Band(explosion.Map,explosion.Position);
            var field=AccessTools.Field(explosion.GetType(),"height");
            float height=field==null?0f:Convert.ToSingle(field.GetValue(explosion)); // CE burst height; vanilla has no field.
            return new V3(explosion.Position.x+.5f,band.Height+Math.Max(0,height),explosion.Position.z-band.Shift+.5f);
        }
        internal static float Distance(Pawn v,V3 origin)
        {
            var shapes=new List<HitVolume>();AerialGeometry.Volumes(v,shapes);
            float best=float.PositiveInfinity;
            foreach(var b in shapes)
            {
                V3 d=origin-b.A;
                V3 local=new V3(V3.Dot(d,b.AxisRight),V3.Dot(d,b.AxisUp),V3.Dot(d,b.AxisForward));
                float x=Math.Max(0,Math.Abs(local.X)-b.B.X),y=Math.Max(0,Math.Abs(local.Y)-b.B.Y),z=Math.Max(0,Math.Abs(local.Z)-b.B.Z);
                best=Math.Min(best,MathF.Sqrt(x*x+y*y+z*z));
            }
            return best;
        }
        public static bool Damage(Explosion explosion,Thing t)
        {
            if(!(t is Pawn v)||!AerialFlight.Airborne(v))return true;
            if(points.TryGetValue(explosion,out var point)&&point.directlyDamaged==v)return false;
            bool hit=Distance(v,Origin(explosion))<=explosion.radius+.01f;
            if(!hit)Rejected++;return hit;
        }
        // Persist elevated delayed bursts; ground explosions retain the native fallback.
        public static void Expose(Thing __instance)
        {
            bool has=points.TryGetValue(__instance,out var p);float x=p?.at.X??0,y=p?.at.Y??0,z=p?.at.Z??0;
            Scribe_Values.Look(ref has,"fpsAirBurstOriginR14");
            Scribe_Values.Look(ref x,"fpsAirBurstX");Scribe_Values.Look(ref y,"fpsAirBurstY");Scribe_Values.Look(ref z,"fpsAirBurstZ");
            // Locals do not survive the separate Scribe passes. Retain coordinates
            // during LoadingVars, then attach the restored map in PostLoadInit.
            if(Scribe.mode==LoadSaveMode.LoadingVars&&has)
            {points.Remove(__instance);points.Add(__instance,new Point{at=new V3(x,y,z)});}
            if(points.TryGetValue(__instance,out var restored))
            {
                Scribe_References.Look(ref restored.directlyDamaged,"fpsAirBurstDirectVictim");
                if(Scribe.mode==LoadSaveMode.PostLoadInit)restored.map=__instance.Map;
            }
        }
        internal static void Install(Harmony h)
        {
            var damage=new HashSet<MethodInfo>();var contexts=new HashSet<MethodInfo>();
            foreach(Type t in GenTypes.AllTypes)
            {
                if(typeof(DamageWorker).IsAssignableFrom(t))
                {var m=AccessTools.DeclaredMethod(t,"ExplosionDamageThing");if(m!=null&&!m.IsAbstract)damage.Add(m);}
                if(typeof(Projectile).IsAssignableFrom(t)||t.FullName=="CombatExtended.ProjectileCE"||t.FullName=="CombatExtended.ProjectileCE_Explosive")
                    foreach(var m in t.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                        if((m.Name=="Impact"||m.Name=="Explode")&&!m.IsAbstract)contexts.Add(m);
            }
            foreach(var m in damage)h.Patch(m,prefix:new HarmonyMethod(typeof(AerialBlast),nameof(Damage)){priority=Priority.First});
            foreach(var m in contexts)h.Patch(m,prefix:new HarmonyMethod(typeof(AerialBlast),nameof(Enter)),finalizer:new HarmonyMethod(typeof(AerialBlast),nameof(Exit)));
            var remember=new HarmonyMethod(typeof(AerialBlast),nameof(Start));
            var render=new HarmonyMethod(typeof(AerialBlast),nameof(StartEffects));
            h.Patch(AccessTools.Method(typeof(Explosion),"StartExplosion"),prefix:remember,postfix:render);
            var ce=AccessTools.TypeByName("CombatExtended.ExplosionCE");if(ce!=null)h.Patch(AccessTools.Method(ce,"StartExplosionCE"),prefix:remember,postfix:render);
            foreach(Type t in new[]{typeof(Explosion),typeof(Projectile)})h.Patch(AccessTools.DeclaredMethod(t,"ExposeData"),postfix:new HarmonyMethod(typeof(AerialBlast),nameof(Expose)));
            Log.Message("[FpsBridge] AERIAL_BLAST READY; native workers="+damage.Count+"; true-space hull distance");
        }
    }
}
