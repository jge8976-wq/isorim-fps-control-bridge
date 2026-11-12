using System;

namespace HSK.FpsBridge
{
    internal static class BulletSpeeds
    {
        // Game-scale speeds, not metres/second. Bounds stay close to vanilla industrial bullets (46..100).
        // Unknown/energy/launcher projectiles retain their authored speed. No changes to AI or global Defs.
        public static float ForWeapon(string defName, float original)
        {
            string key = (defName ?? "").ToLowerInvariant();
            // ProjectileProperties.SpeedTilesPerTick in the installed 1.7 assembly is speed / 100.
            if (key.Contains("sniper") || key.Contains("boltaction") || key.Contains("bolt_action")) return 1f;
            if (key.Contains("smg") || key.Contains("submachine") || key.Contains("pistol") || key.Contains("revolver")) return .55f;
            if (key.Contains("assaultrifle") || key.Contains("assault_rifle") || key.Contains("lmg") || key.Contains("lightmachinegun")) return .75f;
            return float.IsFinite(original) ? Math.Max(.05f, original) : .75f;
        }
    }
}
