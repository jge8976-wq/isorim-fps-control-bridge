using System.Collections.Generic;
using Verse;

namespace HSK.FpsBridge
{
    // Optional author-supplied grip-to-tip length in cells, independent of the visual's cosmetic camera scale.
    public sealed class MeleeReachExtension : DefModExtension
    {
        public float forwardLength = -1f;
    }

    internal static class MeleeReach
    {
        private static readonly Dictionary<Thing, float> lengths = new Dictionary<Thing, float>();
        internal static void Reset() => lengths.Clear();
        internal static float LengthFor(Thing weapon, Verb verb)
        {
            if (weapon == null) return 0f;
            if (lengths.TryGetValue(weapon, out float length)) return length;
            float declared = weapon.def.GetModExtension<MeleeReachExtension>()?.forwardLength ?? -1f;
            if (float.IsFinite(declared) && declared >= 0f) length = MeleeReachRules.ForwardLength(declared, 0f);
            else
            {
                ModelSpec model = ModelSpecs.Find(weapon, verb, out _);
                float grip = model != null && model.Anchors.TryGetValue("PrimaryGrip", out V3 at) ? at.Z : 0f;
                length = model?.Kind == ModelKind.Blade ? MeleeReachRules.ForwardLength(model.Max.Z, grip) : 0f;
            }
            lengths[weapon] = length;
            return length;
        }
        internal static float For(Pawn pawn, bool thrust) => MeleeReachRules.Reach(
            LengthFor(pawn?.equipment?.Primary, pawn?.equipment?.PrimaryEq?.PrimaryVerb), thrust);
    }
}
