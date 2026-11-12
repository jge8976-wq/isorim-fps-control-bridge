using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public partial class StrikeRuntimeProbe
    {
        private Pawn selectionAircraft;
        private int selectionPhase;
        private int selectionWait;
        private AerialFlightRecord selectionPriorFlight;
        private TimeSpeed selectionPriorSpeed;
        private static Vector2? selectionPointer;

        private bool StepSelection(Map map)
        {
            if (selectionPhase == 0)
            {
                selectionAircraft = map.mapPawns.AllPawnsSpawned
                    .Where(p => p.def.defName == "VVE_Mosquito" && AerialFlight.Supports(p))
                    .OrderBy(p => p.Position.DistanceToSquared(map.Center))
                    .FirstOrDefault(p => !p.Position.Fogged(map)
                        && VehicleGeometry.Parts(p, VehicleGeometry.Pose(p), new List<VehicleGeometry.Part>()));
                if (selectionAircraft == null)
                {
                    ThingDef def = ThingDef.Named("VVE_Mosquito");
                    MethodInfo generate = AccessTools.Method(AccessTools.TypeByName("Vehicles.VehicleSpawner"), "GenerateVehicle",
                        new[] { def.GetType(), typeof(Faction) });
                    selectionAircraft = (Pawn)generate.Invoke(null, new object[] { def, Faction.OfPlayer });
                    IntVec3 at = new IntVec3(map.Size.x / 2 + 24, 0, FloorSpace.Bands(map)[0].Rows / 2);
                    foreach (IntVec3 cell in CellRect.CenteredOn(at, 6).Cells)
                    {
                        map.fogGrid.Unfog(cell);
                        map.roofGrid.SetRoof(cell, null);
                        foreach (Thing thing in cell.GetThingList(map).ToArray())
                            if ((thing is Building || thing is Plant) && thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                    }
                    GenSpawn.Spawn(selectionAircraft, at, map, Rot4.North);
                }
                selectionPriorSpeed = Find.TickManager.CurTimeSpeed;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                View3D.Active = true;
                selectionPhase = 1;
                selectionWait = 18;
            }

            V3 focus = FloorSpace.Spot(selectionAircraft);
            Find.CameraDriver.SetRootPosAndSize(new Vector3(focus.X, 0f, focus.Z), 20f);
            if (selectionWait-- > 0) return false;

            if (selectionPhase == 1)
            {
                ClickAircraftModel(selectionAircraft, false);
                var flights = AerialFlight.Store.Flights;
                flights.TryGetValue(selectionAircraft, out selectionPriorFlight);
                AerialMotor raised = VehicleGeometry.Pose(selectionAircraft);
                float home = raised.Position.Y;
                raised.Position.Y += 12f;
                flights[selectionAircraft] = new AerialFlightRecord { Motor = raised, HomeHeight = home, Airborne = true };
                selectionPhase = 2;
                selectionWait = 18;
                return false;
            }

            try { ClickAircraftModel(selectionAircraft, true); }
            finally
            {
                if (selectionPriorFlight == null) AerialFlight.Store.Flights.Remove(selectionAircraft);
                else AerialFlight.Store.Flights[selectionAircraft] = selectionPriorFlight;
                Find.TickManager.CurTimeSpeed = selectionPriorSpeed;
                Find.Selector.ClearSelection();
            }
            selectionPhase = 3;
            return true;
        }

        private static void ClickAircraftModel(Pawn aircraft, bool airborne)
        {
            Require(Api.ViewReady, "IsoRim view did not render before the selection test");
            var parts = new List<VehicleGeometry.Part>();
            Require(VehicleGeometry.Parts(aircraft, VehicleGeometry.Pose(aircraft), parts), "Aircraft render plan unavailable");
            var body = parts.First(part => part.Role == "body");
            var vertices = VehicleMeshContact.Mesh(body.Mesh).Vertices;
            MethodInfo select = AccessTools.DeclaredMethod(typeof(Selector), "SelectUnderMouse")
                ?? throw new MissingMethodException("Selector.SelectUnderMouse");
            MethodInfo mouse = AccessTools.PropertyGetter(typeof(Input), nameof(Input.mousePosition))
                ?? throw new MissingMethodException("Input.mousePosition");
            MethodInfo overrideMouse = AccessTools.DeclaredMethod(typeof(StrikeRuntimeProbe),
                mouse.ReturnType == typeof(Vector2) ? nameof(OverrideSelectionMouse2) : nameof(OverrideSelectionMouse3));
            const string patchId = "hsk.strike.runtimeprobe.selection";
            var harmony = new Harmony(patchId);
            var footprint = GenAdj.OccupiedRect(aircraft.Position, aircraft.Rotation, aircraft.def.size);
            harmony.Patch(mouse, postfix: new HarmonyMethod(overrideMouse) { priority = Priority.Last });
            try
            {
                int triangles = vertices.Length / 3;
                int stride = Math.Max(1, triangles / 96);
                for (int i = 0; i < triangles; i += stride)
                {
                    int index = i * 3;
                    Vector3 center = VehicleMeshContact.U3((vertices[index] + vertices[index + 1] + vertices[index + 2]) / 3f);
                    Vector3 world = body.World.MultiplyPoint3x4(center);
                    Vector2 pixel = View3D.WorldToScreen(world);
                    if (pixel.x < Screen.width * .08f || pixel.x > Screen.width * .92f
                        || pixel.y < Screen.height * .15f || pixel.y > Screen.height * .85f) continue;
                    selectionPointer = pixel;
                    IntVec3 ground = UI.MouseCell();
                    if (airborne && (footprint.Contains(ground)
                        || Interaction.PickUnderPointer(aircraft.Map).Contains(aircraft))) continue;
                    Find.Selector.ClearSelection();
                    select.Invoke(Find.Selector, null);
                    if (Find.Selector.SingleSelectedThing != aircraft) continue;
                    Report(airborne ? "vehicle-selection-airborne" : "vehicle-selection-ground", new
                    {
                        selected = aircraft.ThingID,
                        mouseCell = ground.ToString(),
                        occupiedCell = aircraft.Position.ToString(),
                        outsideFootprint = !footprint.Contains(ground),
                        ordinaryIsoRimPick = Interaction.PickUnderPointer(aircraft.Map).Contains(aircraft),
                        renderedPoint = world.ToString(),
                        screenPixel = pixel.ToString()
                    });
                    return;
                }
                throw new InvalidOperationException(airborne
                    ? "Native selector could not pick the airborne mesh outside its ground footprint"
                    : "Native selector could not pick the parked vehicle mesh");
            }
            finally
            {
                selectionPointer = null;
                harmony.Unpatch(mouse, HarmonyPatchType.Postfix, patchId);
            }
        }

        private static void OverrideSelectionMouse2(ref Vector2 __result)
        {
            if (selectionPointer is Vector2 pixel) __result = pixel;
        }

        private static void OverrideSelectionMouse3(ref Vector3 __result)
        {
            if (selectionPointer is Vector2 pixel) __result = new Vector3(pixel.x, pixel.y, 0f);
        }
    }
}
