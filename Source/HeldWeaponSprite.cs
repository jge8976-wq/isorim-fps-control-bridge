using System;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal sealed record HeldWeaponSprite(Material Material, Matrix4x4 ToView)
    {
        internal static ModelSpec Make(Thing weapon, Verb verb)
        {
            Graphic graphic = weapon?.Graphic;
            Material material = graphic?.MatSingleFor(weapon);
            if (material == null || !material.HasProperty("_MainTex") || material.mainTexture == null) return null;

            Vector2 drawSize = graphic.drawSize;
            float width = Mathf.Clamp(drawSize.x, .1f, 4f) * .8f;
            float height = Mathf.Clamp(drawSize.y, .1f, 4f) * .8f;
            // IsoRim draws the original texture in the XY plane. Align its right edge with the view model's forward axis.
            Matrix4x4 shape = Matrix4x4.Rotate(Quaternion.Euler(0f, -90f, 0f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(-weapon.def.equippedAngleOffset, Vector3.forward))
                * Matrix4x4.Scale(new Vector3(width, height, 1f));
            Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = -min;
            for (int i = 0; i < 8; i++)
            {
                Vector3 point = shape.MultiplyPoint3x4(new Vector3(
                    (i & 1) == 0 ? -.5f : .5f, (i & 2) == 0 ? -.5f : .5f, (i & 4) == 0 ? -.025f : .025f));
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            ModelKind kind = weapon.def.IsMeleeWeapon || verb == null || verb.verbProps.IsMeleeAttack
                ? ModelKind.Blade : verb.verbProps.verbClass == typeof(Verb_LaunchProjectile)
                    ? ModelKind.Grenade : ModelKind.Gun;
            Vector3 grip = new(0f, Mathf.Lerp(min.y, max.y, .32f), Mathf.Lerp(min.z, max.z, .30f));
            min -= grip;
            max -= grip;
            return new ModelSpec(weapon.def.defName, "Sprite:" + weapon.def.defName, kind, ModelSpecs.GraphicPath(weapon),
                "", new V3(min.x, min.y, min.z), new V3(max.x, max.y, max.z), null)
            {
                Sprite = new HeldWeaponSprite(material, Matrix4x4.Translate(-grip) * shape)
            };
        }
    }
}
