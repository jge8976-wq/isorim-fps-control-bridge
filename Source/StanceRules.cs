using System;

namespace HSK.FpsBridge
{
    // 0.7.0 (pre-development) postures for the controlled pawn: engine-free rules, exercised by the offline contracts.
    // Every key acts on press and toggles: LeftCtrl crouch, Z prone, LeftShift sprint (press again to restore);
    // Space jumps (from crouch or prone it stands up instead).
    internal enum Posture { Stand, Crouch, Prone }

    // The whirlwind (double right click, hold): turns per second by the Melee skill, 5 → 1, 10 → 2, 15 → 3, 20 → 4;
    // under 5 the pawn cannot do it. Costs 20 % of a base stamina bar per second.
    internal static class WhirlRules
    {
        public const int MinSkill = 5;
        public const float Drain = .2f, Radius = 2f, DoubleClickSeconds = .35f;
        public static float TurnsPerSecond(int melee) => melee < MinSkill ? 0f : Math.Min(20, melee) / 5f;
    }

    internal sealed class StanceState
    {
        public const float CrouchHeight = .68f, ProneHeight = .28f;      // body/eye height relative to standing
        public const float CrouchSpeed = .55f, ProneSpeed = .25f;        // movement relative to walking
        public const float SprintBoost = 2f;                             // on top of Perspective Shift's own sprint
        public const float JumpSeconds = .6f, JumpPeak = .45f;           // game-time seconds, cells
        public const float HeightRate = 7f;                              // per second toward the posture's height
        public const float FallGravity = 25f;                            // cells/s^2 for the eye after a drop between floors

        // 0.9.0 stamina: running, jumping, climbing, hopping and sliding spend it; it comes back after a short rest.
        // Spent out: no running or jumping (and a shaky view) until it is back to RecoverAt.
        public const float SprintCost = .11f, JumpCost = .12f, VaultCost = .25f, HopCost = .1f, SlideCost = .18f;   // per second / per action
        public const float Regen = .16f, RegenDelay = .8f, RecoverAt = .3f;
        public const float SlideSeconds = .65f, SlideBoost = 2.3f;
        public float Stamina { get; private set; } = 1f;
        public bool Exhausted { get; private set; }
        public float SlideTime { get; private set; } = -1f;
        public bool Sliding => SlideTime >= 0f;
        private float rested;

        // Most stamina the pawn can hold (1 = the old fixed bar, a Melee 5 pawn). Costs are in the same units, so a
        // fitter pawn runs, climbs and spins longer.
        public float Capacity { get; private set; } = 1f;
        public float Fill => Capacity > 0f ? Stamina / Capacity : 0f;

        // Melee 0 → 50 %, 5 → 100 %, 10 → 150 %, 20 → 250 %; each Shooting level adds 5 % more.
        public static float CapacityFor(int melee, int shooting) =>
            Math.Max(.3f, .5f + .1f * Math.Clamp(melee, 0, 20) + .05f * Math.Clamp(shooting, 0, 20));

        public void SetCapacity(float capacity)
        {
            if (!(capacity > 0f)) return;
            Capacity = capacity;
            if (Stamina > Capacity) Stamina = Capacity;
        }

        // A continuous effort (the whirlwind): rate per second; false once spent out (it stops).
        public bool Drain(float rate, float dt)
        {
            if (Exhausted) return false;
            if (!(dt > 0f)) return true;
            Stamina = Math.Max(0f, Stamina - rate * dt); rested = 0f;
            if (Stamina <= .001f) { Exhausted = true; Sprint = false; return false; }
            return true;
        }

        // Pays for an action; false (nothing paid) when spent out or too little is left.
        public bool Spend(float cost)
        {
            if (Exhausted || Stamina < cost * .5f) return false;
            Stamina = Math.Max(0f, Stamina - cost);
            rested = 0f;
            if (Stamina <= .001f) { Exhausted = true; Sprint = false; }
            return true;
        }

        public Posture Posture { get; private set; } = Posture.Stand;
        public bool Sprint { get; private set; }
        public float Height { get; private set; } = 1f;                  // smoothed current height scale
        public float JumpTime { get; private set; } = -1f;               // < 0: on the ground
        public float FallFrom { get; private set; }                      // height of a drop still being fallen (cells)
        public float FallTime { get; private set; }

        public bool Airborne => JumpTime >= 0f;
        private float airPace = 1f;
        private bool airSprint;
        public bool MovementSprint => Airborne || Falling ? airSprint : Sprint;

        public float TargetHeight => Posture == Posture.Crouch ? CrouchHeight : Posture == Posture.Prone ? ProneHeight : 1f;

        // Movement multiplier applied to Perspective Shift's speed (its own sprint multiplier stays in when sprinting).
        public float SpeedFactor => Airborne || Falling ? airPace : GroundSpeed;
        private float GroundSpeed => Sliding ? SlideBoost * (1f - .55f * Math.Clamp(SlideTime / SlideSeconds, 0f, 1f))
            : Sprint ? SprintBoost : Posture == Posture.Crouch ? CrouchSpeed : Posture == Posture.Prone ? ProneSpeed : 1f;

