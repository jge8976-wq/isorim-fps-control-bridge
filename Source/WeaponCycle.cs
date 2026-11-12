using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using UnityEngine;

namespace HSK.FpsBridge
{
    internal static class WeaponCycle
    {
        private static ThingWithComps pending;
        private static Pawn owner;
        private static int pendingSince = -1;   // 0.9.0: tick a slowed (hurt hands) swap began
        private static string notice;
        private static float noticeUntil;
        public static string Notice { get => notice; private set { notice = value; noticeUntil = Time.unscaledTime + 2f; } }
        public static string VisibleNotice => Pending || Time.unscaledTime < noticeUntil ? Notice : null;
        private static bool toHands;   // pending: put the weapon away and fight bare-handed
        public static bool Pending => pending != null || toHands;
        public static void Reset() { pending = null; toHands = false; owner = null; pendingSince = -1; Notice = null; }
        private static string PendingLabel => toHands ? L.T("空手", "bare hands") : pending.LabelShortCap;

        public static List<ThingWithComps> Weapons(Pawn pawn)
        {
            var list = new List<ThingWithComps>();
            ThingWithComps primary = pawn.equipment?.Primary;
            if (primary != null) list.Add(primary);
            if (pawn.inventory != null)
                foreach (Thing item in pawn.inventory.innerContainer)
                    if (item is ThingWithComps candidate && Eligible(candidate, pawn) && !list.Contains(candidate)) list.Add(candidate);
            // Stable item identity, so returning the old weapon to inventory cannot reorder cycling.
            list.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            return list;
        }

        private static bool Eligible(ThingWithComps item, Pawn pawn) => item != null && !item.Destroyed && item.stackCount > 0
            && item.def.IsWeapon && item.def.equipmentType == EquipmentType.Primary
            && item.TryGetComp<CompEquippable>() != null && EquipmentUtility.CanEquip(item, pawn)
            && EquipmentUtility.GetPersonaWeaponConfirmationText(item, pawn).NullOrEmpty();

        public static void Request(Pawn pawn, int direction)
        {
            FireControl.CancelInput();
            var list = Weapons(pawn);
            // People can also go bare-handed: a slot after the weapons (null).
            if (pawn.RaceProps.Humanlike && pawn.equipment != null && pawn.inventory != null) list.Add(null);
            if (list.Count == 0 || (list.Count == 1 && list[0] == null && pawn.equipment.Primary == null))
            { Notice = L.T("库存没有可切换武器", "No weapon to switch to"); return; }
            ThingWithComps current = owner == pawn && Pending ? pending : pawn.equipment?.Primary;
            ThingWithComps next = list[CycleIndex.Next(list.IndexOf(current), list.Count, direction)];
            if (next == pawn.equipment?.Primary) { pending = null; toHands = false; Notice = list.Count == 1 ? L.T("库存没有其他可用武器", "No other usable weapon carried") : null; return; }
            pending = next; toHands = next == null; owner = pawn; pendingSince = -1;
            Update(pawn);
        }

        public static void Update(Pawn pawn)
        {
            if (!Pending || owner != pawn) return;
            if (!toHands && (pawn.inventory == null || !pawn.inventory.innerContainer.Contains(pending) || !Eligible(pending, pawn)))
            { pending = null; Notice = L.T("目标武器已不在库存或不可装备", "That weapon is gone or cannot be equipped"); return; }
            // Do not bypass warmup, a burst, reload jobs or cooldown by cycling weapons.
            if (pawn.stances?.curStance is Stance_Busy || pawn.equipment?.PrimaryEq?.PrimaryVerb?.Bursting == true
                || pawn.CurJob?.ability != null || pawn.CurJob?.def.playerInterruptible == false)
            { Notice = L.T("攻击结束后切换：", "Switching after the attack: ") + PendingLabel; return; }
            // 0.9.0: hurt hands take their time.
            int delay = Condition.SwapDelayTicks(pawn);
            if (delay > 0)
            {
                int now = Find.TickManager.TicksGame;
                if (pendingSince < 0) pendingSince = now;
                if (now - pendingSince < delay) { Notice = L.T("手臂受伤，换枪中：", "Hurt arm, switching slowly: ") + PendingLabel; return; }
            }
            pendingSince = -1;
            ThingWithComps target = pending;
            bool hands = toHands;
            pending = null; toHands = false;
            if (hands) Holster(pawn);
            else Switch(pawn, target);
        }

        // Bare hands: the weapon goes into the inventory (and back out again with Tab).
        private static void Holster(Pawn pawn)
        {
            ThingWithComps previous = pawn.equipment?.Primary;
            if (previous == null) return;
            if (!previous.holdingOwner.TryTransferToContainer(previous, pawn.inventory.innerContainer, canMergeWithExistingStacks: false))
            { Notice = L.T("无法将原武器放回库存", "Cannot put the current weapon away"); return; }
            Notice = L.T("空手", "Bare hands");
            AimPicker.Reset();
            Log.Message("[FpsBridge] WEAPON_SWITCH pawn=" + pawn.thingIDNumber + "; weapon=none");
        }

        private static void Switch(Pawn pawn, ThingWithComps target)
        {
            var inventory = pawn.inventory.innerContainer;
            ThingWithComps previous = pawn.equipment?.Primary;
            if (pawn.equipment == null) { Notice = L.T("该人物不能装备武器", "This pawn cannot equip weapons"); return; }
            ThingWithComps detached = null;
            bool storedPrevious = false;
            try
            {
                // Native holder transfers send unequip/equip notifications and keep object ownership valid.
                if (previous != null)
                {
                    if (!previous.holdingOwner.TryTransferToContainer(previous, inventory, canMergeWithExistingStacks: false))
                    { Notice = L.T("无法将原武器放回库存", "Cannot put the current weapon away"); return; }
                    storedPrevious = true;
                }
                detached = (ThingWithComps)target.SplitOff(1);
                pawn.equipment.AddEquipment(detached);
                if (pawn.equipment.Primary != detached) throw new InvalidOperationException("Equipment holder rejected selected weapon");
                Notice = L.T("已装备：", "Equipped: ") + detached.LabelShortCap;
                AimPicker.Reset();
                SidearmsCompat.RememberSelection(pawn, detached);
                Log.Message("[FpsBridge] WEAPON_SWITCH pawn=" + pawn.thingIDNumber + "; weapon=" + detached.ThingID);
            }
            catch
            {
                // Restore both objects before allowing the session guard to report a failure.
                if (detached != null && !detached.Destroyed && detached.holdingOwner == null) inventory.TryAdd(detached, false);
                if (storedPrevious && pawn.equipment.Primary == null && inventory.Contains(previous))
                { inventory.Remove(previous); pawn.equipment.AddEquipment(previous); }
                throw;
            }
        }
    }
}
