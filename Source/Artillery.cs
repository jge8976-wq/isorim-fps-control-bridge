using System.Collections.Generic;
using HSK.FpsBridge.Interop;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    // 0.8.0 artillery turrets (mortars): the artillery map looks down on the ground, the mouse moves the impact point
    // (screen centre) and the wheel the height; Tab switches to the over-the-shoulder view behind the barrel, where the
    // mouse turns and the wheel sets the elevation. Arc, spread and flight time are the game's own numbers; holding Alt
    // over a moving pawn shows where its path takes it and a lead box where it will be when the shell lands.
    internal static class Artillery
    {
        private enum View { Map, Shoulder }
        private const float MapPitch = 72f, MinElevation = 45f, MaxElevation = 85f;
        private static IArtilleryTurret gun;
        private static Pawn owner;
        private static View view;
        private static float mapX, mapZ, groundY, height = 28f, elevation = 60f;
        private static int aimFrame = -1, leadFrame = -1;
        private static IntVec3 aimCell = IntVec3.Invalid;
        private static Pawn leadPawn;
        private static Vector3 leadPoint;
        private static float leadTicks;
        private static readonly List<Vector3> leadPath = new List<Vector3>(), arc = new List<Vector3>();

        public static bool On => gun != null && TurretControl.Current == gun;
        public static bool MapView => On && view == View.Map;

        public static void Begin(IArtilleryTurret t, Pawn pawn)
        {
            gun = t; owner = pawn; view = View.Map; aimFrame = leadFrame = -1;
            V3 s = FloorSpace.Spot(t.Thing);
            groundY = s.Y;
            float a = t.Rotation * Mathf.Deg2Rad, d = Mathf.Clamp(t.MinRange + 8f, 12f, 40f);
            mapX = s.X + Mathf.Sin(a) * d; mapZ = s.Z + Mathf.Cos(a) * d;
            height = 28f; elevation = 60f;
            ClampMap(); FixMapLook();
            if (!t.Loaded) t.Reload(pawn);
        }

        public static void End() { gun = null; owner = null; leadPawn = null; leadPath.Clear(); aimCell = IntVec3.Invalid; }

        private static void FixMapLook() => FirstPerson.Look(Mathf.DeltaAngle(FirstPerson.Yaw, 0f), MapPitch - FirstPerson.Pitch);

        private static Map Map => gun.Thing.Map;
        private static Vector3 Base { get { V3 s = FloorSpace.Spot(gun.Thing); return new Vector3(s.X, s.Y, s.Z); } }
        private static Vector3 Flat(float angle) => Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
        private static float MaxReach => Mathf.Min(gun.Range, new Vector2(Map.Size.x, FloorSpace.Bands(Map)[0].Rows).magnitude);

        private static void ClampMap()
        {
            mapX = Mathf.Clamp(mapX, .5f, Map.Size.x - .5f);
            mapZ = Mathf.Clamp(mapZ, .5f, FloorSpace.Bands(Map)[0].Rows - .5f);
        }

        // ---- view

        public static Vector3 Eye()
        {
            if (view == View.Map)
            {
                Vector3 f = Quaternion.Euler(MapPitch, 0f, 0f) * Vector3.forward;
                return new Vector3(mapX, groundY, mapZ) - f * (height / Mathf.Sin(MapPitch * Mathf.Deg2Rad));
            }
            float r = gun.Rotation;
            return Base + Vector3.up * 2.3f - Flat(r) * 3.6f + Quaternion.Euler(0f, r, 0f) * Vector3.right * .9f;
        }

        // Mouse movement (sensitivity applied). On the map it moves the impact point; true when used here.
        public static bool Look(float dx, float dy)
        {
            if (!MapView) return false;
            float k = height * .03f;
            mapX += dx * k; mapZ += dy * k;
            ClampMap(); FixMapLook();
            return true;
        }

        // The top's wanted heading on the map: toward the impact point.
        public static float AimYaw()
        {
            Vector3 d = new Vector3(mapX, 0f, mapZ) - Base;
            return d.sqrMagnitude < .01f ? gun.Rotation : Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        public static void Wheel(float delta)
        {
            if (!On || Mathf.Abs(delta) < .01f) return;
            if (view == View.Map) height = Mathf.Clamp(height * (delta > 0f ? .88f : 1.14f), 10f, 90f);
            else
            {
                bool fine = ControlKeys.Held(ControlAction.Sprint);
                elevation = Mathf.Clamp(elevation + Mathf.Sign(delta) * (fine ? .2f : 1f), MinElevation, MaxElevation);
            }
            aimFrame = -1;
        }

        public static void ToggleView()
        {
            if (!On) return;
            IntVec3 c = AimCell();
            if (view == View.Map)
            {
                view = View.Shoulder;
                float dist = c.IsValid ? (c.ToVector3Shifted() - gun.Thing.DrawPos).MagnitudeHorizontal() : MaxReach * .5f;
                // The high-angle solution for that distance (range = reach · sin 2θ, θ above 45°).
                elevation = Mathf.Clamp(90f - .5f * Mathf.Asin(Mathf.Clamp01(dist / MaxReach)) * Mathf.Rad2Deg, MinElevation, MaxElevation);
                FirstPerson.Look(Mathf.DeltaAngle(FirstPerson.Yaw, gun.Rotation), 12f - FirstPerson.Pitch);
            }
            else
            {
                view = View.Map;
                if (c.IsValid) { mapX = c.x + .5f; mapZ = c.z + .5f - FloorSpace.Band(Map, c).Shift; }
                ClampMap(); FixMapLook();
            }
            aimFrame = -1;
            Bridge.SyncView();
        }

        // ---- aim

        private static float ShoulderRange => MaxReach * Mathf.Sin(2f * elevation * Mathf.Deg2Rad);

        public static IntVec3 AimCell()
        {
            if (aimFrame == Time.frameCount) return aimCell;
            aimFrame = Time.frameCount;
            Vector3 p = view == View.Map ? new Vector3(mapX, groundY, mapZ) : Base + Flat(gun.Rotation) * ShoulderRange;
            IntVec3 c = FloorSpace.Cell(Map, new V3(p.x, groundY + .01f, p.z));
            aimCell = c.InBounds(Map) ? c : IntVec3.Invalid;
            return aimCell;
        }

        public static LocalTargetInfo Target() { IntVec3 c = AimCell(); return c.IsValid ? new LocalTargetInfo(c) : LocalTargetInfo.Invalid; }

        // After the trigger: an empty gun reloads by itself; with no shell within reach the operator steps off.
        public static void AfterTrigger(Pawn pawn)
        {
            if (!On || gun.Loaded || gun.Reloading) return;
            string why = gun.Reload(pawn);
            if (why != null) TurretControl.Leave(L.T("炮弹用完：", "Out of shells: ") + why + L.T(" 回到第一人称", " · back to first person"));
        }

        public static void ReloadKey(Pawn pawn)
        {
            if (!On) return;
            if (gun.Loaded) { Messages.Message(L.T("已经装填。", "Already loaded."), MessageTypeDefOf.RejectInput, false); return; }
            string why = gun.Reload(pawn);
            if (why != null) Messages.Message(why, MessageTypeDefOf.RejectInput, false);
        }

        public static void CycleFireMode()
        {
            if (!On || !(gun is ISelectableFireMode f) || f.FireModes.Count < 2) return;
            int i = 0;
            for (int k = 0; k < f.FireModes.Count; k++) if (f.FireModes[k] == f.FireMode) i = k;
            f.FireMode = f.FireModes[(i + 1) % f.FireModes.Count];
            Messages.Message(L.T("射击模式：", "Fire mode: ") + ModeLabel(f.FireMode), MessageTypeDefOf.SilentInput, false);
        }

        private static string ModeLabel(TurretFireMode m) => m == TurretFireMode.Single ? L.T("单发", "Single") : m == TurretFireMode.Burst3 ? L.T("三连发", "Burst x3") : L.T("全自动", "Full auto");

        // ---- Alt: lead

        private static void Lead()
        {
            if (leadFrame == Time.frameCount) return;
            leadFrame = Time.frameCount;
            leadPawn = null; leadPath.Clear();
            IntVec3 c = AimCell();
            if (!c.IsValid || !(ControlKeys.Held(ControlAction.Overlay))) return;
            Pawn best = null; float bestD = 25f;
            foreach (Pawn p in Map.mapPawns.AllPawnsSpawned)
            {
                if (p == owner || p.Dead || p.pather == null || !p.pather.Moving || p.pather.curPath == null) continue;
                float d = (p.Position - c).LengthHorizontalSquared - (p.HostileTo(Faction.OfPlayer) ? 4f : 0f);
                if (d < bestD) { best = p; bestD = d; }
            }
            if (best == null) return;
            // Converge on the flight time to where the pawn will be, not where it is.
            float ticks = gun.FlightTicks(best.Position);
            if (ticks < 0f) return;
            Vector3 at = Predict(best, ticks, null);
            for (int i = 0; i < 3; i++)
            {
                IntVec3 cell = FloorSpace.Cell(Map, new V3(at.x, groundY + .01f, at.z));
                float t2 = gun.FlightTicks(cell.InBounds(Map) ? cell : best.Position);
                if (t2 < 0f) return;
                ticks = t2; at = Predict(best, ticks, null);
            }
            Predict(best, ticks * 1.6f + 60f, leadPath);
            leadPawn = best; leadPoint = at; leadTicks = ticks;
        }

        // Where the pawn is after the given game ticks along its current path (ground space); fills the path walked.
        private static Vector3 Predict(Pawn p, float ticks, List<Vector3> path)
        {
            V3 s = FloorSpace.Spot(p);
            Vector3 pos = new Vector3(s.X, groundY + .05f, s.Z);
            float shift = s.Z - p.DrawPos.z;
            path?.Add(pos);
            Pawn_PathFollower f = p.pather;
            float left = ticks;
            Vector3 Ground(IntVec3 c) => new Vector3(c.x + .5f, groundY + .05f, c.z + .5f + shift);
            IntVec3 prev = p.Position;
            // The step under way, then the remaining nodes at the pawn's own pace.
            var steps = new List<IntVec3> { f.nextCell };
            PawnPath route = f.curPath;
            for (int i = 0; i < route.NodesLeftCount && steps.Count < 200; i++) steps.Add(route.Peek(i));
            for (int i = 0; i < steps.Count; i++)
            {
                IntVec3 next = steps[i];
                if (next == prev && i > 0) continue;
                float cost = i == 0 ? Mathf.Max(1f, f.nextCellCostLeft)
                    : (next.x != prev.x && next.z != prev.z ? p.TicksPerMoveDiagonal : p.TicksPerMoveCardinal);
                Vector3 target = Ground(next);
                if (cost >= left)
                {
                    pos = Vector3.Lerp(pos, target, left / cost);
                    path?.Add(pos);
                    return pos;
                }
                left -= cost; pos = target; prev = next;
                path?.Add(pos);
            }
            return pos;
        }

        // ---- drawing

        public static void Draw3D(Pawn pawn)
        {
            if (!On) return;
            IntVec3 c = AimCell();
            if (!c.IsValid) return;
            Vector3 from = Base + Vector3.up * 1f;
            V3 g = new V3(c.x + .5f, groundY, c.z + .5f - FloorSpace.Band(Map, c).Shift);
            Vector3 land = new Vector3(g.X, groundY + .02f, g.Z);
            float dist = (land - from).MagnitudeHorizontal();
            bool inRange = dist >= gun.MinRange && dist <= gun.Range;
            float rise = Mathf.Clamp(dist * .3f, 2f, 30f);
            arc.Clear();
            for (int i = 0; i <= 32; i++)
            {
                float t = i / 32f;
                arc.Add(Vector3.Lerp(from, land, t) + Vector3.up * rise * 4f * t * (1f - t));
            }
            Color line = inRange ? new Color(1f, 1f, 1f, .7f) : new Color(1f, .35f, .3f, .7f);
            Visuals.Polyline(arc, line, .04f);
            float spread = gun.Spread(c, owner);
            Visuals.GroundRing(land, Mathf.Max(.4f, spread), new Color(1f, .75f, .25f, .85f));
            if (gun.BlastRadius > 0f) Visuals.GroundRing(land, gun.BlastRadius, new Color(1f, .3f, .2f, .6f));
            Visuals.GroundRing(land, .25f, Color.white);
            Lead();
            if (leadPawn != null)
            {
                Visuals.Polyline(leadPath, new Color(.4f, .85f, 1f, .8f), .05f);
                Visuals.GroundSquare(leadPoint, .7f, new Color(.4f, 1f, .5f, .95f));
            }
        }

        public static void DrawHud(Pawn pawn)
        {
            if (!On) return;
            TurretControl.DrawWeaponCard();
            float x = UI.screenWidth * .5f, y = UI.screenHeight * .5f;
            IntVec3 c = AimCell();
            Thing thing = gun.Thing;
            float dist = c.IsValid ? (c.ToVector3Shifted() - thing.DrawPos).MagnitudeHorizontal() : -1f;
            bool inRange = dist >= gun.MinRange && dist <= gun.Range;
            Color cc = !c.IsValid ? new Color(1f, 1f, 1f, .6f) : inRange ? new Color(.55f, 1f, .6f) : new Color(1f, .35f, .3f);
            Widgets.DrawBoxSolid(new Rect(x - 12, y, 9, 1), cc); Widgets.DrawBoxSolid(new Rect(x + 4, y, 9, 1), cc);
            Widgets.DrawBoxSolid(new Rect(x, y - 12, 1, 9), cc); Widgets.DrawBoxSolid(new Rect(x, y + 4, 1, 9), cc);

            float flight = c.IsValid ? gun.FlightTicks(c) : -1f;
            string mode = view == View.Map ? L.T("炮兵地图", "Artillery map") : L.T("第二人称 · 仰角 ", "Shoulder view · elevation ") + elevation.ToString("F1") + "°";
            bool modes = gun is ISelectableFireMode fm && fm.FireModes.Count > 1;
            if (modes) mode += " · " + ModeLabel(((ISelectableFireMode)gun).FireMode);
            string line1 = mode;
            string line2 = (dist >= 0f ? L.T("距离 ", "Distance ") + dist.ToString("F1") : L.T("距离 —", "Distance —")) + L.T(" / 射程 ", " / range ") + gun.MinRange.ToString("F0") + "–" + gun.Range.ToString("F0")
                + L.T(" · 飞行 ", " · flight ") + (flight >= 0f ? (flight / 60f).ToString("F1") + L.T(" 秒", " s") : "—")
                + L.T(" · 散布 ±", " · spread ±") + (c.IsValid ? gun.Spread(c, owner).ToString("F1") : "—") + L.T(" 格", " cells")
                + (gun.BlastRadius > 0f ? L.T(" · 爆炸半径 ", " · blast radius ") + gun.BlastRadius.ToString("F1") : "");
            string line4 = L.T("左键开火", "LMB fire") + (modes ? L.T(" · B 单发/三连发/全自动", " · B single/burst/auto") : L.T(" · R 装填", " · R reload")) + " · Tab " + (view == View.Map ? L.T("第二人称", "shoulder view") : L.T("炮兵地图", "Artillery map")) + L.T(" · 滚轮 ", " · wheel ") + (view == View.Map ? L.T("缩放", "zoom") : L.T("仰角（Shift 微调）", "elevation (Shift: fine)"))
                + L.T(" · 按住 Alt 提前量 · Q/E 切换炮台 · F 离开", " · hold Alt for lead · Q/E switch turret · F leave");
            float w = 640f, top = UI.screenHeight - 176f;
            Widgets.DrawBoxSolid(new Rect(x - w / 2, top, w, 68), CombatHudCard.Background);
            Label(new Rect(x - w / 2, top + 3, w, 22), line1, Color.white, GameFont.Small);
            Label(new Rect(x - w / 2, top + 26, w, 20), line2, new Color(.88f, .88f, .88f), GameFont.Tiny);
            Label(new Rect(x - w / 2, top + 45, w, 20), ControlHints.For(line4), new Color(.7f, .7f, .7f), GameFont.Tiny);
            (gun as VanillaTurret)?.DrawHeat(x, y + 40f);
            if (leadPawn != null)
                Label(new Rect(x - 220, y + 18, 440, 22), L.T("提前量：", "Lead: ") + leadPawn.LabelShort + L.T(" · 着弹时 ", " · impact in ") + (leadTicks / 60f).ToString("F1")
                    + L.T(" 秒后在绿框处", " s at the green box"), new Color(.5f, 1f, .6f), GameFont.Tiny);
        }

        private static void Label(Rect r, string text, Color color, GameFont font)
        {
            GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
            Text.Font = font; Text.Anchor = TextAnchor.UpperCenter; GUI.color = color;
            Widgets.Label(r, text);
            Text.Font = f0; Text.Anchor = a0; GUI.color = c0;
        }
    }
}