        // Hands: steadier low down, shakier at a run, sliding or out of breath.
        public float SwayFactor => (Sliding ? 1.4f : Sprint ? 1.6f : Posture == Posture.Crouch ? .5f : Posture == Posture.Prone ? .2f : 1f) * (Exhausted ? 1.8f : 1f);

        // Running, in the air or sliding: the body works harder (rest and food go twice as fast meanwhile).
        public bool Exerting(bool moving) => (Sprint && moving) || Airborne || Sliding;

        // Height of the jump arc now (cells above the ground), plus what is left of a drop between floors: the pawn is
        // already on the lower floor, the eye comes down to it under gravity.
        public float Lift => (Airborne ? 4f * JumpPeak * Fraction * (1f - Fraction) : 0f) + FallLeft;
        public float FallLeft => FallFrom > 0f ? Math.Max(0f, FallFrom - .5f * FallGravity * FallTime * FallTime) : 0f;
        public bool Falling => FallLeft > 0f;
        private float Fraction => Math.Clamp(JumpTime / JumpSeconds, 0f, 1f);

        public void Reset() { Posture = Posture.Stand; Sprint = false; Height = 1f; JumpTime = -1f; FallFrom = 0f; FallTime = 0f; Stamina = Capacity; Exhausted = false; SlideTime = -1f; rested = 0f; airPace = 1f; airSprint = false; }
        public void SeatForRiding() { Posture = Posture.Stand; Sprint = false; Height = 1f; JumpTime = -1f; FallFrom = 0f; FallTime = 0f; SlideTime = -1f; airPace = 1f; airSprint = false; }

        // Jumped off an upper floor: the pawn now stands below; the eye starts `height` cells up and falls to it.
        public void StartFall(float height)
        {
            CaptureMomentum();
            Sprint = false; Posture = Posture.Stand; JumpTime = -1f;
            FallFrom = float.IsFinite(height) ? Math.Max(0f, height) : 0f;
            FallTime = 0f;
        }

        public void PressCrouch()
        {
            Sprint = false;
            Posture = Posture == Posture.Crouch ? Posture.Stand : Posture.Crouch;   // prone -> crouch
        }

        // 0.9.0: crouching while running (and moving) slides forward instead, ending crouched.
        public void PressCrouch(bool moving)
        {
            if (Sprint && moving && !Airborne && !Sliding && Spend(SlideCost)) { Sprint = false; SlideTime = 0f; Posture = Posture.Crouch; return; }
            PressCrouch();
        }

        public void PressProne()
        {
            Sprint = false;
            if (Airborne) return;
            Posture = Posture == Posture.Prone ? Posture.Stand : Posture.Prone;
        }

        // Sprinting stands the pawn up; pressing again restores walking.
        public void PressSprint()
        {
            if (Sprint) { Sprint = false; return; }
            if (Exhausted) return;   // out of breath: no running until it comes back
            Sprint = true;
            Posture = Posture.Stand;
        }

        // Space: from crouch or prone it stands up; standing on the ground it jumps.
        public bool PressJump()
        {
            if (Posture != Posture.Stand) { Posture = Posture.Stand; return false; }
            if (Airborne || Falling) return false;
            float pace = GroundSpeed; bool running = Sprint;
            if (!Spend(JumpCost)) return false;
            airPace = pace; airSprint = running;
            JumpTime = 0f;
            return true;
        }
        private void CaptureMomentum()
        {
            if (Airborne || Falling) return;
            airPace = GroundSpeed; airSprint = Sprint;
        }

        // A vault over a wall ends the jump where the hands caught the top.
        public void EndJump() { JumpTime = -1f; if (!Falling) { airPace = 1f; airSprint = false; } }

        // Firing, aiming, being unable to run (exhausted) or stopping all end a sprint.
        public void CancelSprint() => Sprint = false;

        // dt: game-time seconds since the last step (0 while paused).
        public void Step(float dt) => Step(dt, false);

        // moving: the pawn is actually under way (running costs stamina only then).
        public void Step(float dt, bool moving)
        {
            if (!(dt > 0f)) return;
            if (Sprint && moving)
            {
                Stamina = Math.Max(0f, Stamina - SprintCost * dt); rested = 0f;
                if (Stamina <= .001f) { Exhausted = true; Sprint = false; }
            }
            else if (!Airborne && !Sliding)
            {
                rested += dt;
                if (rested >= RegenDelay) Stamina = Math.Min(Capacity, Stamina + Regen * dt);
                if (Exhausted && Stamina >= RecoverAt) Exhausted = false;
            }
            if (Sliding)
            {
                SlideTime += dt;
                if (SlideTime >= SlideSeconds) SlideTime = -1f;
            }
            float target = TargetHeight;
            float k = 1f - (float)Math.Exp(-HeightRate * dt);
            Height += (target - Height) * k;
            if (Math.Abs(Height - target) < .001f) Height = target;
            if (Airborne)
            {
                JumpTime += dt;
                if (JumpTime >= JumpSeconds) JumpTime = -1f;
            }
            if (FallFrom > 0f)
            {
                FallTime += dt;
                if (FallLeft <= 0f) { FallFrom = 0f; FallTime = 0f; }
            }
            if (!Airborne && !Falling) { airPace = 1f; airSprint = false; }
        }
    }
}
