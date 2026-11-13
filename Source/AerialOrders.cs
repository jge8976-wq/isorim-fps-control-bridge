using System;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialOrders
    {
        private static int frame=-1;
        private static PropertyInfo nativeTargeter;
        private static bool NativeTargeting => nativeTargeter?.GetValue(null) is object targeter&&VehicleApi.Bool(targeter,"IsTargeting");
        private static AerialDoubleTap landTap;
        private static readonly ControlAction[] ownedActions={ControlAction.Forward,ControlAction.Backward,ControlAction.Left,ControlAction.Right,ControlAction.AirAscend,ControlAction.AirDescend,ControlAction.AirLand,ControlAction.AirOrbit};
        internal static Pawn Selected => Bridge.HasMapInputContext&&!Bridge.Active&&!VehicleControl.On&&!FirstPerson.On&&View3D.Active
            &&Find.Selector?.SingleSelectedThing is Pawn p&&p.Faction==Faction.OfPlayer&&p.Spawned&&AerialFlight.Supports(p)
            &&(VehicleApi.Drafted(p)||AerialFlight.Airborne(p))?p:null;
        internal static bool OwnKeys => Selected!=null;
        internal static bool Consumes(KeyBindingDef def)
        {
            if(!OwnKeys||!Allowed||def==null||!KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(def,out var keys))return false;
            foreach(var action in ownedActions)
            {
                if(AerialInputRules.Direction(action)&&AerialFlight.TryGet(Selected,out var r)&&!AerialInputRules.GroundMovement(r.Airborne,r.Automatic))continue;
                if(ControlKeys.Matches(action,keys.keyBindingA)||ControlKeys.Matches(action,keys.keyBindingB))return true;
            }
            return false;
        }
        internal static bool Allowed => Bridge.HasMapInputContext&&!ControlKeys.Editing&&Application.isFocused
            &&!LongEventHandler.ShouldWaitForEvent&&!WorldRendererUtility.WorldSelected&&!View3D.ControlsLocked&&GUIUtility.keyboardControl==0
            &&Find.WindowStack!=null&&!Find.WindowStack.WindowsPreventCameraMotion&&!Find.WindowStack.AnyWindowAbsorbingAllInput
            &&!Find.WindowStack.AnySearchWidgetFocused&&!Bridge.MainTabBlocks&&Find.Targeter?.IsTargeting!=true&&!NativeTargeting;
        internal static void Install(Harmony h)
        {
            nativeTargeter=AccessTools.Property(AccessTools.TypeByName("Vehicles.TurretTargeter"),"Instance");
            h.Patch(AccessTools.Method(typeof(UIRoot_Play),"UIRootOnGUI"),prefix:new HarmonyMethod(typeof(AerialOrders),nameof(GUIFrame)){priority=Priority.First+200});
            h.Patch(AccessTools.Method(typeof(Selector),"HandleMapClicks"),prefix:new HarmonyMethod(typeof(AerialOrders),nameof(MapClick)){priority=Priority.First+200});
        }
        internal static V3 Point(Pawn v,bool centre)
        {
            Vector3 raw=Api.ScreenToMap(centre?new Vector2(Screen.width*.5f,Screen.height*.5f):new Vector2(Input.mousePosition.x,Input.mousePosition.y));
            if(!float.IsFinite(raw.x)||!float.IsFinite(raw.z))return new V3(float.NaN,0,0);
            FloorBand band=FloorSpace.Band(v.Map,raw.ToIntVec3());return new V3(raw.x,band.Height,raw.z-band.Shift);
        }
        internal static bool Navigate(Pawn v,V3 target,bool orbit)
        {
            if(v==null||v.Faction!=Faction.OfPlayer||!AerialFlight.Supports(v)||!target.Finite)return false;
            if(target.X<2||target.Z<2||target.X>v.Map.Size.x-2||target.Z>FloorSpace.Bands(v.Map)[0].Rows-2)
            {Messages.Message(L.T("请指向地图内的地面。","Point at ground inside the map."),MessageTypeDefOf.RejectInput,false);return false;}
            if(!AerialFlight.TryGet(v,out var r)){AerialFlight.Begin(v);if(!AerialFlight.TryGet(v,out r))return false;}
            if(!r.Airborne){Messages.Message(L.T("请先起飞，再下达空中航行指令。","Take off before giving flight orders."),MessageTypeDefOf.RejectInput,false);return false;}
            if(r.Automatic){Messages.Message(L.T("请等待自动起降完成。","Wait for automatic takeoff or landing to finish."),MessageTypeDefOf.RejectInput,false);return false;}
            bool wing=AerialFlight.Profile(v).kind==AerialFlightKind.FixedWing;
            float speed=AerialFlight.Profile(v).MaximumSpeed(v)*.65f, span=Math.Max(v.def.size.x,v.def.size.z);
            float radius=wing?AerialGuidance.AiRadius(speed,span):AerialGuidance.Radius(speed,span);
            if(wing&&(target.X-radius<4||target.Z-radius<4||target.X+radius>v.Map.Size.x-4||target.Z+radius>FloorSpace.Bands(v.Map)[0].Rows-4))
            {Messages.Message(L.T("盘旋点靠近地图边缘，请选更开阔的位置。","Choose an orbit centre farther from map edges."),MessageTypeDefOf.RejectInput,false);return false;}
            r.Navigate=true;r.Orbit=wing&&orbit;r.Cruise=false;r.NavTarget=target;r.OrbitRadius=radius;r.NavHeight=r.Motor.Position.Y;r.Landing=false;
            r.Status=r.Orbit?L.T("绕点盘旋","Orbiting point"):L.T("飞向指定位置／悬停","Fly to point / hover");
            r.CombatAttackRun=r.CombatAim=false;AerialFireControl.Acknowledge(v,target);
            return true;
        }
        internal static void Hold(Pawn v,AerialFlightRecord r)
        {
            if(r.Landing||r.Automatic||r.Navigate||r.Cruise||!r.Airborne)return;
            if(AerialFlight.Profile(v).kind==AerialFlightKind.FixedWing)
            {
                float speed=AerialFlight.Profile(v).MaximumSpeed(v)*.65f, span=Math.Max(v.def.size.x,v.def.size.z);
                float radius=AerialGuidance.AiRadius(speed,span);
                float a=r.Motor.Yaw*Mathf.Deg2Rad;
                r.NavTarget=r.Motor.Position+new V3(Mathf.Cos(a)*radius,0,-Mathf.Sin(a)*radius);r.Orbit=true;r.OrbitRadius=radius;
                float margin=Math.Max(v.def.size.x,v.def.size.z)*.6f+1f;
                float maxRadius=Math.Min(v.Map.Size.x,FloorSpace.Bands(v.Map)[0].Rows)*.5f-margin;
                r.OrbitRadius=Math.Max(1f,Math.Min(radius,maxRadius));
                r.NavTarget.X=Mathf.Clamp(r.NavTarget.X,r.OrbitRadius+margin,v.Map.Size.x-r.OrbitRadius-margin);
                r.NavTarget.Z=Mathf.Clamp(r.NavTarget.Z,r.OrbitRadius+margin,FloorSpace.Bands(v.Map)[0].Rows-r.OrbitRadius-margin);
            }
            else r.NavTarget=r.Motor.Position;
            r.NavHeight=r.Motor.Position.Y;r.Navigate=true;
        }
        public static void GUIFrame()
        {
            Pawn v=VehicleControl.On?VehicleControl.Vehicle:Selected;
            if(v==null||!AerialFlight.Supports(v)||!Allowed)return;
            Event e=Event.current;if(e==null||e.type!=EventType.KeyDown||e.keyCode==KeyCode.None)return;
            if(AerialFlight.Profile(v).kind==AerialFlightKind.FixedWing&&(e.keyCode==KeyCode.PageUp||e.keyCode==KeyCode.PageDown))
            {
                AerialThrottle.Set(v,AerialThrottle.Get(v)+(e.keyCode==KeyCode.PageUp?.05f:-.05f));
                e.Use();
            }
            else if(ControlKeys.Matches(ControlAction.AirOrbit,e.keyCode))
            {
                if(VehicleControl.On){AerialTargetLock.Toggle();e.Use();return;}
                if(AerialFlight.TryGet(v,out var r)&&r.Orbit){r.Navigate=r.Orbit=false;r.Cruise=true;r.Status=L.T("盘旋取消，保持航向巡航","Orbit cancelled: cruise on current heading");}
                else Navigate(v,Point(v,VehicleControl.On),true);
                e.Use();
            }
            else if(!VehicleControl.On&&(ControlKeys.Matches(ControlAction.AirAscend,e.keyCode)||ControlKeys.Matches(ControlAction.AirDescend,e.keyCode)||ControlKeys.Matches(ControlAction.AirLand,e.keyCode)))e.Use();
        }
        public static bool MapClick()
        {
            Pawn v=Selected;Event e=Event.current;
            if(v==null||!Allowed||!AerialFlight.Airborne(v)||e==null||e.type!=EventType.MouseDown||e.button!=1)return true;
            if(e.mousePosition.y>UI.screenHeight-280||e.mousePosition.x>UI.screenWidth-300)return true;
            V3 point=Point(v,false);IntVec3 cell=FloorSpace.Cell(v.Map,point,v);
            if(point.Finite&&cell.InBounds(v.Map))
                foreach(Thing t in cell.GetThingList(v.Map))if(AerialFireControl.Enemy(v,t))
                {AerialFireControl.Attack(v,t);e.Use();return false;}
            Navigate(v,point,true);e.Use();return false;
        }
        internal static void Frame()
        {
            if(frame==Time.frameCount)return;frame=Time.frameCount;
            Pawn v=Selected;if(v==null||!Allowed||Find.TickManager.Paused)return;
            bool wing=AerialFlight.Profile(v).kind==AerialFlightKind.FixedWing;
            int climb=(ControlKeys.Held(ControlAction.AirAscend)?1:0)-(!wing&&ControlKeys.Held(ControlAction.AirDescend)?1:0);
            AerialFlight.TryGet(v,out var r);
            AerialThrottle.ReadInput(v,r,Mathf.Clamp(Time.unscaledDeltaTime,0f,.1f)*Find.TickManager.TickRateMultiplier);
            if(r==null)
            {
                // Selecting a grounded drafted aircraft must not stop its native path.
                // Acquire local motion only when the player actually asks to drive or take off.
                bool driving=ControlKeys.Held(ControlAction.Forward)||ControlKeys.Held(ControlAction.Backward)
                    ||ControlKeys.Held(ControlAction.Left)||ControlKeys.Held(ControlAction.Right);
                if(!driving&&!ControlKeys.Down(ControlAction.AirAscend))return;
                AerialFlight.Begin(v);if(!AerialFlight.TryGet(v,out r))return;
            }
            if(!r.Airborne&&!r.Automatic&&ControlKeys.Down(ControlAction.AirAscend))AerialRunway.RequestTakeoff(v,r);
            if(!ControlKeys.Held(ControlAction.AirAscend))r.TakeoffInputLatch=false;
            if(r.TakeoffInputLatch||!r.Airborne||r.Automatic)climb=0;
            if(landTap.Press(ControlKeys.RawDown(KeyCode.LeftControl),Time.unscaledTime,!wing)||ControlKeys.Down(ControlAction.AirLand))AerialLanding.Request(v,r);
            r.GodClimb=climb;r.GodClimbFrame=Time.frameCount;
            bool ground=AerialInputRules.GroundMovement(r.Airborne,r.Automatic);
            r.GodForward=ground?(ControlKeys.Held(ControlAction.Forward)?1:0)-(ControlKeys.Held(ControlAction.Backward)?1:0):0;
            r.GodTurn=ground?(ControlKeys.Held(ControlAction.Right)?1:0)-(ControlKeys.Held(ControlAction.Left)?1:0):0;
            if((r.GodForward!=0||r.GodTurn!=0)&&!r.Automatic){r.Navigate=r.Orbit=r.Cruise=false;}
            if(climb!=0&&!r.Automatic)
            {
                if(!r.Airborne)r.EngineStopped=false;
                if(r.Navigate)r.NavHeight=Math.Clamp(r.NavHeight+climb*3f*Time.unscaledDeltaTime*Find.TickManager.TickRateMultiplier,r.HomeHeight,r.HomeHeight+AerialFlight.Profile(v).maximumHeight);
            }
        }
    }
}
