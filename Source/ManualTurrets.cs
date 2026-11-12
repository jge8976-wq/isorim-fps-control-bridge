using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace HSK.FpsBridge.Interop
{
    public enum TurretAimMode { Direct, Artillery }

    // A turret a possessed pawn can operate in person (E next to it). Other mods implement this for their own turret
    // classes and register a provider; vanilla Building_TurretGun is served by the built-in adapter.
    // Angles are RimWorld's flat angles (0 = north, clockwise, as TurretTop.CurRotation); times are game seconds.
    public interface IManualTurret
    {
        Thing Thing { get; }
        TurretAimMode AimMode { get; }
        // Camera height above the floor the turret stands on, in cells.
        float EyeHeight { get; }
        // Greatest turn speed of the top, degrees per game second.
        float TurnRate { get; }
        float Range { get; }
        float MinRange { get; }
        // Current top rotation; Aim sets it (the bridge already limits the speed).
        float Rotation { get; }
        void Aim(float rotation);
        // Null when the turret may be operated by this pawn, else the reason shown to the player.
        string Refusal(Pawn by);
        // Short status for the HUD: ready, warming up, cooling down, out of power, hold fire...
        string Status { get; }
        // 0-1 remaining ammunition/barrel, or null when the turret has none.
        float? Ammo { get; }
        // Trigger state, once per frame while operated: target is what the crosshair rests on (Invalid when nothing),
        // held/pressed the fire button. The turret starts and steers its bursts from this.
        void Trigger(LocalTargetInfo target, bool held, bool pressed);
        // Whether this verb belongs to the turret (its bursts then follow the crosshair between shots).
        bool OwnsVerb(Verb verb);
        void Begin(Pawn by);
        void End();
    }

    // Optional opt-in for pawn-free direct-fire control. Begin receives null in remote mode.
    // Implementers must support a turret caster without depending on a ManningPawn.
    public interface IRemoteManualTurret : IManualTurret
    {
        string RemoteRefusal { get; }
    }

    // A turret aimed on the artillery map (AimMode Artillery): it fires at ground cells and reloads by hand.
    public interface IArtilleryTurret : IManualTurret
    {
        // Game ticks from pulling the trigger to impact at the cell (warmup included); negative when unknown (no shell).
        float FlightTicks(IntVec3 target);
        // Radius in cells around the aimed cell the shell may land, for this operator.
        float Spread(IntVec3 target, Pawn by);
        float BlastRadius { get; }
        bool Loaded { get; }
        bool Reloading { get; }
        // Loaded shell and what lies within reach, for the HUD.
        string AmmoLabel(Pawn by);
        // Starts a quick reload from shells within two cells of the turret or carried by the operator.
        // Null when started (or already loaded), else the reason it cannot.
        string Reload(Pawn by);
    }

    public enum TurretFireMode { Single, Burst3, Auto }

    // A turret whose shots per trigger pull the operator chooses (B). Single and three-round bursts need a press per
    // burst; Auto keeps firing while the trigger is held.
    public interface ISelectableFireMode
    {
        IReadOnlyList<TurretFireMode> FireModes { get; }
        TurretFireMode FireMode { get; set; }
    }

    public static class ManualTurrets
    {
        private static readonly List<Func<Thing, IManualTurret>> providers = new List<Func<Thing, IManualTurret>>();

        // Providers are asked newest first; the first non-null answer wins, the built-in vanilla adapter last.
        public static void Register(Func<Thing, IManualTurret> provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            providers.Insert(0, provider);
        }

        public static IManualTurret For(Thing thing)
        {
            if (thing == null) return null;
            foreach (var provider in providers)
            {
                IManualTurret turret = null;
                try { turret = provider(thing); }
                catch (Exception e) { Log.ErrorOnce("[FpsBridge] Manual turret provider failed: " + e, 19271710); }
                if (turret != null) return turret;
            }
            return thing is Building_TurretGun gun ? new HSK.FpsBridge.VanillaTurret(gun) : null;
        }
    }
}
