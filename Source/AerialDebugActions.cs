using System;
using System.Linq;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialDebugActions
    {
        [DebugAction("ISORIM", "Create VTOL test range", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        internal static void CreateRange()
        {
            Map map = Find.CurrentMap;
            if (map == null || !VehicleApi.Ready) return;
            Type spawner = AccessTools.TypeByName("Vehicles.VehicleSpawner");
            int index = 0;
            foreach (var profile in DefDatabase<AerialFlightDef>.AllDefsListForReading)
            {
                int slot=index++;
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(profile.vehicleDef);
                if (def == null) continue;
                if(map.mapPawns.AllPawnsSpawned.Any(p=>p.def==def&&p.Faction==Faction.OfPlayer))continue;
                IntVec3 wanted = new IntVec3(map.Center.x + (slot-2)*35, 0, map.Center.z);
                if (!CellFinder.TryFindRandomCellNear(wanted,map,5,c => c.Standable(map) && !c.Fogged(map)
                    && GenAdj.OccupiedRect(c,Rot4.North,def.size).InBounds(map)
                    && GenAdj.OccupiedRect(c,Rot4.North,def.size).Cells.All(c2 => c2.Standable(map) && c2.GetEdifice(map)==null&&map.roofGrid.RoofAt(c2)==null),out IntVec3 cell)) continue;
                var generate = AccessTools.Method(spawner,"GenerateVehicle",new[]{def.GetType(),typeof(Faction)});
                if (generate == null) throw new MissingMethodException("VehicleSpawner.GenerateVehicle");
                Pawn vehicle = (Pawn)generate.Invoke(null,new object[]{def,Faction.OfPlayer});
                GenSpawn.Spawn(vehicle,cell,map,Rot4.North);
                AerialArmament.Ensure(vehicle);
                object fuel = VehicleApi.Get(vehicle,"CompFueledTravel");
                if (fuel != null) VehicleApi.Call(fuel,"Refuel",VehicleApi.Float(fuel,"FuelCapacity"));
                object seat = VehicleApi.Handlers(vehicle).FirstOrDefault(VehicleApi.Movement);
                Pawn pilot=null;
                for(int attempt=0;attempt<24;attempt++)
                {
                    var p=PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist,Faction.OfPlayer);
                    GenSpawn.Spawn(p,cell+new IntVec3(0,0,-5),map);
                    p.Name=new NameSingle("Flight Pilot "+index);
                    if(p.skills!=null)foreach(var skill in p.skills.skills)skill.Level=20;
                    p.jobs.StopAll();
                    if(seat!=null&&VehicleApi.OperatorCapable(p,true)&&VehicleApi.Available(seat,p)&&VehicleApi.Board(vehicle,p,seat)){pilot=p;break;}
                    p.Destroy(DestroyMode.Vanish);
                }
                if(pilot==null)throw new InvalidOperationException("Unable to prepare a qualified pilot for "+def.defName);
                Supply(vehicle);
                Log.Message("[FpsBridge] AERIAL_RANGE vehicle="+vehicle.ThingID+"; model="+def.defName+"; crew="+pilot.ThingID);
            }
            foreach(Pawn existing in map.mapPawns.AllPawnsSpawned.Where(p=>AerialFlight.Supports(p)&&p.Faction==Faction.OfPlayer).ToArray())Supply(existing);
            Messages.Message(L.T("试飞场已就绪。WASD滑行，空格自动起飞；空中空格上升、Ctrl下降，双击左Ctrl着陆。", "Flight range ready. WASD taxi, Space auto takeoff; airborne: Space up, Ctrl down, double left Ctrl to land."),MessageTypeDefOf.TaskCompletion,false);
        }
        // Only the explicitly created, isolated range receives sample ammunition.
        internal static void Supply(Pawn vehicle)
        {
                AerialArmament.Ensure(vehicle);
                foreach (object gun in VehicleApi.Turrets(vehicle))
                {
                    object gunDef = VehicleApi.Get(gun,"def");
                    ThingFilter filter = VehicleApi.Get(gunDef,"ammunition") as ThingFilter;
                    ThingDef ammo = filter?.AllowedThingDefs.FirstOrDefault();
                    if (ammo == null) continue;
                    VehicleApi.Set(gun,"loadedAmmo",ammo);
                    VehicleApi.Set(gun,"shellCount",Math.Max(1,VehicleApi.Int(gunDef,"magazineCapacity")));
                    if(vehicle.inventory?.innerContainer.Any(t=>t.def==ammo)!=true)
                    {Thing spare = ThingMaker.MakeThing(ammo); spare.stackCount = Math.Min(ammo.stackLimit,Math.Min(40,Math.Max(4,VehicleApi.Int(gunDef,"magazineCapacity"))));vehicle.inventory?.innerContainer.TryAdd(spare);}
                }
        }
        [DebugAction("ISORIM", "Toggle selected turret 3D range", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        internal static void ToggleTurretRange()
        {
            var state = AerialFlight.Store;
            if (state == null) return;
            if (Find.Selector.SingleSelectedThing is not Building_Turret turret || turret.AttackVerb == null)
            {
                state.DebugRangeTurret = null;
                Messages.Message(L.T("请选择一门炮塔。", "Select a turret."), MessageTypeDefOf.RejectInput, false);
                return;
            }
            state.DebugRangeTurret = state.DebugRangeTurret == turret ? null : turret;
        }

        internal static void DrawTurretRange(Building_Turret turret, bool debug)
        {
            if (debug && !Prefs.DevMode || !IsoRim.View3D.Active || turret?.Spawned != true || turret.Map != Find.CurrentMap)
                return;
            var verb = turret.AttackVerb;
            if (verb == null || !float.IsFinite(verb.verbProps.range) || verb.verbProps.range <= 0f)
                return;
            Visuals.TurretRange(AerialCombat.Origin(turret), verb.verbProps.range);
        }

        [DebugAction("ISORIM", "Lift selected VTOL target to 6 cells", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LiftTarget()
        {
            if (!(Find.Selector.SingleSelectedThing is Pawn v) || !AerialFlight.Supports(v)) return;
            AerialFlight.Begin(v);
            if (!AerialFlight.TryGet(v,out var r)) return;
            var m = r.Motor; m.Position.Y = r.HomeHeight+6f; r.Motor = m.Brake(); r.Airborne = true;
            VehicleApi.Draft(v,true);
            Log.Message("[FpsBridge] AERIAL_TARGET_READY vehicle="+v.ThingID+"; height="+m.Position.Y);
        }
    }
}
