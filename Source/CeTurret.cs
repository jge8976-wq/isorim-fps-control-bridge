using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HSK.FpsBridge.Interop;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace HSK.FpsBridge
{
    internal sealed partial class CeTurret : IArtilleryTurret,IRemoteManualTurret,ISelectableFireMode
    {
        private readonly Building_Turret gun;
        private Pawn by;
        private int nextTick,queued;
        private TurretFireMode mode;
        private static CeTurret operated;
        private static MethodInfo bunkerOperator;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Building_Turret,CeTurret> adapters=new System.Runtime.CompilerServices.ConditionalWeakTable<Building_Turret,CeTurret>();
        internal CeTurret(Building_Turret gun){this.gun=gun;mode=gun.AttackVerb.verbProps.burstShotCount>1?TurretFireMode.Auto:TurretFireMode.Single;}
        internal static void Install(Harmony harmony,Type ceType)
        {
            ManualTurrets.Register(t=>t is Building_Turret turret&&ceType.IsInstanceOfType(t)?adapters.GetValue(turret,g=>new CeTurret(g)):null);
            harmony.Patch(AccessTools.Method(ceType,"TryStartShootSomething"),prefix:new HarmonyMethod(typeof(CeTurret),nameof(AllowAutoTarget)));
            var orderReload = AccessTools.Method(ceType,"TryOrderReload",new[]{typeof(bool)});
            if(orderReload!=null) harmony.Patch(orderReload,prefix:new HarmonyMethod(typeof(CeTurret),nameof(AllowNativeReload)));
            harmony.Patch(AccessTools.Method(typeof(TurretTop),"TurretTopTick"),prefix:new HarmonyMethod(typeof(CeTurret),nameof(AllowTopTick)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(CompMannable),"MannedNow"),postfix:new HarmonyMethod(typeof(CeTurret),nameof(Manned)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(CompMannable),"ManningPawn"),postfix:new HarmonyMethod(typeof(CeTurret),nameof(ManningPawn)));
            Type bunker=AccessTools.TypeByName("FortifiedCE.Building_TurretCapacityCE");
            bunkerOperator=bunker==null?null:AccessTools.PropertyGetter(bunker,"PawnInside");
            if(bunkerOperator?.ReturnType==typeof(Pawn))
                harmony.Patch(bunkerOperator,postfix:new HarmonyMethod(typeof(CeCompat),nameof(CeCompat.BunkerShotOperator)));
        }
        private object Get(string name)=>(AccessTools.Property(gun.GetType(),name)?.GetValue(gun))??AccessTools.Field(gun.GetType(),name)?.GetValue(gun);
        private void Set(string name,object value)=>AccessTools.Field(gun.GetType(),name).SetValue(gun,value);
        internal static bool AllowAutoTarget(Thing __instance)=>operated?.gun!=__instance;
        internal static bool AllowTopTick(Building_Turret ___parentTurret)=>operated?.gun!=___parentTurret;
        internal static void Manned(CompMannable __instance,ref bool __result){if(operated?.gun==__instance.parent&&operated.by!=null)__result=true;}
        internal static void ManningPawn(CompMannable __instance,ref Pawn __result){if(operated?.gun==__instance.parent&&operated.by!=null)__result=operated.by;}
        public Thing Thing=>gun;
        public TurretAimMode AimMode=>CeCompat.ProjectileFor(gun.AttackVerb)?.projectile.flyOverhead==true?TurretAimMode.Artillery:TurretAimMode.Direct;
        public float EyeHeight=>1.6f;
        public float TurnRate=>Math.Max(gun.def.size.x,gun.def.size.z)>=2?180f:360f;
        public float Range=>gun.AttackVerb.EffectiveRange;
        public float MinRange=>gun.AttackVerb.verbProps.minRange;
        private TurretTop Top=>(TurretTop)Get("top");
        private bool Bunker=>bunkerOperator?.DeclaringType.IsInstanceOfType(gun)==true;
        public float Rotation=>Top.CurRotation;
        public void Aim(float value)
        {
            Top.CurRotation=value;
            // CE writes this angle back into TurretTop during DrawAt for non-snap turrets.
            var nativeAngle=AccessTools.Field(gun.GetType(),"NonSnapTurretRot");
            nativeAngle?.SetValue(gun,value);
        }
        public bool Loaded=>CeCompat.TurretLoaded(gun.AttackVerb)??true;
        public bool Reloading=>reloadUntil>=0||(bool)Get("isReloading");
        public string Refusal(Pawn pawn)=>gun.Faction!=Faction.OfPlayer?L.T("不是己方炮台","Not your turret")
            :(gun.TryGetComp<CompMannable>()?.ManningPawn??(Bunker?bunkerOperator.Invoke(gun,null) as Pawn:null)) is Pawn crew&&crew!=pawn?L.T("炮台已有操作员","Turret already has an operator")
            :operated!=null&&operated.gun==gun&&operated!=this?L.T("炮台正在操纵中","Turret already controlled"):null;
        public string RemoteRefusal=>Bunker||gun.TryGetComp<CompMannable>()!=null||AimMode==TurretAimMode.Artillery?L.T("这座炮台需要操作员","This turret needs an operator"):Refusal(null);
        public string Status=>!((bool)Get("Active"))?L.T("未运作","Offline"):Reloading?L.T("CE 装填中","CE reloading")
            :!Loaded?L.T("CE 无弹药","CE empty"):mode.ToString()+" · "+AmmoLabel(by);
        public float? Ammo=>CeCompat.TurretAmmo(gun.AttackVerb);
        public string AmmoLabel(Pawn pawn)=>CeCompat.TurretAmmoLabel(gun.AttackVerb);
        public float BlastRadius=>CeCompat.ProjectileFor(gun.AttackVerb)?.projectile.explosionRadius??0f;
        public float FlightTicks(IntVec3 target)=>CeCompat.TurretFlightTicks(gun,gun.AttackVerb,target);
        public float Spread(IntVec3 target,Pawn pawn)=>0f;
        public IReadOnlyList<TurretFireMode> FireModes=>gun.AttackVerb.verbProps.burstShotCount>1?new[]{TurretFireMode.Single,TurretFireMode.Burst3,TurretFireMode.Auto}:new[]{TurretFireMode.Single};
        public TurretFireMode FireMode{get=>mode;set{mode=value;queued=0;}}
        public bool OwnsVerb(Verb verb)=>verb==gun.AttackVerb;
        public void Begin(Pawn pawn){by=pawn;operated=this;nextReloadAttempt=0;gun.AttackVerb.Reset();Set("currentTargetInt",LocalTargetInfo.Invalid);Set("burstWarmupTicksLeft",0);queued=0;}
        public void End(){CancelReload();gun.AttackVerb.Reset();int left=Math.Max(0,nextTick-Find.TickManager.TicksGame);Set("burstCooldownTicksLeft",Math.Max(left,(int)Get("burstCooldownTicksLeft")));queued=0;by=null;if(operated==this)operated=null;}
        public void Trigger(LocalTargetInfo target,bool held,bool pressed)
        {
            if (!Find.TickManager.Paused) StepReload();
            if(Find.TickManager.Paused||Reloading||!((bool)Get("Active"))||(bool)Get("holdFire")){queued=0;return;}
            if(!Loaded){AutoReload(pressed);queued=0;return;}
            if(pressed)queued=mode==TurretFireMode.Burst3?3:1;
            bool wants=mode==TurretFireMode.Auto?(held||pressed):queued>0;
            int now=Find.TickManager.TicksGame;if(!wants||now<nextTick)return;
            if(!CeCompat.TurretShot(gun,by,gun.AttackVerb,target)){queued=0;return;}
            gun.AttackVerb.verbProps.soundCast?.PlayOneShot(new TargetInfo(gun.Position,gun.Map));
            gun.AttackVerb.verbProps.soundCastTail?.PlayOneShotOnCamera(gun.Map);
            queued=Math.Max(0,queued-1);
            int interval=gun.AttackVerb.verbProps.burstShotCount>1?gun.AttackVerb.TicksBetweenBurstShots:gun.AttackVerb.verbProps.AdjustedCooldownTicks(gun.AttackVerb,by);
            nextTick=now+Math.Max(1,interval);
        }
    }
    internal static partial class CeCompat
    {
        internal static float? TurretAmmo(Verb verb)
        {object ammo=Ready&&launchType.IsInstanceOfType(verb)?compAmmo.GetValue(verb):null;return ammo!=null&&(int)magSize.GetValue(ammo)>0?(float)(int)curRounds.GetValue(ammo)/(int)magSize.GetValue(ammo):null;}
        internal static string TurretAmmoLabel(Verb verb)
        {object ammo=Ready&&launchType.IsInstanceOfType(verb)?compAmmo.GetValue(verb):null;return ammo==null?"CE":curRounds.GetValue(ammo)+" / "+magSize.GetValue(ammo)+" · "+((currentAmmo.GetValue(ammo) as Def)?.LabelCap.ToString()??"CE");}
        internal static object TurretAmmoUser(Verb verb) => Owns(verb) ? compAmmo.GetValue(verb) : null;
    }
}
