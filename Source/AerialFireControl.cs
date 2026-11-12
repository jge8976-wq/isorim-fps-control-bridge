using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialFireControl
    {
        private sealed class Lock {internal Thing Target;internal int Since,Last;}
        private static readonly ConditionalWeakTable<object,Lock> locks=new();
        internal static int Orders,Feedback,Reacquisitions;
        [ThreadStatic] private static bool assigning;
        internal static bool Enemy(Pawn v,Thing t)=>v?.Map!=null&&AerialTargetLock.Valid(t,v.Map)&&t!=v&&t.HostileTo(v)
            &&!t.Position.Fogged(v.Map)&&!(t is Pawn p&&(p.Dead||p.Downed));
        internal static void Acknowledge(Pawn vehicle,V3 point)
        {
            Vector3 raw=AerialFlight.Raw(vehicle,VehicleModels.U(point));raw.y=0;
            FleckMaker.Static(raw,vehicle.Map,FleckDefOf.FeedbackGoto,1.8f);Feedback++;
        }
        internal static bool Attack(Pawn v,Thing target)
        {
            if(v==null||v.Faction!=Faction.OfPlayer||!AerialFlight.TryGet(v,out var r)||!r.Airborne||!Enemy(v,target))return false;
            if(r.Automatic||r.Landing){Messages.Message(L.T("请等待自动起降完成。","Wait for automatic takeoff or landing to finish."),MessageTypeDefOf.RejectInput,false);return false;}
            if(VehicleApi.Turrets(v).Count==0){Messages.Message(L.T("该飞行器没有机载武器。","This aircraft has no weapons."),MessageTypeDefOf.RejectInput,false);return false;}
            r.CombatGunKey=null;r.CombatOrbit=false;
            r.CombatEnabled=true;r.CombatTarget=target;r.CombatAttackRun=true;r.CombatRecoverUntil=0;
            r.Navigate=r.Orbit=false;r.Cruise=true;r.Landing=false;
            foreach(var gun in VehicleApi.Turrets(v))Assign(gun,target);
            r.Status=L.T("已接受整机攻击命令","Aircraft attack order accepted");
            Acknowledge(v,VehicleModels.V(AerialTargetLock.Point(target)));Orders++;return true;
        }
        internal static void Assign(object gun,Thing target)
        {
            var old=(LocalTargetInfo)VehicleApi.Get(gun,"targetInfo");
            if(old.Thing==target&&old.IsValid)return;
            bool previous=assigning;
            assigning=true;
            try { VehicleApi.Call(gun,"SetTarget",target==null?LocalTargetInfo.Invalid:new LocalTargetInfo(target)); }
            finally { assigning=previous; }
            VehicleApi.Call(gun,"StartTicking");
        }
        public static void NativeTargetAssigned(object __instance)
        {
            if(assigning||!AerialWeaponSolution.Owns(__instance,out var vehicle)||VehicleControl.Owns(vehicle))return;
            if(Find.Selector?.SingleSelectedThing!=vehicle)return;
            var target=(LocalTargetInfo)VehicleApi.Get(__instance,"targetInfo");
            if(!target.HasThing||!Enemy(vehicle,target.Thing))return;
            if(!AerialFlight.TryGet(vehicle,out var flight)||flight.Automatic||flight.Landing)return;
            flight.CombatOrbit=false;
            flight.CombatEnabled=true;
            flight.CombatAttackRun=true;
            flight.CombatTarget=target.Thing;
            flight.CombatGunKey=VehicleApi.TurretKey(__instance);
            flight.CombatRecoverUntil=0;
            flight.Navigate=flight.Orbit=false;
            flight.Cruise=true;
            Acknowledge(vehicle,VehicleModels.V(AerialTargetLock.Point(target.Thing)));
        }
        internal static void Stop(Pawn v,AerialFlightRecord r)
        {
            r.CombatOrbit=false;
            r.CombatEnabled=r.CombatAttackRun=r.CombatAim=false;r.CombatTarget=null;r.CombatGunKey=null;
            foreach(var gun in VehicleApi.Turrets(v)){Assign(gun,null);VehicleApi.Set(gun,"AutoTarget",false);}
            VehicleApi.ClearQueue(v);r.Navigate=r.Cruise=r.Orbit=false;AerialOrders.Hold(v,r);
            r.Status=L.T("停止交战，保持航行","Combat stopped; holding flight");
        }
        internal static IEnumerable<Gizmo> Commands(Pawn v)
        {
            if(!AerialFlight.Supports(v)||!AerialFlight.TryGet(v,out var r)||!r.Airborne)yield break;
            yield return new Command_Toggle
            {
                defaultLabel=L.T("半自动交战","Semi-auto combat"),
                defaultDesc=L.T("由各武器按射角、射程和装填状态攻击可见敌人，目标失效后重新索敌。固定挂载仍须让机头对准目标。","Weapons engage visible hostiles within their firing arcs and range, and reacquire after target loss. Fixed mounts require the aircraft to face the target."),
                icon=TexCommand.FireAtWill,
                isActive=()=>r.CombatEnabled,
                toggleAction=()=>
                {
                    if(r.CombatEnabled)Stop(v,r);
                    else
                    {
                        r.CombatEnabled=true;
                        r.Status=L.T("半自动交战已开启","Semi-auto combat enabled");
                        Acknowledge(v,r.Motor.Position);
                    }
                }
            };
            yield return new Command_Toggle
            {
                defaultLabel=L.T("盘旋自动攻击","Orbit auto-attack"),
                defaultDesc=L.T("保持当前盘旋点或悬停点，自动锁定附近敌人。敌人周围七格内有我方或盟友人员时避用爆炸类武器。","Hold the current orbit or hover point and engage nearby hostiles. Explosive weapons wait when friendly personnel are within seven tiles of the target."),
                icon=TexCommand.FireAtWill,
                isActive=()=>r.CombatOrbit,
                toggleAction=()=>AerialAutoAttack.TogglePatrol(v,r)
            };
            yield return new Command_Target
            {
                defaultLabel=L.T("整机攻击","Aircraft attack"),
                defaultDesc=L.T("指定可见敌人。各武器共用优先目标；固定翼会转入攻击航线并在低空拉起。","Assign a visible hostile to the aircraft. Weapons share this priority; fixed wings turn into an attack run and pull out at low altitude."),
                icon=TexCommand.Attack,
                targetingParams=new TargetingParameters
                {
                    canTargetLocations=false,
                    canTargetPawns=true,
                    canTargetBuildings=true,
                    validator=t=>t.HasThing&&Enemy(v,t.Thing)
                },
                action=t=>Attack(v,t.Thing)
            };
            yield return new Command_Action
            {
                defaultLabel=L.T("停止交战","Cease combat"),
                defaultDesc=L.T("清除整机交战目标并保持飞行。","Clear aircraft combat targets and hold flight."),
                icon=TexCommand.CannotShoot,
                action=()=>Stop(v,r)
            };
        }
        internal static string Status(Pawn v)=>AerialFlight.TryGet(v,out var r)&&r.CombatEnabled
            ?r.CombatTarget!=null?(r.CombatOrbit?L.T("盘旋自动攻击：","Orbit auto-attack: "):L.T("半自动交战：","Semi-auto: "))+r.CombatTarget.LabelShort+(string.IsNullOrEmpty(r.CombatStatus)?"":" · "+r.CombatStatus):L.T("半自动交战：搜索可见敌人","Semi-auto: scanning visible hostiles"):null;
        private static string Waiting(string reason)=>reason switch
        {
            "friendly-near-target"=>L.T("目标七格内有友军，停用爆炸武器","Friendlies within seven tiles; explosive weapon held"),
            "range"=>L.T("目标超出射程","Target outside range"),
            "alignment" or "pitch" or "traverse"=>L.T("等待射角","Aligning to fire"),
            "lock"=>L.T("导弹正在锁定","Acquiring missile lock"),
            "drop-window"=>L.T("等待投弹窗口","Waiting for bomb release window"),
            "obstacle"=>L.T("射界被遮挡","Line of fire blocked"),
            _=>L.T("暂无可用射击解算","No firing solution")
        };
        internal static bool LockReady(object gun,Thing target)
        {
            if(AerialArmament.Data(gun)?.lockableMissile!=true)return true;
            if(!AerialWeaponSolution.Owns(gun,out var v)||!Enemy(v,target))return false;
            var state=locks.GetOrCreateValue(gun);int now=Find.TickManager.TicksGame;
            return state.Target==target&&now-state.Last<=2&&now-state.Since>=Math.Max(1,(int)(AerialArmament.Data(gun).lockSeconds*60));
        }
        internal static Thing Captured(object gun)
        {var t=(LocalTargetInfo)VehicleApi.Get(gun,"targetInfo");return LockReady(gun,t.Thing)?t.Thing:null;}
        private static void TrackLock(Pawn v,object gun,LocalTargetInfo target)
        {
            var data=AerialArmament.Data(gun);if(data?.lockableMissile!=true)return;
            var state=locks.GetOrCreateValue(gun);int now=Find.TickManager.TicksGame;
            bool valid=Enemy(v,target.Thing)&&AerialWeaponSolution.Solve(v,gun,target,out var d,out _)
                &&(AerialAutoAttack.Automatic(v)?AerialAutoAttack.Facing(v,target):Vector3.Angle(VehicleModels.U(AerialWeaponSolution.Bore(v,gun)),VehicleModels.U(d))<=data.seekerDegrees);
            if(!valid){state.Target=null;state.Since=now;return;}
            if(state.Target!=target.Thing||now-state.Last>2){state.Target=target.Thing;state.Since=now;}
            state.Last=now;
        }
        private static Thing FindEnemy(Pawn v,List<object> guns)
        {
            float range=guns.Count==0?0:guns.Max(AerialArmament.AiRange);
            float searchRadius=range+AerialGeometry.LocalBounds(v).extents.magnitude+2f;
            V3 root=AerialFlight.Store.Flights[v].Motor.Position;
            return v.Map.listerThings.AllThings.Where(t=>(t is Pawn||t is Building)&&Enemy(v,t))
                .Where(t=>AerialAutoAttack.PlanarDistance(VehicleModels.V(AerialTargetLock.Point(t)),root)<=searchRadius&&AerialAutoAttack.InsidePatrol(v,AerialFlight.Store.Flights[v],t))
                .OrderBy(t=>(VehicleModels.V(AerialTargetLock.Point(t))-root).Length)
                .FirstOrDefault(t=>guns.Any(g=>!AerialAutoAttack.UnsafeExplosive(v,g,new LocalTargetInfo(t))&&AerialWeaponSolution.Solve(v,g,new LocalTargetInfo(t),out _,out _,false)));
        }
        internal static void Tick()
        {
            if(AerialFlight.Store==null)return;
            int now=Find.TickManager.TicksGame;
            foreach(var pair in AerialFlight.Store.Flights.ToArray())
            {
                Pawn v=pair.Key;var r=pair.Value;
                if(v==null||!v.Spawned||v.Dead||!AerialFlight.Supports(v)||v.Faction!=Faction.OfPlayer)continue;
                r.CombatAim=false;
                if(VehicleControl.Owns(v))continue;
                if(!r.Airborne){if(r.CombatEnabled||r.CombatAttackRun)Stop(v,r);continue;}
                var guns=VehicleApi.Turrets(v);
                if(r.CombatEnabled&&(r.CombatTarget==null||!Enemy(v,r.CombatTarget)||!AerialAutoAttack.InsidePatrol(v,r,r.CombatTarget)
                    ||r.CombatOrbit&&guns.All(g=>AerialAutoAttack.UnsafeExplosive(v,g,new LocalTargetInfo(r.CombatTarget)))))
                {
                    bool lost=r.CombatTarget!=null;r.CombatTarget=null;
                    if(lost||now%30==v.thingIDNumber%30){r.CombatTarget=FindEnemy(v,guns);if(r.CombatTarget!=null)Reacquisitions++;}
                }
                foreach(var gun in guns)
                {
                    AerialAccuracy.Apply(gun);
                    var target=(LocalTargetInfo)VehicleApi.Get(gun,"targetInfo");
                    if(target.HasThing&&(!AerialTargetLock.Valid(target.Thing,v.Map)||r.CombatEnabled&&!Enemy(v,target.Thing))){Assign(gun,null);target=LocalTargetInfo.Invalid;}
                    if(r.CombatEnabled&&!target.IsValid&&r.CombatTarget!=null){Assign(gun,r.CombatTarget);target=new LocalTargetInfo(r.CombatTarget);}
                    if(r.CombatOrbit&&target.Thing!=r.CombatTarget)
                    {
                        Assign(gun,r.CombatTarget);
                        target=r.CombatTarget==null?LocalTargetInfo.Invalid:new LocalTargetInfo(r.CombatTarget);
                    }
                    if(target.IsValid)AerialWeaponSolution.Lay(v,gun,target);
                    TrackLock(v,gun,target);
                    if(r.CombatEnabled&&!VehicleApi.Bool(gun,"IsTargetable")&&target.IsValid&&!VehicleApi.Bool(gun,"queuedToFire")&&VehicleApi.Bool(gun,"ReadyToFire")&&AerialWeaponSolution.CanFire(v,gun,target,out _))
                    {
                        // Native TurretTargeterTick owns warmup even for static mounts.
                        if(VehicleApi.Bool(gun,"TargetLocked")&&VehicleApi.Int(gun,"PrefireTickCount")<=0)VehicleApi.Call(gun,"PushTurretToQueue");
                    }
                    if(r.CombatEnabled&&VehicleApi.Int(gun,"shellCount")==0&&VehicleApi.Int(gun,"ReloadTicks")<=0&&now%60==v.thingIDNumber%60)VehicleApi.Call(gun,"ReloadIfEmpty");
                }
                if(r.CombatEnabled&&!r.CombatOrbit&&(r.CombatAttackRun||guns.Any(g=>AerialWeaponSolution.Fixed(v,g)))&&Enemy(v,r.CombatTarget)&&!r.Automatic&&!r.Landing&&!r.EngineStopped)
                    Guide(v,r,guns,now);
                else if(r.CombatAttackRun&&!Enemy(v,r.CombatTarget))
                {r.CombatAttackRun=false;r.Cruise=r.Navigate=r.Orbit=false;AerialOrders.Hold(v,r);}
                if(r.CombatEnabled&&now%15==v.thingIDNumber%15)
                {
                    r.CombatStatus=null;
                    foreach(var gun in guns)
                    {
                        var target=(LocalTargetInfo)VehicleApi.Get(gun,"targetInfo");if(!target.IsValid)continue;
                        if(AerialWeaponSolution.CanFire(v,gun,target,out string reason))
                        {r.CombatStatus=VehicleApi.Int(gun,"ReloadTicks")>0?L.T("装填中","Reloading"):L.T("按原生射速交战","Native fire cycle");break;}
                        r.CombatStatus??=Waiting(reason);
                    }
                }
            }
        }
        internal static void Guide(Pawn v,AerialFlightRecord r,List<object> guns,int now)
        {
            if(!string.IsNullOrEmpty(r.CombatGunKey))
            {
                var selected=guns.FirstOrDefault(g=>VehicleApi.TurretKey(g)==r.CombatGunKey);
                if(selected!=null)guns=new List<object>{selected};
            }
            V3 delta=VehicleModels.V(AerialTargetLock.Point(r.CombatTarget))-r.Motor.Position;
            float horizontal=MathF.Sqrt(delta.X*delta.X+delta.Z*delta.Z),speed=r.Motor.Velocity.Length;
            if(AerialFlight.Profile(v).kind!=AerialFlightKind.FixedWing)
            {
                // Hover at a stand-off point. Nose-mounted weapons still require actual pitch/yaw.
                float reach = guns.Where(g => !AerialArmament.Bomb(g))
                    .Select(g => AerialArmament.AiRange(g) * .85f)
                    .Select(range => MathF.Sqrt(Math.Max(0f, range * range - delta.Y * delta.Y)))
                    .Where(range => range > 0f).DefaultIfEmpty(15f).Min();
                reach = Math.Max(4f, reach);
                float distance=guns.Any(g=>AerialWeaponSolution.Fixed(v,g)&&!AerialArmament.Bomb(g))?reach:Math.Min(reach,Math.Max(4,horizontal));
                V3 aim=VehicleModels.V(AerialTargetLock.Point(r.CombatTarget));
                r.Navigate=true;r.Orbit=false;r.NavHeight=r.Motor.Position.Y;
                r.NavTarget=aim-new V3(delta.X,0,delta.Z)*(distance/Math.Max(.01f,horizontal));
                r.CombatAim=true;r.CombatYaw=Mathf.Atan2(delta.X,delta.Z)*Mathf.Rad2Deg;r.CombatPitch=0;
                foreach(var gun in guns)if(AerialWeaponSolution.Fixed(v,gun)&&!AerialArmament.Bomb(gun)&&AerialWeaponSolution.Solve(v,gun,new LocalTargetInfo(r.CombatTarget),out var d,out _,false))
                {r.CombatPitch=Math.Clamp(-Mathf.Atan2(d.Y,new Vector2(d.X,d.Z).magnitude)*Mathf.Rad2Deg,-30,35);break;}
                return;
            }
            float altitude=AerialCollision.Altitude(v,AircraftFootprint.BoundsAt(v,r.Motor));
            r.CombatAim=true;
            float safeHeight=Math.Max(5,speed*.35f),cruiseHeight=Math.Clamp(speed*.65f,12,22);
            if(r.CombatRecoverUntil>0)
            {
                V3 leg=r.CombatEgress-r.Motor.Position;float distance=new Vector2(leg.X,leg.Z).magnitude;
                if((distance<12||now>=r.CombatRecoverUntil)&&altitude>=safeHeight+2)r.CombatRecoverUntil=0;
                else
                {
                    r.CombatYaw=Mathf.Atan2(leg.X,leg.Z)*Mathf.Rad2Deg;
                    r.CombatPitch=Math.Clamp(-Mathf.Atan2(cruiseHeight-altitude,Math.Max(12,distance))*Mathf.Rad2Deg,-20,12);return;
                }
            }
            if(altitude<safeHeight||horizontal<Math.Max(8,speed*.45f))
            {
                // A finite exit leg lets the aircraft line up another pass. An
                // orbit inside the pull-out radius would re-trigger forever and climb to the ceiling.
                float distance=Math.Max(45,speed*2.8f);V3 forward=HitMath.Direction(r.Motor.Yaw,0);
                r.CombatEgress=VehicleModels.V(AerialTargetLock.Point(r.CombatTarget))-forward*distance;
                r.CombatEgress.X=Math.Clamp(r.CombatEgress.X,15,v.Map.Size.x-15);
                r.CombatEgress.Z=Math.Clamp(r.CombatEgress.Z,15,FloorSpace.Bands(v.Map)[0].Rows-15);
                r.CombatRecoverUntil=now+300;r.CombatYaw=Mathf.Atan2(r.CombatEgress.X-r.Motor.Position.X,r.CombatEgress.Z-r.Motor.Position.Z)*Mathf.Rad2Deg;
                r.CombatPitch=-20;return;
            }
            r.CombatYaw=Mathf.Atan2(delta.X,delta.Z)*Mathf.Rad2Deg;
            float down = -Mathf.Atan2(delta.Y,Math.Max(1,horizontal))*Mathf.Rad2Deg;
            r.CombatPitch = Math.Clamp(down, -30f, 25f);
            if(Math.Abs(Mathf.DeltaAngle(r.Motor.Yaw,r.CombatYaw))>25){r.CombatPitch=0;return;}
            foreach(var gun in guns)
                if(AerialWeaponSolution.Fixed(v,gun)&&!AerialArmament.Bomb(gun)&&AerialWeaponSolution.Solve(v,gun,new LocalTargetInfo(r.CombatTarget),out var d,out _,false))
                {r.CombatPitch=Math.Clamp(-Mathf.Atan2(d.Y,new Vector2(d.X,d.Z).magnitude)*Mathf.Rad2Deg,-35,35);return;}
        }
    }
}
