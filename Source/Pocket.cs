using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    // 0.9.0 the pack in first person.
    //   - E with no work within reach (hauling aside): the item under the crosshair, within two cells, goes into the pack
    //     (as much of the stack as the pawn can carry; forbidden items too, unforbidden as they are taken).
    //   - Mouse wheel (no scope, not on a turret): steps through food, drugs and medicine in the pack; shown at the bottom.
    //   - Enter: uses the chosen one through the game's own jobs: eating/taking a drug, or tending one's own wounds.
    internal static class Pocket
    {
        private const float Reach = WorkControl.Range;
        private static ThingDef chosen;
        private static readonly List<Thing> usable = new List<Thing>();

        private static bool Usable(Thing t) => t?.def != null && (t.def.IsMedicine || (t.def.IsIngestible && t.def.ingestible != null));

        public static List<Thing> Items(Pawn pawn)
        {
            usable.Clear();
            if (pawn?.inventory?.innerContainer == null) return usable;
            foreach (Thing t in pawn.inventory.innerContainer) if (Usable(t)) usable.Add(t);
            usable.Sort((a, b) => string.CompareOrdinal(a.def.defName, b.def.defName));
            return usable;
        }

        private static Thing Current(Pawn pawn)
        {
            List<Thing> items = Items(pawn);
            if (items.Count == 0) return null;
            foreach (Thing t in items) if (t.def == chosen) return t;
            chosen = items[0].def;
            return items[0];
        }

        // ---- E: pick up

        public static bool TryPickUp(Pawn pawn)
        {
            Map map = pawn?.Map;
            if (map == null || pawn.inventory == null) return false;
            if (WorkControl.Enabled && WorkControl.Manual(pawn, true, probe: true)) return false;   // work here comes first (F)
            V3 aim = ControlCamera.AimPoint(pawn);
            V3 dir = HitMath.Direction(IsoRim.FirstPerson.Yaw, IsoRim.FirstPerson.Pitch);
            Thing item = null;
            foreach (IntVec3 c in new[] { FloorSpace.Cell(map, aim + dir * .05f), FloorSpace.Cell(map, aim - dir * .3f) })
            {
                if (!c.InBounds(map) || (c - pawn.Position).LengthHorizontal > Reach) continue;
                foreach (Thing t in c.GetThingList(map))
                    if (t.def.category == ThingCategory.Item && t.def.EverHaulable) { item = t; break; }
                if (item != null) break;
            }
            if (item == null) return false;
            TryPickUp(pawn, item);
            return true;
        }

        internal static string PickUpInvalid(Pawn pawn, Thing item)
        {
            string reason = InteractionActions.TargetInvalid(pawn, item);
            if (reason != null) return reason;
            if (item.def.category != ThingCategory.Item || !item.def.EverHaulable || item.stackCount < 1)
                return L.T("该物品不能拾取。", "This item cannot be picked up.");
            if (pawn.inventory?.innerContainer == null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                return L.T("没有可用背包或操作能力。", "No inventory or manipulation capacity is available.");
            if (item.IsBurning()) return L.T("物品正在燃烧。", "The item is burning.");
            if (!pawn.CanReserveAndReach(item, PathEndMode.ClosestTouch, Danger.Deadly))
                return L.T("无法接近物品，或物品已被占用。", "The item cannot be reached or is reserved.");
            return MassUtility.CountToPickUpUntilOverEncumbered(pawn, item) > 0 ? null : L.T("背包装不下了。", "The pack is full.");
        }

        internal static bool TryPickUp(Pawn pawn, Thing item)
        {
            string reason = PickUpInvalid(pawn, item);
            if (reason != null) return InteractionActions.Reject(reason);
            Map map = pawn.Map;
            int count = Mathf.Min(item.stackCount, MassUtility.CountToPickUpUntilOverEncumbered(pawn, item));
            string label = item.LabelShortCap;
            bool forbidden = item.IsForbidden(pawn);
            IntVec3 originalCell = item.Position;
            Thing taken = item.SplitOff(count);
            taken.SetForbidden(false, false);
            if (!pawn.inventory.innerContainer.TryAdd(taken, true))
            {
                if (!taken.Spawned) GenPlace.TryPlaceThing(taken, originalCell, map, ThingPlaceMode.Near);
                taken.SetForbidden(forbidden, false);
                Messages.Message(L.T("没能放进背包。", "Could not put it in the pack."), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            // A merged transfer can destroy the detached stack; retain only its def for the selection.
            if (Usable(item)) chosen = item.def;
            Messages.Message(L.T("拾取：", "Picked up: ") + label + (count > 1 ? " ×" + count : ""), MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] PICKUP pawn=" + pawn.thingIDNumber + "; item=" + taken.def.defName + "; count=" + count);
            return true;
        }

        // ---- wheel and Enter

        public static void Wheel(Pawn pawn, float delta)
        {
            if (Mathf.Abs(delta) < .01f) return;
            List<Thing> items = Items(pawn);
            if (items.Count < 2) return;
            int at = 0;
            for (int i = 0; i < items.Count; i++) if (items[i].def == chosen) at = i;
            at = (at + (delta < 0f ? 1 : items.Count - 1)) % items.Count;
            chosen = items[at].def;
        }

        public static void Use(Pawn pawn)
        {
            Thing item = Current(pawn);
            if (item == null) { Messages.Message(L.T("背包里没有食物、药品或成瘾品。", "No food, medicine or drugs in the pack."), MessageTypeDefOf.RejectInput, false); return; }
            Use(pawn, item);
        }

        internal static string UseInvalid(Pawn pawn, Thing item)
        {
            string reason = WorkControl.ExplicitInvalid(pawn);
            if (reason != null) return reason;
            if (item == null || item.Destroyed || item.stackCount < 1) return L.T("物品已消失。", "The item is gone.");
            bool inInventory = pawn.inventory?.Contains(item) == true;
            if (!inInventory)
            {
                reason = InteractionActions.TargetInvalid(pawn, item);
                if (reason != null) return reason;
                if (!pawn.CanReserveAndReach(item, PathEndMode.ClosestTouch, Danger.Deadly, 10, 1))
                    return L.T("无法接近物品，或物品已被占用。", "The item cannot be reached or is reserved.");
            }
            if (item.IsBurning()) return L.T("物品正在燃烧。", "The item is burning.");
            if (item.def.IsMedicine)
            {
                reason = QuickTend.Invalid(pawn, pawn);
                if (reason != null) return reason;
                if (pawn.playerSettings != null && !pawn.playerSettings.medCare.AllowsMedicine(item.def))
                    return L.T("医疗策略不允许使用此药品。", "Medical care policy does not allow this medicine.");
                return null;
            }
            if (!CanUseByHand(item)) return L.T("现在不能食用此物品。", "This item cannot be consumed now.");
            return null;
        }

        internal static bool Use(Pawn pawn, Thing item)
        {
            string reason = UseInvalid(pawn, item);
            if (reason != null) return InteractionActions.Reject(reason);
            Job job;
            if (item.def.IsMedicine)
            {
                // Explicit first-person tending uses our native-toil driver, without changing selfTend.
                // targetC locks the selected stack; exhaustion ends this command instead of seeking more medicine.
                bool wasForbidden = item.IsForbidden(pawn), started = false;
                try
                {
                    item.SetForbidden(false, false);
                    started = QuickTend.Start(pawn, pawn, item);
                    return started;
                }
                finally { if (!started && !item.Destroyed) item.SetForbidden(wasForbidden, false); }
            }
            else
            {
                // This explicit FPS command is the player's decision, independent of AI race/ideology/diet policy.
                // The native job still owns real ingestion, nutrition, drug effects and stack consumption.
                job = JobMaker.MakeJob(JobDefOf.Ingest, item);
                job.count = item.def.IsDrug || pawn.needs?.food == null ? 1 : Mathf.Max(1, Mathf.Min(item.stackCount, FoodUtility.WillIngestStackCountOf(pawn, item.def, FoodUtility.GetNutrition(pawn, item, item.def))));
                job.takeExtraIngestibles = 0; // Never collect a different stack on the way.
            }
            bool forbidden = item.IsForbidden(pawn), accepted = false;
            try
            {
                item.SetForbidden(false, false);
                accepted = WorkControl.StartExplicit(pawn, job, (item.def.IsMedicine ? L.T("包扎：", "Tending: ") : L.T("使用：", "Using: ")) + item.LabelNoCount);
                if (!accepted) return InteractionActions.Reject(L.T("现在无法使用。", "Cannot use it now."));
            }
            finally { if (!accepted && !item.Destroyed) item.SetForbidden(forbidden, false); }
            Log.Message("[FpsBridge] POCKET_USE pawn=" + pawn.thingIDNumber + "; item=" + item.def.defName + "; job=" + job.def.defName);
            return true;
        }
        internal static bool CanUseByHand(Thing item) => item?.def?.ingestible != null && !item.Destroyed && item.IngestibleNow;

        internal static string EquipInvalid(Pawn pawn, Thing item)
        {
            string reason = InteractionActions.TargetInvalid(pawn, item);
            if (reason != null) return reason;
            if (pawn.equipment == null || item.def.equipmentType != EquipmentType.Primary || !item.def.IsWeapon || item.TryGetComp<CompEquippable>() == null)
                return L.T("该人物无法装备此武器。", "This pawn cannot equip this weapon.");
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return L.T("缺少操作能力。", "Manipulation capacity is missing.");
            if (pawn.WorkTagIsDisabled(WorkTags.Violent) || (item.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting)))
                return L.T("该人物无法使用这类武器。", "This pawn is incapable of using this weapon type.");
            if (item.IsBurning()) return L.T("武器正在燃烧。", "The weapon is burning.");
            if (!pawn.CanReserveAndReach(item, PathEndMode.ClosestTouch, Danger.Deadly)) return L.T("无法接近武器，或武器已被占用。", "The weapon cannot be reached or is reserved.");
            if (pawn.IsQuestLodger() && !EquipmentUtility.QuestLodgerCanEquip(item, pawn)) return L.T("任务客人不能更换为此武器。", "The quest guest cannot equip this weapon.");
            if (!EquipmentUtility.CanEquip(item, pawn, out string cantReason)) return L.T("无法装备：", "Cannot equip: ") + cantReason;
            if (!EquipmentUtility.GetPersonaWeaponConfirmationText(item, pawn).NullOrEmpty())
                return L.T("人格武器需要通过原生装备菜单确认绑定。", "Use the native equipment menu to confirm bonding this persona weapon.");
            return null;
        }

        internal static bool Equip(Pawn pawn, Thing item)
        {
            string reason = EquipInvalid(pawn, item);
            if (reason != null) return InteractionActions.Reject(reason);
            bool forbidden = item.IsForbidden(pawn), accepted = false;
            try
            {
                item.SetForbidden(false, false);
                accepted = WorkControl.StartExplicit(pawn, JobMaker.MakeJob(JobDefOf.Equip, item), L.T("装备：", "Equip: ") + item.LabelShortCap);
                return accepted || InteractionActions.Reject(L.T("现在无法装备。", "Cannot equip now."));
            }
            finally { if (!accepted && !item.Destroyed) item.SetForbidden(forbidden, false); }
        }

        public static void DrawHud(Pawn pawn)
        {
            Thing item = Current(pawn);
            if (item == null) return;
            List<Thing> items = usable;
            int at = Mathf.Max(0, items.IndexOf(item));
            float w = 300f, x = UI.screenWidth * .5f - w / 2f, y = UI.screenHeight - 112f;
            GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
            try
            {
                Widgets.DrawBoxSolid(new Rect(x, y, w, 30f), new Color(0f, 0f, 0f, .5f));
                Widgets.ThingIcon(new Rect(x + 4f, y + 3f, 24f, 24f), item);
                Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleLeft; GUI.color = Color.white;
                int total = 0; foreach (Thing t in items) if (t.def == item.def) total += t.stackCount;
                Widgets.Label(new Rect(x + 32f, y, w - 36f, 30f), item.LabelNoCount + " ×" + total + "   (" + (at + 1) + "/" + items.Count + ")");
                Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.MiddleRight; GUI.color = new Color(.75f, .75f, .75f);
                Widgets.Label(new Rect(x, y, w - 6f, 30f), ControlHints.For(L.T("滚轮选择 · Enter 使用", "Wheel to choose · Enter to use")));
            }
            finally { Text.Font = f0; Text.Anchor = a0; GUI.color = c0; }
        }
    }
}
