using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public sealed class RatkinVisualDef : Def
    {
        public string race = "Ratkin";
        public List<RatkinPiece> pieces = new List<RatkinPiece>();
    }
    public sealed class RatkinPiece
    {
        public string id, model, category, slot, age = "Thin";
        public bool hideHair, hideEars, hideHead, hideUpper, hideLower;
        internal ModelHandle handle;
        internal bool attempted;
        internal ModelHandle Load(ModContentPack mod)
        {
            if (!attempted) { attempted = true; handle = Api.LoadModel(mod, model); }
            return handle;
        }
    }

    // Uses the documented figure API. Age assemblies remain in their authored dimensions: undo the
    // engine's child size once, then use the approved age head / tail pivot transforms.
    [StaticConstructorOnStartup]
    internal static class ImportedRatkin
    {
        private static RatkinVisualDef definition;
        private static readonly Dictionary<string, RatkinPiece> pieces = new Dictionary<string, RatkinPiece>();
        private static FieldInfo wheelSpin;
        private static ModelHandle rotor;
        private static bool wheelFailed;
        static ImportedRatkin()
        {
            if (Api.Version < 25) return;
            foreach (RatkinVisualDef d in DefDatabase<RatkinVisualDef>.AllDefsListForReading)
            {
                if (d.modContentPack == null || DefDatabase<ThingDef>.GetNamedSilentFail(d.race) == null) continue;
                definition = d;
                foreach (RatkinPiece p in d.pieces) pieces[p.id] = p;
                break;
            }
            if (definition == null) return;
            Api.AddFigureHook(new FigureHook(DrawFigure));
            try { InstallEquipment(new Harmony("hsk.fpsbridge.ratkin-equipment")); }
            catch (Exception e) { Log.Warning("[FpsBridge] Ratkin external equipment hook unavailable; native equipment retained: " + e.Message); }
            Type wheel = AccessTools.TypeByName("NewRatkin.CompPowerPlantHamsterWheel");
            MethodInfo draw = wheel == null ? null : AccessTools.DeclaredMethod(wheel, "PostDraw");
            wheelSpin = wheel == null ? null : AccessTools.Field(wheel, "spinPosition");
            if (draw != null && wheelSpin?.FieldType == typeof(float))
                new Harmony("hsk.fpsbridge.ratkin-art").Patch(draw, prefix: new HarmonyMethod(typeof(ImportedRatkin), nameof(WheelDraw)));
            else Log.Warning("[FpsBridge] Ratkin wheel visual hook unavailable; original wheel drawing retained.");
            Log.Message("[FpsBridge] RATKIN_ART READY pieces=" + pieces.Count + "; API=" + Api.Version);
        }

        // Only replace the two native figure weapon slabs. Native jobs, recoil, carried/aimed poses,
        // worn extras and other mods' unbound weapons keep their original call chain.
        [ThreadStatic] private static Thing equipment;
        internal static void InstallEquipment(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(FigureWeapons), "Draw"),
                prefix: new HarmonyMethod(typeof(ImportedRatkin), nameof(EquipmentBegin)),
                finalizer: new HarmonyMethod(typeof(ImportedRatkin), nameof(EquipmentEnd)),
                transpiler: new HarmonyMethod(typeof(ImportedRatkin), nameof(EquipmentDraws)));
            // NewRatkin's comp bypasses IsoRim's captured DrawWornExtras and submits a flat card
            // directly to Graphics. Suppress that visual only when our replacement can be drawn.
            Type extra = AccessTools.TypeByName("NewRatkin.CompExtraDrawer");
            MethodInfo drawExtra = extra == null ? null : AccessTools.DeclaredMethod(extra, "CompDrawWornExtras");
            if (drawExtra != null)
                harmony.Patch(drawExtra, prefix: new HarmonyMethod(typeof(ImportedRatkin), nameof(ExtraWorn)));
            Type scaling = AccessTools.TypeByName("NewRatkin.GeneDrawScaleRenderPatches");
            MethodInfo scaledWeapon = scaling == null ? null : AccessTools.DeclaredMethod(scaling, "DrawEquipmentAimingScaled");
            if (scaledWeapon != null)
                harmony.Patch(scaledWeapon, transpiler: new HarmonyMethod(typeof(ImportedRatkin), nameof(ScaledEquipmentDraws)));
            Type extension = AccessTools.TypeByName("SYS.DrawEquipment_WeaponBackPatch");
            MethodInfo lance = extension == null ? null : AccessTools.DeclaredMethod(extension, "DrawEquipmentAiming",
                new[] { typeof(Thing), typeof(Vector3), typeof(float), typeof(bool) });
            if (lance != null)
                harmony.Patch(lance, prefix: new HarmonyMethod(typeof(ImportedRatkin), nameof(LanceAiming)));
        }
        // SYS's custom carried lance bypasses both vanilla and the gene-scale renderer. Forward only
        // the two Ratkin lances through the same capture/suppression contract, preserving its pose/flip.
        internal static bool LanceAiming(Thing eq, Vector3 drawLoc, float aimAngle, bool isFlip)
        {
            string id = eq?.def?.defName;
            if ((id != "RK_HeavyLance" && id != "RK_LightLance") || !View3D.Active || !Api.ModelsOn) return true;
            bool capture = FigureWeapons.Capturing;
            if (capture) FigureWeapons.Weapon(eq);
            try
            {
                DrawHook.DrawMesh(isFlip ? MeshPool.plane10Flip : MeshPool.plane10, drawLoc,
                    Quaternion.AngleAxis(aimAngle, Vector3.up), eq.Graphic.MatSingle, 0);
            }
            finally { if (capture) FigureWeapons.Weapon(null); }
            return false;
        }
        private static IEnumerable<CodeInstruction> ScaledEquipmentDraws(IEnumerable<CodeInstruction> instructions)
        {
            Type[] signature = { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int) };
            MethodInfo from = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh), signature);
            MethodInfo to = AccessTools.Method(typeof(DrawHook), nameof(DrawHook.DrawMesh), signature);
            int count = 0;
            var result = new List<CodeInstruction>();
            foreach (CodeInstruction ci in instructions)
            { if (ci.Calls(from)) { ci.opcode = OpCodes.Call; ci.operand = to; count++; } result.Add(ci); }
            if (count != 1) throw new InvalidOperationException("Ratkin gene-scaled equipment drawing changed: expected 1 draw, got " + count);
            return result;
        }
        internal static bool ExtraWorn(ThingComp __instance)
        {
            Apparel apparel = __instance?.parent as Apparel;
            Pawn wearer = apparel?.Wearer;
            if (definition == null || !View3D.Active || !Api.ModelsOn || wearer?.def?.defName != definition.race) return true;
            RatkinPiece piece = Get(apparel.def.defName);
            // Unknown equipment and failed loads retain their original graphic, as does 2D mode.
            return piece == null || piece.category != "apparel" || piece.Load(definition.modContentPack) == null;
        }
        private static void EquipmentBegin(Pawn pawn, bool measure, out Thing __state)
        {
            __state = equipment;
            equipment = !measure && pawn?.def?.defName == "Ratkin" ? pawn.equipment?.Primary : null;
        }
        private static Exception EquipmentEnd(Exception __exception, Thing __state) { equipment = __state; return __exception; }
        private static IEnumerable<CodeInstruction> EquipmentDraws(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = AccessTools.Method(typeof(SpriteSolids), nameof(SpriteSolids.DrawSlab));
            MethodInfo replacement = AccessTools.Method(typeof(ImportedRatkin), nameof(DrawEquipment));
            int count = 0;
            var result = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(original)) { instruction.operand = replacement; count++; }
                result.Add(instruction);
            }
            if (count != 2) throw new InvalidOperationException("IsoRim figure equipment contract changed: expected 2 slab calls, got " + count);
            return result;
        }
        private static bool DrawEquipment(Material material, Matrix4x4 matrix)
        {
            if (equipment != null && WeaponView.DrawFigureWeapon(equipment, material, matrix)) return true;
            return SpriteSolids.DrawSlab(material, matrix);
        }
        internal static void DrawFirstPersonShield(Pawn pawn, bool moving, float aim, float sprint, float time)
        {
            if (definition == null || pawn?.apparel == null || !Api.ModelsOn || ControlCamera.External || SocialHands.Busy || ConsumableHands.Using(pawn)
                || TurretControl.Manned || VehicleControl.On || FirstPersonTransition.Active) return;
            foreach (Apparel apparel in pawn.apparel.WornApparel)
            {
                RatkinPiece piece = Get(apparel.def.defName);
                if (piece?.slot != "Shield") continue;
                ModelHandle model = piece.Load(definition.modContentPack);
                if (model == null) continue;
                Quaternion view = Quaternion.Euler(FirstPerson.Pitch, FirstPerson.Yaw, 0f);
                Matrix4x4 local = RatkinGearPose.Shield(model.Min, model.Max, aim, sprint, moving, time);
                Api.DrawModel(model, FirstPerson.Eye, 0f, Matrix4x4.Rotate(view) * local, apparel.DrawColor);
                break; // one visible offhand shield, native protection calculations remain untouched
            }
        }

        private static RatkinPiece Get(string id) => pieces.TryGetValue(id ?? "", out RatkinPiece p) ? p : null;
        private static void Attach(FigureFrame f, string id, Matrix4x4 local, bool head, Color colour)
        {
            RatkinPiece p = Get(id);
            ModelHandle m = p?.Load(definition.modContentPack);
            if (m == null) return;
            if (head) f.AttachToHead(m, local, colour); else f.AttachToBody(m, local, colour);
        }
        private static string Age(Pawn p)
        {
            string type = p.story?.bodyType?.defName;
            return type == "Baby" ? "Baby" : type == "Child" ? "Child" : "Thin";
        }
        private static float HeadY(string age) => age == "Baby" ? .395f : age == "Child" ? .522f : .65f;
        private static float HeadScale(string age) => age == "Baby" ? .70f : age == "Child" ? .88f : 1f;
        private static Matrix4x4 HeadAge(string age) => Matrix4x4.Translate(new Vector3(0, HeadY(age), 0))
            * Matrix4x4.Scale(Vector3.one * HeadScale(age)) * Matrix4x4.Translate(new Vector3(0, -.65f, 0));
        private static void DrawFigure(FigureFrame f)
        {
            Pawn p = f.Pawn;
            bool riderSelf = MountedControl.For(p) && !ControlCamera.External && !FirstPersonTransition.Active;
            if (p?.def?.defName != definition.race || f.Skull || (Bridge.Active && Bridge.ShootingPawn == p && !ControlCamera.External && !FirstPersonTransition.Active && !riderSelf)) return;
            string age = Age(p);
            Matrix4x4 body = Matrix4x4.Scale(Vector3.one / Mathf.Max(.01f, f.Size));
            // AlienModelDef.head=.65 maps the engine's .74 neck to our .65 pivot.
            Matrix4x4 head = Matrix4x4.Translate(new Vector3(0, .09f, 0)) * body * HeadAge(age);
            List<Apparel> worn = p.apparel?.WornApparel;
            bool hideHair = !f.HairShown || riderSelf, hideEars = riderSelf, hideHead = f.HeadHidden || f.Headless || riderSelf;
            bool hideUpper = false, hideLower = MountedControl.For(p);
            bool customBody = true, customHead = true, customGear = true, customEyes = true, customShield = true, customNeck = true;
            if (worn != null)
                foreach (Apparel a in worn)
                {
                    RatkinPiece piece = Get(a.def.defName);
                    if (piece != null)
                    {
                        if (piece.slot != "Head" || f.HeadgearShown)
                        {
                            hideHair |= piece.hideHair; hideEars |= piece.hideEars; hideHead |= piece.hideHead;
                        }
                        hideUpper |= piece.hideUpper; hideLower |= piece.hideLower;
                    }
                    else
                    {
                        // If a slot also contains unbound apparel, keep IsoRim's native drawing for that entire slot.
                        // Its race-specific ApparelModelDefs still draw our piece once, beside the foreign garment.
                        ApparelProperties ap = a.def.apparel;
                        if (ap == null) continue;
                        if (ap.layers.Contains(ApparelLayerDefOf.Overhead)) customHead = false;
                        else if (ap.layers.Contains(ApparelLayerDefOf.Belt)) { customGear = false; customShield = false; }
                        else { customBody = false; customEyes = false; customNeck = false; }
                    }
                }
            FigureParts hidden = FigureParts.Head | FigureParts.Face | FigureParts.Hair | FigureParts.Beard | FigureParts.Body | FigureParts.Addons;
            if (customBody) hidden |= FigureParts.Clothes;
            if (customHead) hidden |= FigureParts.Headgear;
            if (customEyes) hidden |= FigureParts.EyeWear | FigureParts.MouthWear;
            if (customGear) hidden |= FigureParts.Gear;
            if (customShield) hidden |= FigureParts.Shield;
            f.Hide(hidden);
            if (f.BodyShown)
            {
                if (!hideUpper) Attach(f, "Ratkin_Body_" + age + "_Upper", body, false, Color.white);
                if (!hideLower) Attach(f, "Ratkin_Body_" + age + "_Lower", body, false, Color.white);
                Vector3 tailAt = age == "Baby" ? new Vector3(0, .276f, -.047f) : age == "Child" ? new Vector3(0, .345f, -.055f) : new Vector3(0, .39f, -.065f);
                float tailScale = age == "Baby" ? .58f : age == "Child" ? .82f : 1f;
                Attach(f, "Shared_Tail", body * Matrix4x4.Translate(tailAt) * Matrix4x4.Scale(Vector3.one * tailScale)
                    * Matrix4x4.Translate(new Vector3(0, -.39f, .065f)), false, Color.white);
            }
            string headType = p.story?.headType?.defName ?? "RK_HeadType_Head1";
            string number = headType.Substring(headType.Length - 1);
            if (Get("Shared_Head" + number + "_Bare") == null) number = "1";
            if (!hideHead) Attach(f, "Shared_Head" + number + "_Bare", head, true, Color.white);
            if (!f.HeadHidden && !f.Headless)
            {
                if (!hideEars)
                {
                    Attach(f, "Shared_Ratkin_Ear_L", head, true, Color.white);
                    Attach(f, "Shared_Ratkin_Ear_R", head, true, Color.white);
                }
                string hair = p.story?.hairDef?.defName;
                if (!hideHair && !string.Equals(hair, "Bald", StringComparison.OrdinalIgnoreCase))
                    Attach(f, Get(hair) != null ? hair : "Shared_Ratkin_Hair_Default", head, true, p.story?.HairColor ?? Color.white);
            }
            if (worn == null) return;
            foreach (Apparel a in worn)
            {
                RatkinPiece piece = Get(a.def.defName);
                if (piece == null || piece.category != "apparel") continue;
                bool onHead = piece.slot == "Head" || piece.slot == "Eyes" || piece.slot == "Face";
                if (riderSelf && (onHead || piece.slot == "Shield" || piece.slot == "Belt" || piece.slot == "Neck")) continue;
                if (onHead && (piece.slot == "Head" ? !f.HeadgearShown || !customHead : !customEyes)) continue;
                if (!onHead && !f.BodyShown) continue;
                if (piece.slot == "Body" && !customBody || piece.slot == "Belt" && !customGear || piece.slot == "Shield" && !customShield || piece.slot == "Neck" && (!customBody || !customNeck)) continue;
                Matrix4x4 local = onHead ? head : body;
                if (piece.slot == "Shield")
                    local *= Matrix4x4.TRS(new Vector3(age == "Thin" ? -.18f : -.125f, age == "Thin" ? .46f : .31f, .04f), Quaternion.Euler(0, -25, 0), Vector3.one * HeadScale(age));
                // Child garments were separately authored in child assembly space; never multiply them by .88 again.
                Attach(f, piece.id, local, onHead, a.DrawColor);
            }
        }

        private static bool WheelDraw(ThingComp __instance)
        {
            if (wheelFailed || definition == null || !Api.ModelsOn || !Api.ModelStandsIn(__instance.parent)) return true;
            try
            {
                rotor ??= Get("RK_HamsterWheelGenerator__WheelRotor")?.Load(definition.modContentPack);
                if (rotor == null) return true;
                float angle = (float)wheelSpin.GetValue(__instance) * Mathf.Rad2Deg;
                Thing t = __instance.parent;
                Vector3 at = t.TrueCenter(); at.y = 0f;
                Matrix4x4 local = Matrix4x4.TRS(new Vector3(0, .54000002f, 0), Quaternion.AngleAxis(angle, Vector3.back), Vector3.one);
                Api.DrawModel(rotor, at, t.Rotation.AsAngle, local, Color.white);
                return false;
            }
            catch (Exception e) { wheelFailed = true; Log.Warning("[FpsBridge] Ratkin wheel visual fallback: " + e.Message); return true; }
        }

        internal static void DrawDrills(Map map)
        {
            if (definition == null || Find.CurrentMap != map || !Api.ModelsOn) return;
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("RK_GuerrillaTunnel");
            RatkinPiece piece = Get("RK_GuerrillaTunnel__DrillRotor");
            ModelHandle model = piece?.Load(definition.modContentPack);
            if (def == null || model == null) return;
            foreach (Thing t in map.listerThings.ThingsOfDef(def))
            {
                if (!t.Spawned || t.Position.Fogged(map) || !Api.ModelStandsIn(t)) continue;
                Vector3 at = t.TrueCenter(); at.y = 0f;
                // The separately supplied rotor is exported in full-building coordinates.
                Vector3 pivot = new Vector3(0, .8f, 0);
                float angle = Find.TickManager.TicksGame * .6f;
                Matrix4x4 local = Matrix4x4.Translate(pivot) * Matrix4x4.Rotate(Quaternion.AngleAxis(angle, new Vector3(.121f, .993f, 0))) * Matrix4x4.Translate(-pivot);
                Api.DrawModel(model, at, t.Rotation.AsAngle, local, Color.white);
            }
        }
    }

    public sealed class MapComponent_ImportedRatkin : MapComponent
    {
        public MapComponent_ImportedRatkin(Map map) : base(map) { }
        public override void MapComponentUpdate() => ImportedRatkin.DrawDrills(map);
    }
}
