using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    public enum ConsumableMotion { Auto, Drink, Pill, Powder, Spoon, Meat, Vegetable, Bandage, Inject, Smoke }
    // Optional per-item override for mod authors. Effects and consumption always belong to the native job.
    public sealed class ConsumableAnimationExtension : DefModExtension
    {
        public ConsumableMotion motion = ConsumableMotion.Auto;
    }

    internal static class ConsumableHands
    {
        private static Pawn owner;
        private static Job job;
        private static int toilIndex = -1, total, remaining;
        private static ThingDef item;
        private static ConsumableMotion motion;
        private static Material material;
        private static Mesh sphere, cylinder, vessel;
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private static int colorId;
        public static bool Busy => owner != null && owner.CurJob == job && remaining > 0;
        internal static float Progress => Busy ? Mathf.Clamp01(1f-(float)remaining/Math.Max(1,total)) : 0f;
        internal static float SecondsLeft => Busy ? remaining/60f : 0f;
        public static void DrawHud()
        {
            if (!Busy || InventoryPanel.Blocking || Bridge.MainTabBlocks) return;
            float width=320f,x=UI.screenWidth*.5f-width*.5f,y=UI.screenHeight-164f;
            GameFont font=Text.Font;TextAnchor anchor=Text.Anchor;Color color=GUI.color;
            try
            {
                Widgets.DrawBoxSolid(new Rect(x-8,y-23,width+16,46),new Color(0,0,0,.72f));
                Text.Font=GameFont.Small;Text.Anchor=TextAnchor.MiddleLeft;GUI.color=Color.white;
                string action=motion==ConsumableMotion.Bandage?L.T("包扎伤口", "Tending wounds"):L.T("使用：", "Using: ")+(item?.LabelCap??"");
                Widgets.Label(new Rect(x,y-23,width-65,23),action);
                Text.Anchor=TextAnchor.MiddleRight;
                Widgets.Label(new Rect(x+width-70,y-23,70,23),SecondsLeft.ToString("F1")+" s");
                Widgets.DrawBoxSolid(new Rect(x,y+3,width,9),new Color(.12f,.17f,.19f,.95f));
                Widgets.DrawBoxSolid(new Rect(x,y+3,width*Progress,9),motion==ConsumableMotion.Bandage?new Color(.35f,.95f,.65f):new Color(.35f,.75f,1f));
            }
            finally {Text.Font=font;Text.Anchor=anchor;GUI.color=color;}
        }
        public static bool Using(Pawn p)
        {
            string name = p?.CurJob?.def?.defName;
            return name == "Ingest" || name == "TendPatient" || name == "FPS_QuickTend";
        }
        public static void Reset() { owner = null; job = null; item = null; toilIndex = -1; total = remaining = 0; }
        private static bool Has(string s, params string[] words)
        { foreach (string w in words) if (s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }
        internal static ConsumableMotion Classify(ThingDef def)
        {
            if (def == null) return ConsumableMotion.Spoon;
            ConsumableMotion custom = def.GetModExtension<ConsumableAnimationExtension>()?.motion ?? ConsumableMotion.Auto;
            if (custom != ConsumableMotion.Auto) return custom;
            var food = def.ingestible;
            string effect = food?.ingestEffect?.defName ?? "";
            string sound = food?.ingestSound?.defName ?? "";
            string use = food?.ingestReportString ?? "";
            string id = def.defName + "|" + def.label;
            // Native use effects take precedence over generic names such as 'tea leaves' or 'drug meal'.
            if (Has(effect, "Inject") || Has(use, "Injecting", "注射")) return ConsumableMotion.Inject;
            if (Has(effect, "Smoke")) return ConsumableMotion.Smoke;
            if (Has(effect, "Snort") || Has(use, "Snorting", "吸食", "吸粉")) return ConsumableMotion.Powder;
            if (Has(effect, "Drink") || ((food?.foodType ?? 0) & (FoodTypeFlags.Fluid | FoodTypeFlags.Liquor)) != 0) return ConsumableMotion.Drink;
            if (Has(sound, "Pill")) return ConsumableMotion.Pill;
            if (def.IsDrug)
            {
                if (Has(id, "GoJuice", "injection", "针剂", "注射")) return ConsumableMotion.Inject;
                if (Has(id, "Smokeleaf", "joint", "cigarette", "香烟", "烟草")) return ConsumableMotion.Smoke;
                if (Has(id, "Yayo", "Flake", "powder", "薄片", "粉末", "吸粉")) return ConsumableMotion.Powder;
                if (Has(id, "beer", "tea", "wine", "juice", "water", "啤酒", "饮料", "果汁", "茶")) return ConsumableMotion.Drink;
                return ConsumableMotion.Pill;
            }
            if (def.IsMedicine) return ConsumableMotion.Bandage;
            FoodTypeFlags flags = food?.foodType ?? FoodTypeFlags.None;
            if ((flags & (FoodTypeFlags.Meal | FoodTypeFlags.Processed)) != 0) return ConsumableMotion.Spoon;
            if ((flags & (FoodTypeFlags.Meat | FoodTypeFlags.Corpse)) != 0) return ConsumableMotion.Meat;
            if ((flags & (FoodTypeFlags.VegetableOrFruit | FoodTypeFlags.Plant | FoodTypeFlags.Seed)) != 0) return ConsumableMotion.Vegetable;
            return ConsumableMotion.Spoon;
        }

        // Observe the real job's timed use stage. Never call Ingested, FinalizeTend or remove inventory here.
        // Observing also handles loading a save in the middle of eating/tending, without adding saved state.
        public static void Frame(Pawn p, bool input)
        {
            if (!Using(p)) { Reset(); return; }
            if (input && (ControlKeys.Held(ControlAction.Forward) || ControlKeys.Held(ControlAction.Backward)
                || ControlKeys.Held(ControlAction.Left) || ControlKeys.Held(ControlAction.Right)
                || ControlKeys.Held(ControlAction.Fire) || ControlKeys.Down(ControlAction.Aim)
                || ControlKeys.Down(ControlAction.Jump) || ControlKeys.Down(ControlAction.NextWeapon)
                || ControlKeys.Down(ControlAction.Reload)))
            { p.jobs.EndCurrentJob(JobCondition.InterruptForced); Reset(); return; }
            Job current = p.CurJob;
            JobDriver driver = p.jobs?.curDriver;
            string stage = driver?.CurToilString ?? "";
            bool eating = current.def.defName == "Ingest";
            bool working = eating ? stage == "ChewIngestible" : stage == "Wait" || stage == "WaitWith";
            if (!working || driver.ticksLeftThisToil <= 0) { Reset(); return; }
            if (p != owner || current != job || driver.CurToilIndex != toilIndex)
            {
                owner = p; job = current; toilIndex = driver.CurToilIndex;
                item = eating ? current.targetA.Thing?.def : current.targetB.Thing?.def;
                motion = eating ? Classify(item) : ConsumableMotion.Bandage;
                float seconds = eating ? item?.ingestible?.baseIngestTicks ?? 500 : 600;
                if (!eating || item?.ingestible?.useEatingSpeedStat == true)
                    seconds /= Mathf.Max(.05f, p.GetStatValue(eating ? StatDefOf.EatingSpeed : StatDefOf.MedicalTendSpeed));
                total = Math.Max(driver.ticksLeftThisToil, Mathf.RoundToInt(seconds));
                Magazines.CancelReload(); FireControl.CancelInput(); Stances.State.CancelSprint();
            }
            remaining = driver.ticksLeftThisToil;
        }

        private static bool Ready()
        {
            if (material != null) return true;
            material = R3DMaterials.Make(R3DMaterials.SolidDef, "FpsBridge_Consumables", -1, R3DMaterials.LightModel);
            if (material == null) return false;
            colorId = Shader.PropertyToID("_Color");
            Visuals.Primitives(out sphere, out cylinder);
            vessel = Vessel();
            return true;
        }
        // One reusable open cup/bowl, with actual inner walls and a rim, rather than a filled cylinder.
        private static Mesh Vessel()
        {
            var v = new List<Vector3>(); var tris = new List<int>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            { int n = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(d); tris.AddRange(new[] { n, n+2, n+1, n, n+3, n+2 }); }
            const int count = 20;
            for (int i=0; i<count; i++)
            {
                float a = i * Mathf.PI*2/count, b = (i+1)*Mathf.PI*2/count;
                Vector3 p = new Vector3(Mathf.Cos(a),0,Mathf.Sin(a)), q = new Vector3(Mathf.Cos(b),0,Mathf.Sin(b));
                Quad(p*.72f,q*.72f,q+Vector3.up,p+Vector3.up);
                Quad(p+Vector3.up,q+Vector3.up,q*.86f+Vector3.up,p*.86f+Vector3.up);
                Quad(p*.86f+Vector3.up,q*.86f+Vector3.up,q*.58f+Vector3.up*.14f,p*.58f+Vector3.up*.14f);
                Quad(Vector3.up*.14f,p*.58f+Vector3.up*.14f,q*.58f+Vector3.up*.14f,Vector3.up*.14f);
                Quad(Vector3.zero,q*.72f,p*.72f,Vector3.zero);
            }
            var mesh = new Mesh { name = "FPS reusable open vessel" };
            var colors = new Color32[v.Count]; for(int i=0;i<colors.Length;i++) colors[i]=new Color32(255,255,255,255);
            mesh.vertices = v.ToArray(); mesh.colors32=colors; mesh.triangles = tris.ToArray(); mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
        private static Matrix4x4 view;
        private static void Part(Mesh mesh, Vector3 at, Vector3 scale, Color color, Quaternion rotation)
        {
            block.SetColor(colorId, color);
            Graphics.DrawMesh(mesh, view * Matrix4x4.TRS(at, rotation, scale), material, 0, null, 0, block);
        }
        private static void Ball(Vector3 at, Vector3 scale, Color color) => Part(sphere, at, scale, color, Quaternion.identity);
        private static void Rod(Vector3 a, Vector3 b, float radius, Color color)
        { Part(cylinder, a, new Vector3(radius,(b-a).magnitude,radius),color,Quaternion.FromToRotation(Vector3.up,b-a)); }
        internal static Vector3 Hand(float phase, bool left)
        {
            float lift = .5f-.5f*Mathf.Cos(Mathf.Clamp01(phase)*Mathf.PI*2);
            return Vector3.Lerp(new Vector3(left?-.16f:.17f,-.29f,.39f),new Vector3(left?-.08f:.035f,-.10f,.18f),lift);
        }
        public static void Draw()
        {
            if (!Busy || !View3D.Active || ControlCamera.External || TurretControl.Manned || VehicleControl.On || !Ready()) return;
            float done = Mathf.Clamp01(1f-(float)remaining/Math.Max(1,total));
            float elapsed = (total-remaining)/60f, phase = elapsed/1.6f % 1f;
            float appear = Mathf.Clamp01((total-remaining)/10f) * Mathf.Clamp01(remaining/10f);
            view = Matrix4x4.Scale(new Vector3(1,1f/View3D.Squash,1))
                * Matrix4x4.TRS(FirstPerson.Eye,Quaternion.Euler(FirstPerson.Pitch,FirstPerson.Yaw,0),Vector3.one)
                * Matrix4x4.Translate(new Vector3(0,-.32f*(1-appear),0));
            Color skin = owner.story?.SkinColor ?? new Color(.92f,.76f,.62f); skin.a=1;
            Color metal = new Color(.72f,.76f,.8f), white = new Color(.94f,.92f,.84f);
            Vector3 right = Hand(phase,false), left = new Vector3(-.14f,-.26f,.37f);
            float lift = .5f-.5f*Mathf.Cos(phase*Mathf.PI*2);
            switch(motion)
            {
                case ConsumableMotion.Drink:
                    right=Vector3.Lerp(new Vector3(.14f,-.29f,.4f),new Vector3(.045f,-.1f,.2f),lift);
                    Part(vessel,right+new Vector3(-.035f,0,0),new Vector3(.055f,.12f,.055f),metal,Quaternion.Euler(-55*lift,0,-8*lift));
                    left=right+new Vector3(-.09f,-.04f,.025f); break;
                case ConsumableMotion.Pill:
                    Ball(left+new Vector3(0,.035f,0),new Vector3(.012f,.007f,.023f),white);
                    if(phase<.6f) Ball(right+new Vector3(-.025f,.025f,0),new Vector3(.009f,.006f,.018f),white);
                    break;
                case ConsumableMotion.Powder:
                    Part(vessel,left+new Vector3(.015f,.01f,.015f),new Vector3(.065f,.012f,.055f),metal,Quaternion.identity);
                    for(int i=0;i<5;i++) Ball(left+new Vector3(-.012f+i*.008f,.026f,.01f),new Vector3(.008f,.003f,.012f),white);
                    right=Vector3.Lerp(new Vector3(.15f,-.23f,.35f),new Vector3(.02f,-.045f,.2f),lift);
                    Rod(right,right+new Vector3(-.035f,.055f,-.045f),.004f,white); break;
                case ConsumableMotion.Meat:
                case ConsumableMotion.Vegetable:
                    Vector3 food=Vector3.Lerp(new Vector3(0,-.24f,.4f),new Vector3(0,-.085f,.2f),lift);
                    bool meat=motion==ConsumableMotion.Meat;
                    Ball(food,new Vector3(.1f,.068f,.06f)*(1f-.18f*done),meat?new Color(.58f,.17f,.12f):new Color(.27f,.58f,.15f));
                    if(meat) Rod(food+new Vector3(.07f,0,0),food+new Vector3(.16f,0,0),.017f,white);
                    else for(int i=0;i<3;i++) Ball(food+new Vector3((i-1)*.035f,.045f,.01f),new Vector3(.04f,.035f,.05f),new Color(.4f,.7f,.18f));
                    left=food+new Vector3(-.09f,-.035f,0); right=food+new Vector3(.09f,-.035f,0); break;
                case ConsumableMotion.Bandage:
                    left=new Vector3(-.08f,-.24f,.36f);
                    Rod(left-new Vector3(.075f,0,0),left+new Vector3(.07f,0,0),.035f,skin);
                    Part(cylinder,left-new Vector3(.025f,0,0),new Vector3(.04f,.05f,.04f),white,Quaternion.Euler(0,0,-90));
                    right=left+new Vector3(.025f,.06f*Mathf.Sin(phase*Mathf.PI*2),.06f*Mathf.Cos(phase*Mathf.PI*2));
                    Part(cylinder,right,new Vector3(.028f,.035f,.028f),white,Quaternion.Euler(90,0,0));
                    Rod(left,right,.007f,white); break;
                case ConsumableMotion.Inject:
                    right=Vector3.Lerp(new Vector3(.17f,-.23f,.38f),left+new Vector3(.075f,.04f,0),lift);
                    Rod(right,right-new Vector3(.075f,0,0),.012f,white);
                    Rod(right-new Vector3(.075f,0,0),right-new Vector3(.12f,0,0),.002f,metal); break;
                case ConsumableMotion.Smoke:
                    Rod(right,right+new Vector3(-.025f,.02f,-.06f),.008f,white);
                    Ball(right+new Vector3(0,0,.01f),Vector3.one*.008f,new Color(.95f,.25f,.04f)); break;
                default:
                    Part(vessel,left+new Vector3(.03f,0,0),new Vector3(.09f,.06f,.09f),white,Quaternion.identity);
                    Ball(left+new Vector3(.03f,.035f,0),new Vector3(.07f,.012f,.07f),new Color(.57f,.38f,.16f));
                    right=Vector3.Lerp(left+new Vector3(.14f,.07f,.01f),new Vector3(.055f,-.09f,.19f),lift);
                    Vector3 tip=right+new Vector3(-.07f,.025f,-.025f);
                    Rod(right,tip,.005f,metal); Ball(tip,new Vector3(.018f,.004f,.025f),metal); break;
            }
            Ball(left,new Vector3(.045f,.035f,.048f),skin); Ball(right,new Vector3(.045f,.035f,.048f),skin);
        }
    }
}
