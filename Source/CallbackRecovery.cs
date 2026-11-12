using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Bridge
    {
        private static readonly MovementFrameGate renderGate = new MovementFrameGate();
        private static bool renderFallbackReported, hudRenderedReported;

        internal static void InstallHudCallbacks(Harmony harmony)
        {
            // A live control session must not depend on State.OnGUI being dispatched by another mod.
            // This replaces the old State.OnGUI postfix; it is not a second rendering route.
            harmony.Patch(AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)),
                postfix: new HarmonyMethod(typeof(Bridge), nameof(AfterAvatarGUI)) { priority = Priority.Last });
            foreach (string name in new[] { "SelectorOnGUI", "HandleMapClicks", "SelectorOnGUI_BeforeMainTabs" })
                harmony.Patch(AccessTools.Method(typeof(Selector), name),
                    prefix: new HarmonyMethod(typeof(Bridge), nameof(BeforeCapturedSelector)) { priority = Priority.First });
            bool HasPerspectiveHook(System.Reflection.MethodBase method)
                => Harmony.GetPatchInfo(method)?.Owners.Contains("PerspectiveShiftMod") == true;
            Log.Message("[FpsBridge] CALLBACK_GUARD READY; hud=UIRoot_Play; render=once-per-frame"
                + "; upstreamRootHook=" + HasPerspectiveHook(AccessTools.Method(typeof(Root_Play), "Update"))
                + "; upstreamGuiHook=" + HasPerspectiveHook(AccessTools.Method(typeof(UIRoot_Play), "UIRootOnGUI"))
                + "; upstreamTweenerHook=" + HasPerspectiveHook(AccessTools.Method(typeof(PawnTweener), "PreDrawPosCalculation")));
        }

        private static bool CaptureOwnsSelection() => Active && policy.Captured && LookAllows();

        public static bool BeforeCapturedSelector(Selector __instance)
        {
            if (!CaptureOwnsSelection()) return true;
            if (__instance?.dragBox != null) __instance.dragBox.active = false;
            return false;
        }

        public static bool BeforeRecoveryRender(object __instance)
        {
            // Leave other pawns and native PS-only sessions untouched.
            if (Ours(__instance) && !diagInsideRenderFallback) diagNativeRender++;
            return !Ours(__instance) || renderGate.Enter(__instance, Time.frameCount);
        }

        private static bool RenderRecoveryEligible() => Active && ValidSession()
            && ReferenceEquals(Bindings.Avatar, avatar) && !VehicleControl.On && !RemoteTurret.On;

        private static void MovementRenderFrame()
        {
            if (!recoveryInstalled || !RenderRecoveryEligible() || renderGate.Seen(avatar, Time.frameCount)) return;
            if (!renderFallbackReported)
            {
                renderFallbackReported = true;
                Log.Message("[FpsBridge] CALLBACK_GUARD upstream pawn render not observed this frame; syncing controlled pawn via native RenderPawn");
            }
            // Reuse PS's own tween/lean synchronization. It does not move the pawn or advance game ticks.
            // Render remains allowed while paused or while a panel has released input, as in State.Update.
            diagRenderFallback++; diagInsideRenderFallback = true;
            try { Bindings.AvatarRender.Invoke(avatar, null); }
            finally { diagInsideRenderFallback = false; }
        }

        private static void ReportHudRendered()
        {
            if (hudRenderedReported) return;
            hudRenderedReported = true;
            Log.Message("[FpsBridge] HUD_READY source=UIRoot_Play; pawn=" + pawn.thingIDNumber);
        }
    }
}
