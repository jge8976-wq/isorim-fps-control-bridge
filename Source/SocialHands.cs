using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // First-person hands for the social acts (Social.cs): floating round balls in the pawn's own skin colour (no arms or
    // fingers, which looked uncanny), IsoRim-lit, placed in the same space as the held weapon (FirstPerson.Eye, true
    // height, squashed when drawn) and aimed at the other pawn's live position.
    //   - head pat: the right hand reaches over the head, pats three times, comes back;
    //   - hug: both arms go round the sides to the back, squeeze a little, let go.
    // The held weapon is hidden meanwhile (WeaponView).
    internal static class SocialHands
    {
        public const float PatSeconds = 1.4f, HugSeconds = 1.6f;
        private static Material mat;
        private static bool tried;
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private static int colorId;
        private static Mesh sphere, cylinder;
        private static Pawn self, target;
        private static bool hug;
        private static float start = -10f;
        private static Color skin;

        public static bool Busy => target != null && Time.unscaledTime - start < (hug ? HugSeconds : PatSeconds);

        public static void Begin(Pawn by, Pawn other, bool isHug)
        {
            self = by; target = other; hug = isHug; start = Time.unscaledTime;
            skin = by.story?.SkinColor ?? new Color(.92f, .76f, .62f);
            skin.a = 1f;
        }

        public static void Reset() { target = null; start = -10f; }

        private static bool Ready()
        {
            if (tried) return mat != null;
            tried = true;
            colorId = Shader.PropertyToID("_Color");
            mat = R3DMaterials.Make(R3DMaterials.SolidDef, "FpsBridge_Hand", -1, R3DMaterials.LightModel);
            if (mat == null || !Visuals.Primitives(out sphere, out cylinder)) { mat = null; Log.Warning("[FpsBridge] Hand drawing unavailable (IsoRim solid shader)."); }
            return mat != null;
        }

        private static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

        // Every frame in first person.
        public static void Draw()
        {
            if (!Busy) return;
            if (!target.Spawned || self == null || target.Map != self.Map) { Reset(); return; }
            if (!Ready()) return;
            float t = Time.unscaledTime - start;
            Vector3 eye = FirstPerson.Eye;
            Quaternion yaw = Quaternion.Euler(0f, FirstPerson.Yaw, 0f);
            Vector3 fwd = yaw * Vector3.forward, right = yaw * Vector3.right, up = Vector3.up;
            V3 sp = FloorSpace.Spot(target);
            Vector3 feet = new Vector3(sp.X, sp.Y, sp.Z);
            float tall = Bindings.PickHeight(target);
            Vector3 shoulderR = eye + right * .19f - up * .27f + fwd * .02f, shoulderL = eye - right * .19f - up * .27f + fwd * .02f;
            Vector3 restR = shoulderR + fwd * .22f - up * .28f, restL = shoulderL + fwd * .22f - up * .28f;
            if (!hug) Pat(t, eye, fwd, up, feet, tall, shoulderR, restR);
            else Hug(t, eye, up, feet, tall, shoulderR, shoulderL, restR, restL);
        }

        private static void Pat(float t, Vector3 eye, Vector3 fwd, Vector3 up, Vector3 feet, float tall, Vector3 shoulder, Vector3 rest)
        {
            Vector3 toEye = eye - feet; toEye.y = 0f;
            Vector3 head = feet + up * (tall + .05f) + (toEye.sqrMagnitude > 1e-4f ? toEye.normalized * .06f : Vector3.zero);
            Vector3 over = head + up * .12f;
            Vector3 hand;
            if (t < .35f) hand = Vector3.Lerp(rest, over, Smooth(t / .35f));
            else if (t < 1.1f)
            {
                float u = (t - .35f) / .75f;
                float lift = .1f * (.5f + .5f * Mathf.Cos(2f * Mathf.PI * 3f * u));   // three pats: down to the hair and back up
                hand = head + up * (.02f + lift) + fwd * (.02f * Mathf.Sin(2f * Mathf.PI * 3f * u));
            }
            else hand = Vector3.Lerp(head + up * .12f, rest, Smooth((t - 1.1f) / .3f));
            // Palm down, fingers away and a little down over the crown.
            Vector3 fingers = (fwd * .94f - up * .34f).normalized;
            Arm(shoulder, hand, fingers, up, false);
        }

        private static void Hug(float t, Vector3 eye, Vector3 up, Vector3 feet, float tall, Vector3 shoulderR, Vector3 shoulderL, Vector3 restR, Vector3 restL)
        {
            Vector3 chest = feet + up * (tall * .66f);
            Vector3 d = chest - eye; d.y = 0f;
            d = d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, d);
            float r = .16f * Mathf.Clamp(target.BodySize, .6f, 1.6f);
            float squeeze = t > .7f && t < 1.2f ? .025f * Mathf.Sin((t - .7f) / .5f * Mathf.PI) : 0f;
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 rest = s > 0 ? restR : restL, shoulder = s > 0 ? shoulderR : shoulderL;
                Vector3 around = chest + side * s * (r + .12f) - d * .04f;
                Vector3 back = chest + side * s * (r * .45f - squeeze) + d * (r * .9f);
                Vector3 hand;
                if (t < .45f) hand = Vector3.Lerp(rest, around, Smooth(t / .45f));
                else if (t < .7f) hand = Vector3.Lerp(around, back, Smooth((t - .45f) / .25f));
                else if (t < 1.2f) hand = back;
                else if (t < 1.4f) hand = Vector3.Lerp(back, around, Smooth((t - 1.2f) / .2f));
                else hand = Vector3.Lerp(around, rest, Smooth((t - 1.4f) / .2f));
                // Fingers round the back, palm toward the body.
                Vector3 fingers = (-side * s * .75f + d * .45f).normalized;
                Arm(shoulder, hand, fingers, (up * .3f + -side * s).normalized, s < 0);
            }
        }

        // The hand: a floating round ball (no arm, no fingers), a touch flattened against what it presses on.
        private static void Arm(Vector3 shoulder, Vector3 hand, Vector3 fingers, Vector3 back, bool left)
        {
            Shade(1f);
            Sphere(hand, Quaternion.LookRotation(fingers, back), new Vector3(.055f, .048f, .055f));
        }

        private static void Shade(float k) => block.SetColor(colorId, new Color(skin.r * k, skin.g * k, skin.b * k, 1f));

        private static Matrix4x4 Drawn(Matrix4x4 m) => Matrix4x4.Scale(new Vector3(1f, 1f / View3D.Squash, 1f)) * m;

        private static void Cylinder(Vector3 a, Vector3 b, float radius)
        {
            Vector3 axis = b - a;
            float len = axis.magnitude;
            if (len < 1e-4f) return;
            Graphics.DrawMesh(cylinder, Drawn(Matrix4x4.TRS(a, Quaternion.FromToRotation(Vector3.up, axis / len), new Vector3(radius, len, radius))), mat, 0, null, 0, block);
        }

        private static void Sphere(Vector3 c, Quaternion rot, Vector3 radii) =>
            Graphics.DrawMesh(sphere, Drawn(Matrix4x4.TRS(c, rot, radii)), mat, 0, null, 0, block);
    }
}
