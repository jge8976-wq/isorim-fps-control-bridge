using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Use the same providers as the map's right-click menu, while retaining the
    // possessed pawn and the precise Thing selected by the FPS crosshair.
    internal static class NativeInteractions
    {
        private sealed class Query
        {
            internal List<Pawn> Pawns;
            internal Map Map;
            internal IntVec3 Cell;
            internal Thing Target;
            internal bool Matched;
        }

        [ThreadStatic] private static Query activeQuery;
        private static bool ready;

        internal static void Install(Harmony harmony)
        {
            var method = AccessTools.DeclaredMethod(typeof(FloatMenuMakerMap), "GetProviderOptions",
                new[] { typeof(FloatMenuContext), typeof(List<FloatMenuOption>) });
            if (method == null)
            {
                Log.Warning("[FpsBridge] Native right-click providers could not be bound.");
                return;
            }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeInteractions), nameof(LockProviderTargets))
                { priority = Priority.First });
            ready = true;
        }

        public static void LockProviderTargets(FloatMenuContext __0)
        {
            Query query = activeQuery;
            // Perspective Shift copies the pawn list in the context constructor.
            if (query == null || query.Matched || __0.allSelectedPawns.Count != 1 || query.Pawns.Count != 1
                || __0.allSelectedPawns[0] != query.Pawns[0]
                || __0.map != query.Map || __0.ClickedCell != query.Cell) return;
            query.Matched = true;
            // Preserve the actual click cell's stacked targets. The camera-locked
            // Thing comes first so native duplicate resolution prefers the model the player aimed at.
            __0.ClickedThings.RemoveAll(t => t != query.Target && !t.OccupiedRect().Contains(query.Cell));
            __0.ClickedPawns.RemoveAll(p => p != query.Target && !p.OccupiedRect().Contains(query.Cell));
            if (query.Target != null)
            {
                __0.ClickedThings.Remove(query.Target);
                __0.ClickedThings.Insert(0, query.Target);
                if (query.Target is Pawn pawn)
                {
                    __0.ClickedPawns.Remove(pawn);
                    __0.ClickedPawns.Insert(0, pawn);
                }
            }
        }

        internal static void AddOptions(List<InteractionOption> options, Pawn actor, Thing target, IntVec3 clickedCell)
        {
            List<FloatMenuOption> available = GetOptions(actor, target, clickedCell);
            var labels = new HashSet<(string, Thing)>(options.Select(o => (o.Label, target)));
            foreach (FloatMenuOption native in available.OrderByDescending(o => o.Priority).ThenByDescending(o => o.orderInPriority))
            {
                var key = Key(native, target);
                if (!labels.Add(key)) continue;
                options.Add(new InteractionOption
                {
                    Id = "native:" + native.Label,
                    Label = native.Label,
                    DisabledReason = native.Disabled ? native.Label : null,
                    Execute = () => Execute(actor, target, clickedCell, key)
                });
            }
            if (available.Any(o => o.extraPartOnGUI != null))
                options.Add(new InteractionOption { Id = "native-menu-widgets", Label = L.T("原生菜单附加选项", "Native menu extras"),
                    Execute = () => OpenNativeMenu(actor, target, clickedCell) });
        }

        private static bool OpenNativeMenu(Pawn actor, Thing target, IntVec3 cell)
        {
            var available = GetOptions(actor, target, cell);
            if (available.Count == 0) return false;
            return WorkControl.RunNative(actor, () => Find.WindowStack.Add(new ExtrasMenu(available, actor, target, cell)));
        }

        private sealed class ExtrasMenu : FloatMenu
        {
            private readonly Pawn actor;
            private readonly Thing target;
            private readonly IntVec3 cell;

            public ExtrasMenu(List<FloatMenuOption> options, Pawn actor, Thing target, IntVec3 cell) : base(options)
            {
                this.actor = actor; this.target = target; this.cell = cell;
                preventCameraMotion = absorbInputAroundWindow = true;
            }

            public override void DoWindowContents(Rect inRect)
            {
                if (InteractionActions.NativeTargetInvalid(actor, target, cell) != null) { Close(false); return; }
                base.DoWindowContents(inRect);
            }

            public override void PreOptionChosen(FloatMenuOption option)
            {
                base.PreOptionChosen(option);
                if (option.Disabled) return;
                var key = Key(option, target);
                var current = GetOptions(actor, target, cell).FirstOrDefault(o => !o.Disabled && Key(o, target) == key);
                if (current == null) option.Disabled = true;
                else option.action = () => WorkControl.RunNative(actor, current.action);
            }
        }

        private static (string Label, Thing Target) Key(FloatMenuOption option, Thing target) =>
            (option.Label, option.revalidateClickTarget ?? option.iconThing ?? target);

        private static bool Execute(Pawn actor, Thing target, IntVec3 clickedCell, (string, Thing) key)
        {
            // Native FloatMenuMap revalidates before executing. Rebuild against
            // this locked target too, since reservations/ownership may change.
            FloatMenuOption current = GetOptions(actor, target, clickedCell)
                .FirstOrDefault(o => !o.Disabled && Key(o, target) == key);
            return current != null && WorkControl.RunNative(actor, () => current.Chosen(true, null));
        }

        private static List<FloatMenuOption> GetOptions(Pawn actor, Thing target, IntVec3 clickedCell)
        {
            if (!ready || actor?.Map is not Map map || map != Find.CurrentMap) return new List<FloatMenuOption>();
            if (target != null)
            {
                if (!target.Spawned || target.Map != map || target.Destroyed || target.Fogged())
                    return new List<FloatMenuOption>();
                if (!target.OccupiedRect().Contains(clickedCell)) clickedCell = target.Position;
            }
            if (!clickedCell.InBounds(map) || clickedCell.Fogged(map)) return new List<FloatMenuOption>();
            var query = new Query { Pawns = new List<Pawn> { actor }, Map = map, Cell = clickedCell, Target = target };
            Query previous = activeQuery;
            Pawn oldMakingFor = FloatMenuMakerMap.makingFor;
            FloatMenuOptionProvider oldProvider = FloatMenuMakerMap.currentProvider;
            // Some provider patches use the selector in addition to the supplied context.
            // Give them the normal one-pawn selection without firing selection UI callbacks.
            List<object> selected = Find.Selector.SelectedObjects;
            object[] oldSelection = selected.ToArray();
            selected.Clear();
            selected.Add(actor);
            activeQuery = query;
            try
            {
                var options = FloatMenuMakerMap.GetOptions(query.Pawns, clickedCell.ToVector3Shifted(), out _);
                return query.Matched ? options : new List<FloatMenuOption>();
            }
            catch (Exception e)
            {
                Log.WarningOnce("[FpsBridge] Native interaction providers failed: " + e, 19272926);
                return new List<FloatMenuOption>();
            }
            finally
            {
                activeQuery = previous;
                FloatMenuMakerMap.makingFor = oldMakingFor;
                FloatMenuMakerMap.currentProvider = oldProvider;
                selected.Clear();
                selected.AddRange(oldSelection);
            }
        }
    }
}
