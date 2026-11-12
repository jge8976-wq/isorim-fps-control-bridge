using System;
using System.Collections.Generic;
using System.IO;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal enum ModelKind { Gun, Blade, Grenade, Bow, Launcher, Mounted, Spotter }

    // NoCE/DLC model: mesh Models/<Id>.cwm, atlas Textures/HSKFps/Weapons/<Atlas>.png.
    // Model space: +Z along the barrel/blade, +Y up, the primary grip at the origin, units about a cell.
    internal sealed class ModelSpec
    {
        public readonly string DefName, Id, TexPath, Atlas;
        public readonly ModelKind Kind;
        public readonly V3 Min, Max;
        public readonly Dictionary<string, V3> Anchors;
        internal ImportedWeaponDef Import;
        internal HeldWeaponBinding Held;
        internal HeldWeaponSprite Sprite;

        public ModelSpec(string defName, string id, ModelKind kind, string texPath, string atlas, V3 min, V3 max, Dictionary<string, V3> anchors)
        { DefName = defName; Id = id; Kind = kind; TexPath = texPath; Atlas = atlas; Min = min; Max = max; Anchors = anchors ?? new Dictionary<string, V3>(); }

        public float Length => Math.Max(0.05f, Max.Z - Min.Z);
        public Vector3 Center => new Vector3((Min.X + Max.X) * 0.5f, (Min.Y + Max.Y) * 0.5f, (Min.Z + Max.Z) * 0.5f);

        public bool Anchor(string name, out Vector3 at)
        {
            if (Anchors.TryGetValue(name, out V3 v)) { at = new Vector3(v.X, v.Y, v.Z); return true; }
            at = Vector3.zero;
            return false;
        }
    }

    internal static partial class ModelSpecs
    {
        private sealed class Variants
        {
            public readonly ModelSpec[] Specs;
            public readonly string[] Ids;
            public Variants(List<ModelSpec> specs)
            {
                Specs = specs.ToArray(); Ids = new string[Specs.Length];
                for (int i = 0; i < Specs.Length; i++) Ids[i] = Specs[i].Id;
            }
        }
        private static Dictionary<string, Variants> byDef;
        private static Dictionary<string, ModelSpec> byTex, byId;

        private static void Index()
        {
            if (byDef != null) return;
            var groups = new Dictionary<string, List<ModelSpec>>(StringComparer.OrdinalIgnoreCase);
            byTex = new Dictionary<string, ModelSpec>(StringComparer.OrdinalIgnoreCase);
            byId = new Dictionary<string, ModelSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelSpec s in All)
            {
                if (!groups.TryGetValue(s.DefName, out List<ModelSpec> list)) groups[s.DefName] = list = new List<ModelSpec>();
                list.Add(s);
                if (!byId.ContainsKey(s.Id)) byId[s.Id] = s;
            }
            var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelSpec s in Dlc)
            {
                // New art replaces the old def's visual, but every A-E/style variant is retained.
                if (replaced.Add(s.DefName)) groups[s.DefName] = new List<ModelSpec>();
                groups[s.DefName].Add(s);
                byId[s.Id] = s;
            }
            byDef = new Dictionary<string, Variants>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in groups)
            {
                var variants = new Variants(pair.Value); byDef[pair.Key] = variants;
                byId[pair.Key] = variants.Specs[ModelVariantRules.Pick(variants.Ids, null, null, 0)];
            }
            foreach (ModelSpec s in All)
                if (!string.IsNullOrEmpty(s.TexPath) && !byTex.ContainsKey(s.TexPath)) byTex[s.TexPath] = byId[s.DefName];
        }

        // A library model by id or defName (first variant), or null.
        internal static ModelSpec Get(string id) { Index(); return byId.TryGetValue(id, out ModelSpec s) ? s : null; }

        internal static string GraphicPath(Thing weapon)
        {
            Graphic graphic = weapon?.Graphic;
            for (int depth = 0; depth < 8 && graphic != null; depth++)
            {
                if (graphic is Graphic_RandomRotated rotated) { graphic = rotated.SubGraphic; continue; }
                if (graphic is Graphic_Random random) { graphic = random.SubGraphicFor(weapon); continue; }
                break;
            }
            return graphic?.path;
        }

        private static bool Has(string text, params string[] terms)
        {
            foreach (string t in terms) if (text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static ModelSpec Find(Thing weapon, Verb verb, out bool exact, bool forView = false)
        {
            Index();
            exact = true;
            ThingDef def = weapon.def;
            ModelSpec s;
            WeaponModelExtension extension = def.GetModExtension<WeaponModelExtension>();
            if (!string.IsNullOrWhiteSpace(extension?.modelId))
            {
                s = Get(extension.modelId);
                if (s != null) return s;
                Log.WarningOnce("[FpsBridge] Unknown explicit weapon model: " + extension.modelId + "; weapon=" + def.defName, (def.defName + extension.modelId).GetHashCode());
            }
            s = ImportedWeapons.For(def.defName);
            if (s != null) return s;
            s = HeldWeaponModels.Find(weapon, byDef.ContainsKey(def.defName));
            if (s != null) return s;
            if (byDef.TryGetValue(def.defName, out Variants variants))
                return variants.Specs[ModelVariantRules.Pick(variants.Ids, weapon.StyleDef?.defName, GraphicPath(weapon), weapon.OverrideGraphicIndex ?? weapon.thingIDNumber)];
            string tex = def.graphicData?.texPath;
            if (forView)
            {
                string graphicPath = GraphicPath(weapon);
                if (graphicPath != null && byTex.TryGetValue(graphicPath, out s)) return s;
                exact = false;
                return HeldWeaponSprite.Make(weapon, verb);
            }
            // Legacy length estimates are still used by melee reach.
            if (FpsMod.Settings.keywordWeaponModels && extension?.disableKeywordMatching != true)
            {
                string keyword = WeaponKeywordRules.Pick(def.defName, def.label, tex, def.IsMeleeWeapon);
                if (keyword != null && byId.TryGetValue(keyword, out s)) { exact = false; return s; }
            }
            if (tex != null && byTex.TryGetValue(tex, out s)) return s;
            exact = false;
            string key = def.defName + "|" + tex;
            string pick;
            if (def.IsMeleeWeapon || verb == null || verb.verbProps.IsMeleeAttack)
                pick = Has(key, "knife", "dagger", "shiv") ? "MeleeWeapon_Knife" : Has(key, "spear", "pike", "lance") ? "MeleeWeapon_Spear"
                    : Has(key, "mace", "hammer", "maul") ? "MeleeWeapon_Mace" : Has(key, "club") ? "MeleeWeapon_Club"
                    : Has(key, "axe") ? "MeleeWeapon_BreachAxe" : Has(key, "gladius", "shortsword") ? "MeleeWeapon_Gladius" : "MeleeWeapon_LongSword";
            else if (verb.verbProps.verbClass == typeof(Verb_LaunchProjectile))
                pick = Has(key, "molotov", "incendiary", "fire") ? "Weapon_GrenadeMolotov" : Has(key, "emp") ? "Weapon_GrenadeEMP" : "Weapon_GrenadeFrag";
            else
            {
                int burst = verb.verbProps.burstShotCount;
                pick = Has(key, "bow") ? "Bow_Recurve" : Has(key, "minigun", "gatling", "rotary") ? "Gun_Minigun"
                    : Has(key, "lmg", "hmg", "machinegun") && !Has(key, "submachine") ? "Gun_LMG"
                    : Has(key, "machinepistol") ? "Gun_MachinePistol" : Has(key, "smg", "submachine") ? "Gun_HeavySMG"
                    : Has(key, "revolver") ? "Gun_Revolver" : Has(key, "pistol") ? "Gun_Autopistol"
                    : Has(key, "shotgun") ? "Gun_PumpShotgun" : Has(key, "sniper") ? "Gun_SniperRifle"
                    : Has(key, "rocket", "launcher") ? "Gun_TripleRocket" : Has(key, "charge") ? "Gun_ChargeRifle"
                    : burst > 1 ? "Gun_AssaultRifle" : "Gun_BoltActionRifle";
            }
            return byId.TryGetValue(pick, out s) ? s : null;
        }
    }

    // The weapon in the first-person pawn's hands, drawn in the 3D view in front of the eye: hip fire by default, iron
    // sights while RMB is held (the rear aperture and front tip of the 1.6 model put on the view's centre line, the view
    // narrowed), recoil, a bob while walking, thrust/slash swings for blades, a throw for grenades.
    internal static class WeaponView
    {
        private sealed class Loaded { public Mesh mesh; public Material material; }

        private static readonly Dictionary<string, Loaded> loaded = new Dictionary<string, Loaded>();
        private static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        private static readonly HashSet<string> failed = new HashSet<string>();
        private static string root;
        private static float sprintBlend;
        private static float aimBlend, kick, kickSize = 1f, meleeStart = -10f, throwStart = -10f, lastTime;
        private static bool heavy;
        private static bool fovOwned;
        private static float baseFov;
        private static Thing lastWeapon;
        private static Graphic lastGraphic;
        private static ThingStyleDef lastStyle;
        private static Verb lastVerb;
        private static int? lastGraphicIndex;
        private static ModelSpec lastSpec;
        private static bool lastExact;
        private static int lastModelGeneration = -1;
        private static bool lastIsoWeaponModels;
        private static bool scopeLatched, simpleReticle;
        private static int aimWeaponId = -1;
        private static ModelSpec aimSpec;
        private static float scopeMagnification = 2.5f;
        private static bool singleGun;
        private static ScopeStyle scopeStyle;

        public static bool Aiming => aimBlend > 0.5f;
        public static bool ScopedAiming => scopeLatched && Aiming;
        public static bool SimpleReticle => Aiming && simpleReticle;
        public static bool ScopeLatched => scopeLatched;
        public static float ScopeMagnification => scopeMagnification;
        public static ScopeStyle CurrentScope => scopeStyle;
        public static float AimBlend => aimBlend;
        public static bool SteadySingleAim => singleGun && Aiming;
        public static string Status { get; private set; }

        // Mouse sensitivity scale while the view is narrowed (the same turn per pixel on screen).
        public static float LookScale
        {
            get
            {
                if (!fovOwned) return 1f;
                float now = IsoRimMod.Settings.fpFov;
                return Mathf.Clamp(Mathf.Tan(now * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(baseFov * 0.5f * Mathf.Deg2Rad), 0.1f, 1f);
            }
        }

        public static void Kick(float damage) { kick = 1f; kickSize = Mathf.Clamp(0.6f + damage / 25f, 0.6f, 2f); }
        public static void Melee(bool isHeavy) => Melee(isHeavy ? SwingKind.Heavy : SwingKind.Thrust);
        public static void Melee(SwingKind kind) { swingKind = kind; heavy = kind == SwingKind.Heavy; meleeStart = Time.unscaledTime; }
        private static SwingKind swingKind;
        public static void Throw() { throwStart = Time.unscaledTime; }

        // Leaving first person, saving, an error: the view's field of view goes back exactly as it was.
        public static void Release()
        {
            if (fovOwned && IsoRimMod.Settings != null) IsoRimMod.Settings.fpFov = baseFov;
            fovOwned = false;
            scopeLatched = simpleReticle = false; aimWeaponId = -1;
            aimBlend = 0f; kick = 0f; meleeStart = throwStart = -10f;
            singleGun = false;
            scopeStyle = ScopeStyle.None;
            lastWeapon = null; lastGraphic = null; lastStyle = null; lastVerb = null; lastSpec = null; lastGraphicIndex = null;
        }

        private static string Root()
        {
            if (root != null) return root;
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                if (mod.assemblies?.loadedAssemblies != null && mod.assemblies.loadedAssemblies.Contains(typeof(WeaponView).Assembly)) { root = mod.RootDir; break; }
            if (root == null)
            {
                string at = typeof(WeaponView).Assembly.Location;
                root = string.IsNullOrEmpty(at) ? "" : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(at), "..", ".."));
            }
            return root;
        }

        private static Loaded Load(ModelSpec spec)
        {
            if (loaded.TryGetValue(spec.Id, out Loaded l)) return l;
            if (failed.Contains(spec.Id)) return null;
            try
            {
                string path = spec.Import != null ? Path.Combine(spec.Import.modContentPack.RootDir, spec.Import.mesh) : Path.Combine(Root(), "Models", spec.Id + ".cwm");
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadUInt32() != 827152195u || r.ReadUInt32() != 1u) throw new InvalidDataException("CWM header");
                    int n = r.ReadInt32(), count = r.ReadInt32();
                    if (n < 3 || n > 65000 || count < 3 || count % 3 != 0 || r.BaseStream.Length != 16L + n * 32L + count * 4L) throw new InvalidDataException("CWM counts");
                    var v = new Vector3[n]; var nr = new Vector3[n]; var uv = new Vector2[n]; var c = new Color32[n];
                    for (int i = 0; i < n; i++)
                    {
                        v[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        nr[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                        c[i] = new Color32(255, 255, 255, 255);
                    }
                    var t = new int[count];
                    for (int i = 0; i < count; i++) { t[i] = r.ReadInt32(); if (t[i] < 0 || t[i] >= n) throw new InvalidDataException("CWM index"); }
                    var mesh = new Mesh();
                    mesh.vertices = v; mesh.normals = nr; mesh.uv = uv; mesh.colors32 = c; mesh.triangles = t;
                    mesh.RecalculateBounds();
                    Material mat = MaterialFor(spec.Atlas, spec.Import != null);
                    if (mat == null) throw new FileNotFoundException("atlas texture HSKFps/Weapons/" + spec.Atlas);
                    l = new Loaded { mesh = mesh, material = mat };
                }
                loaded[spec.Id] = l;
                return l;
            }
            catch (Exception e)
            {
                failed.Add(spec.Id);
                Log.Warning("[FpsBridge] Weapon model " + spec.Id + " not loaded: " + e.Message);
                return null;
            }
        }

        internal static bool DrawFigureWeapon(Thing weapon, Material original, Matrix4x4 matrix)
        {
            ModelSpec spec = ImportedWeapons.For(weapon?.def?.defName);
            if (spec == null || original?.mainTexture == null || weapon.Graphic?.MatSingleFor(weapon)?.mainTexture != original.mainTexture) return false;
            Loaded model = Load(spec);
            if (model == null) return false;
            float longest = Mathf.Max(spec.Max.X - spec.Min.X, Mathf.Max(spec.Max.Y - spec.Min.Y, spec.Max.Z - spec.Min.Z));
            Matrix4x4 posed = RatkinGearPose.Equipment(matrix, View3D.Squash, spec.Center, longest);
            Graphics.DrawMesh(model.mesh, posed, model.material, 0);
            return true;
        }

        private static Material MaterialFor(string atlas, bool imported = false)
        {
            if (materials.TryGetValue(atlas, out Material m)) return m;
            Texture2D tex = ContentFinder<Texture2D>.Get(imported ? atlas : "HSKFps/Weapons/" + atlas, false);
            if (tex != null)
            {
                tex.wrapMode = TextureWrapMode.Clamp;
                m = R3DMaterials.Make(R3DMaterials.SolidTexDef, "HSKFps_W_" + atlas, -1, R3DMaterials.LightModel);
                if (m != null)
                {
                    m.mainTexture = tex;
                    m.color = Color.white;
                    IsoGlow.Use(m, IsoGlow.OwnCell);
                }
            }
            materials[atlas] = m;
            return m;
        }

        // Once per frame while the session is live and the 3D view is on. Drafting is optional.
        public static void Frame(Pawn pawn, bool wantAim, bool moving, bool allowAim)
        {
            bool usingItem = ConsumableHands.Using(pawn);
            if (Magazines.Reloading || usingItem) { wantAim = false; allowAim = false; scopeLatched = false; }
            float now = Time.unscaledTime, dt = Mathf.Clamp(now - lastTime, 0f, 0.1f);
            lastTime = now;
            Thing weapon = pawn.equipment?.Primary;
            Verb verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            ModelSpec spec = null;
            if (weapon != null)
            {
                Graphic graphic = weapon.Graphic;
                int? graphicIndex = weapon.OverrideGraphicIndex;
                if (weapon != lastWeapon || graphic != lastGraphic || weapon.StyleDef != lastStyle || graphicIndex != lastGraphicIndex || verb != lastVerb || lastModelGeneration != HeldWeaponModels.Generation || lastIsoWeaponModels != WeaponModels.On)
                {
                    lastModelGeneration = HeldWeaponModels.Generation;
                    lastIsoWeaponModels = WeaponModels.On;
                    lastWeapon = weapon; lastGraphic = graphic; lastStyle = weapon.StyleDef; lastGraphicIndex = graphicIndex; lastVerb = verb;
                    lastSpec = ModelSpecs.Find(weapon, verb, out lastExact, forView: true);
                    if (lastSpec is { Held: null, Sprite: null } && Load(lastSpec) == null)
                    {
                        lastExact = false;
                        lastSpec = HeldWeaponSprite.Make(weapon, verb);
                    }
                    Log.Message("[FpsBridge] WEAPON_MODEL def=" + weapon.def.defName + " thing=" + weapon.thingIDNumber
                        + " model=" + (lastSpec?.Id ?? "NONE") + " graphic=" + ModelSpecs.GraphicPath(weapon)
                        + " style=" + weapon.StyleDef?.defName + " exact=" + lastExact);
                }
                spec = lastSpec;
            }
            else { lastWeapon = null; lastGraphic = null; lastStyle = null; lastVerb = null; lastSpec = null; lastGraphicIndex = null; }
            bool gun = spec != null && spec.Kind != ModelKind.Blade && spec.Kind != ModelKind.Grenade && verb != null && !verb.verbProps.IsMeleeAttack;
            singleGun = gun && verb.verbProps.burstShotCount <= 1;
            int id = weapon?.thingIDNumber ?? -1;
            if (aimWeaponId != id || aimSpec != spec)
            {
                aimWeaponId = id; aimSpec = spec; scopeLatched = false; aimBlend = 0f;
                scopeStyle = gun ? (spec.Sprite != null ? ScopeStyle.ModernCombat : spec.Held != null && Enum.TryParse(spec.Held.Scope, out ScopeStyle heldScope) ? heldScope : spec.Import != null && Enum.TryParse(spec.Import.scope, out ScopeStyle importedScope) ? importedScope : ScopeRules.Style(weapon?.def.defName)) : ScopeStyle.None;
                scopeMagnification = spec?.Held != null && scopeStyle != ScopeStyle.None
                    ? ScopeRules.Limit(spec.Held.ScopeZoom, fovOwned ? baseFov : IsoRimMod.Settings.fpFov, scopeStyle) : ScopeRules.DefaultZoom(scopeStyle);
            }
            bool scoped = gun && scopeStyle != ScopeStyle.None;
            if (!allowAim || !scoped) scopeLatched = false;
            else
            {
                if (ControlKeys.Down(ControlAction.Aim)) scopeLatched = !scopeLatched;
                if (scopeLatched)
                {
                    float fov = fovOwned ? baseFov : IsoRimMod.Settings.fpFov;
                    scopeMagnification = ScopeRules.Zoom(scopeMagnification, Input.mouseScrollDelta.y, fov, scopeStyle);
                }
            }
            if (scoped) wantAim = scopeLatched;
            bool physicalIron = gun && !scoped && WeaponAimProfiles.UseIron(spec);
            simpleReticle = scoped || (!physicalIron && (singleGun || (gun && (!spec.Anchor("RearSightAperture", out _) || !spec.Anchor("FrontSightTip", out _)))));
            aimBlend = Mathf.MoveTowards(aimBlend, wantAim && gun ? 1f : 0f, dt * 7f);
            sprintBlend = Mathf.MoveTowards(sprintBlend, Stances.For(pawn) && Stances.State.Sprint && moving ? 1f : 0f, dt * 6f);
            kick *= Mathf.Exp(-14f * dt);
            ApplyFov();
            if (usingItem) return;
            ImportedRatkin.DrawFirstPersonShield(pawn, moving, aimBlend, sprintBlend, now);
            if (spec == null && weapon == null && MechControl.BladeArms(pawn)) { DrawBlades(moving, now); return; }
            if (spec == null) { Status = weapon == null ? null : L.T("无武器模型", "No weapon model"); return; }
            Status = spec.Sprite != null ? L.T("原贴图手持模型", "Original-texture held model") : spec.Id;
            Loaded l = spec.Held == null && spec.Sprite == null ? Load(spec) : null;
            if ((spec.Held == null && spec.Sprite == null && l == null) || !View3D.Active) return;
            if ((scoped || (spec.Held == null && singleGun && !physicalIron)) && Aiming) return;
            Pose(spec, moving, now, out Vector3 pos, out Quaternion rot, out float scale, out bool hidden);
            float reload = Magazines.ReloadPose;
            pos += new Vector3(.1f, -.24f, -.08f) * reload;
            rot = rot * Quaternion.Euler(14f * reload, 0f, -24f * reload);
            if (hidden) return;
            Quaternion view = Quaternion.Euler(FirstPerson.Pitch, FirstPerson.Yaw, 0f);
            Vector3 eye = FirstPerson.Eye;
            Matrix4x4 world = Matrix4x4.TRS(eye + view * pos, view * rot, Vector3.one * scale);
            Matrix4x4 render = Matrix4x4.Scale(new Vector3(1f, 1f / View3D.Squash, 1f)) * world;
            if (spec.Held != null) HeldWeaponModels.Draw(weapon, spec, render);
            else if (spec.Sprite != null) SpriteSolids.DrawSlab(spec.Sprite.Material, render * spec.Sprite.ToView);
            else Graphics.DrawMesh(l.mesh, render, l.material, 0);
        }

        // A Scyther's two arm blades, one each side of the view, reaching in from below. A light strike swings the
        // right blade, a heavy one the left; the mono-sword art stands in for the blades (longsword without the DLC).
        private static void DrawBlades(bool moving, float now)
        {
            ModelSpec spec = ModelSpecs.Get("MeleeWeapon_MonoSword") ?? ModelSpecs.Get("MeleeWeapon_LongSword");
            Status = spec == null ? null : L.T("机械刃 ", "Mech blades ") + spec.Id;
            Loaded l = spec == null ? null : Load(spec);
            if (l == null || !View3D.Active) return;
            float scale = Mathf.Min(1.1f, 0.75f / spec.Length);
            Vector3 bob = moving ? new Vector3(0f, Mathf.Abs(Mathf.Cos(now * 7f)) * 0.01f, 0f) : Vector3.zero;
            Quaternion view = Quaternion.Euler(FirstPerson.Pitch, FirstPerson.Yaw, 0f);
            Vector3 eye = FirstPerson.Eye;
            float u = (now - meleeStart) / 0.26f;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 pos = new Vector3(0.34f * side, -0.30f, 0.34f) + bob;
                Quaternion rot = Quaternion.Euler(-28f, -22f * side, -12f * side);
                bool striking = u >= 0f && u < 1f && (side > 0) != heavy;
                if (striking)
                {
                    // A forward hook across the centre of the view.
                    float k = Mathf.Sin(u * Mathf.PI), a = Mathf.Lerp(50f, -40f, u * u * (3f - 2f * u));
                    pos = Vector3.Lerp(pos, new Vector3(0.06f * side, -0.12f, 0.70f), k);
                    rot = Quaternion.Euler(-6f, a * side, 70f * side * k);
                }
                Matrix4x4 world = Matrix4x4.TRS(eye + view * pos, view * rot, Vector3.one * scale);
                Graphics.DrawMesh(l.mesh, Matrix4x4.Scale(new Vector3(1f, 1f / View3D.Squash, 1f)) * world, l.material, 0);
            }
        }

        // The model's pose in view space (x right, y up, z ahead of the eye).
        private static void Pose(ModelSpec spec, bool moving, float now, out Vector3 pos, out Quaternion rot, out float scale, out bool hidden)
        {
            hidden = false;
            // Both hands busy with a hug or a pat (SocialHands): the weapon is put away meanwhile.
            if (SocialHands.Busy) { hidden = true; pos = Vector3.zero; rot = Quaternion.identity; scale = 1f; return; }
            // A run swings the weapon faster and wider (0.7.0 sprint).
            float pace = 8f + 4f * sprintBlend, swing = 1f + 2.2f * sprintBlend;
            Vector3 bob = moving ? new Vector3(Mathf.Sin(now * pace) * 0.006f, Mathf.Abs(Mathf.Cos(now * pace)) * 0.007f, 0f) * swing * (1f - aimBlend * 0.8f) : Vector3.zero;
            switch (spec.Kind)
            {
                case ModelKind.Blade:
                {
                    scale = Mathf.Min(1f, 0.6f / spec.Length);
                    Vector3 idleP = new Vector3(0.22f, -0.27f, 0.40f);
                    Quaternion idleR = Quaternion.Euler(-38f, -14f, 0f);
                    float u = (now - meleeStart) / SwingPath.Seconds(swingKind);
                    if (u >= 0f && u < 1f)
                    {
                        float e = u * u * (3f - 2f * u), arc = Mathf.Sin(u * Mathf.PI);
                        switch (swingKind)
                        {
                            case SwingKind.Thrust:
                                pos = Vector3.Lerp(idleP, new Vector3(0.06f, -0.13f, 0.72f), arc);
                                rot = Quaternion.Slerp(idleR, Quaternion.Euler(-2f, -3f, 0f), arc);
                                break;
                            case SwingKind.Overhead:
                            {
                                // Raised over the right shoulder, brought straight down through the middle.
                                float p = Mathf.Lerp(-100f, 35f, e);
                                pos = new Vector3(Mathf.Lerp(0.16f, 0.03f, e), Mathf.Lerp(0.10f, -0.28f, e), 0.40f + 0.14f * arc);
                                rot = Quaternion.Euler(p, -6f, 0f);
                                break;
                            }
                            case SwingKind.Diagonal:
                            {
                                // From high right down to low left, the edge turned along the cut.
                                float a = Mathf.Lerp(55f, -50f, e), p = Mathf.Lerp(-55f, 30f, e);
                                pos = new Vector3(0.26f * Mathf.Sin(a * Mathf.Deg2Rad) + 0.04f, Mathf.Lerp(0.06f, -0.26f, e), 0.38f + 0.13f * arc);
                                rot = Quaternion.Euler(p, a, 45f);
                                break;
                            }
                            default:
                            {
                                // Sweep (light) and heavy slash: right to left across the view, the blade flat.
                                float a = Mathf.Lerp(65f, -65f, e);
                                pos = new Vector3(0.28f * Mathf.Sin(a * Mathf.Deg2Rad) + 0.04f, -0.16f - 0.06f * arc, 0.38f + 0.12f * arc);
                                rot = Quaternion.Euler(4f, a, 80f);
                                break;
                            }
                        }
                    }
                    else { pos = idleP; rot = idleR; }
                    pos += bob + new Vector3(0.02f, -0.05f, -0.05f) * sprintBlend;
                    return;
                }
                case ModelKind.Grenade:
                {
                    scale = Mathf.Min(1f, 0.18f / spec.Length);
                    rot = Quaternion.identity;
                    pos = new Vector3(0.20f, -0.20f, 0.46f) - spec.Center * scale + bob;
                    float u = (now - throwStart) / 0.5f;
                    if (u >= 0f && u < 1f)
                    {
                        if (u > 0.45f) { hidden = true; return; }
                        pos += new Vector3(-0.05f, 0.18f, 0.25f) * Mathf.Sin(u / 0.45f * Mathf.PI * 0.5f);
                    }
                    return;
                }
                default:
                {
                    scale = Mathf.Min(1f, 0.42f / spec.Length);
                    if (!spec.Anchor("VisualMuzzle", out Vector3 muzzle)) muzzle = new Vector3(0f, spec.Center.y, spec.Max.Z);
                    Quaternion hipR = spec.Sprite == null ? Quaternion.identity : Quaternion.Euler(5f, -18f, -8f);
                    Vector3 hipP = new Vector3(0.12f, -0.12f, 0.62f) - hipR * (muzzle * scale);
                    Vector3 adsP; Quaternion adsR;
                    if (spec.Held?.Scope != "None" && spec.Anchor("ScopeEye", out Vector3 scopeEye) && spec.Anchor("ScopeFront", out Vector3 scopeFront))
                    {
                        adsR = Quaternion.Inverse(Quaternion.LookRotation((scopeFront-scopeEye).normalized,Vector3.up));
                        adsP = new Vector3(0f,0f,WeaponAimProfiles.RearDistance(spec)) - adsR*(scopeEye*scale);
                    }
                    else if (spec.Anchor("RearSightAperture", out Vector3 rear) && spec.Anchor("FrontSightTip", out Vector3 front) && (front - rear).sqrMagnitude > 0.0009f)
                    {
                        // The sight line onto the view's centre line, the rear aperture just beyond the near plane.
                        adsR = Quaternion.Inverse(Quaternion.LookRotation((front - rear).normalized, Vector3.up));
                        adsP = new Vector3(0f, 0f, WeaponAimProfiles.RearDistance(spec)) - adsR * (rear * scale);
                    }
                    else
                    {
                        // No sight in the model (scoped and mounted weapons): the barrel under the centre.
                        adsR = Quaternion.identity;
                        adsP = new Vector3(0f, -0.075f, 0.5f) - muzzle * scale;
                    }
                    pos = Vector3.Lerp(hipP, adsP, aimBlend);
                    rot = Quaternion.Slerp(hipR, adsR, aimBlend);
                    // Running with the gun held across the body: muzzle low and to the left, pulled in.
                    float run = sprintBlend * (1f - aimBlend);
                    if (run > 0f)
                    {
                        pos += new Vector3(-0.07f, -0.08f, -0.09f) * run;
                        rot = Quaternion.Slerp(rot, Quaternion.Euler(24f, -40f, 22f) * rot, run);
                    }
                    float k = kick * kickSize * (1f - 0.5f * aimBlend);
                    pos += new Vector3(0f, 0.008f * k, -0.045f * k) + bob;
                    rot = Quaternion.Euler(-5f * k, 0f, 0f) * rot;
                    return;
                }
            }
        }

        // Iron sights use 1.35x; each optic has its own default/cap, eased with the same camera FOV.
        private static void ApplyFov()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (st == null) return;
            if (aimBlend <= 0f)
            {
                if (fovOwned) { st.fpFov = baseFov; fovOwned = false; }
                return;
            }
            if (!fovOwned) { baseFov = st.fpFov; fovOwned = true; }
            float mag = scopeStyle != ScopeStyle.None ? ScopeRules.Limit(scopeMagnification, baseFov, scopeStyle) : WeaponAimProfiles.Magnification(lastSpec);
            float target = 2f * Mathf.Atan(Mathf.Tan(baseFov * 0.5f * Mathf.Deg2Rad) / mag) * Mathf.Rad2Deg;
            st.fpFov = Mathf.Lerp(baseFov, target, aimBlend);
        }
    }
}
