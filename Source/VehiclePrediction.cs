using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Same initial speed, gravity, semi-implicit tick and collision walker as AdoptVehicleRound.
    // This marks the first contact. Bullet penetration and moving targets can change the eventual result.
    internal static class VehiclePrediction
    {
        private static readonly List<V3> path = new List<V3>(256);
        private static int frame=-1;
        internal static int Recomputations;
        private static object gun;
        internal static bool Contact { get; private set; }
        internal static V3 Start { get; private set; }
        internal static V3 Direction { get; private set; }
        internal static V3 Impact { get; private set; }
        internal static IntVec3 ImpactCell { get; private set; } = IntVec3.Invalid;
        internal static float Radius { get; private set; }
        internal static float Seconds { get; private set; }
        internal static float Range { get; private set; }
        internal static Color Tint => new Color(.4f, 1f, .65f, .85f);
        internal static bool Visible => VehicleControl.On && VehicleWeapons.Current != null && Ballistics.Enabled;
        internal static void Clear() { Contact = false; ImpactCell = IntVec3.Invalid; path.Clear(); gun = null; frame = -1; }
        internal static void Update()
        {
            if (!Visible)
            { Clear(); return; }
            object turret = VehicleWeapons.Current;
            if (gun == turret && frame == Time.frameCount) return;
            frame = Time.frameCount; Recomputations++; gun = turret; path.Clear(); Contact = false; ImpactCell = IntVec3.Invalid;
            Pawn vehicle = VehicleControl.Vehicle;
            Map map = vehicle.Map;
            ThingDef projectile = VehicleWeapons.ProjectileFor(turret);
            // Arc dispatchers have no flight and immediately create their native
            // payload fan. The sight previews that fan's central payload path.
            if (CeCompat.ArcPayload(projectile, out ThingDef payload)) projectile = payload;
            if (map == null || projectile?.projectile == null || typeof(RimWorld.Beam).IsAssignableFrom(projectile.thingClass)
                || VehicleProjectileRules.NativeAreaFlight(projectile.thingClass)
                || (AerialArmament.Bomb(turret) && !AerialFlight.Airborne(vehicle))) return;
            V3 a = Start = VehicleWeapons.FlightMuzzle(vehicle, turret);
            Direction = VehicleWeapons.Direction(vehicle, turret);
            float originalSpeed = VehicleWeapons.NativeSpeed(turret, projectile);
            float speed = ViewRules.Speed(originalSpeed, FpsMod.Settings.speed);
            V3 velocity = AerialArmament.ReleaseVelocity(vehicle, turret, Direction, speed);
            if (CeCompat.NativeProjectile(projectile) && !AerialArmament.Bomb(turret)) velocity = CeCompat.NativeShotVelocity(Direction, speed);
            var data=AerialArmament.Data(turret);
            Thing locked=data?.lockableMissile==true?AerialTargetLock.Capture(turret):null;
            float gravity = data?.lockableMissile==true?0:ViewRules.Gravity(FpsMod.Settings.gravity);
            CeCompat.NativeMotionPreview native = CeCompat.CreateNativeMotionPreview(vehicle, projectile, a, velocity, locked,
                AerialArmament.Bomb(turret) || data?.lockableMissile == true, (LocalTargetInfo)VehicleApi.Get(turret, "targetInfo"), originalSpeed);
            var trace = new Ballistics.ExternalTrace(vehicle);
            path.Add(a);
            for (int tick = 1; tick <= 2400; tick++)
            {
                if(data?.lockableMissile==true)
                    velocity=AerialTargetLock.Steer(locked,map,a,velocity,data.guidanceDegreesPerSecond * FpsMod.Settings.missileTurn);
                V3 b;
                if (native != null)
                {
                    b = native.Step(velocity);
                    velocity = native.Velocity;
                }
                else
                {
                    velocity.Y -= gravity;
                    b = a + velocity;
                }
                if (trace.Segment(map, a, b, out float fraction, out Thing target, out _, out _, out _))
                {
                    V3 hit = V3.Lerp(a, b, fraction);
                    path.Add(hit); Impact = hit; Seconds = tick / 60f; Range = (hit - Start).Length;
                    ImpactCell = FloorSpace.ContactCell(map, hit, velocity.Y, target);
                    Radius = Math.Max(.35f, CeCompat.VehicleBlastRadius(projectile));
                    Contact = true; break;
                }
                if (!FloorSpace.InWorld(map, b)) { path.Add(b); break; }
                if (tick % 2 == 0) path.Add(b);
                a = b;
            }
        }
        internal static bool Project(V3 world, out Vector2 screen)
        {
            Vector2 pixel = View3D.WorldToScreen(VehicleModels.U(world));
            screen = new Vector2(pixel.x / Screen.width * UI.screenWidth, (1f - pixel.y / Screen.height) * UI.screenHeight);
            return float.IsFinite(screen.x) && float.IsFinite(screen.y);
        }
        internal static bool Segment(V3 a,V3 b,out Vector2 p,out Vector2 q)
        {
            Vector3 forward = Quaternion.Euler(FirstPersonTransition.RenderPitch(), FirstPersonTransition.RenderYaw(), 0f) * Vector3.forward;
            Vector3 eye = View3D.EyePosition;
            float az = Vector3.Dot(VehicleModels.U(a) - eye, forward), bz = Vector3.Dot(VehicleModels.U(b) - eye, forward);
            p=q=default;if(az<.26f&&bz<.26f)return false;
            if(az<.26f)a=V3.Lerp(a,b,(.26f-az)/(bz-az));
            else if(bz<.26f)b=V3.Lerp(a,b,(.26f-az)/(bz-az));
            return Project(a,out p)&&Project(b,out q);
        }
        internal static void Draw()
        {
            if (!VehicleControl.Scoped && !AerialFlight.Supports(VehicleControl.Vehicle)) return;
            Color old = GUI.color; GameFont font = Text.Font; TextAnchor anchor = Text.Anchor;
            try
            {
                GUI.color = Color.white;
                for (int i = 1; i < path.Count; i++)
                    if (Segment(path[i - 1],path[i],out Vector2 a,out Vector2 b)) Widgets.DrawLine(a, b, Tint, 1.5f);
                if (Contact)
                {
                    const int segments = 40;
                    for (int i = 0; i < segments; i++)
                    {
                        float a = i * Mathf.PI * 2f / segments, b = (i + 1) * Mathf.PI * 2f / segments;
                        if (Segment(Impact + new V3(Mathf.Cos(a) * Radius, .035f, Mathf.Sin(a) * Radius),
                            Impact + new V3(Mathf.Cos(b) * Radius, .035f, Mathf.Sin(b) * Radius),out Vector2 p,out Vector2 q)) Widgets.DrawLine(p, q, Tint, 2f);
                    }
                }
                Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperCenter; GUI.color = Tint;
                Widgets.Label(new Rect(UI.screenWidth * .5f - 230f, UI.screenHeight * .5f + 145f, 460f, 40f),
                    Contact ? L.T("弹道计算 · 首次接触 ", "Ballistic computer · first contact ") + Range.ToString("0.0") + "m · " + Seconds.ToString("0.00") + "s"
                    : L.T("弹道计算 · 弹道离开地图 / 无落点", "Ballistic computer · trajectory exits map / no contact"));
            }
            finally { GUI.color = old; Text.Font = font; Text.Anchor = anchor; }
        }
        internal static bool TryMinimapPosition(Rect area, bool wholeMap, out Vector2 center)
        {
            center = default;
            return Visible && Contact && ImpactMinimap.TryPosition(VehicleControl.Vehicle,
                new ImpactMinimap.Impact(Impact, ImpactCell, Radius), area, wholeMap, out center);
        }
        internal static void DrawMinimap(Rect area, bool wholeMap)
        {
            if (Visible && Contact)
                ImpactMinimap.DrawMarker(VehicleControl.Vehicle, new ImpactMinimap.Impact(Impact, ImpactCell, Radius), area, wholeMap);
        }
    }
}
