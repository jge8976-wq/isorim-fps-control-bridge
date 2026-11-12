using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using N=System.Numerics;
namespace HSK.FpsBridge
{
    public sealed class VehicleContactGame:GameComponent
    {
        public VehicleContactGame(Game game){VehicleContacts.Reset();}
        public override void GameComponentTick(){if(VehicleApi.Ready)VehicleContacts.Tick();}
    }
    internal static class VehicleContacts
    {
        sealed class Body {internal AerialMotor Pose;internal int LastTick;internal float Recovery;internal IntVec3 NativeCell;internal bool Subcell;}
        sealed class Episode {internal bool Armed=true;internal int Last;}
        static readonly Dictionary<Pawn,Body> bodies=new();static readonly Dictionary<(Pawn,Pawn),Episode> episodes=new();
        internal static int Events,DamageEvents;internal static bool Debug=false;
        internal static readonly List<object> Diagnostics=new();
        internal static readonly HashSet<Pawn> DebugActors=new();
        internal static int NativePathContacts,NativeFootprintBypasses,NativeMotionContacts;
        static DamageDef collisionDamage, airBlastStress;
        static bool ceStructuralGuard;
        static void EnsureCeStructuralGuard()
        {
            if(ceStructuralGuard)return;
            var prefix=AccessTools.Method(AccessTools.TypeByName("CombatExtended.Compatibility.VehiclesCompat.VehicleArmorPatch"),"Prefix");
            if(prefix==null)return;
            // CE's vehicle prefix assumes every damage has an armour category.
            // VF handles structural deformation. CE still resolves the blast and fragments.
            new Harmony("hsk.fpsbridge.contact.r4").Patch(prefix,prefix:new HarmonyMethod(typeof(VehicleContacts),nameof(BeforeCeArmour)));
            ceStructuralGuard=true;Log.Message("[FpsBridge] CE_VEHICLE_STRUCTURAL_CONTACT READY; native VF structural damage");
        }
        public static bool BeforeCeArmour(DamageInfo dinfo,ref bool __result)
        {if(dinfo.Def!=collisionDamage&&dinfo.Def!=airBlastStress)return true;__result=true;return false;}
        internal static void Reset(){bodies.Clear();episodes.Clear();Events=DamageEvents=NativePathContacts=NativeFootprintBypasses=NativeMotionContacts=0;Diagnostics.Clear();DebugActors.Clear();Debug=false;}
        internal static bool Recovering(Pawn p)=>p!=null&&bodies.TryGetValue(p,out var b)&&b.Recovery>0;
        internal static bool RecoveredPose(Pawn p,out AerialMotor m)
        {m=default;if(p==null||!bodies.TryGetValue(p,out var b)||!b.Subcell||p.Position!=b.NativeCell||VehicleApi.Bool(VehicleApi.Get(p,"vehiclePather"),"Moving")||AerialFlight.TryGet(p,out _))return false;m=b.Pose;return true;}
        internal static void Install(Harmony h)
        {
            collisionDamage=DefDatabase<DamageDef>.GetNamed("FPS_VehicleCollisionR4");
            airBlastStress=DefDatabase<DamageDef>.GetNamedSilentFail("FPS_AirblastStress");
            if(collisionDamage.armorCategory!=null)throw new InvalidOperationException("Vehicle structural impact must not be filtered as ballistic armour");
            h.Patch(AccessTools.Method(VehicleApi.PawnType,"DynamicDrawPhaseAt",new[]{typeof(DrawPhase),typeof(Vector3),typeof(bool)}),postfix:new HarmonyMethod(typeof(VehicleContacts),nameof(DrawDebug)));
            h.Patch(AccessTools.Method(AccessTools.TypeByName("Vehicles.VehiclePathFollower"),"TryEnterNextPathCell"),prefix:new HarmonyMethod(typeof(VehicleContacts),nameof(NativeEnter)));
            h.Patch(AccessTools.Method(AccessTools.TypeByName("Vehicles.PathingHelper"),"AnyVehicleBlockingPathAt"),postfix:new HarmonyMethod(AccessTools.Method(typeof(VehicleContacts),nameof(NativeFootprint)).MakeGenericMethod(VehicleApi.PawnType)));
            h.Patch(AccessTools.Method(AccessTools.TypeByName("Vehicles.VehicleStatHandler"),"HitPawn"),postfix:new HarmonyMethod(typeof(VehicleContacts),nameof(AfterCollisionCrewHit)));
            Log.Message("[FpsBridge] VEHICLE_CONTACTS READY; active triangle models; symmetric response; native path cell interception; native component damage");
        }
        public static void AfterCollisionCrewHit(DamageInfo dinfo,ref bool __result)
        {
            // Keep the native occupant injury, but a crew hit cannot absorb the
            // structural deformation that must also reach the vehicle itself.
            if(dinfo.Def==collisionDamage||dinfo.Def==airBlastStress)__result=false;
        }
        public static void NativeFootprint<T>(T vehicle,ref T __result) where T:Pawn
        {
            Pawn a=vehicle,b=__result;if(a==null||b==null||!a.Spawned||!b.Spawned)return;
            // This helper reports VEHICLES only; terrain/buildings remain native.
            // Known physical models defer admission to NativeEnter's swept query.
            if(VehicleGeometry.Parts(a,VehicleGeometry.Pose(a),new List<VehicleGeometry.Part>())&&VehicleGeometry.Parts(b,VehicleGeometry.Pose(b),new List<VehicleGeometry.Part>()))
            {__result=default;NativeFootprintBypasses++;}
        }
        public static bool NativeEnter(object __instance)
        {
            if(VehicleApi.Get(__instance,"vehicle") is not Pawn p||!p.Spawned||VehicleDriving.ActiveFor(p)||AerialFlight.OwnsMotion(p))return true;
            if(Recovering(p))return false;
            IntVec3 cell=(IntVec3)VehicleApi.Get(__instance,"nextCell");if(!cell.IsValid||cell==p.Position)return true;
            var before=VehicleGeometry.Pose(p);var next=before;
            next.Position+=new V3(cell.x-p.Position.x,0,cell.z-p.Position.z);
            next.Yaw=Mathf.Atan2(next.Position.X-before.Position.X,next.Position.Z-before.Position.Z)*Mathf.Rad2Deg;
            float speed=Math.Max(.5f,VehicleApi.ManualSpeed(p));var dir=next.Position-before.Position;next.Velocity=dir*(speed/Math.Max(.001f,dir.Length));
            if(!Resolve(p,before,ref next,1f/60))return true;
            NativePathContacts++;Write(p,next,.3f);return false;
        }
        internal static bool Clear(Pawn a,AerialMotor pose,Map map)
        {
            foreach(var b in map.mapPawns.AllPawnsSpawned)
                if(b!=a&&VehicleApi.IsVehicle(b)&&!b.Dead&&VehicleMeshContact.Contact(a,pose,b,VehicleGeometry.Pose(b),out _))return false;
            return true;
        }
        internal static float Mass(Pawn v)
        {object def=AccessTools.Field(AccessTools.TypeByName("Vehicles.VehicleStatDefOf"),"Mass")?.GetValue(null);float m=def==null?0:Convert.ToSingle(VehicleApi.Call(v,"GetStatValue",def));return float.IsFinite(m)&&m>1?m:Math.Max(100,v.def.size.x*v.def.size.z*150);}
        internal static bool Resolve(Pawn a,AerialMotor before,ref AerialMotor next,float dt)
        {
            if(!a.Spawned||a.Dead)return false;bool touched=false;
            foreach(Pawn b in a.Map.mapPawns.AllPawnsSpawned.ToArray())
            {
                if(b==a||b.Destroyed||b.Dead||!b.Spawned||!VehicleApi.IsVehicle(b))continue;
                bool nativeMotion=(!VehicleDriving.ActiveFor(a)&&!AerialFlight.TryGet(a,out _)&&VehicleApi.Bool(VehicleApi.Get(a,"vehiclePather"),"Moving"))||(!VehicleDriving.ActiveFor(b)&&!AerialFlight.TryGet(b,out _)&&VehicleApi.Bool(VehicleApi.Get(b,"vehiclePather"),"Moving"));
                AerialMotor bp=VehicleGeometry.Pose(b);
                if(!VehicleMeshContact.Sweep(a,before,next,b,bp,out var contact))continue;
                bool aircraft=AerialFlight.Supports(a)||AerialFlight.Supports(b);
                bool modelTouch=!aircraft||AircraftModelContact.Touches(a,before,next,b);
                bool groundA=!AerialFlight.Airborne(a),groundB=!AerialFlight.Airborne(b);
                Vector3 n=VehicleMeshContact.U3(contact.Normal);
                Vector3 va=VehicleModels.U(next.Velocity),vb=Velocity(b,bp),relative=va-vb;
                if(groundA&&groundB)n.y=0;
                if(n.sqrMagnitude<1e-7f)n=relative.sqrMagnitude>1e-7f?-relative:VehicleModels.U(before.Position-bp.Position);
                if(groundA&&groundB)n.y=0;if(n.sqrMagnitude<1e-7f)n=Vector3.right;n=n.normalized;
                if(Vector3.Dot(n,relative)>0)n=-n;
                float ma=Mass(a),mb=Mass(b),wa=mb/(ma+mb),wb=ma/(ma+mb);
                var corrected=before;corrected.Position=before.Position+(next.Position-before.Position)*Math.Max(0,contact.Time-.001f);corrected.Yaw=before.Yaw+Mathf.DeltaAngle(before.Yaw,next.Yaw)*contact.Time;
                corrected.Pitch=before.Pitch+Mathf.DeltaAngle(before.Pitch,next.Pitch)*contact.Time;corrected.Roll=before.Roll+Mathf.DeltaAngle(before.Roll,next.Roll)*contact.Time;
                var other=bp;
                // Resolve even deep/spawn overlaps using the same mesh query.
                // Try a physical normal first; a jam can leave along a second
                // actual clear direction, never by replacing geometry with a box.
                if(!Separate(a,ref corrected,b,ref other,ref n,wa,wb,groundA&&groundB))continue;
                var na=VehicleMeshContact.N3(va);var nb=VehicleMeshContact.N3(vb);VehicleImpulseRules.Bounce(ref na,ref nb,VehicleMeshContact.N3(n),ma,mb);
                corrected.Velocity=VehicleModels.V(VehicleMeshContact.U3(na));other.Velocity=VehicleModels.V(VehicleMeshContact.U3(nb));
                if(AerialFlight.Profile(a)?.kind==AerialFlightKind.FixedWing)corrected=AerialFixedWing.Synchronize(corrected,!groundA);
                if(AerialFlight.Profile(b)?.kind==AerialFlightKind.FixedWing)other=AerialFixedWing.Synchronize(other,!groundB);
                next=corrected;touched=true;Write(b,other,.3f);Stamp(a,next,.3f);
                var key=a.thingIDNumber<b.thingIDNumber?(a,b):(b,a);int now=Find.TickManager.TicksGame;
                if(!episodes.TryGetValue(key,out var episode))episodes[key]=episode=new Episode();
                if(episode.Armed&&modelTouch)
                {
                    episode.Armed=false;Events++;if(nativeMotion)NativeMotionContacts++;float damage=VehicleImpulseRules.Damage(relative.magnitude,ma,mb);
                    Vector3 point=VehicleMeshContact.U3(contact.Point);
                    float ha=Health(a),hb=Health(b);
                    if(aircraft)
                    {
                        Pawn actor=AerialFlight.Supports(a)?a:b,target=actor==a?b:a;
                        AerialMotor start=actor==a?before:bp,end=actor==a?next:other;
                        if(modelTouch)
                        {
                            float health=Health(target);
                            Damage(target,actor,point,damage);
                            AircraftCollisionDamage.ReturnDamage(actor,target,Math.Max(0,health-Health(target)),start.Yaw);
                            DamageEvents++;
                        }
                    }
                    else {Damage(a,b,point,damage);if(b.Spawned&&!b.Dead)Damage(b,a,point,damage);DamageEvents++;}
                    if(Diagnostics.Count>128)Diagnostics.RemoveAt(0);
                    Diagnostics.Add(new{tick=now,a=a.ThingID,b=b.ThingID,point=point.ToString("F4"),normal=n.ToString("F4"),massA=ma,massB=mb,relativeSpeed=relative.magnitude,damage,
                        aBefore=VehicleModels.U(before.Position).ToString("F4"),aAfter=VehicleModels.U(next.Position).ToString("F4"),bBefore=VehicleModels.U(bp.Position).ToString("F4"),bAfter=VehicleModels.U(other.Position).ToString("F4"),aVelocity=VehicleModels.U(next.Velocity).ToString("F4"),bVelocity=VehicleModels.U(other.Velocity).ToString("F4"),aHealthBefore=ha,aHealthAfter=Health(a),bHealthBefore=hb,bHealthAfter=Health(b)});
                }
                episode.Last=now;
                if(!a.Spawned||a.Dead)return true;
                before=next;
            }
            return touched;
        }
        // HealthPercent is a dirty cache and can still be zero on a freshly
        // generated full-health VF vehicle. Record native component HP itself.
        internal static float Health(Pawn v)=>VehicleApi.List(VehicleApi.Get(v,"statHandler"),"components").Sum(c=>VehicleApi.Float(c,"Health"));
        static Vector3 Velocity(Pawn v,AerialMotor pose)
        {if(AerialFlight.TryGet(v,out _)||VehicleDriving.ActiveFor(v))return VehicleModels.U(pose.Velocity);return bodies.TryGetValue(v,out var b)?VehicleModels.U(b.Pose.Velocity):Vector3.zero;}
        static bool Separate(Pawn a,ref AerialMotor ap,Pawn b,ref AerialMotor bp,ref Vector3 normal,float wa,float wb,bool flat)
        {
            var a0=ap;var b0=bp;
            var dirs=new List<Vector3>{normal};if(flat){dirs.Add(Vector3.right);dirs.Add(Vector3.left);dirs.Add(Vector3.forward);dirs.Add(Vector3.back);}else{dirs.Add(Vector3.up);dirs.Add(Vector3.down);dirs.Add(Vector3.right);dirs.Add(Vector3.left);dirs.Add(Vector3.forward);dirs.Add(Vector3.back);}
            float best=float.PositiveInfinity;AerialMotor aa=ap,bb=bp;Vector3 chosen=normal;
            foreach(Vector3 dir in dirs)
            {
                float distance=.012f;
                for(int i=0;i<18&&distance<best&&distance<30;i++,distance*=1.8f)
                {
                    var x=a0;var y=b0;x.Position+=VehicleModels.V(dir*(distance*wa));y.Position-=VehicleModels.V(dir*(distance*wb));
                    if(!Fits(a,x)||!Fits(b,y)||VehicleMeshContact.Contact(a,x,b,y,out _))continue;
                    // Avoid pushing either vehicle through scenery while solving
                    // a vehicle pair. Other vehicle pairs are settled next.
                    if(!EnvironmentPath(a,a0,x)||!EnvironmentPath(b,b0,y))continue;
                    best=distance;aa=x;bb=y;chosen=dir;break;
                }
            }
            if(!float.IsFinite(best)){Log.WarningOnce("[FpsBridge] VEHICLE_CONTACT_NO_ESCAPE "+a.ThingID+" / "+b.ThingID,19104378);return false;}
            ap=aa;bp=bb;normal=chosen;return true;
        }
        static bool EnvironmentPath(Pawn p,AerialMotor from,AerialMotor to)
        {
            float length=(to.Position-from.Position).Length;int steps=Math.Max(1,Mathf.CeilToInt(length/.04f));
            for(int i=1;i<=steps;i++){var sample=from;sample.Position=V3.Lerp(from.Position,to.Position,i/(float)steps);if(!AerialCollision.ClearEnvironment(p,sample,p.Map))return false;}return true;
        }
        static bool Fits(Pawn v,AerialMotor m)
        {
            if(!FloorSpace.InWorld(v.Map,m.Position))return false;
            var bounds=AerialFlight.Supports(v)?AircraftFootprint.BoundsAt(v,m):AerialGeometry.BoundsAt(v,m);int rows=FloorSpace.Bands(v.Map)[0].Rows;
            float margin=AerialFlight.Supports(v)?0:.02f;
            if(bounds.min.x<margin||bounds.min.z<margin||bounds.max.x>v.Map.Size.x-margin||bounds.max.z>rows-margin)return false;
            Vector3 raw=AerialFlight.Raw(v,VehicleModels.U(m.Position));Rot4 rot=Rot4.FromAngleFlat(m.Yaw);
            Vector3 native=GenThing.TrueCenter(v.Position,rot,v.def.size,0);
            var cell=new IntVec3(Mathf.RoundToInt(raw.x-(native.x-v.Position.x)),0,Mathf.RoundToInt(raw.z-(native.z-v.Position.z)));
            return GenAdj.OccupiedRect(cell,rot,v.def.size).InBounds(v.Map);
        }
        internal static void Damage(Pawn victim,Thing source,Vector3 point,float amount) =>
            StructuralDamage(victim,source,point,amount,-1f,collisionDamage);

