using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    internal sealed class InteractionOption
    {
        public string Id, Label, DisabledReason;
        public Func<string> Validate;
        public Func<bool> Execute;
    }

    // Options close over the Thing itself. Validation never designates, reserves, creates a job or orders a pawn.
    internal static class InteractionActions
    {
        private enum Work { Chop, Harvest, Cut, Mine, Repair, Deconstruct, Uninstall }

        internal static List<InteractionOption> Build(Pawn actor, Thing target, IntVec3 clickedCell)
        {
            var options = new List<InteractionOption>();
            NativeInteractions.AddOptions(options, actor, target, clickedCell);
            try { SelectedInteractions.AddOptions(options, actor, target, clickedCell); }
            catch (Exception e) { Log.WarningOnce("[FpsBridge] Selected target actions unavailable: " + e, 19272929); }
            if (target is Plant plant)
            {
                if (plant.def.plant.IsTree)
                    AddWork(options, actor, target, "chop", L.T("砍伐树木", "Chop tree"), Work.Chop);
                else
                {
                    AddWork(options, actor, target, "harvest", L.T("收获", "Harvest"), Work.Harvest);
                    AddWork(options, actor, target, "cut", L.T("割除植物", "Cut plant"), Work.Cut);
                }
            }
            else if (target?.def?.mineable == true)
                AddWork(options, actor, target, "mine", L.T("开采", "Mine"), Work.Mine);
            else if (target is Building)
            {
                AddWork(options, actor, target, "repair", L.T("修理", "Repair"), Work.Repair);
                AddWork(options, actor, target, "deconstruct", L.T("拆除", "Deconstruct"), Work.Deconstruct);
                AddWork(options, actor, target, "uninstall", L.T("卸载", "Uninstall"), Work.Uninstall);
            }
            else if (target?.def?.category == ThingCategory.Item)
            {
                Add(options, "pickup", L.T("拾取到背包", "Pick up"), () => Pocket.PickUpInvalid(actor, target), () => Pocket.TryPickUp(actor, target));
                if (target.def.IsWeapon)
                    Add(options, "equip", L.T("装备武器", "Equip weapon"), () => Pocket.EquipInvalid(actor, target), () => Pocket.Equip(actor, target));
                if (target.def.ingestible != null && !target.def.IsMedicine)
                    Add(options, "ingest", L.T("食用 / 使用", "Eat / use"), () => Pocket.UseInvalid(actor, target), () => Pocket.Use(actor, target));
                if (target.def.IsMedicine)
                    Add(options, "use-medicine", L.T("用药包扎自己", "Tend self with medicine"), () => Pocket.UseInvalid(actor, target), () => Pocket.Use(actor, target));
            }
            if (target is Pawn patient && patient != actor)
                Add(options, "tend", L.T("治疗队友", "Tend ally"), () => QuickTend.Invalid(actor, patient), () => QuickTend.Start(actor, patient));
            // An empty crosshair may explicitly select self. A locked ally is never replaced by self.
            if (target == null || target == actor)
                Add(options, "tend-self", L.T("治疗自己", "Tend self"), () => QuickTend.Invalid(actor, actor), () => QuickTend.Start(actor, actor));
            return options;
        }

        private static void Add(List<InteractionOption> options, string id, string label, Func<string> validate, Func<bool> execute)
        {
            var option = new InteractionOption { Id = id, Label = label, DisabledReason = null, Validate = validate };
            option.Execute = () =>
            {
                string reason = validate();
                return reason == null ? execute() : Reject(reason);
            };
            options.Add(option);
        }

        internal static bool Reject(string reason)
        {
            Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
            return false;
        }

        internal static string TargetInvalid(Pawn actor, Thing target)
        {
            string reason = WorkControl.ExplicitInvalid(actor);
            if (reason != null) return reason;
            if (target == null || target.Destroyed || !target.Spawned || target.Map != actor.Map)
                return L.T("目标已消失或不在当前地图。", "The target is gone or on another map.");
            if (target.Fogged()) return L.T("目标不可见。", "The target is not visible.");
            if (FloorSpace.Band(actor.Map, actor.Position).Level != FloorSpace.Band(target.Map, target.Position).Level)
                return L.T("目标不在同一楼层。", "The target is on another floor.");
            bool inRange = false;
            foreach (IntVec3 cell in target.OccupiedRect())
            {
                if (actor.Position.DistanceToSquared(cell) > WorkControl.Range * WorkControl.Range) continue;
                inRange = true;
                // The target's own wall/ore cell is an allowed endpoint; intermediate walls remain blockers.
                if (GenSight.LineOfSight(actor.Position, cell, actor.Map, CellRect.SingleCell(actor.Position), target.OccupiedRect())) return null;
            }
            return inRange ? L.T("视线被遮挡。", "Line of sight is blocked.")
                : L.T("目标超出 2.9 格交互范围。", "The target is beyond the 2.9-cell interaction range.");
        }

        internal static string NativeTargetInvalid(Pawn actor, Thing target, IntVec3 clickedCell)
        {
            string reason = WorkControl.NativeInvalid(actor);
            if (reason != null) return reason;
            if (target != null && (!target.Spawned || target.Destroyed || target.Map != actor.Map || target.Fogged()))
                return L.T("目标已消失或不可见。", "The target is gone or no longer visible.");
            if (target == null && (!clickedCell.InBounds(actor.Map) || clickedCell.Fogged(actor.Map)))
                return L.T("目标格不可见。", "The target cell is not visible.");
            return null;
        }

        // Lock the visible model or the actual ground cell under the camera. Vanilla handles reachability.
        internal static LocalTargetInfo Pick(Pawn actor)
        {
            if (actor?.Spawned != true || actor.Map == null) return LocalTargetInfo.Invalid;
            Map map = actor.Map;
            V3 eye = VehicleModels.V(ControlCamera.RenderEye());
            V3 end = eye + HitMath.Direction(IsoRim.FirstPerson.Yaw, IsoRim.FirstPerson.Pitch) * 120f;
            var trace = new Ballistics.ExternalTrace(actor);
            bool contact = trace.Segment(map, eye, end, out float best, out Thing found, out _, out _, out _);
            if (!contact) best = float.PositiveInfinity;
            // Nearby small objects without combat volumes remain selectable before the first solid hit.
            var volumes = new List<HitVolume>();
            var seen = new HashSet<Thing>();
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(actor.Position, WorkControl.Range + 1f, true))
            {
                if (!cell.InBounds(actor.Map)) continue;
                foreach (Thing thing in cell.GetThingList(actor.Map))
                {
                    if (thing == actor || !thing.Spawned || !seen.Add(thing) || thing.Fogged()) continue;
                    if (!(thing is Plant) && thing.def.category != ThingCategory.Item && !(thing is Building)) continue;
                    if (FloorSpace.Band(actor.Map, thing.Position).Level != FloorSpace.Band(actor.Map, actor.Position).Level) continue;
                    volumes.Clear();
                    ObjectTargets.Volumes(thing, volumes);
                    if (volumes.Count == 0)
                    {
                        Vector3 center = thing.TrueCenter(), offset = FloorSpace.Offset(thing);
                        float height = Bindings.PickHeight(thing);
                        if (!(height > .05f)) height = thing is Plant p ? (p.def.plant.IsTree ? 2.4f : .55f) : .18f;
                        float radius = thing is Plant tree && tree.def.plant.IsTree ? .24f : .32f;
                        volumes.Add(BodyVolumes.Box(HitRegion.Solid,
                            new V3(center.x + offset.x, offset.y + height * .5f, center.z + offset.z),
                            new V3(radius, height * .5f, radius), 0f));
                    }
                    int i = HitMath.Nearest(eye, end, volumes, out float t);
                    if (i >= 0 && t < best) { best = t; found = thing; }
                }
            }
            if (found != null) return found.Fogged() ? LocalTargetInfo.Invalid : new LocalTargetInfo(found);
            if (!contact) return LocalTargetInfo.Invalid;
            IntVec3 cellAtHit = FloorSpace.ContactCell(map, V3.Lerp(eye, end, best), end.Y - eye.Y);
            return cellAtHit.InBounds(map) && !cellAtHit.Fogged(map) ? new LocalTargetInfo(cellAtHit) : LocalTargetInfo.Invalid;
        }

        private static void AddWork(List<InteractionOption> options, Pawn actor, Thing target, string id, string label, Work work) =>
            Add(options, id, label, () => WorkInvalid(actor, target, work), () => StartWork(actor, target, work, label));

        private static string GiverName(Work work) => work == Work.Mine ? "Mine" : work == Work.Repair ? "Repair"
            : work == Work.Deconstruct ? "Deconstruct" : work == Work.Uninstall ? "Uninstall" : "PlantsCut";
        private static DesignationDef DesignationFor(Thing target, Work work) => work == Work.Repair ? null
            : work == Work.Mine ? DesignationDefOf.Mine : work == Work.Deconstruct ? DesignationDefOf.Deconstruct
            : work == Work.Uninstall ? DesignationDefOf.Uninstall
            : work == Work.Harvest || (work == Work.Chop && ((Plant)target).HarvestableNow) ? DesignationDefOf.HarvestPlant : DesignationDefOf.CutPlant;
        private static Designation Existing(Thing target, DesignationDef def) => def == null ? null
            : def == DesignationDefOf.Mine ? target.Map.designationManager.DesignationAt(target.Position, def)
            : target.Map.designationManager.DesignationOn(target, def);
        private static Designator DesignatorFor(Thing target, Work work)
        {
            if (work == Work.Mine) return new Designator_Mine();
            if (work == Work.Deconstruct) return new Designator_Deconstruct();
            if (work == Work.Uninstall) return new Designator_Uninstall();
            if (work == Work.Harvest) return new Designator_PlantsHarvest();
            if (work == Work.Chop && ((Plant)target).HarvestableNow) return new Designator_PlantsHarvestWood();
            return new Designator_PlantsCut { isOrder = true };
        }

        private static string WorkInvalid(Pawn actor, Thing target, Work work)
        {
            string reason = TargetInvalid(actor, target);
            if (reason != null) return reason;
            WorkGiverDef def = DefDatabase<WorkGiverDef>.GetNamedSilentFail(GiverName(work));
            if (def == null || !def.directOrderable) return L.T("原生工作定义不可用。", "The native work definition is unavailable.");
            if (actor.workSettings?.EverWork != true || actor.WorkTypeIsDisabled(def.workType) || actor.WorkTagIsDisabled(def.workTags))
                return L.T("该人物无法从事这类工作。", "This pawn is incapable of this work.");
            foreach (PawnCapacityDef capacity in def.requiredCapacities)
                if (!actor.health.capacities.CapableOf(capacity)) return L.T("缺少工作能力：", "Missing work capacity: ") + capacity.LabelCap;
            if (def.workType.relevantSkills != null && actor.skills != null)
                foreach (SkillDef skill in def.workType.relevantSkills)
                    if (actor.skills.GetSkill(skill).TotallyDisabled)
                        return L.T("所需工作技能不可用：", "Required work skill is disabled: ") + skill.LabelCap;
            if (target.IsForbidden(actor)) return L.T("目标已被禁止。", "The target is forbidden.");
            if (target.IsBurning()) return L.T("目标正在燃烧。", "The target is burning.");
            if (work == Work.Mine && !IdeoUtility.DoerWillingToDo(HistoryEventDefOf.Mined, actor))
                return L.T("该人物的信仰禁止采矿。", "This pawn's ideoligion forbids mining.");
            if (!actor.CanReserveAndReach(target, PathEndMode.Touch, Danger.Deadly))
                return L.T("无法接近目标，或目标已被占用。", "The target cannot be reached or is reserved.");
            if (work == Work.Repair || work == Work.Deconstruct || work == Work.Uninstall)
            {
                if (actor.Faction != Faction.OfPlayer || target.Faction != Faction.OfPlayer)
                    return L.T("只能操作己方建筑。", "Only player-owned buildings can be worked on.");
                if (target.TryGetComp<CompExplosive>()?.wickStarted == true)
                    return L.T("目标即将爆炸。", "The target is about to explode.");
            }
            if (work == Work.Repair)
            {
                if (!target.def.useHitPoints || target.HitPoints >= target.MaxHitPoints)
                    return L.T("建筑无需修理。", "The building needs no repair.");
                if (!RepairUtility.PawnCanRepairNow(actor, target)) return L.T("当前建筑不符合原生修理条件。", "Native repair conditions are not met.");
                if (!actor.Map.areaManager.Home[target.Position]) return L.T("建筑不在居住区内。", "The building is outside the home area.");
                if (Existing(target, DesignationDefOf.Deconstruct) != null || Existing(target, DesignationDefOf.Uninstall) != null)
                    return L.T("建筑已有拆除或卸载指定。", "The building is designated for removal.");
                return null;
            }
            if (target is Plant plant)
            {
                if (plant.def.plant.IsTree && plant.def.plant.treeLoversCareIfChopped
                    && !IdeoUtility.DoerWillingToDo(HistoryEventDefOf.CutTree, actor))
                    return L.T("该人物的信仰禁止砍树。", "This pawn's ideoligion forbids cutting trees.");
                if (plant.TryGetComp<CompPlantPreventCutting>()?.PreventCutting == true)
                    return L.T("该植物禁止割除或砍伐。", "This plant cannot be cut.");
                if (work == Work.Harvest && (!plant.HarvestableNow || plant.def.plant.harvestTag != "Standard"))
                    return L.T("作物尚不可收获。", "The crop cannot be harvested yet.");
            }
            if (actor.Map != Find.CurrentMap) return L.T("请先切换到目标所在地图。", "Switch to the target's map first.");
            DesignationDef designation = DesignationFor(target, work);
            // Do not let a new wheel command remove someone else's plant/building/mine designation.
            foreach (Designation other in target.Map.designationManager.AllDesignationsOn(target))
                if (other.def != designation) return L.T("目标已有其他指定，请先取消。", "Cancel the target's other designation first.");
            if (work == Work.Mine && actor.Map.designationManager.DesignationAt(target.Position, DesignationDefOf.MineVein) != null)
                return null; // MineAIUtility accepts the existing vein designation.
            if (Existing(target, designation) != null) return null;
            AcceptanceReport report = DesignatorFor(target, work).CanDesignateThing(target);
            return report.Accepted ? null : L.T("无法指定该动作：", "Cannot designate this action: ")
                + (string.IsNullOrEmpty(report.Reason) ? L.T("目标不符合原生条件。", "Native target conditions are not met.") : report.Reason);
        }

        private static bool StartWork(Pawn actor, Thing target, Work work, string label)
        {
            string reason = WorkInvalid(actor, target, work);
            if (reason != null) return Reject(reason);
            Map map = target.Map;
            Designation added = null;
            bool accepted = false;
            try
            {
                DesignationDef designation = DesignationFor(target, work);
                if (designation != null && Existing(target, designation) == null
                    && !(work == Work.Mine && map.designationManager.DesignationAt(target.Position, DesignationDefOf.MineVein) != null))
                {
                    // Native DesignateThing removes unrelated plant designations or immediately destroys buildings
                    // in god mode. Its CanDesignateThing contract was checked above; add only our exact designation.
                    added = new Designation(work == Work.Mine ? new LocalTargetInfo(target.Position) : new LocalTargetInfo(target), designation);
                    map.designationManager.AddDesignation(added);
                    if (target is Plant)
                    {
                        Designator_PlantsHarvestWood.PossiblyWarnPlayerImportantPlantDesignateCut(target);
                        if (ModsConfig.IdeologyActive && target.def.plant.IsTree && target.def.plant.treeLoversCareIfChopped)
                            Designator_PlantsHarvestWood.PossiblyWarnPlayerOnDesignatingTreeCut();
                    }
                }
                WorkGiverDef def = DefDatabase<WorkGiverDef>.GetNamedSilentFail(GiverName(work));
                if (!(def?.Worker is WorkGiver_Scanner scanner) || scanner.MissingRequiredCapacity(actor) != null
                    || scanner.ShouldSkip(actor, true))
                    return Reject(L.T("原生工作条件不满足（意愿、技能、占用或目标状态）。", "Native work conditions failed (willingness, skill, reservation or target state)."));
                // The base HasJobOnThing calls JobOnThing and allocates a throwaway job. Only invoke real overrides
                // (repair/removal and mod-specific conditions), then ask for one exact native job.
                if (scanner.GetType().GetMethod(nameof(WorkGiver_Scanner.HasJobOnThing))?.DeclaringType != typeof(WorkGiver_Scanner)
                    && !scanner.HasJobOnThing(actor, target, true))
                    return Reject(L.T("原生目标工作条件不满足。", "Native work conditions for the target are not met."));
                Job job = scanner.JobOnThing(actor, target, true);
                JobDef expected = work == Work.Repair ? JobDefOf.Repair : work == Work.Mine ? JobDefOf.Mine
                    : work == Work.Deconstruct ? JobDefOf.Deconstruct : work == Work.Uninstall ? JobDefOf.Uninstall
                    : designation == DesignationDefOf.HarvestPlant ? JobDefOf.HarvestDesignated : JobDefOf.CutPlantDesignated;
                if (job == null || job.targetA.Thing != target || job.def != expected)
                    return Reject(L.T("原生工作未指向锁定目标，已取消。", "The native job did not match the locked target; cancelled."));
                job.workGiverDef = def;
                accepted = WorkControl.StartExplicit(actor, job, label + L.T("：", ": ") + target.LabelShortCap);
                if (!accepted) return Reject(L.T("这项工作现在无法开始。", "This work cannot start now."));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[FpsBridge] INTERACTION_WORK " + work + ": " + ex);
                return Reject(L.T("工作派发失败。", "Work dispatch failed."));
            }
            finally
            {
                if (!accepted && added != null && added.designationManager == map.designationManager)
                    map.designationManager.RemoveDesignation(added);
            }
        }
    }
}
