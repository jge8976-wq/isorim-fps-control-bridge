using System.Linq;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace HSK.FpsBridge
{
    // Walking on tops (设计-墙顶与屋顶行走.md): walls, rock, impassable things and roofs. The possessed pawn gets up by
    // climbing (Vault: Space, Space again at the wall); WASD walks across tops of about the same height or down onto
    // lower ones; walking off an edge drops to the ground; walking into a ProjectZ floor at about the top's height
    // steps onto that floor, and Space at an upper floor's edge drops onto a top below. The session ending (save,
    // downed, leaving) or the game's own "unwalkable cell" recovery set the pawn back on the nearest free ground.
    // While on top the pawn's cell is the top's own cell (a wall cell for a wall) and Perspective Shift's physics is
    // held off. SURFACE_STATUS lines log how hostiles see the pawn up there.
    internal static class Surface
    {
        public const float StepTolerance = .35f;   // tops within this height of each other join up (true cells)
        private static Pawn on;
        private static Thing furniture;
        private static object avatarOf;
        private static IntVec3 cell;
        private static float height, nextLog;
        private static Vector3 pos;
        private static int hediffs;

        public static bool For(Pawn pawn) => on != null && pawn == on;
        // Added to the eye, the figure and the hit volumes (true cells over the ground).
        public static float HeightOf(Pawn pawn) => For(pawn) ? height : 0f;
        public static float Height => on != null ? height : 0f;

        public static void Install(Harmony harmony)
        {
            SurfaceRegions.Install(harmony);
            var recover = AccessTools.Method(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.TryRecoverFromUnwalkablePosition));
            if (recover != null) harmony.Patch(recover, prefix: new HarmonyMethod(typeof(Surface), nameof(BeforeRecover)) { priority = Priority.First });
            else Log.Warning("[FpsBridge] Pawn_PathFollower.TryRecoverFromUnwalkablePosition not found: a pather start on a top is not caught.");
            // IsoRim opens the roofs of rooms under the pointer and under colonists; from a top they stay on.
            var room = AccessTools.Method(typeof(Cutaway), "AddRoom", new[] { typeof(Map), typeof(IntVec3) });
            if (room != null) harmony.Patch(room, prefix: new HarmonyMethod(typeof(Surface), nameof(BeforeOpenRoof)));
            else Log.Warning("[FpsBridge] IsoRim Cutaway.AddRoom not found: roofs may open under a pawn standing on them.");
            // Up on a top nothing stands between the pawn and a shooter: no cover (the game's hit chances and the AI's
            // own reading of cover both come from these two).
            var coverSet = AccessTools.Method(typeof(CoverUtility), nameof(CoverUtility.CalculateCoverGiverSet));
            var coverChance = AccessTools.Method(typeof(CoverUtility), nameof(CoverUtility.CalculateOverallBlockChance));
            if (coverSet != null && coverChance != null)
            {
                harmony.Patch(coverSet, prefix: new HarmonyMethod(typeof(Surface), nameof(NoCoverSet)));
                harmony.Patch(coverChance, prefix: new HarmonyMethod(typeof(Surface), nameof(NoCoverChance)));
            }
            else Log.Warning("[FpsBridge] CoverUtility changed: a pawn on a top keeps the game's cover.");
            // Up on a roof the pawn is not in the room under it: area triggers (ancient ruins, sleeping threats) do not
            // fire for it, and nobody under a roof can see or reach it (melee never reaches a top).
            var trigger = AccessTools.Method(typeof(PawnTrigger), "TriggeredBy", new[] { typeof(Thing) });
            if (trigger != null) harmony.Patch(trigger, postfix: new HarmonyMethod(typeof(Surface), nameof(AfterTriggeredBy)));
            else Log.Warning("[FpsBridge] PawnTrigger.TriggeredBy not found: area triggers fire for a pawn on a roof.");
            var line = AccessTools.Method(typeof(Verb), nameof(Verb.TryFindShootLineFromTo));
            if (line != null) harmony.Patch(line, postfix: new HarmonyMethod(typeof(Surface), nameof(AfterShootLine)));
            else Log.Warning("[FpsBridge] Verb.TryFindShootLineFromTo not found: pawns under a roof can shoot a pawn on it.");
            WakeHooks(harmony, typeof(CompWakeUpDormant), "TickRareWorker", "Activate", nameof(BeforeWake));
            WakeHooks(harmony, typeof(CompSendSignalOnMotion), "CompTickRare", "Trigger", nameof(BeforeMotionTrigger));
        }

        private static void WakeHooks(Harmony harmony, System.Type comp, string check, string fire, string gate)
        {
            var checker = AccessTools.Method(comp, check);
            var firer = AccessTools.Method(comp, fire);
            if (checker == null || firer == null) { Log.Warning("[FpsBridge] " + comp.Name + " changed: it wakes for a pawn on a top as on the ground."); return; }
            harmony.Patch(checker, prefix: new HarmonyMethod(typeof(Surface), nameof(BeforeWakeCheck)), finalizer: new HarmonyMethod(typeof(Surface), nameof(AfterWakeCheck)));
            harmony.Patch(firer, prefix: new HarmonyMethod(typeof(Surface), gate));
        }

        private static bool Up(Thing t) => on != null && t == on && height > .5f;

        public static void AfterTriggeredBy(Thing thing, ref bool __result)
        {
            if (__result && Up(thing)) __result = false;
        }

        public static void AfterShootLine(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (!__result || !Up(targ.Thing) || __instance.caster == on) return;
            Map map = on.Map;
            if (map == null || !root.InBounds(map)) return;
            if (__instance.verbProps.IsMeleeAttack || __instance.EffectiveRange <= 1.42f) { __result = false; return; }
            // Sight in height: a roof overhead, a ridge or a wall between the shooter's eye and the pawn on its top.
            if (FloorSpace.Band(map, root).Level != 0) return;
            GroundView(out Vector3 at, out float top);
            if (!Sight3D.SeesOnTop(map, Sight3D.EyeOf(__instance.caster, root), on, at, top)) __result = false;
        }

        // An eye (a dormant mech, a motion sensor) sees the pawn on its top: the 3D sight line.
        private static bool SeenBy(Thing watcher)
        {
            if (watcher?.Map != on.Map || FloorSpace.Band(on.Map, watcher.Position).Level != 0) return true;
            GroundView(out Vector3 at, out float top);
            return Sight3D.SeesOnTop(on.Map, Sight3D.EyeOf(watcher, watcher.Position), on, at, top);
        }

        // Dormant threats wake when a player pawn comes into their radius and flat line of sight; a pawn on a top
        // wakes them only when they could really see it. Waking by damage or signals is left alone.
        private static Thing proximityWatcher;
        public static void BeforeWakeCheck(ThingComp __instance) => proximityWatcher = __instance.parent;
        public static System.Exception AfterWakeCheck(System.Exception __exception) { proximityWatcher = null; return __exception; }
        public static bool BeforeWake(Thing waker) => proximityWatcher == null || !Up(waker) || SeenBy(proximityWatcher);
        public static bool BeforeMotionTrigger(Thing initiator) => proximityWatcher == null || !Up(initiator) || SeenBy(proximityWatcher);

        public static bool NoCoverSet(LocalTargetInfo target, ref List<CoverInfo> __result)
        {
            if (on == null || furniture != null || target.Thing != on) return true;
            __result = new List<CoverInfo>();
            return false;
        }

        public static bool NoCoverChance(LocalTargetInfo target, ref float __result)
        {
            if (on == null || furniture != null || target.Thing != on) return true;
            __result = 0f;
            return false;
        }

        public static bool BeforeOpenRoof(Map map) => on == null || furniture != null || on.Map != map || on != Bridge.ShootingPawn;

        // ProjectZ draws the floors above the viewed one only when asked: on a top (about a floor up) the floor above
        // c's own is shown, back down on a floor that floor again.
        private static void View(Map map, IntVec3 c, bool up)
        {
            if (Api.Floors == null || map == null || map != Find.CurrentMap || !c.IsValid) return;
            if (up && !FloorSpace.CellAbove(map, c).IsValid) return;
            int level = FloorSpace.Band(map, c).Level;
            Api.Floors.ViewFloor(map, up ? level + 1 : level);
        }

        // The same column on another floor (ProjectZ stacks floors as bands of rows further north), Invalid if none.
        private static IntVec3 Column(Map map, IntVec3 c, int level)
        {
            FloorBand from = FloorSpace.Band(map, c);
            foreach (FloorBand b in FloorSpace.Bands(map))
                if (b.Level == level)
                {
                    IntVec3 r = new IntVec3(c.x, 0, c.z - from.Shift + b.Shift);
                    return r.InBounds(map) && FloorSpace.Band(map, r).Level == level ? r : IntVec3.Invalid;
                }
            return IntVec3.Invalid;
        }

        private static float FloorHeight(Map map, int level)
        {
            foreach (FloorBand b in FloorSpace.Bands(map)) if (b.Level == level) return b.Height;
            return 0f;
        }

        private static bool SameFloor(Map map, IntVec3 a, IntVec3 b) => FloorSpace.Band(map, a).Level == FloorSpace.Band(map, b).Level;

        // The pawn on its top as the ground floor's sight sees it: its column on the ground rows, the top's height over
        // the ground (an upper floor's height added).
        private static void GroundView(out Vector3 at, out float top)
        {
            FloorBand band = FloorSpace.Band(on.Map, cell);
            at = new Vector3(pos.x, 0f, pos.z - band.Shift);
            top = band.Height + height;
        }

        // Every frame: a pawn left on a top after its session ended (back to the command view, another pawn taken)
        // stays up there until the game moves it, it goes down or it leaves the map.
        public static void Frame(Pawn possessed)
        {
            if(on!=null && furniture!=null && !(ModelSurfaces.Ratkin(on)?ModelSurfaces.Valid(furniture):FurnitureSurfaces.IsSeat(furniture))){Drop("support removed");return;}
            if (on == null || on == possessed) return;
            if (!on.Spawned || on.Map == null || on.Dead) { Release(); return; }
            if (on.Downed) { Drop("downed while parked"); return; }
            if (on.Position != cell) { Log.Message("[FpsBridge] SURFACE_LEAVE reason=moved while parked; from=" + cell + "; to=" + on.Position); Release(); }
        }

        // The top of a cell on any floor (true cells over that cell's own floor), or -1 where there is none to stand on.
        public static float TopAt(Map map, IntVec3 c, out string what)
        {
            what = null;
            if (!c.InBounds(map)) return -1f;
            // A roof IsoRim draws (pitched or flat, over rooms and the walls under its eaves): its outer surface.
            if (RoofShape.Covers(map, c)) { what = L.T("屋顶", "roof"); return RoofShape.HeightAt(map, c.x + .5f, c.z + .5f); }
            float wall = View3D.WallHeight;
            Building e = c.GetEdifice(map);
            if (e != null && !(e is Building_Door) && e.def.passability == Traversability.Impassable)
            {
                what = e.LabelShort;
                return e.def.building?.isNaturalRock == true || e.def.fillPercent >= .99f ? wall : wall * Mathf.Clamp(e.def.fillPercent, .3f, 1f);
            }
            List<Thing> things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t is Pawn || t.def.passability != Traversability.Impassable) continue;
                what = t.LabelShort;
                return wall * Mathf.Clamp(t.def.fillPercent, .3f, 1f);
            }
            RoofDef roof = map.roofGrid.RoofAt(c);
            // ProjectZ's own roofs are the floor of the level above: walked as that floor instead.
            if (roof != null && !(roof.defName != null && roof.defName.StartsWith("PZ_"))) { what = L.T("屋顶", "roof"); return wall; }
            return -1f;
        }

        // The top under a point of cell c: a drawn roof follows its slope, anything else is level.
        private static float TopAtPoint(Map map, IntVec3 c, Vector3 p, out string what)
        {
            if(furniture!=null)
            {
                if(ModelSurfaces.Ratkin(on) && ModelSurfaces.At(map,c,p,out Thing modelSupport,out float modelTop))
                {what=modelSupport.LabelShort;return modelTop;}
                Thing seat=FurnitureSurfaces.At(map,c);
                if(!ModelSurfaces.Ratkin(on) && FurnitureSurfaces.Covers(seat,p)){what=seat.LabelShort;return FurnitureSurfaces.SeatHeight;}
                if(c.Standable(map)){what=null;return -1f;}
            }
            float h = TopAt(map, c, out what);
            return h >= 0f && RoofShape.Covers(map, c) ? RoofShape.HeightAt(map, p.x, p.z) : h;
        }

        // A ProjectZ floor over this ground column the pawn can stand on, with its height over the ground.
        private static bool FloorOver(Map map, IntVec3 c, out IntVec3 up, out float h)
        {
            up = FloorSpace.CellAbove(map, c); h = 0f;
            if (!up.IsValid || !FloorSpace.HasFloorAt(map, up) || Vault.Solid(up, map)) return false;
            h = FloorSpace.Band(map, up).Height - FloorSpace.Band(map, c).Height;
            return true;
        }

        // Onto a top (from the climb).
        public static void Enter(Pawn pawn, object avatar, IntVec3 c, float h, string what, Thing support = null)
        {
            if (on != null && on != pawn) Drop("another pawn climbs");   // one pawn up at a time
            furniture=support;
            on = pawn; avatarOf = avatar; height = h;
            Place(pawn, avatar, c, c.ToVector3Shifted());
            hediffs = pawn.health.hediffSet.hediffs.Count;
            nextLog = 0f;
            View(pawn.Map, c, furniture == null);
            Messages.Message(L.T("爬上", "Climbed onto ") + what, MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] SURFACE_ENTER pawn=" + pawn.thingIDNumber + "; cell=" + c + "; top=" + what + "; height=" + h.ToString("0.00")
                + "; walkable=" + c.Walkable(pawn.Map) + "; roofed=" + c.Roofed(pawn.Map) + "; room=" + RoomText(pawn));
        }

        // Off the tops without moving (the caller puts the pawn somewhere).
        internal static void EnterFurniture(Pawn pawn,object avatar,Thing seat)
        {
            Enter(pawn,avatar,seat.Position,FurnitureSurfaces.SeatHeight,seat.LabelShort,seat);
        }
        internal static void EnterModel(Pawn pawn,object avatar,Thing support,Vector3 at,float top)
        {
            Enter(pawn,avatar,at.ToIntVec3(),top,support.LabelShort,support);
            Place(pawn,avatar,at.ToIntVec3(),at);
        }
        public static void Release() { on = null; furniture=null; avatarOf = null; height = 0f; flying = false; }

        // ---- leaping between tops: a jump from a top carries over gaps at the take-off height (the jump arc on top);
        // where it comes down is settled when the jump ends. In the air the pawn goes at least LongJump times its pace.
        public const float LongJump = 1.5f;
        private static bool flying;

        private static bool InAir(Pawn pawn) => Stances.For(pawn) && Stances.State.Airborne;

        // The jump is over: onto the top under the pawn, else down the column (upper floors) or onto the floor there.
        private static void Touchdown(Pawn pawn, object avatar)
        {
            flying = false;
            Map map = pawn.Map;
            float h = TopAtPoint(map, cell, pos, out string what);
            if (h >= 0f)
            {
                float drop = height - h;
                height = h;
                if (drop > .02f && Stances.For(pawn)) Stances.State.StartFall(drop);
                if (FpsMod.Settings.verboseLog) Log.Message("[FpsBridge] SURFACE_LEAP landed on " + what + " @" + cell + "; height=" + h.ToString("0.00"));
                return;
            }
            IntVec3 before = cell;
            if (FloorSpace.Band(map, cell).Level > 0 && !FloorSpace.HasFloorAt(map, cell))
            {
                if (DropDown(pawn, avatar, map, cell, pos) || cell != before) return;
            }
            else if (cell.Standable(map))
            {
                float fall = height;
                Release();
                Land(pawn, avatar, before, pos, fall);
                View(map, before, false);
                Log.Message("[FpsBridge] SURFACE_LEAVE reason=leap fell short; at=" + before + "; fall=" + fall.ToString("0.00"));
                return;
            }
            Drop("leap came down nowhere");
        }

        private static void Place(Pawn pawn, object avatar, IntVec3 c, Vector3 at)
        {
            pawn.pather?.StopDead();
            if (pawn.Position != c) { pawn.Position = c; pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: true); }
            if (pawn.pather != null) pawn.pather.nextCell = c;
            cell = c;
            pos = at; pos.y = pawn.def.Altitude;
            if (avatar != null) Bindings.PhysicsPosition.SetValue(avatar, (Vector3?)pos);
        }

        // Back down to the nearest free ground cell (the cell itself when it is one, e.g. under a roof), falling the
        // top's height. Safe to call at any time; no move while the game is not playing.
        public static void Drop(string reason)
        {
            if (on == null) return;
            Pawn pawn = on; object avatar = avatarOf; float h = height;
            Release();
            try
            {
                if (Current.ProgramState != ProgramState.Playing || !pawn.Spawned || pawn.Map == null) { Log.Message("[FpsBridge] SURFACE_LEAVE reason=" + reason + "; no move"); return; }
                Map map = pawn.Map;
                IntVec3 land = IntVec3.Invalid;
                // Free floor near by on the top's own floor, else on each floor below in turn.
                int own = FloorSpace.Band(map, cell).Level;
                for (int level = own; level >= 0 && !land.IsValid; level--)
                {
                    IntVec3 centre = level == own ? cell : Column(map, cell, level);
                    if (!centre.IsValid) continue;
                    for (int i = 0; i < 300 && i < GenRadial.RadialPattern.Length; i++)
                    {
                        IntVec3 c = centre + GenRadial.RadialPattern[i];
                        if (c.InBounds(map) && FloorSpace.Band(map, c).Level == level && c.Standable(map)) { land = c; break; }
                    }
                }
                if (land.IsValid) Land(pawn, avatar, land, land.ToVector3Shifted(), h + FloorHeight(map, own) - FloorSpace.Band(map, land).Height);
                if (pawn == Bridge.ShootingPawn) View(map, land, false);
                Log.Message("[FpsBridge] SURFACE_LEAVE reason=" + reason + "; from=" + cell + "; to=" + land);
            }
            catch (System.Exception e) { Log.Error("[FpsBridge] Surface drop failed: " + e); }
        }

        private static void Land(Pawn pawn, object avatar, IntVec3 c, Vector3 at, float fall)
        {
            pawn.pather?.StopDead();
            if (pawn.Position != c) { pawn.Position = c; pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: true); }
            if (pawn.pather != null) pawn.pather.nextCell = c;
            at.y = pawn.def.Altitude;
            if (avatar != null && ReferenceEquals(Bindings.Avatar, avatar)) Bindings.PhysicsPosition.SetValue(avatar, (Vector3?)at);
            if (fall > .02f && Stances.For(pawn)) Stances.State.StartFall(fall);
            // Two storeys or more (a high roof's ridge) hurt the legs, as a jump down two ProjectZ floors does.
            int storeys = Mathf.FloorToInt(fall / (View3D.WallHeight + .02f) + .001f);
            if (storeys >= 2 && !pawn.Dead)
            {
                ProjectZStairs.HurtLegs(pawn, storeys);
                Log.Message("[FpsBridge] SURFACE_FALL_HURT pawn=" + pawn.thingIDNumber + "; fall=" + fall.ToString("0.00") + "; storeys=" + storeys);
            }
            AimPicker.Reset();
        }

        // The game's pather is about to move a pawn off an unwalkable cell (a job wanted a path): our pawn is put
        // down properly first, so the game finds it on free ground and never teleports or removes it.
        public static void BeforeRecover(Pawn ___pawn)
        {
            if (For(___pawn)) Drop("vanilla pather recover (job=" + ___pawn.CurJobDef?.defName + ")");
        }

        // Instead of Perspective Shift's physics, once per frame while on a top.
        public static void Physics(Pawn pawn, object avatar, bool canDrive)
        {
            if (!pawn.Spawned || pawn.Downed || pawn.Map == null) { Drop("pawn not standing"); return; }
            if (pawn.Position != cell)
            {
                Log.Warning("[FpsBridge] SURFACE_EJECTED pawn moved by something else: " + cell + " -> " + pawn.Position + "; job=" + pawn.CurJobDef?.defName);
                Release();
                return;
            }
            if (!ReferenceEquals(avatarOf, avatar)) { avatarOf = avatar; View(pawn.Map, cell, furniture == null); }   // a new session on a pawn left up there
            Status(pawn);
            Bindings.PhysicsPosition.SetValue(avatar, (Vector3?)pos);
            if (flying && !InAir(pawn)) { Touchdown(pawn, avatar); if (!For(pawn)) return; }
            if (!canDrive || Vault.Active) { Bindings.ClearMovement(avatar); return; }
            if (furniture==null && BreakRoofFrame(pawn, avatar)) return;
            Bindings.AvatarInput.Invoke(avatar, null);   // Perspective Shift's keys; our postfix turns them to the view
            Vector3 move = (Vector3)Bindings.MoveField.GetValue(avatar);
            move.y = 0f;
            if (move.sqrMagnitude < .01f || Find.TickManager.Paused) return;
            float dt = Mathf.Min(Time.deltaTime, .1f) * Find.TickManager.TickRateMultiplier * CyberSkills.PlayerTime;
            float pace = Stances.For(pawn) ? Stances.State.SpeedFactor : 1f;
            if (InAir(pawn)) pace = Mathf.Max(pace, LongJump);   // a leap carries further than a step
            float speed = 60f / Mathf.Max(pawn.TicksPerMoveCardinal, 1f) * pace;
            Vector3 step = move.normalized * speed * dt;
            Map map = pawn.Map;
            if (!TryAxis(pawn, avatar, map, new Vector3(step.x, 0f, 0f))) return;
            if (!TryAxis(pawn, avatar, map, new Vector3(0f, 0f, step.z))) return;
            if (furniture==null && RoofShape.Covers(map, cell)) height = RoofShape.HeightAt(map, pos.x, pos.z);
        }

        // Attacks aimed down at a nearby roof share its persistent HP pool. Structural floors use ProjectZ integrity.
        private const float RoofReach = 1.6f, HitGap = .35f;
        private static float lastRoofHit;

        // True when the pawn fell through.
        private static bool BreakRoofFrame(Pawn pawn, object avatar)
        {
            if (!ControlKeys.Down(ControlAction.Fire) || Find.TickManager.Paused || Time.unscaledTime - lastRoofHit < HitGap) return false;
            V3 dir = HitMath.Direction(FirstPerson.Yaw, FirstPerson.Pitch);
            if (dir.Y > -.5f) return false;   // not looking down
            float standing = Stances.StandingEye(pawn);
            float eye = Stances.For(pawn) ? standing * Stances.State.Height + Stances.State.Lift : standing;
            float flat = Mathf.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
            float reach = eye * flat / -dir.Y;   // where the view meets the top's plane, flat distance
            if (reach > RoofReach) return false;
            Vector3 at = flat > 1e-4f ? pos + new Vector3(dir.X / flat, 0f, dir.Z / flat) * reach : pos;
            Map map = pawn.Map;
            IntVec3 c = at.ToIntVec3();
            RoofDef roof = c.InBounds(map) ? map.roofGrid.RoofAt(c) : null;
            if (roof == null || TopAt(map, c, out _) < 0f || (c.GetEdifice(map) is Building b && b.def.passability == Traversability.Impassable)) return false;
            if (!RoofWear.Breakable(roof))
            { Messages.Message(roof.isThickRoof ? L.T("厚岩顶打不穿。", "A thick rock roof cannot be broken.") : L.T("楼板打不穿。", "A floor slab cannot be broken."), MessageTypeDefOf.RejectInput, false); return false; }
            // Shots wear the roof where the round meets it (RoofWear); here only blows.
            if (!(pawn.equipment?.Primary == null || pawn.equipment.Primary.def.IsMeleeWeapon)) return false;
            lastRoofHit = Time.unscaledTime;
            DefDatabase<SoundDef>.GetNamedSilentFail("Pawn_Melee_Punch_HitBuilding_Generic")?.PlayOneShot(new TargetInfo(c,map));
            var tools=pawn.equipment?.Primary?.def.tools??pawn.def.tools;
            float damage=tools==null||tools.Count==0?8f:tools.Max(tool=>tool.power);
            // Damage owns the fall transition when the destroyed cell supports this pawn.
            return RoofWear.Damage(map,c,roof,damage)&&!For(pawn);
        }

        // The roof over c is gone. A pawn standing on it is left to the caller (blows) or dropped (rounds).
        internal static void Collapse(Map map, IntVec3 c, RoofDef roof, bool notify=true)
        {
            map.roofGrid.SetRoof(c, null);
            RoofShape.Forget();
            FleckMaker.ThrowDustPuffThick(c.ToVector3Shifted(), map, 2f, Color.gray);
            SoundDefOf.Building_Deconstructed?.PlayOneShot(new TargetInfo(c, map));
            Log.Message("[FpsBridge] ROOF_BROKEN cell=" + c + "; roof=" + roof.defName + "; under=" + (on != null && c == cell));
            if (notify&&(on == null || c != cell || !c.Standable(map))) Messages.Message(L.T("屋顶被打穿了", "The roof gave way"), MessageTypeDefOf.SilentInput, false);
        }

        internal static bool StandingOn(Map map, IntVec3 c) => on != null && furniture == null && on.Map == map && c == cell;

        // False when the pawn left the tops.
        private static bool TryAxis(Pawn pawn, object avatar, Map map, Vector3 delta)
        {
            if (Mathf.Abs(delta.x) + Mathf.Abs(delta.z) < 1e-5f) return true;
            Vector3 next = pos + delta;
            IntVec3 c = next.ToIntVec3();
            if (c == cell)
            {
                if(furniture!=null)
                {
                    if(ModelSurfaces.Ratkin(pawn))
                    {
                        if(ModelSurfaces.At(map,c,next,out Thing support,out float top))
                        {if(top>height+StepTolerance)return true;furniture=support;height=top;}
                        else if(c.Standable(map)){float fall=height;Release();Land(pawn,avatar,c,next,fall);View(map,c,false);return false;}
                        else {Drop("walked off model edge");return false;}
                    }
                    else if(!FurnitureSurfaces.Covers(furniture,next))
                    {float fall=height;Release();Land(pawn,avatar,c,next,fall);View(map,c,false);return false;}
                }
                pos = next; Bindings.PhysicsPosition.SetValue(avatar, (Vector3?)pos); return true;
            }
            if (!c.InBounds(map)) return true;
            if (furniture==null && FloorOver(map, c, out IntVec3 up, out float floorH))
            {
                // A ProjectZ floor ahead at about this height: step onto it (higher ones are climbed).
                if (floorH > height + StepTolerance) return true;
                if (height - floorH <= StepTolerance)
                {
                    float drop = height - floorH;
                    IntVec3 from = cell;
                    Release();
                    Land(pawn, avatar, up, new Vector3(next.x, 0f, next.z + (up.z - c.z)), drop);
                    Api.Floors?.ViewFloor(map, FloorSpace.Band(map, up).Level);
                    Log.Message("[FpsBridge] SURFACE_TO_FLOOR from=" + from + "; to=" + up + "; level=" + FloorSpace.Band(map, up).Level);
                    return false;
                }
            }
            float h = TopAtPoint(map, c, next, out string what);
            // Across one roof the surface is continuous: up a slope as well as down.
            bool slope = furniture==null && RoofShape.Covers(map, cell) && RoofShape.Covers(map, c);
            if (h >= 0f && (slope || h <= height + StepTolerance))
            {
                float drop = height - h;
                height = h;
                if(furniture!=null)
                {
                    if(ModelSurfaces.Ratkin(pawn))
                    {furniture=ModelSurfaces.At(map,c,next,out Thing support,out _)?support:null;}
                    else if(FurnitureSurfaces.At(map,c) is Thing seat && FurnitureSurfaces.Covers(seat,next))furniture=seat;
                }
                Place(pawn, avatar, c, next);
                if (drop > StepTolerance && Stances.For(pawn)) Stances.State.StartFall(drop);
                flying = false;
                if (FpsMod.Settings.verboseLog) Log.Message("[FpsBridge] SURFACE_STEP cell=" + c + "; top=" + what + "; height=" + h.ToString("0.00") + "; walkable=" + c.Walkable(map) + "; room=" + RoomText(pawn));
                return true;
            }
            if (h < 0f && SameFloor(map, c, cell) && InAir(pawn))
            {
                // In the air over a gap: on at the take-off height; where it comes down is settled at touchdown.
                if (!flying && FpsMod.Settings.verboseLog) Log.Message("[FpsBridge] SURFACE_LEAP from=" + cell + "; height=" + height.ToString("0.00"));
                flying = true;
                Place(pawn, avatar, c, next);
                return true;
            }
            if (h < 0f && SameFloor(map, c, cell) && c.Standable(map))
            {
                // Off the edge: down to the floor there.
                float fall = height;
                IntVec3 from = cell;
                Release();
                Land(pawn, avatar, c, next, fall);
                View(map, c, false);
                Log.Message("[FpsBridge] SURFACE_LEAVE reason=walked off edge; from=" + from + "; to=" + c + "; fall=" + fall.ToString("0.00"));
                return false;
            }
            // Off an upper floor's top into open air: down the column to the first top or floor below.
            if (h < 0f && SameFloor(map, c, cell) && FloorSpace.Band(map, c).Level > 0 && !FloorSpace.HasFloorAt(map, c))
                return !DropDown(pawn, avatar, map, c, next);
            return true;   // higher top or blocked: stay
        }

        // From the top at height `height` on c's floor, falling down c's column; true when the pawn left the tops.
        private static bool DropDown(Pawn pawn, object avatar, Map map, IntVec3 c, Vector3 next)
        {
            FloorBand own = FloorSpace.Band(map, c);
            float from = own.Height + height;
            for (int level = own.Level - 1; level >= 0; level--)
            {
                IntVec3 below = Column(map, c, level);
                if (!below.IsValid) continue;
                Vector3 at = new Vector3(next.x, 0f, next.z + (below.z - c.z));
                float floor = FloorSpace.Band(map, below).Height;
                float h = TopAtPoint(map, below, at, out string what);
                if (h >= 0f)
                {
                    float drop = from - floor - h;
                    height = h;
                    Place(pawn, avatar, below, at);
                    if (drop > .02f && Stances.For(pawn)) Stances.State.StartFall(drop);
                    View(map, below, true);
                    Log.Message("[FpsBridge] SURFACE_DROP to=" + below + "; top=" + what + "; level=" + level + "; fall=" + drop.ToString("0.00"));
                    return false;
                }
                if (below.Standable(map))
                {
                    IntVec3 start = cell;
                    Release();
                    Land(pawn, avatar, below, at, from - floor);
                    View(map, below, false);
                    Log.Message("[FpsBridge] SURFACE_LEAVE reason=dropped off upper floor; from=" + start + "; to=" + below + "; fall=" + (from - floor).ToString("0.00"));
                    return true;
                }
            }
            return false;
        }

        // Walking on an upper floor into its open edge with a top at the same height just past it (a wall under the
        // floor's rim): straight onto that top, as stepping the other way does.
        public static bool TryWalkFromFloor(Pawn pawn, object avatar, Vector3 move)
        {
            if (pawn?.Map == null || avatar == null || For(pawn) || move.sqrMagnitude < .01f) return false;
            Vector3 at = (Vector3?)Bindings.PhysicsPosition.GetValue(avatar) ?? pawn.DrawPos;
            IntVec3 ahead = (at + move.normalized * .55f).ToIntVec3();
            if (ahead == pawn.Position) return false;
            return TryFromFloor(pawn, avatar, new Vector3(ahead.x - pawn.Position.x, 0f, ahead.z - pawn.Position.z), true);
        }

        // Space at an upper floor's open edge with a top below it (a wall under the floor's rim): onto that top.
        public static bool TryFromFloor(Pawn pawn, object avatar, Vector3 direction, bool levelOnly = false)
        {
            Map map = pawn?.Map;
            if (map == null || avatar == null || For(pawn) || direction.sqrMagnitude < 1e-4f) return false;
            FloorBand band = FloorSpace.Band(map, pawn.Position);
            if (band.Level < 1) return false;   // the tops below are those of the floor under this one
            float ang = Mathf.Atan2(direction.x, direction.z);
            IntVec3 d = new IntVec3(Mathf.RoundToInt(Mathf.Sin(ang)), 0, Mathf.RoundToInt(Mathf.Cos(ang)));
            if (d.x != 0 && d.z != 0) d = Mathf.Abs(Mathf.Sin(ang)) > Mathf.Abs(Mathf.Cos(ang)) ? new IntVec3(d.x, 0, 0) : new IntVec3(0, 0, d.z);
            IntVec3 edge = pawn.Position + d;
            if (!edge.InBounds(map) || FloorSpace.HasFloorAt(map, edge) || !SameFloor(map, edge, pawn.Position)) return false;
            IntVec3 under = Column(map, edge, band.Level - 1);
            if (!under.IsValid || FloorSpace.CellAbove(map, under) != edge) return false;
            float rise = band.Height - FloorSpace.Band(map, under).Height;   // this floor over the one below
            float h = TopAt(map, under, out string what);
            if (h < 0f || h > rise + StepTolerance) return false;
            if (levelOnly && rise - h > StepTolerance) return false;
            if (on != null) Drop("another pawn climbs");
            on = pawn; avatarOf = avatar; height = h;
            Place(pawn, avatar, under, under.ToVector3Shifted());
            hediffs = pawn.health.hediffSet.hediffs.Count;
            nextLog = 0f;
            AimPicker.Reset();
            if (rise - h > .02f && Stances.For(pawn)) Stances.State.StartFall(rise - h);
            View(map, under, true);
            Log.Message("[FpsBridge] FLOOR_TO_SURFACE from=" + edge + "; cell=" + under + "; top=" + what + "; height=" + h.ToString("0.00") + "; level=" + (band.Level - 1));
            return true;
        }

        private static string RoomText(Pawn pawn)
        {
            Room room = pawn.GetRoom();
            return room == null ? "none" : room.ID + (room.PsychologicallyOutdoors ? "/outdoors" : "/indoors") + (room.IsDoorway ? "/door" : "");
        }

        // Every 2 s on top: what the pawn's state is and how hostiles see it.
        private static void Status(Pawn pawn)
        {
            if (!FpsMod.Settings.verboseLog || Time.unscaledTime < nextLog) return;
            nextLog = Time.unscaledTime + 2f;
            var sb = new StringBuilder("[FpsBridge] SURFACE_STATUS cell=").Append(cell).Append("; height=").Append(height.ToString("0.00"))
                .Append("; walkable=").Append(cell.Walkable(pawn.Map)).Append("; room=").Append(RoomText(pawn))
                .Append("; job=").Append(pawn.CurJobDef?.defName ?? "none");
            int count = pawn.health.hediffSet.hediffs.Count;
            if (count != hediffs) { sb.Append("; NEW_HEDIFFS=").Append(count - hediffs); hediffs = count; }
            int shown = 0;
            foreach (Pawn h in pawn.Map.mapPawns.AllPawnsSpawned)
            {
                if (shown >= 6 || h == pawn || h.Downed || !h.HostileTo(pawn) || !h.Position.InHorDistOf(pawn.Position, 60f)) continue;
                Verb verb = h.CurrentEffectiveVerb;
                LocalTargetInfo aim = (h.stances?.curStance as Stance_Warmup)?.focusTarg ?? LocalTargetInfo.Invalid;
                bool aimed = aim.Thing == pawn || h.CurJob?.targetA.Thing == pawn;
                sb.Append(" | ").Append(h.LabelShort).Append('@').Append(h.Position).Append(" job=").Append(h.CurJobDef?.defName ?? "none")
                    .Append(aimed ? " TARGETING_ME" : "");
                if (verb != null && !verb.IsMeleeAttack)
                {
                    bool canHit = verb.CanHitTarget(pawn);
                    sb.Append(" canHit=").Append(canHit);
                    if (canHit)
                    {
                        ShotReport r = ShotReport.HitReportFor(h, verb, pawn);
                        sb.Append(" hit=").Append(r.TotalEstimatedHitChance.ToString("0.00")).Append(" passCover=").Append(r.PassCoverChance.ToString("0.00"));
                    }
                }
                else sb.Append(" melee adjacent=").Append(h.Position.AdjacentTo8WayOrInside(pawn.Position));
                shown++;
            }
            Log.Message(sb.ToString());
        }
    }

    // Every contact shares the persistent roof HP pool, including CE fragments.
    internal static class RoofWear
    {
        public const int Shots=2;
        internal static bool StructuralFloor(RoofDef roof)=>roof?.defName?.StartsWith("PZ_")==true;
        public static bool Breakable(RoofDef roof)=>roof!=null&&!StructuralFloor(roof);
        public static bool Hit(Map map,V3 at,float damage=50f)
        {
            if(map==null)return false;
            IntVec3 chosen=IntVec3.Invalid;
            RoofDef roof=null;
            float nearest=.12f;
            foreach(FloorBand band in FloorSpace.Bands(map))
            {
                IntVec3 cell=new IntVec3(Mathf.FloorToInt(at.X),0,Mathf.FloorToInt(at.Z)+band.Shift);
                if(!cell.InBounds(map))continue;
                RoofDef candidate=map.roofGrid.RoofAt(cell);
                if(!Breakable(candidate))continue;
                float drawn=band.Height+RoofShape.HeightAt(map,at.X,at.Z+band.Shift);
                float distance=Mathf.Min(Mathf.Abs(at.Y-drawn),Mathf.Abs(at.Y-band.Height-View3D.WallHeight));
                if(distance>nearest)continue;
                nearest=distance;chosen=cell;roof=candidate;
            }
            return chosen.IsValid&&Damage(map,chosen,roof,damage);
        }
        internal static bool Hit(Map map,IntVec3 cell,RoofDef roof,int strikes)=>Damage(map,cell,roof,strikes*50f);
        internal static bool Damage(Map map,IntVec3 cell,RoofDef roof,float amount)=>map?.GetComponent<RoofHealthMap>()?.Damage(cell,roof,amount)==true;
        public static void Reset(){}
    }
}
