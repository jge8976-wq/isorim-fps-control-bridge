using System;
using System.Linq;
using IsoRim;
using RimWorld;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    public sealed partial class StrikeRuntimeProbe
    {
        private static int interactionStage;
        private static DateTime interactionDeadline;
        private static Thing interactionContainer, interactionLoot;
        private static Building_Door interactionDoor;
        private static IntVec3 interactionExit;
        private static bool interactionSawOpenJob, interactionSawDoorway;

        private static bool StepInteraction(Map map)
        {
            if (interactionStage == 8) return true;
            if (interactionStage == 0)
            {
                combatActor.jobs.StopAll();
                combatActor.Position = combatOrigin;
                combatActor.Notify_Teleported();
                EnterCombatActor();
                NextInteractionStage();
                return false;
            }
            Require(DateTime.UtcNow < interactionDeadline,
                $"Interaction stage {interactionStage} timed out. Pawn={combatActor.Position}; job={combatActor.CurJob}; container={interactionContainer}; door={interactionDoor?.Open}.");
            if (FirstPersonTransition.Active) return false;
            Require(Bridge.Active && FirstPerson.On && FirstPerson.Pawn == combatActor,
                "A native interaction exited or switched the possessed pawn.");
            switch (interactionStage)
            {
                case 1:
                case 3:
                    string def = interactionStage == 1 ? "AncientCryptosleepCasket" : "SealedCrate";
                    IntVec3 cell = combatOrigin + new IntVec3(0, 0, interactionStage == 1 ? 5 : 11);
                    interactionContainer = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDef.Named(def)), cell, map, Rot4.South);
                    interactionContainer.SetForbidden(false, false);
                    if (interactionContainer is Building_Casket casket && !casket.HasAnyContents)
                    {
                        interactionLoot = ThingMaker.MakeThing(ThingDefOf.Steel);
                        Require(casket.TryAcceptThing(interactionLoot, false), "Unable to seed the casket with an actual item.");
                    }
                    Require(interactionContainer is IOpenable { CanOpen: true }, def + " is not openable in the fixture.");
                    string openLabel = "Open".Translate(interactionContainer).ToString();
                    var native = FloatMenuMakerMap.GetOptions(new() { combatActor }, cell.ToVector3Shifted(), out _);
                    Require(native.Any(o => o.Label == openLabel && !o.Disabled), "Native right-click Open is unavailable for " + def);
                    ExecuteInteraction(interactionContainer, "native:" + openLabel);
                    interactionSawOpenJob = combatActor.CurJob?.def == JobDefOf.Open && combatActor.CurJob.targetA.Thing == interactionContainer;
                    NextInteractionStage();
                    return false;
                case 2:
                case 4:
                    interactionSawOpenJob |= combatActor.CurJob?.def == JobDefOf.Open && combatActor.CurJob.targetA.Thing == interactionContainer;
                    if (interactionContainer.Spawned && interactionContainer is IOpenable { CanOpen: true }) return false;
                    Require(interactionSawOpenJob, "The container opened without observing the pawn's native Open job.");
                    Report(interactionStage == 2 ? "interaction-ancient-casket-native-open" : "interaction-sealed-crate-native-open",
                        new { target = interactionContainer.def.defName, job = JobDefOf.Open.defName, pawn = combatActor.ThingID,
                            spawned = interactionContainer.Spawned, firstPerson = FirstPerson.On, nativeMenuParity = true });
                    if (interactionContainer.Spawned) interactionContainer.Destroy(DestroyMode.Vanish);
                    NextInteractionStage();
                    return false;
                case 5:
                    Thing wall = SpawnStructureWall(map, combatOrigin + new IntVec3(4, 0, 8));
                    Require(wall.Faction != Faction.OfPlayer, "Claim fixture already belongs to the player.");
                    ExecuteInteraction(wall, "claim");
                    Require(wall.Faction == Faction.OfPlayer, "Native Claim did not transfer the building.");
                    Report("interaction-native-building-claim", new { target = wall.ThingID, owned = true, firstPerson = FirstPerson.On });
                    Thing item = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), combatOrigin + new IntVec3(3, 0, 7), map);
                    item.SetForbidden(true, false);
                    Require(item.IsForbidden(combatActor), "Allow fixture is not forbidden.");
                    var allow = item.GetGizmos().OfType<Command_Toggle>().Single(g => g.hotKey == KeyBindingDefOf.Command_ItemForbid);
                    ExecuteInteraction(item, null, allow.LabelCap);
                    Require(!item.IsForbidden(combatActor), "Native Allow did not clear the forbidden flag.");
                    Report("interaction-native-item-allow", new { target = item.ThingID, forbidden = false, firstPerson = FirstPerson.On });
                    IntVec3 doorCell = combatOrigin + new IntVec3(0, 0, 17);
                    SpawnStructureWall(map, doorCell + IntVec3.West).SetFaction(Faction.OfPlayer);
                    SpawnStructureWall(map, doorCell + IntVec3.East).SetFaction(Faction.OfPlayer);
                    interactionDoor = (Building_Door)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Door, ThingDefOf.WoodLog), doorCell, map, Rot4.North);
                    interactionDoor.SetFaction(Faction.OfPlayer);
                    combatActor.jobs.StopAll();
                    IntVec3 facing = interactionDoor.Rotation.FacingCell;
                    combatActor.Position = doorCell - facing;
                    combatActor.Notify_Teleported();
                    interactionExit = doorCell + facing;
                    Require(!interactionDoor.Open, "Door fixture is already open.");
                    ExecuteInteraction(interactionDoor, "door-open");
                    NextInteractionStage();
                    return false;
                case 6:
                    if (!interactionDoor.Open) return false;
                    ExecuteInteraction(interactionDoor, "door-through");
                    Require(combatActor.CurJob?.def == JobDefOf.Goto && combatActor.CurJob.targetA.Cell == interactionExit,
                        "The native doorway order did not target the far side.");
                    NextInteractionStage();
                    return false;
                default:
                    interactionSawDoorway |= interactionDoor.OccupiedRect().Contains(combatActor.Position);
                    if (combatActor.Position != interactionExit) return false;
                    Require(interactionSawDoorway, "The pawn reached the far side without crossing the actual doorway.");
                    Report("interaction-native-open-and-cross-door", new { door = interactionDoor.ThingID,
                        destination = interactionExit.ToString(), crossedDoor = true, firstPerson = FirstPerson.On });
                    NextInteractionStage();
                    return true;
            }
        }

        private static void ExecuteInteraction(Thing target, string id, string label = null)
        {
            var options = InteractionActions.Build(combatActor, target, target.Position);
            var option = options.FirstOrDefault(o => id == null ? o.Id.StartsWith("gizmo:") && o.Label == label : o.Id == id);
            Require(option != null, $"Missing wheel option {id ?? label} for {target}. Visible: {string.Join(", ", options.Select(o => o.Id + "/" + o.Label))}");
            string disabled = option.Validate != null ? option.Validate() : option.DisabledReason;
            Require(disabled == null, $"Wheel option {id ?? label} is disabled: {disabled}");
            Require(option.Execute(), $"Wheel option {id ?? label} did not execute.");
        }

        private static void NextInteractionStage()
        {
            interactionStage++;
            interactionDeadline = DateTime.UtcNow.AddSeconds(40);
        }
    }
}