        internal static void AirBlastStress(Pawn victim,Thing source,Vector3 point,float amount,float angle)
        {
            if(airBlastStress!=null)StructuralDamage(victim,source,point,amount,angle,airBlastStress);
        }

        private static void StructuralDamage(Pawn victim,Thing source,Vector3 point,float amount,float angle,DamageDef damage)
        {
            EnsureCeStructuralGuard();
            if(!victim.Spawned||victim.Dead)return;var cell=FloorSpace.Cell(victim.Map,VehicleModels.V(point),victim);var rect=victim.OccupiedRect();
            cell.x=Math.Clamp(cell.x,rect.minX,rect.maxX);cell.z=Math.Clamp(cell.z,rect.minZ,rect.maxZ);
            // VF maps this exact native contact cell to its armour/components.
            VehicleApi.Call(victim,"TakeDamage",new DamageInfo(damage,amount,0,angle,source),new IntVec2(cell.x-victim.Position.x,cell.z-victim.Position.z));
        }
        static void Stamp(Pawn v,AerialMotor m,float recovery)
        {
            if(!bodies.TryGetValue(v,out var body))bodies[v]=body=new Body();body.Pose=m;body.LastTick=Find.TickManager.TicksGame;body.Recovery=recovery;
            if(AerialFlight.TryGet(v,out var flight))
            {if(AerialFlight.Profile(v)?.kind==AerialFlightKind.FixedWing)m=AerialFixedWing.Synchronize(m,flight.Airborne);flight.Motor=m;flight.Navigate=flight.Orbit=flight.Cruise=false;flight.CollisionRecoverySeconds=recovery;flight.CollisionRecoveryVelocity=m.Velocity;}
        }
        static void Write(Pawn v,AerialMotor m,float recovery)
        {
            if(!v.Spawned)return;Stamp(v,m,recovery);
            if(VehicleDriving.ActiveFor(v)&&!AerialFlight.TryGet(v,out _))VehicleDriving.AcceptContact(m);
            Vector3 raw=AerialFlight.Raw(v,VehicleModels.U(m.Position));VehicleApi.AerialPosition(v,raw,m.Yaw,0);
            VehicleApi.Call(VehicleApi.Get(v,"vehiclePather"),"StopDead");
            if(bodies.TryGetValue(v,out var body)){body.NativeCell=v.Position;body.Subcell=true;}
        }
        internal static void Tick()
        {
            int now=Find.TickManager.TicksGame;
            foreach(Map map in Find.Maps)foreach(Pawn p in map.mapPawns.AllPawnsSpawned.ToArray())
            {
                if(p.Dead||!VehicleApi.IsVehicle(p))continue;
                AerialMotor current=VehicleGeometry.Pose(p);
                if(!bodies.TryGetValue(p,out var body)){bodies[p]=new Body{Pose=current,LastTick=now};continue;}
                if(VehicleDriving.ActiveFor(p)){body.LastTick=now;body.Pose=current;body.Recovery=Math.Max(0,body.Recovery-1f/60);continue;}
                AerialMotor next=current;float dt=Math.Max(1,now-body.LastTick)/60f;
                bool aerial=AerialFlight.TryGet(p,out _),recovering=body.Recovery>0;
                body.Recovery=Math.Max(0,body.Recovery-1f/60);
                if(recovering&&!aerial)
                {next=body.Pose;next.Position+=next.Velocity*(1f/60);next.Velocity*=MathF.Exp(-4f/60);if(!EnvironmentPath(p,body.Pose,next)){next.Position=body.Pose.Position;next.Velocity=default;body.Recovery=0;}}
                else if(!aerial)
                {
                    // VF interpolates DrawPos once per rendered frame, while
                    // multiple simulation ticks may run in that frame. A frame
                    // catch-up jump is not a 60+ cells/s impact velocity.
                    V3 direction=current.Position-body.Pose.Position;float length=direction.Length;
                    if(length>.00001f)next.Velocity=direction*(Math.Min(length/Math.Max(.001f,dt),VehicleApi.ManualSpeed(p))/length);
                    else next.Velocity=VehicleApi.Bool(VehicleApi.Get(p,"vehiclePather"),"Moving")?body.Pose.Velocity:default;
                }
                var before=body.Pose;bool hit=Resolve(p,before,ref next,dt);
                if(hit||recovering&&!aerial)Write(p,next,body.Recovery);
                body.Pose=next;body.LastTick=now;
            }
            foreach(var pair in episodes)
                if(!pair.Value.Armed&&now-pair.Value.Last>=3&&pair.Key.Item1.Spawned&&pair.Key.Item2.Spawned&&!VehicleMeshContact.Contact(pair.Key.Item1,VehicleGeometry.Pose(pair.Key.Item1),pair.Key.Item2,VehicleGeometry.Pose(pair.Key.Item2),out _))pair.Value.Armed=true;
            foreach(var p in bodies.Keys.Where(p=>p.Destroyed||!p.Spawned).ToArray())bodies.Remove(p);
            foreach(var key in episodes.Keys.Where(k=>k.Item1.Destroyed||k.Item2.Destroyed||!k.Item1.Spawned||!k.Item2.Spawned).ToArray())episodes.Remove(key);
        }
        public static void DrawDebug(Pawn __instance,DrawPhase phase)
        {
            if(!Debug||phase!=DrawPhase.Draw||!View3D.Active||!VehicleApi.IsVehicle(__instance)||DebugActors.Count>0&&!DebugActors.Contains(__instance))return;
            var parts=new List<VehicleGeometry.Part>();if(!VehicleGeometry.Parts(__instance,VehicleGeometry.Pose(__instance),parts))return;
            Color color=DebugActors.FirstOrDefault()==__instance?new Color(.1f,1,.6f,.8f):new Color(1,.6f,.1f,.8f);
            foreach(var p in parts)
            {var line=VehicleMeshContact.Edges(VehicleMeshContact.Mesh(p.Mesh));for(int i=0;i<line.Length;i+=2)Visuals.ContactEdge(p.World.MultiplyPoint3x4(VehicleMeshContact.U3(line[i])),p.World.MultiplyPoint3x4(VehicleMeshContact.U3(line[i+1])),color);}
        }
    }
}
