using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Everything the bridge draws in the 3D view besides the weapon model: tracers, the grenade's arc and landing ring,
    // the Alt hit-volume view. Points are in true ground space (x east, z north, y height in cells, no floor offset); the
    // caller's floor offset is added and the y axis is squashed as IsoRim draws (drawn y = true y / Squash).
    internal static class Visuals
    {
        private static bool tried;
        private static Material blend, overlay;
        private static Mesh tracer, quad, cube, sphere, cylinder, ring;
        private static Mesh aerialCone;
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private static int colorId;

        public static readonly Color TracerCore = new Color(1f, 1f, .9f, 1f);
        public static readonly Color TracerGlow = new Color(1f, .78f, .32f, .65f);
        public static readonly Color HeadColor = new Color(1f, 0.25f, 0.25f, 0.33f);
        public static readonly Color TorsoColor = new Color(1f, 0.85f, 0.2f, 0.26f);
        public static readonly Color LegColor = new Color(0.25f, 0.85f, 1f, 0.3f);
        public static readonly Color SolidColor = new Color(0.7f, 0.7f, 0.75f, 0.12f);

        private static bool Ready()
        {
            if (tried) return blend != null && overlay != null;
            tried = true;
            colorId = Shader.PropertyToID("_Color");
            blend = R3DMaterials.Make(R3DMaterials.BlendDef, "HSKFps_Blend", 3060, R3DMaterials.LightUnlit);
            overlay = R3DMaterials.Make(R3DMaterials.OverlayDef, "HSKFps_Overlay", 3660, R3DMaterials.LightUnlit);
            if (blend == null || overlay == null) { Log.Warning("[FpsBridge] IsoRim blend/overlay shaders missing: tracers and overlays are off."); return false; }
            tracer = Quad(true); quad = Quad(false); cube = Cube(); sphere = Sphere(10, 7); cylinder = Cylinder(12); ring = Ring(40, 0.86f);
            return true;
        }

        // The unit sphere (radius 1) and cylinder (radius 1, y 0..1), white, for solid shapes drawn elsewhere (the hands).
        public static bool Primitives(out Mesh unitSphere, out Mesh unitCylinder)
        {
            bool ok = Ready();
            unitSphere = sphere; unitCylinder = cylinder;
            return ok && sphere != null && cylinder != null;
        }
        internal static void AerialWeapon(Vector3 muzzle,Quaternion rotation,bool rocket,AerialWeaponExtension data=null)
        {
            if(!View3D.Active||!Ready())return;
            Quaternion q=rotation*Quaternion.Euler(90,0,0);
            float radius=data?.barrelRadius??(rocket?.3f:.07f),length=data?.barrelLength??.55f;int count=Math.Clamp(data?.barrels??1,1,8);
            for(int i=0;i<count;i++)
            {
                float angle=i*Mathf.PI*2/count;
                Vector3 spread=count==1?Vector3.zero:rotation*new Vector3(Mathf.Cos(angle)*radius*2.1f,Mathf.Sin(angle)*radius*2.1f,0);
                Draw(cylinder,Matrix4x4.TRS(muzzle+spread-rotation*Vector3.forward*length,q,new Vector3(radius,length,radius)),Vector3.zero,blend,new Color(.16f,.19f,.2f,1f));
            }
        }
        private static Mesh rangeRing, aircraftRangeSphere;
        internal static void TurretRange(Vector3 origin, float radius)
        {
            if (!Ready()) return;
            rangeRing ??= Ring(80, .993f);
            var scale = Vector3.one * radius;
            var tint = new Color(.25f, .8f, 1f, .8f);
            Draw(sphere, Matrix4x4.TRS(origin, Quaternion.identity, scale), Vector3.zero, blend,
                new Color(tint.r, tint.g, tint.b, .08f));
            Draw(rangeRing, Matrix4x4.TRS(origin, Quaternion.identity, scale), Vector3.zero, blend, tint);
            Draw(rangeRing, Matrix4x4.TRS(origin, Quaternion.Euler(90, 0, 0), scale), Vector3.zero, blend, tint);
            Draw(rangeRing, Matrix4x4.TRS(origin, Quaternion.Euler(0, 0, 90), scale), Vector3.zero, blend, tint);
        }

        internal static bool AircraftRangeSphere(Vector3 origin, float radius, Color color)
        {
            if (Find.Camera == null || !Ready()) return false;
            if (aircraftRangeSphere == null)
            {
                aircraftRangeSphere = Sphere(64, 32);
                var vertices = aircraftRangeSphere.vertices;
                var transparency = new Color32[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    // Bake the brighter rim along the local Z view axis and rotate the sphere at draw time.
                    float rim = 1f - Mathf.Abs(vertices[i].z);
                    byte alpha = (byte)Mathf.RoundToInt(255f * Mathf.Lerp(.04f, .34f, rim * rim * rim));
                    transparency[i] = new Color32(255, 255, 255, alpha);
                }
                aircraftRangeSphere.colors32 = transparency;
            }
            Vector3 towardCamera = -Find.Camera.transform.forward;
            towardCamera.y *= View3D.Squash;
            var orientation = Quaternion.FromToRotation(Vector3.forward, towardCamera.normalized);
            Draw(aircraftRangeSphere, Matrix4x4.TRS(origin, orientation, Vector3.one * radius), Vector3.zero, blend, color);
            return true;
        }

        private static Mesh firingSector;
        internal static bool AircraftFiringSector(Vector3 origin,Vector3 direction,float radius)
        {
            if (Find.Camera == null || !Ready()) return false;
            if (firingSector == null)
            {
                const int sides=48, rings=8;
                var vertices=new List<Vector3>{Vector3.zero};
                var faces=new List<int>();
                for(int row=0;row<=rings;row++)
                {
                    float inclination=AerialAiAim.HalfAngle*Mathf.Deg2Rad*row/rings;
                    for(int i=0;i<sides;i++)
                    {
                        float azimuth=2f*Mathf.PI*i/sides;
                        vertices.Add(new Vector3(Mathf.Sin(inclination)*Mathf.Cos(azimuth),Mathf.Sin(inclination)*Mathf.Sin(azimuth),Mathf.Cos(inclination)));
                        int a=1+row*sides+i,b=1+row*sides+(i+1)%sides;
                        if(row==rings)faces.AddRange(new[]{0,b,a});
                        else faces.AddRange(new[]{a,b,b+sides,a,b+sides,a+sides});
                    }
                }
                var both=new List<int>(faces);
                for(int i=0;i<faces.Count;i+=3)both.AddRange(new[]{faces[i],faces[i+2],faces[i+1]});
                firingSector=new Mesh{name="Aircraft AI firing sector",vertices=vertices.ToArray(),triangles=both.ToArray()};
                var color=new Color[vertices.Count];
                Array.Fill(color,Color.white);firingSector.colors=color;firingSector.RecalculateBounds();
            }
            Draw(firingSector,Matrix4x4.TRS(origin,Quaternion.FromToRotation(Vector3.forward,direction),Vector3.one*radius),
                Vector3.zero,blend,new Color(1f,.18f,.13f,.10f));
            return true;
        }

        internal static void GunFlash(V3 position,V3 direction,float opacity)
        {
            if(!Ready())return;
            Vector3 axis=VehicleModels.U(direction).normalized;
            Vector3 center=VehicleModels.U(position)+axis*.13f;
            var rotation=Quaternion.FromToRotation(Vector3.forward,axis);
            Draw(sphere,Matrix4x4.TRS(center,rotation,new Vector3(.20f,.20f,.43f)),Vector3.zero,blend,new Color(1f,.50f,.10f,.65f*opacity));
            Draw(sphere,Matrix4x4.TRS(center,rotation,new Vector3(.10f,.10f,.30f)),Vector3.zero,blend,new Color(1f,1f,.68f,.9f*opacity));
        }

        internal static int OrdnanceDraws;
        private static Mesh fragment;
        internal static bool Fragment(V3 position, V3 velocity, float authoredSize, int seed)
        {
            if (!View3D.Active || !Ready()) return false;
            if (fragment == null)
            {
                var corners = new[] {
                    new Vector3(-.7f, -.3f, -.6f), new Vector3(.5f, -.5f, -.4f),
                    new Vector3(.6f, .4f, -.2f), new Vector3(-.4f, .5f, -.3f),
                    new Vector3(.1f, .05f, 1f), new Vector3(-.15f, -.1f, -.8f)
                };
                var faces = new[] { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4,
                    1, 0, 5, 2, 1, 5, 3, 2, 5, 0, 3, 5 };
                var vertices = new Vector3[faces.Length];
                var indices = new int[faces.Length];
                var colors = new Color32[faces.Length];
                Vector3 light = new Vector3(.3f, .8f, -.5f).normalized;
                for (int face = 0; face < faces.Length; face += 3)
                {
                    Vector3 a = corners[faces[face]], b = corners[faces[face + 1]], c = corners[faces[face + 2]];
                    Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                    byte shade = (byte)(115f + 140f * Mathf.Max(0, Vector3.Dot(normal, light)));
                    for (int vertex = 0; vertex < 3; vertex++)
                    {
                        int index = face + vertex;
                        vertices[index] = corners[faces[index]];
                        indices[index] = index;
                        colors[index] = new Color32(shade, shade, shade, 255);
                    }
                }
                fragment = Make(vertices, indices, colors);
                fragment.name = "HSK_CE_Fragment3D";
            }
            Vector3 direction = U(velocity).normalized;
            if (direction.sqrMagnitude < .00001f) direction = Vector3.forward;
            Quaternion rotation = Quaternion.FromToRotation(Vector3.forward, direction)
                * Quaternion.Euler(0, 0, seed * 37f + Time.time * 180f);
            float size = Mathf.Clamp(authoredSize * .07f, .035f, .13f);
            Draw(fragment, Matrix4x4.TRS(U(position), rotation, new Vector3(size, size * .75f, size * 1.8f)),
                Vector3.zero, blend, new Color(.9f, .68f, .32f, 1f));
            return true;
        }
        internal static void Ordnance(V3 position,V3 velocity)
        {
            if(!View3D.Active||!Ready())return;
            Vector3 p=U(position),d=U(velocity).normalized;if(d.sqrMagnitude<.01f)d=Vector3.forward;
            Quaternion q=Quaternion.FromToRotation(Vector3.forward,d);
            Draw(sphere,Matrix4x4.TRS(p,q,new Vector3(.045f,.045f,.16f)),Vector3.zero,blend,new Color(.44f,.48f,.23f,1));
            Draw(cylinder,Matrix4x4.TRS(p-d*.12f,q*Quaternion.Euler(90,0,0),new Vector3(.05f,.035f,.05f)),Vector3.zero,blend,new Color(.9f,.7f,.2f,1));
            OrdnanceDraws++;
        }
        internal static void AerialOrdnanceTrail(V3 position, V3 velocity)
        {
            if (!View3D.Active || !Ready() || velocity.Length < .001f) return;
            Vector3 head = U(position), direction = U(velocity).normalized;
            Vector3 eye = EyeGround(Vector3.zero);
            Vector3 exhaust = head - direction * .16f;
            Draw(tracer, Strip(exhaust - direction * 2.0f, exhaust, .12f, eye),
                Vector3.zero, blend, new Color(.75f, .67f, .57f, .62f));
            Draw(tracer, Strip(exhaust - direction * .65f, exhaust, .055f, eye),
                Vector3.zero, blend, new Color(1f, .74f, .32f, .9f));
        }

        internal static void AerialJet(Vector3 at,Vector3 direction,float length,float radius,bool blue)
        {
            if(!View3D.Active||!Ready()||length<.015f||radius<.002f)return;
            if(aerialCone==null)
            {
                const int n=12;var verts=new Vector3[n+2];var tris=new int[n*6];verts[0]=Vector3.up;verts[n+1]=Vector3.zero;
                for(int i=0;i<n;i++){float a=i*Mathf.PI*2/n;verts[i+1]=new Vector3(Mathf.Cos(a),0,Mathf.Sin(a));int j=(i+1)%n;int t=i*6;tris[t]=i+1;tris[t+1]=0;tris[t+2]=j+1;tris[t+3]=n+1;tris[t+4]=i+1;tris[t+5]=j+1;}
                aerialCone=new Mesh{name="HSK_AircraftPlume"};aerialCone.vertices=verts;aerialCone.triangles=tris;aerialCone.RecalculateNormals();aerialCone.RecalculateBounds();
                var colors=new Color[n+2];for(int i=0;i<colors.Length;i++)colors[i]=Color.white;aerialCone.colors=colors;
            }
            Quaternion q=Quaternion.FromToRotation(Vector3.up,direction.normalized);
            Draw(aerialCone,Matrix4x4.TRS(at,q,new Vector3(radius,length,radius)),Vector3.zero,blend,
                blue?new Color(.15f,.5f,1f,.6f):new Color(1f,.32f,.04f,.65f));
            Draw(aerialCone,Matrix4x4.TRS(at,q,new Vector3(radius*.55f,length*.65f,radius*.55f)),Vector3.zero,blend,new Color(1f,1f,.7f,.95f));
        }

        // The matrix as IsoRim draws: floor offset added, height squashed.
        private static Matrix4x4 Drawn(Matrix4x4 trueMatrix, Vector3 offset) =>
            Matrix4x4.Scale(new Vector3(1f, 1f / View3D.Squash, 1f)) * Matrix4x4.Translate(offset) * trueMatrix;

        private static void Draw(Mesh mesh, Matrix4x4 trueMatrix, Vector3 offset, Material mat, Color color)
        {
            block.SetColor(colorId, color);
            Graphics.DrawMesh(mesh, Drawn(trueMatrix, offset), mat, 0, null, 0, block);
        }

        private static Material explosionLight;

        internal static bool DrawExplosionMesh(Mesh mesh, bool luminous)
        {
            if (!Ready()) return false;
            if (luminous) explosionLight ??= R3DMaterials.Make(R3DMaterials.BlendDef, "HSKFps_ExplosionLight", 3061, R3DMaterials.LightUnlit);
            Draw(mesh, Matrix4x4.identity, Vector3.zero, luminous && explosionLight != null ? explosionLight : blend, Color.white);
            return true;
        }

        private static Vector3 U(V3 v) => new Vector3(v.X, v.Y, v.Z);

        // The eye in true ground space (no floor offset), for facing billboards.
        private static Vector3 EyeGround(Vector3 offset) => ControlCamera.RenderEye() - offset;

        // A flat strip from a to b, turned about its own axis to face the eye.
        private static Matrix4x4 Strip(Vector3 a, Vector3 b, float width, Vector3 eye)
        {
            Vector3 axis = b - a;
            float len = axis.magnitude;
            if (len < 1e-5f) return Matrix4x4.zero;
            Vector3 dir = axis / len;
            Vector3 toEye = eye - (a + b) * 0.5f;
            Vector3 side = Vector3.Cross(dir, toEye);
            if (side.sqrMagnitude < 1e-8f) side = Vector3.Cross(dir, Vector3.up);
            if (side.sqrMagnitude < 1e-8f) side = Vector3.right;
            side = side.normalized;
            Vector3 normal = Vector3.Cross(side, dir);
            Matrix4x4 m = Matrix4x4.identity;
            m.SetColumn(0, new Vector4(side.x * width, side.y * width, side.z * width, 0f));
            m.SetColumn(1, new Vector4(axis.x, axis.y, axis.z, 0f));
            m.SetColumn(2, new Vector4(normal.x, normal.y, normal.z, 0f));
            m.SetColumn(3, new Vector4(a.x, a.y, a.z, 1f));
            return m;
        }

        // ---- tracers

        // A glowing streak behind a round in flight: its head at pos, len cells back along the velocity, fading at the tail.
        // 0.8.1: a small, very bright yellow round in 3D: a yellow glow streak behind it, a pale-yellow hot core, and a
        // small glowing slug at the head (a sphere, so it reads from any side, also head-on).
        private static readonly Color TracerHaze = new Color(1f, .82f, 0f, .4f), TracerYellow = new Color(1f, .92f, .05f, 1f),
            TracerHot = new Color(1f, 1f, .7f, 1f), HeadGlow = new Color(1f, .9f, .1f, 1f);

        public static void Tracer(V3 pos, V3 vel, float len, Vector3 offset, float sizeMultiplier = 1f)
        {
            if (!View3D.Active || !Ready() || len <= 0.02f) return;
            float speed = vel.Length;
            if (speed < 1e-5f) return;
            Vector3 head = U(pos), tail = head - U(vel) * (len / speed);
            Vector3 eye = EyeGround(offset);
            // Thin up close, wider far away so a distant round still shows.
            float far = Mathf.Clamp((head - eye).magnitude / 25f, 0f, 2.5f);
            float visibility = FpsMod.Settings.tracerVisibility, w = visibility * (1f + far) * sizeMultiplier;
            Draw(tracer, Strip(tail, head, .09f * w, eye), offset, blend, TracerHaze);
            Draw(tracer, Strip(tail, head, .045f * w, eye), offset, blend, TracerYellow);
            Draw(tracer, Strip(tail, head, .02f * w, eye), offset, blend, TracerHot);
            Draw(sphere, Matrix4x4.TRS(head, Quaternion.identity, Vector3.one * .07f * w), offset, blend, HeadGlow);
            Draw(sphere, Matrix4x4.TRS(head, Quaternion.identity, Vector3.one * .03f * w), offset, blend, TracerHot);
        }
        internal static bool StyledTracer(V3 position, V3 velocity, float distance, float scale, TurretTracers.Profile profile)
        {
            if (profile == null || !View3D.Active || !Ready() || distance <= .02f || velocity.Length < .00001f) return false;
            Vector3 head = U(position);
            Vector3 eye = View3D.EyePosition;
            float width = scale * FpsMod.Settings.tracerVisibility * (1f + Mathf.Clamp((head - eye).magnitude / 25f, 0f, 2.5f));
            Vector3 tail = head - U(velocity).normalized * Mathf.Min(profile.Length, distance);
            Draw(tracer, Strip(tail, head, .11f * width * profile.Width, eye), Vector3.zero, blend, profile.Glow);
            Draw(tracer, Strip(tail, head, .034f * width * profile.Width, eye), Vector3.zero, blend, profile.Core);
            Draw(sphere, Matrix4x4.TRS(head, Quaternion.identity, Vector3.one * (profile.Head * width)), Vector3.zero, blend, profile.Core);
            return true;
        }

        // Compact emissive energy packet, with two moving electric arcs; it follows the same physical 3D shot.
        public static void Pulse(V3 pos,V3 vel,float len,Vector3 offset,int seed)
        {
            if(!View3D.Active || !Ready() || vel.Length<1e-5f)return;
            Vector3 head=U(pos),dir=U(vel).normalized,eye=EyeGround(offset);
            float w=FpsMod.Settings.tracerVisibility*(1+Mathf.Clamp((head-eye).magnitude/30,0,1.8f));
            Quaternion turn=Quaternion.FromToRotation(Vector3.forward,dir);
            Vector3 tail=head-dir*Mathf.Min(.65f,len);
            Draw(tracer,Strip(tail,head,.07f*w,eye),offset,blend,new Color(.03f,.3f,1f,.55f));
            Draw(tracer,Strip(tail,head,.025f*w,eye),offset,blend,new Color(.4f,.85f,1f,.9f));
            Draw(sphere,Matrix4x4.TRS(head,turn,new Vector3(.12f,.12f,.25f)*w),offset,blend,new Color(.04f,.35f,1f,.35f));
            Draw(sphere,Matrix4x4.TRS(head,turn,new Vector3(.065f,.065f,.15f)*w),offset,blend,new Color(.15f,.7f,1f,.95f));
            Draw(sphere,Matrix4x4.TRS(head,turn,new Vector3(.025f,.025f,.10f)*w),offset,blend,new Color(.75f,.95f,1f,1));
            for(int arc=0;arc<2;arc++)
            {
                Vector3 previous=head+turn*PulseArc(0,Time.unscaledTime,seed,arc)*w;
                for(int i=1;i<=5;i++)
                {
                    Vector3 next=head+turn*PulseArc(i/5f,Time.unscaledTime,seed,arc)*w;
                    Draw(tracer,Strip(previous,next,.012f*w,eye),offset,blend,new Color(.3f,.85f,1f,.9f));previous=next;
                }
            }
        }
        internal static Vector3 PulseArc(float phase,float time,int seed,int arc)
        {
            float angle=phase*Mathf.PI*3+time*22+(seed%101)*.31f+arc*Mathf.PI;
            float radius=.075f*Mathf.Sin(phase*Mathf.PI);
            return new Vector3(Mathf.Cos(angle)*radius,Mathf.Sin(angle)*radius,.12f-.42f*phase);
        }

        // A short pulse, drawn at the actual 3D contact rather than a 2D interaction mote on the source floor.
        public static void BeamLine(V3 start, V3 end, float fade)
        {
            if (!View3D.Active || !Ready() || (end - start).Length < .01f) return;
            Vector3 a = U(start), b = U(end), eye = EyeGround(Vector3.zero);
            float width = FpsMod.Settings.tracerVisibility;
            Draw(quad, Strip(a, b, .10f * width, eye), Vector3.zero, blend, new Color(.6f, .85f, 1f, .6f * fade));
            Draw(quad, Strip(a, b, .025f * width, eye), Vector3.zero, blend, new Color(1f, 1f, 1f, fade));
        }

        // ---- impacts (the game's own flecks)

        // A round's contact: a red flash and blood for anything of flesh, a spark burst and an orange flash for the rest.
        public static void ImpactLight(Map map, V3 pos, Thing thing)
        {
            if (map == null) return;
            Vector3 at = new Vector3(pos.X, Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead), pos.Z);
            IntVec3 cell = at.ToIntVec3();
            if (!cell.InBounds(map)) return;
            if (thing is Pawn pawn && pawn.RaceProps != null && pawn.RaceProps.IsFlesh)
            {
                Flash(map, at, FleckDefOf.ExplosionFlash, 0.55f, new Color(0.85f, 0.05f, 0.03f, 0.9f));
                Flash(map, at, FleckDefOf.FireGlow, 0.45f, new Color(0.7f, 0.02f, 0.02f, 0.7f));
                ThingDef blood = pawn.RaceProps.BloodDef;
                if (blood != null) FilthMaker.TryMakeFilth(cell, map, blood, 1, FilthSourceFlags.None, true);
                return;
            }
            FleckMaker.ThrowMicroSparks(at, map);
            Flash(map, at, FleckDefOf.ExplosionFlash, 0.4f, new Color(1f, 0.6f, 0.2f, 0.85f));
            if (thing == null && pos.Y < 0.2f) FleckMaker.Static(at, map, FleckDefOf.ShotHit_Dirt, 1f);
        }

        private static void Flash(Map map, Vector3 at, FleckDef def, float scale, Color color)
        {
            if (def == null) return;
            FleckCreationData data = FleckMaker.GetDataStatic(at, map, def, scale);
            data.instanceColor = color;
            map.flecks.CreateFleck(data);
        }

        // ---- grenade arc

        // Dashed: every other piece of the predicted path, and a ring of the blast radius where it lands.
        public static void ThrowPreview(List<V3> path, V3 landing, float radius, Vector3 offset)
        {
            if (!View3D.Active || !Ready() || path.Count < 2) return;
            Vector3 eye = EyeGround(offset);
            Color dash = new Color(1f, 1f, 1f, 0.75f);
            float run = 0f;
            const float on = 0.35f, period = 0.6f;
            for (int i = 1; i < path.Count; i++)
            {
                Vector3 a = U(path[i - 1]), b = U(path[i]);
                float len = (b - a).magnitude;
                if (len < 1e-5f) continue;
                // Pieces of the step that fall in the "on" part of the period.
                float t = 0f;
                while (t < len)
                {
                    float phase = (run + t) % period;
                    float step = phase < on ? Mathf.Min(on - phase, len - t) : Mathf.Min(period - phase, len - t);
                    if (phase < on)
                    {
                        Vector3 p0 = a + (b - a) * (t / len), p1 = a + (b - a) * ((t + step) / len);
                        float w = 0.025f + 0.004f * (p0 - eye).magnitude;
                        Draw(quad, Strip(p0, p1, w, eye), offset, overlay, dash);
                    }
                    t += Mathf.Max(step, 1e-3f);
                }
                run += len;
            }
            float r = Mathf.Max(0.35f, radius);
            Matrix4x4 flat = Matrix4x4.TRS(new Vector3(landing.X, Mathf.Max(0.02f, landing.Y) + 0.02f, landing.Z), Quaternion.identity, new Vector3(r, 1f, r));
            Draw(ring, flat, offset, overlay, new Color(1f, 0.45f, 0.2f, 0.8f));
            Draw(ring, flat * Matrix4x4.Scale(new Vector3(0.12f / r, 1f, 0.12f / r)), offset, overlay, new Color(1f, 1f, 1f, 0.9f));
        }

        // ---- artillery map (true ground space, no floor offset)

        public static void Line(Vector3 a, Vector3 b, Color color, float width = .05f)
        {
            if (!View3D.Active || !Ready() || (b - a).sqrMagnitude < 1e-6f) return;
            Vector3 eye = EyeGround(Vector3.zero);
            Draw(quad, Strip(a, b, width + .003f * (a - eye).magnitude, eye), Vector3.zero, overlay, color);
        }
        internal static void ContactEdge(Vector3 a,Vector3 b,Color color)
        {if(View3D.Active&&Ready()&&(b-a).sqrMagnitude>1e-8f)Draw(quad,Strip(a,b,.006f,EyeGround(Vector3.zero)),Vector3.zero,overlay,color);}

        public static void Polyline(List<Vector3> points, Color color, float width = .05f)
        {
            for (int i = 1; i < points.Count; i++) Line(points[i - 1], points[i], color, width);
        }

        public static void GroundRing(Vector3 center, float radius, Color color)
        {
            if (!View3D.Active || !Ready() || radius <= .05f) return;
            Matrix4x4 flat = Matrix4x4.TRS(center + Vector3.up * .03f, Quaternion.identity, new Vector3(radius, 1f, radius));
            Draw(ring, flat, Vector3.zero, overlay, color);
        }

        public static void GroundSquare(Vector3 center, float half, Color color)
        {
            Vector3 y = Vector3.up * .04f, a = center + y + new Vector3(-half, 0f, -half), b = center + y + new Vector3(half, 0f, -half),
                c = center + y + new Vector3(half, 0f, half), d = center + y + new Vector3(-half, 0f, half);
            Line(a, b, color, .07f); Line(b, c, color, .07f); Line(c, d, color, .07f); Line(d, a, color, .07f);
        }

        // ---- hit volumes (Alt)

        public static Color RegionColor(HitRegion region) =>
            region == HitRegion.Head ? HeadColor : region == HitRegion.Leg ? LegColor : region == HitRegion.Torso ? TorsoColor : SolidColor;

        internal static bool HitMesh(Mesh mesh, Matrix4x4 trueMatrix)
        {
            if (!View3D.Active || !Ready()) return false;
            Draw(mesh, trueMatrix, Vector3.zero, overlay, TorsoColor);
            return true;
        }

        public static void Volume(in HitVolume v, Vector3 offset)
        {
            if (!View3D.Active || !Ready()) return;
            Color c = RegionColor(v.Region);
            switch (v.Kind)
            {
                case VolumeKind.Sphere:
                    Draw(sphere, Matrix4x4.TRS(U(v.A), Quaternion.identity, Vector3.one * v.Radius), offset, overlay, c);
                    break;
                case VolumeKind.Capsule:
                {
                    Vector3 a = U(v.A), b = U(v.B), axis = b - a;
                    float len = axis.magnitude;
                    Quaternion rot = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, axis / len) : Quaternion.identity;
                    Draw(cylinder, Matrix4x4.TRS(a, rot, new Vector3(v.Radius, len, v.Radius)), offset, overlay, c);
                    Draw(sphere, Matrix4x4.TRS(a, Quaternion.identity, Vector3.one * v.Radius), offset, overlay, c);
                    Draw(sphere, Matrix4x4.TRS(b, Quaternion.identity, Vector3.one * v.Radius), offset, overlay, c);
                    break;
                }
                default:
                    Draw(cube, Matrix4x4.TRS(U(v.A), Quaternion.AngleAxis(v.Yaw, Vector3.up), new Vector3(v.B.X * 2f, v.B.Y * 2f, v.B.Z * 2f)), offset, overlay, c);
                    break;
            }
        }

        // ---- meshes (white vertex colours: the shader multiplies vertex colour by _Color)

        private static Mesh Make(Vector3[] v, int[] t, Color32[] c = null)
        {
            var m = new Mesh();
            m.vertices = v;
            if (c == null) { c = new Color32[v.Length]; for (int i = 0; i < c.Length; i++) c[i] = new Color32(255, 255, 255, 255); }
            m.colors32 = c;
            m.triangles = t;
            m.RecalculateBounds();
            return m;
        }

        // x across -0.5..0.5, y along 0..1. The tracer's fades from transparent (tail) to full (head).
        private static Mesh Quad(bool fade)
        {
            var v = new[] { new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f),
                            new Vector3(0f, 0.85f, 0f) };
            byte tail = fade ? (byte)0 : (byte)255;
            var c = new[] { new Color32(255, 255, 255, tail), new Color32(255, 255, 255, tail), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            return Make(v, new[] { 0, 2, 1, 0, 3, 2 }, c);
        }

        private static Mesh Cube()
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int axis = 0; axis < 3; axis++) for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 n = Vector3.zero; n[axis] = sign * 0.5f;
                Vector3 u = Vector3.zero; u[(axis + 1) % 3] = 0.5f;
                Vector3 w = Vector3.zero; w[(axis + 2) % 3] = 0.5f;
                int b = v.Count;
                v.Add(n - u - w); v.Add(n + u - w); v.Add(n + u + w); v.Add(n - u + w);
                t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
            return Make(v.ToArray(), t.ToArray());
        }

        private static Mesh Sphere(int around, int rings)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int r = 0; r <= rings; r++)
            {
                float phi = Mathf.PI * r / rings;
                for (int a = 0; a <= around; a++)
                {
                    float th = 2f * Mathf.PI * a / around;
                    v.Add(new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th)));
                }
            }
            for (int r = 0; r < rings; r++) for (int a = 0; a < around; a++)
            {
                int i = r * (around + 1) + a, j = i + around + 1;
                t.AddRange(new[] { i, j, i + 1, i + 1, j, j + 1 });
            }
            return Make(v.ToArray(), t.ToArray());
        }

        // Radius 1, from y 0 to y 1, open ends (the capsule's spheres close it).
        private static Mesh Cylinder(int around)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int a = 0; a <= around; a++)
            {
                float th = 2f * Mathf.PI * a / around;
                v.Add(new Vector3(Mathf.Cos(th), 0f, Mathf.Sin(th)));
                v.Add(new Vector3(Mathf.Cos(th), 1f, Mathf.Sin(th)));
            }
            for (int a = 0; a < around; a++)
            {
                int i = a * 2;
                t.AddRange(new[] { i, i + 1, i + 2, i + 2, i + 1, i + 3 });
            }
            return Make(v.ToArray(), t.ToArray());
        }

        // A flat annulus in xz, outer radius 1.
        private static Mesh Ring(int around, float inner)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int a = 0; a <= around; a++)
            {
                float th = 2f * Mathf.PI * a / around;
                v.Add(new Vector3(Mathf.Cos(th) * inner, 0f, Mathf.Sin(th) * inner));
                v.Add(new Vector3(Mathf.Cos(th), 0f, Mathf.Sin(th)));
            }
            for (int a = 0; a < around; a++)
            {
                int i = a * 2;
                t.AddRange(new[] { i, i + 1, i + 2, i + 2, i + 1, i + 3 });
            }
            return Make(v.ToArray(), t.ToArray());
        }
    }
}
