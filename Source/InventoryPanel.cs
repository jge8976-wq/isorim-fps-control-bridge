using System;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Reuse the game's gear/inventory tab, including mod subclasses. I closes it even while cursor capture is off.
    internal static class InventoryPanel
    {
        private static Pawn owner;
        private static Type tabType;
        private static MainTabWindow_Inspect Pane => MainButtonDefOf.Inspect.TabWindow as MainTabWindow_Inspect;
        public static bool Blocking => owner != null && owner == Bridge.ShootingPawn
            && Find.MainTabsRoot?.OpenTab == MainButtonDefOf.Inspect && Find.Selector.SingleSelectedThing == owner;

        public static void Frame(Pawn pawn)
        {
            if (!ControlKeys.Down(ControlAction.Inventory) || !Application.isFocused || LongEventHandler.ShouldWaitForEvent
                || GUIUtility.keyboardControl != 0 || Find.WindowStack == null
                || Find.WindowStack.AnyWindowAbsorbingAllInput || Find.WindowStack.AnySearchWidgetFocused
                || Find.WindowStack.WindowsPreventCameraMotion || Bridge.MainTabBlocks || Find.Targeter?.IsTargeting == true) return;
            if (Blocking) { Reset(); return; }
            if (TurretControl.Manned || View3D.ControlsLocked) return;
            Find.Selector.ClearSelection();
            Find.Selector.Select(pawn);
            InspectTabBase tab = InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Gear));
            if (tab == null || !tab.IsVisible)
            {
                Find.Selector.ClearSelection();
                Messages.Message(L.T("这个单位没有可用的装备 / 背包面板。", "This unit has no available gear / inventory tab."), MessageTypeDefOf.RejectInput, false);
                return;
            }
            owner = pawn; tabType = tab.GetType();
        }

        public static void Reset()
        {
            if (owner != null && Find.MainTabsRoot?.OpenTab == MainButtonDefOf.Inspect
                && Find.Selector.SingleSelectedThing == owner)
            {
                if (Pane?.OpenTabType == tabType) Pane.CloseOpenTab();
                Find.Selector.ClearSelection();
            }
            owner = null; tabType = null;
        }
    }
}
