using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace HSK.FpsBridge
{
    // Climbing: Space at a wall jumps, Space again in the air catches the top and climbs up onto it (Surface.cs walks
    // the tops; walking off the far side comes down). From a top the same goes for a higher top in reach.
    //   - a floor laid over the wall (ProjectZ upper floor): up onto that floor;
    //   - the wall goes on up (a wall/solid on the floor above): no way up;
    //   - a top higher than about one wall over the pawn's feet: out of reach.
    internal static class Vault
    {
        private const float Reach = 1.15f;   // how close to the wall's centre the hands reach (cells, flat)
        // Highest climb over the feet (true cells): one wall and the eaves of a roof on it.
        private static float MaxClimb => View3D.WallHeight * 1.25f + .1f;

        internal static bool Solid(IntVec3 c, Map map) => !c.InBounds(map) || !c.Standable(map) || (c.GetEdifice(map) is Building b && b.def.passability == Traversability.Impassable);

        private static IntVec3 Cardinal(Vector3 direction)
        {
            float ang = Mathf.Atan2(direction.x, direction.z);
            IntVec3 d = new IntVec3(Mathf.RoundToInt(Mathf.Sin(ang)), 0, Mathf.RoundToInt(Mathf.Cos(ang)));
            if (d.x != 0 && d.z != 0) d = Mathf.Abs(Mathf.Sin(ang)) > Mathf.Abs(Mathf.Cos(ang)) ? new IntVec3(d.x, 0, 0) : new IntVec3(0, 0, d.z);
            return d;
        }

        // A top this low over the feet is jumped onto with one Space, quickly, instead of climbed.
        public const float LowClimb = .75f;
        internal static bool TryModel(Pawn pawn,object avatar,Vector3 forward,StanceState state)
        {
            if(go!=null || pawn?.Map==null || state.Posture!=Posture.Stand)return false;
            Vector3 at=(Vector3?)Bindings.PhysicsPosition.GetValue(avatar)??pawn.DrawPos;
            if(!ModelSurfaces.Near(pawn,at,forward,out Thing support,out Vector3 landing,out float top))return false;
            // A physical floor overhead remains a barrier; the existing floor climb handles it.
            IntVec3 above=FloorSpace.CellAbove(pawn.Map,landing.ToIntVec3());
            if(above.IsValid && FloorSpace.HasFloorAt(pawn.Map,above)
                && top+Stances.StandingEye(pawn)>FloorSpace.Band(pawn.Map,above).Height-FloorSpace.Band(pawn.Map,pawn.Position).Height)return false;
            if(!state.Spend(StanceState.HopCost))return true;
            Vector3 delta=landing-at;delta.y=0;
            go=new Plan {pawn=pawn,avatar=avatar,map=pawn.Map,what=support.LabelShort,wallCell=landing.ToIntVec3(),target=landing.ToIntVec3(),
                dir=delta.normalized,reach=delta.magnitude,climb=top-Surface.HeightOf(pawn),top=top,startLift=state.Lift,
                furniture=support,modelLanding=landing,rise=.22f,strain=0,over=Mathf.Clamp(.30f+top*.05f,.30f,.75f),dipDeg=5};
            state.EndJump();pawn.pather?.StopDead();Bindings.ClearMovement(avatar);return true;
        }

        // direction: the view (world x/z). True when the climb started or was refused with a message.
        // lowOnly (a single Space on the ground): only a top low enough to jump onto, quietly ignoring the rest.
        public static bool TryMantle(Pawn pawn, object avatar, Vector3 direction, StanceState state, bool lowOnly = false)
        {
            Map map = pawn?.Map;
            if (map == null || direction.sqrMagnitude < 1e-4f || go != null) return false;
            IntVec3 d = Cardinal(direction);
            IntVec3 front = pawn.Position + d;
            // Any floor: the top is that of the cell ahead on the pawn's own floor.
            if (!front.InBounds(map) || FloorSpace.Band(map, front).Level != FloorSpace.Band(map, pawn.Position).Level) return false;
            Vector3 at = (Vector3?)Bindings.PhysicsPosition.GetValue(avatar) ?? pawn.DrawPos;
            if (new Vector2(at.x - (front.x + .5f), at.z - (front.z + .5f)).magnitude > Reach + .5f) return false;
            float feet = Surface.HeightOf(pawn);   // 0 on the ground
            float top = Surface.TopAt(map, front, out string what);
            Thing furniture = FurnitureSurfaces.At(map,front);
            if(furniture!=null)
            {
                foreach(Thing t in front.GetThingList(map))if(t is Pawn other && other!=pawn && !other.Downed)return false;
                top=FurnitureSurfaces.SeatHeight;what=furniture.LabelShort;
            }
            IntVec3 above = furniture!=null ? IntVec3.Invalid : FloorSpace.CellAbove(map, front);
            if (top >= 0f && above.IsValid && Solid(above, map) && above.GetEdifice(map) != null)
            { Messages.Message(L.T("墙一直通到楼上，爬不上去。", "The wall goes on up to the next floor: no way up."), MessageTypeDefOf.RejectInput, false); return true; }
            IntVec3 target;
            float climb;
            bool up = false;
            if (above.IsValid && FloorSpace.HasFloorAt(map, above) && !Solid(above, map))
            {
                // A floor over it: straight onto that floor.
                target = above; up = true; what = L.T("楼板", "floor");
                climb = FloorSpace.Band(map, above).Height - FloorSpace.Band(map, front).Height - feet;
            }
            else if (top >= 0f) { target = front; climb = top - feet; }
            else return false;
            if (climb <= Surface.StepTolerance) return false;   // low enough to walk on
            bool low = climb <= LowClimb;
            if (lowOnly && !low) return false;
            if (climb > MaxClimb) { Messages.Message(L.T("太高了，够不着。", "Too high to reach."), MessageTypeDefOf.RejectInput, false); return true; }
            if (!state.Spend(low ? StanceState.HopCost : StanceState.VaultCost)) { Messages.Message(L.T("太累了，爬不上去。", "Too tired to climb."), MessageTypeDefOf.RejectInput, false); return true; }

            // The climb plays out over the next ~1.6 s (Step); the pawn moves onto the top at the end.
            float reach = Mathf.Max(.3f, Vector3.Dot(new Vector3(front.x + .5f - at.x, 0f, front.z + .5f - at.z), new Vector3(d.x, 0f, d.z)));
            go = new Plan { pawn = pawn, avatar = avatar, map = map, what = what, wallCell = front, target = target, up = up,
                dir = new Vector3(d.x, 0f, d.z), reach = reach, climb = climb, top = top, startLift = state.Lift, dipDeg = 16f, furniture=furniture };
            if (low) { go.rise = .2f; go.strain = 0f; go.over = .35f; go.dipDeg = 6f; }   // a jump up, no hanging
            state.EndJump();
            pawn.pather?.StopDead();
            Bindings.ClearMovement(avatar);
            Log.Message("[FpsBridge] CLIMB_START pawn=" + pawn.thingIDNumber + "; onto=" + what + "@" + front + "; to=" + target + "; climb=" + climb.ToString("0.00"));
            return true;
        }

        // 0.9.0 low cover (sandbags, barricades: a building that only partly fills its cell): Space hops over it in about
        // 0.6 s instead of jumping, landing on the far side. True when it hopped (or was refused with a message).
        public static bool TryHop(Pawn pawn, object avatar, Vector3 direction, StanceState state)
        {
            Map map = pawn?.Map;
            if (go != null || map == null || direction.sqrMagnitude < 1e-4f) return false;
            IntVec3 d = Cardinal(direction);
            IntVec3 coverCell = pawn.Position + d;
            Building cover = coverCell.InBounds(map) ? coverCell.GetEdifice(map) : null;
            if (cover == null || cover is Building_Door || cover.def.building == null || cover.def.building.isNaturalRock) return false;
            float fill = cover.def.fillPercent;
            if (fill >= 1f || fill < .2f) return false;
            Vector3 at = (Vector3?)Bindings.PhysicsPosition.GetValue(avatar) ?? pawn.DrawPos;
            if (new Vector2(at.x - (coverCell.x + .5f), at.z - (coverCell.z + .5f)).magnitude > Reach + .3f) return false;
            IntVec3 beyond = coverCell + d;
            if (!beyond.InBounds(map) || Solid(beyond, map)) return false;   // nowhere to land: an ordinary jump instead
            if (!state.Spend(StanceState.HopCost)) { Messages.Message(L.T("太累了，跨不过去。", "Too tired to hop over."), MessageTypeDefOf.RejectInput, false); return true; }
            go = new Plan { pawn = pawn, avatar = avatar, map = map, what = cover.LabelShort, wallCell = coverCell, target = beyond,
                dir = new Vector3(d.x, 0f, d.z), reach = 2f, startLift = state.Lift,
                rise = .2f, strain = 0f, over = .4f, chinAt = .25f, overAt = .45f, dipDeg = 8f };
            state.EndJump();
            pawn.pather?.StopDead();
            Bindings.ClearMovement(avatar);
            pawn.skills?.Learn(SkillDefOf.Melee, 30f);
            Log.Message("[FpsBridge] HOP_START pawn=" + pawn.thingIDNumber + "; cover=" + cover.def.defName + "@" + coverCell + "; to=" + beyond);
            return true;
        }

        // ---- the climb, step by step

        private sealed class Plan
        {
            public Pawn pawn; public object avatar; public Map map; public string what;
            public Thing furniture;
            public Vector3? modelLanding;
            public IntVec3 wallCell, target; public bool up;
            public Vector3 dir; public float reach, startLift, time, dip;
            // climb: how far the feet rise (true cells; 0 for a hop over cover); top: the top's height over the ground.
            public float climb, top;
            // Stage lengths (game seconds) and heights: a wall is a struggle, low cover a quick hop.
            public float rise = Rise, strain = Strain, over = Over, chinAt = -1f, overAt = -1f, dipDeg = 28f;
        }

        private const float Rise = .4f, Strain = .55f, Over = .6f;   // game seconds per stage
        private static Plan go;
        private static Vector3 offset;

        public static bool Active => go != null;
        // Added to the eye while climbing (true cells, world axes).
        public static Vector3 EyeOffset => go != null ? offset : Vector3.zero;

        public static void Cancel()
        {
            if (go != null && go.dip != 0f) FirstPerson.Look(0f, -go.dip);
            go = null; offset = Vector3.zero;
        }

        // Once per frame (dt in game seconds, 0 while paused).
        public static void Step(Pawn pawn, float dt, StanceState state)
        {
            Plan p = go;
            if (p == null) return;
            if (p.pawn != pawn || !pawn.Spawned || pawn.Map != p.map || pawn.Downed) { Cancel(); return; }
            p.time += dt;
            float t = p.time, standing = Stances.StandingEye(pawn);
            float top = p.climb > 0f ? p.climb : View3D.WallHeight;   // the top over the pawn's feet
            float chin = p.chinAt >= 0f ? p.chinAt : Mathf.Max(.25f, top + .05f - standing);  // eye just over the top: hanging on it
            float over = p.overAt >= 0f ? p.overAt : top;
            float y, f, wantDip = 0f;
            if (t < p.rise)
            {
                // Jump and catch the top: up fast, then slower as the hands take it.
                float u = t / p.rise, e = 1f - (1f - u) * (1f - u);
                y = Mathf.Lerp(p.startLift, chin, e); f = Mathf.Min(.25f, p.reach * .4f) * e;
            }
            else if (t < p.rise + p.strain)
            {
                // Hanging, arms working: a little up, a shake, the body pulled in to the wall.
                float u = (t - p.rise) / p.strain;
                y = Mathf.Lerp(chin, chin + .18f, u * u) + Mathf.Sin(t * 34f) * .025f * (1f - u);
                f = Mathf.Lerp(Mathf.Min(.25f, p.reach * .4f), Mathf.Min(.42f, p.reach * .6f), u);
            }
            else if (t < p.rise + p.strain + p.over)
            {
                // Up and onto it: a knee over the edge, the view tips down as the body rolls up, then stands.
                float u = (t - p.rise - p.strain) / p.over, e = u * u * (3f - 2f * u);
                y = p.climb > 0f ? Mathf.Lerp(chin + .18f, over, e) : Mathf.Lerp(chin + .18f, over, Mathf.Sin(e * Mathf.PI * .5f));
                f = Mathf.Lerp(Mathf.Min(.42f, p.reach * .6f), p.reach, e);
                wantDip = p.dipDeg * Mathf.Sin(u * Mathf.PI);
            }
            else
            {
                Land(p, state);
                return;
            }
            offset = p.dir * f + Vector3.up * y;
            float delta = wantDip - p.dip;
            if (Mathf.Abs(delta) > .01f) { FirstPerson.Look(0f, delta); p.dip = wantDip; }
        }

        // At the top: the pawn moves there and the eye settles onto it.
        private static void Land(Plan p, StanceState state)
        {
            Pawn pawn = p.pawn;
            float eyeNow = offset.y;   // over the feet before the climb
            if (p.dip != 0f) FirstPerson.Look(0f, -p.dip);
            go = null; offset = Vector3.zero;
            Map map = p.map;
            if (p.climb > 0f && !p.up)
            {
                // Onto a wall, rock or roof top.
                if (p.furniture != null)
                {
                    if(p.modelLanding.HasValue)
                    {
                        if(!ModelSurfaces.Top(p.furniture,p.modelLanding.Value,out float top))return;
                        Surface.EnterModel(pawn,p.avatar,p.furniture,p.modelLanding.Value,top);
                    }
                    else
                    {
                        if (!FurnitureSurfaces.IsSeat(p.furniture)) return;
                        Surface.EnterFurniture(pawn,p.avatar,p.furniture);
                    }
                }
                else
                {
                    if (Surface.TopAt(map, p.target, out _) < 0f) { Messages.Message(L.T("上面没有落脚的地方了。", "Nowhere to stand up there."), MessageTypeDefOf.RejectInput, false); return; }
                    Surface.Enter(pawn, p.avatar, p.target, p.top, p.what);
                }
                if (eyeNow - p.climb > .02f) state.StartFall(eyeNow - p.climb);
                pawn.skills?.Learn(SkillDefOf.Melee, 80f);   // 0.9.0: climbing by hand trains Melee
                Log.Message("[FpsBridge] CLIMB pawn=" + pawn.thingIDNumber + "; onto=" + p.what + "@" + p.target + "; height=" + p.top.ToString("0.00"));
                return;
            }
            if (!p.target.InBounds(map) || Solid(p.target, map)) { Messages.Message(L.T("落脚点被挡住了。", "The landing spot is blocked."), MessageTypeDefOf.RejectInput, false); return; }
            float feet = Surface.HeightOf(pawn);
            if (Surface.For(pawn)) Surface.Release();
            FloorBand own = FloorSpace.Band(map, pawn.Position), now = FloorSpace.Band(map, p.target);
            pawn.pather?.StopDead();
            pawn.Position = p.target;
            pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: true);
            if (pawn.pather != null) pawn.pather.nextCell = p.target;
            Vector3 logical = p.target.ToVector3Shifted(); logical.y = pawn.def.Altitude;
            Bindings.PhysicsPosition.SetValue(p.avatar, (Vector3?)logical);
            if (now.Level != own.Level) Api.Floors?.ViewFloor(map, now.Level);
            AimPicker.Reset();
            float fall = feet + eyeNow - (now.Height - own.Height);
            if (fall > .02f) state.StartFall(fall);
            if (p.up) Messages.Message(L.T("爬上楼层", "Climbed up a floor"), MessageTypeDefOf.SilentInput, false);
            if (p.strain > 0f) pawn.skills?.Learn(SkillDefOf.Melee, 80f);
            Log.Message("[FpsBridge] VAULT pawn=" + pawn.thingIDNumber + "; over=" + p.what + "@" + p.wallCell + "; to=" + p.target + "; level=" + own.Level + "->" + now.Level);
        }
    }
}
