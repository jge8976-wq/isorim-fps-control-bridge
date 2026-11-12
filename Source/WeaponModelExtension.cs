using Verse;

namespace HSK.FpsBridge
{
    // Optional explicit choice for third-party Def patches. Library identifiers only; no filesystem paths.
    public sealed class WeaponModelExtension : DefModExtension
    {
        public string modelId;
        public bool disableKeywordMatching;
    }
}
