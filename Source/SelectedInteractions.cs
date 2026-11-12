using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    internal static class SelectedInteractions
    {
        private readonly record struct CommandKey(Type Type, string Label, string Description, string Tutor, int Group, int GroupIgnoreContent);
        private static Thing inspectTarget;
        private static bool inspectControls;
        private static MainTabWindow_Inspect InspectPane => MainButtonDefOf.Inspect.TabWindow as MainTabWindow_Inspect;
        internal static bool Blocking
        {
            get
            {
                if (inspectTarget?.Spawned == true && Find.Selector?.SingleSelectedThing == inspectTarget
                    && Find.MainTabsRoot?.OpenTab == MainButtonDefOf.Inspect && (inspectControls || InspectPane?.OpenTabType != null))
                    return true;
                inspectTarget = null;
                inspectControls = false;
                return false;
            }
        }

        internal static void AddOptions(List<InteractionOption> options, Pawn actor, Thing target, IntVec3 clickedCell)
        {
            if (actor == null || target == null || !ThingSelectionUtility.SelectableByMapClick(target)) return;
            if (target is Building_Door door) AddDoorOptions(options, actor, door, clickedCell);

            // A few native tabs/gizmos inspect the selection even while constructing UI. Supply the one
            // locked target for the query without actually changing the selection, drawer, or designator.
            var (commands, reverse, tabs) = ReadSelected(target, () => (Commands(target), ReverseCommands(target), Tabs(target)));
            foreach (var (designator, command) in reverse)
            {
                // Ordinary work options assign this pawn a job; these give the colony a designation.
                // Match actual callbacks if a target happens to yield the same command directly.
                if (designator is not Designator_Claim && commands.OfType<Command_Action>().Any(c => c.action == command.action)) continue;
                string id = designator is Designator_Claim ? "claim" : "reverse:" + designator.GetType().FullName;
                string label = designator is Designator_Open or Designator_Deconstruct or Designator_Uninstall
                    or Designator_Mine or Designator_PlantsCut or Designator_PlantsHarvest or Designator_PlantsHarvestWood
                    ? L.T("指定：", "Designate: ") + command.LabelCap : command.LabelCap;
                Add(options, id, label, () => DisabledReverse(command),
                    () => WorkControl.RunNative(actor, () => WithSelection(actor, target, () => ActivateReverse(designator, target))));
            }
            var keys = commands.Select(Key).ToArray();
            var labelCounts = keys.GroupBy(key => key.Label).ToDictionary(group => group.Key, group => group.Count());
            var labelOrdinals = new Dictionary<string, int>();
            for (int i = 0; i < commands.Length; i++)
            {
                Command original = commands[i];
                CommandKey key = keys[i];
                int count = keys.Count(candidate => candidate == key);
                int occurrence = keys.Take(i).Count(candidate => candidate == key);
                int ordinal = labelOrdinals[key.Label] = labelOrdinals.GetValueOrDefault(key.Label) + 1;
                string label = labelCounts[key.Label] == 1 ? original.LabelCap : $"{original.LabelCap} ({ordinal})";
                Add(options, "gizmo:" + i, label, () => Disabled(original),
                    () => WorkControl.RunNative(actor, () => WithSelection(actor, target, () => Activate(target, key, count, occurrence))));
            }

            Add(options, "inspect-controls", L.T("原生操作面板", "Native controls"), () => null,
                () => WorkControl.RunNative(actor, () => WithSelection(actor, target, () => OpenControls(target))));
            foreach (InspectTabBase tab in tabs)
            {
                Type type = tab.GetType();
                Add(options, "inspect-tab:" + type.FullName, tab.labelKey.Translate(), () => null,
                    () => WorkControl.RunNative(actor, () => WithSelection(actor, target, () => OpenTab(target, type))));
            }
            Add(options, "inspect-info", L.T("详细资料", "Information"), () => null,
                () => WorkControl.RunNative(actor, () =>
                {
                    if (TargetStillValid(actor, target)) Find.WindowStack.Add(new Dialog_InfoCard(target));
                }));
        }

        private static void Add(List<InteractionOption> options, string id, string label, Func<string> validate, Func<bool> execute)
        {
            options.Add(new InteractionOption
            {
                Id = id, Label = label, Validate = validate,
                Execute = () => validate() is string reason ? InteractionActions.Reject(reason) : execute(),
            });
        }

        private static bool TargetStillValid(Pawn actor, Thing target) =>
            InteractionActions.NativeTargetInvalid(actor, target, target.Position) is not string reason || InteractionActions.Reject(reason);

        private static List<(Designator, Command_Action)> ReverseCommands(Thing target)
        {
            var commands = new List<(Designator, Command_Action)>();
            // The selected-object gizmo grid uses this same database and factory.
            foreach (Designator designator in Find.ReverseDesignatorDatabase.AllDesignators)
            {
                try
                {
                    if (designator.CreateReverseDesignationGizmo(target) is { Visible: true } command)
                        commands.Add((designator, command));
                }
                catch (Exception error)
                {
                    Log.WarningOnce("[FpsBridge] Reverse designation unavailable: " + designator.GetType().Name + ": " + error, 19272864);
                }
            }
            return commands;
        }

        private static void ActivateReverse(Designator designator, Thing target)
        {
            // Rebuild on confirmation so the native action sees current ownership, designations and eligibility.
            Command_Action command = Find.ReverseDesignatorDatabase.AllDesignators.Contains(designator)
                ? designator.CreateReverseDesignationGizmo(target) : null;
            if (command == null || !command.Visible)
            {
                InteractionActions.Reject(L.T("该目标已无法指定此操作。", "This designation is no longer available."));
                return;
            }
            if (DisabledReverse(command) is string reason) { InteractionActions.Reject(reason); return; }
            ActivateCommand(command);
        }

        private static string DisabledReverse(Command_Action command) => command.Disabled && command.disabledReason.NullOrEmpty()
            && !command.defaultDesc.NullOrEmpty() ? command.defaultDesc : Disabled(command);

        private static void AddDoorOptions(List<InteractionOption> options, Pawn actor, Building_Door door, IntVec3 clickedCell)
        {
            bool opening = !door.Open;
            string label = opening ? L.T("开门", "Open door") : L.T("关门", "Close door");

            string ManualInvalid()
            {
                string reason = DoorInvalid(actor, door);
                if (reason != null) return reason;
                if (FloorSpace.Band(actor.Map, actor.Position).Level != FloorSpace.Band(door.Map, door.Position).Level
                    || !door.OccupiedRect().ExpandedBy(1).Contains(actor.Position))
                    return L.T("请先靠近门。", "Move next to the door first.");
                if (door.Open == opening) return L.T("门的状态已改变。", "The door's state changed.");
                if (!opening && (door.HoldOpen || AccessTools.PropertyGetter(door.GetType(), "AlwaysOpen")?.Invoke(door, null) is true))
                    return L.T("这扇门正在保持开启。", "This door is being held open.");
                return null;
            }

            Add(options, opening ? "door-open" : "door-close", label, ManualInvalid, () => WorkControl.RunNative(actor, () =>
            {
                if (ManualInvalid() is string reason) { InteractionActions.Reject(reason); return; }
                door.CheckFriendlyTouched(actor);
                if (opening) door.StartManualOpenBy(actor);
                else door.StartManualCloseBy(actor);
            }));

            string goLabel = L.T("走到门的另一侧", "Go to the other side of the door");
            Add(options, "door-through", goLabel, () => DoorInvalid(actor, door) ?? CheckFarSide(actor, door, clickedCell, out _), () =>
            {
                IntVec3 destination = IntVec3.Invalid;
                string reason = DoorInvalid(actor, door) ?? CheckFarSide(actor, door, clickedCell, out destination);
                if (reason != null) return InteractionActions.Reject(reason);
                return WorkControl.StartExplicit(actor, JobMaker.MakeJob(JobDefOf.Goto, destination), goLabel);
            });
        }

        private static string DoorInvalid(Pawn actor, Building_Door door)
        {
            string reason = InteractionActions.NativeTargetInvalid(actor, door, door.Position);
            if (reason != null) return reason;
            if (actor.Faction != Faction.OfPlayer || door.Faction != Faction.OfPlayer)
                return L.T("只能操作己方的门。", "Only player-owned doors can be operated.");
            if (door.IsForbidden(actor) || !door.PawnCanOpen(actor))
                return L.T("该人物不能打开这扇门。", "This pawn cannot open the door.");
            return null;
        }

        private static string CheckFarSide(Pawn actor, Building_Door door, IntVec3 clickedCell, out IntVec3 destination)
        {
            destination = IntVec3.Invalid;
            CellRect occupied = door.OccupiedRect();
            IntVec3 doorway = occupied.Contains(clickedCell) ? clickedCell : door.Position;
            IntVec3 facing = door.Rotation.FacingCell;
            IntVec3 delta = actor.Position - doorway;
            int side = Math.Sign(delta.x * facing.x + delta.z * facing.z);
            if (side == 0) return L.T("请先站到门的一侧。", "Stand on one side of the door first.");
            destination = doorway;
            do { destination -= facing * side; } while (occupied.Contains(destination));

            Map map = door.Map;
            if (!destination.InBounds(map) || destination.Fogged(map) || !destination.Standable(map)
                || FloorSpace.Band(map, destination).Level != FloorSpace.Band(map, doorway).Level
                || destination.IsForbidden(actor) || !actor.CanReach(doorway, PathEndMode.OnCell, Danger.Deadly)
                || !actor.CanReach(destination, PathEndMode.OnCell, Danger.Deadly))
                return L.T("无法走到门的另一侧。", "The other side of the door cannot be reached.");
            return null;
        }

        private static T ReadSelected<T>(Thing target, Func<T> read)
        {
            if (Find.Selector.SingleSelectedThing == target) return read();
            List<object> selection = Find.Selector.SelectedObjects;
            object[] before = selection.ToArray();
            selection.Clear();
            selection.Add(target);
            try { return read(); }
            finally { selection.Clear(); selection.AddRange(before); }
        }

        private static Command[] Commands(Thing target)
        {
            try
            {
                return target.GetGizmos()?.OfType<Command>()
                    .Where(c => c is not Designator && c.Visible && !c.Label.NullOrEmpty()
                        && !c.Label.StartsWith("DEV:", StringComparison.OrdinalIgnoreCase)
                        && !c.Label.StartsWith("Debug:", StringComparison.OrdinalIgnoreCase)).ToArray() ?? Array.Empty<Command>();
            }
            catch (Exception error)
            {
                Log.WarningOnce("[FpsBridge] Selected thing commands unavailable: " + error, 19272862);
                return Array.Empty<Command>();
            }
        }

        private static InspectTabBase[] Tabs(Thing target)
        {
            try
            {
                return target.GetInspectTabs()?.Where(tab => tab != null && !tab.Hidden && tab.IsVisible && !tab.labelKey.NullOrEmpty())
                    .ToArray() ?? Array.Empty<InspectTabBase>();
            }
            catch (Exception error)
            {
                Log.WarningOnce("[FpsBridge] Selected thing inspect tabs unavailable: " + error, 19272863);
                return Array.Empty<InspectTabBase>();
            }
        }

        private static string Disabled(Command command) => !command.Disabled ? null : command.disabledReason.NullOrEmpty()
            ? L.T("原生命令不可用。", "The native command is unavailable.") : command.disabledReason;

        private static CommandKey Key(Command command) => new(command.GetType(), command.Label, command.defaultDesc,
            command.tutorTag, command.groupKey, command.groupKeyIgnoreContent);

        private static void Activate(Thing target, CommandKey key, int count, int occurrence)
        {
            Command[] matching = Commands(target).Where(command => Key(command) == key).ToArray();
            if (matching.Length != count)
            {
                InteractionActions.Reject(L.T("原生命令已变化，请重新打开轮盘。", "The native command changed. Reopen the wheel."));
                return;
            }
            Command command = matching[occurrence];
            if (Disabled(command) is string reason) { InteractionActions.Reject(reason); return; }
            ActivateCommand(command);
        }

        private static void ActivateCommand(Command command)
        {
            if (!TutorSystem.AllowAction(command.TutorTagSelect)) return;
            TutorSystem.Notify_Event(command.TutorTagSelect);

            // Wheel confirmation can be processed in Update instead of OnGUI. Use an isolated primary
            // click event for commands that read the argument or Event.current, then restore the latter.
            Event before = Event.current;
            var click = new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = before?.mousePosition ?? default };
            try
            {
                Event.current = click;
                command.ProcessInput(click);
                command.ProcessGroupInput(click, new List<Gizmo> { command });
            }
            finally { Event.current = before; }
        }

        private static void OpenTab(Thing target, Type type)
        {
            InspectTabBase tab = Tabs(target).FirstOrDefault(t => t.GetType() == type);
            if (tab == null)
            {
                InteractionActions.Reject(L.T("该目标没有可用的检查面板。", "This target has no available inspect tab."));
                return;
            }
            if (!TutorSystem.AllowAction("ITab-" + tab.tutorTag + "-Open")) return;
            if (InspectPaneUtility.OpenTab(type) != null)
            {
                inspectTarget = target;
                inspectControls = false;
            }
        }

        private static void OpenControls(Thing target)
        {
            Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect);
            InspectPane?.CloseOpenTab();
            inspectTarget = target;
            inspectControls = true;
        }

        private static void WithSelection(Pawn actor, Thing target, Action action)
        {
            if (!TargetStillValid(actor, target)) return;
            if (target.Map != Find.CurrentMap || !ThingSelectionUtility.SelectableByMapClick(target))
            {
                InteractionActions.Reject(L.T("目标已无法选中。", "The target is no longer selectable."));
                return;
            }

            Selector selector = Find.Selector;
            bool alreadySelected = selector.SingleSelectedThing == target;
            object[] before = alreadySelected ? null : selector.SelectedObjectsListForReading.ToArray();
            Window[] oldWindows = Find.WindowStack.Windows.ToArray();
            Designator oldDesignator = Find.DesignatorManager?.SelectedDesignator;
            bool keep = false;
            if (!alreadySelected) { selector.ClearSelection(); selector.Select(target, false, false); }
            Type oldTab = InspectPane?.OpenTabType;
            MainButtonDef oldMain = Find.MainTabsRoot.OpenTab;
            bool prepared = selector.SingleSelectedThing == target;
            try
            {
                if (!prepared)
                {
                    InteractionActions.Reject(L.T("该物体将选中转交给了另一个目标。", "This object redirects selection to another target."));
                    return;
                }
                action();
                bool inspecting = Find.MainTabsRoot.OpenTab == MainButtonDefOf.Inspect
                    && (inspectTarget == target && inspectControls || InspectPane?.OpenTabType != null
                        && (inspectTarget == target || alreadySelected || oldMain != MainButtonDefOf.Inspect || InspectPane.OpenTabType != oldTab));
                if (inspecting) inspectTarget = target;
                keep = inspecting || Find.Targeter?.IsTargeting == true || Find.MainTabsRoot.OpenTab != oldMain
                    || Find.DesignatorManager?.SelectedDesignator != oldDesignator
                    || Find.WindowStack.Windows.Any(w => !oldWindows.Contains(w));
            }
            finally
            {
                if (!alreadySelected && !keep && (!prepared || selector.SingleSelectedThing == target))
                {
                    selector.ClearSelection();
                    foreach (object obj in before)
                        if (obj is not Thing thing || !thing.Destroyed && thing.MapHeld == Find.CurrentMap)
                            selector.Select(obj, false, false);
                }
            }
        }
    }
}
