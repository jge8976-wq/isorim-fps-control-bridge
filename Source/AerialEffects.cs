using System;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialEffects
    {
        internal static int RotorSubmissions,JetSubmissions;
        internal static bool HasExhaust(Pawn v) => AerialFlight.Profile(v)?.propulsion is AerialPropulsion.Jet or AerialPropulsion.VerticalThruster;
        internal static bool IsRotor(string path) => path?.IndexOf("Rotor",StringComparison.OrdinalIgnoreCase)>=0;

        internal static Matrix4x4 Attachment(Pawn v, VehicleViewDef profile, VehicleAttachmentView part,
            ModelHandle mesh, float baseAngle, float nativeAngle, float? spinAngle)
        {
            bool spin=spinAngle.HasValue&&IsRotor(part.graphicPath);
            if(VehicleAttachmentBridge.TryLocalTransform(v,profile,part,baseAngle+(spin?spinAngle.Value:nativeAngle),out var anchored))return anchored;

            // Older model packs still use their original overlay transform.
            Vector3 scale=profile.modelScale;
            var local=Matrix4x4.Scale(scale);
            if(spin)
            {
                Vector3 pivot=Vector3.Scale((mesh.Min+mesh.Max)*.5f,scale);
                local=Matrix4x4.Translate(pivot)*Matrix4x4.Rotate(Quaternion.Euler(0,spinAngle.Value,0))*Matrix4x4.Translate(-pivot)*local;
            }
            return Matrix4x4.Rotate(Quaternion.Euler(0,baseAngle+(spin?0:nativeAngle),0))*local;
        }
        internal static void Draw(Pawn v,AerialFlightRecord r)
        {
            if(r.Spool<.025f||r.EngineStopped)return;
            Bounds b=AerialGeometry.LocalBounds(v);AerialFlightKind kind=AerialFlight.Profile(v).kind;
            Quaternion q=AerialGeometry.Hull(v);Vector3 root=VehicleModels.U(r.Motor.Position);
            float pulse=.92f+.08f*Mathf.Sin(Find.TickManager.TicksGame*1.8f+v.thingIDNumber);
            if(!HasExhaust(v))return;
            JetSubmissions++;
            if(kind==AerialFlightKind.DropPod)
            {
                for(int x=-1;x<=1;x+=2)for(int z=-1;z<=1;z+=2)
                {
                    Vector3 at=b.center+new Vector3(x*b.size.x*.22f,-b.extents.y+.08f,z*b.size.z*.22f);
                    int size=ExhaustTier(v);
                    Visuals.AerialJet(root+q*at,Vector3.down,JetLength(size)*r.Spool*pulse,JetRadius(size)*r.Spool,true);
                }
            }
            else
            {
                for(int x=-1;x<=1;x+=2)
                {
                    Vector3 at=b.center+new Vector3(x*b.size.x*.18f,b.size.y*.25f,-b.extents.z*.75f);
                    Visuals.AerialJet(root+q*at,q*new Vector3(0,-.1f,-1),.42f*r.Spool*pulse,.08f*r.Spool,false);
                }
            }
        }
        internal static int JetTier(Bounds b)=>b.size.x*b.size.z<18f?0:b.size.x*b.size.z<48f?1:2;
        internal static int ExhaustTier(Pawn v)=>AerialFlight.Profile(v).exhaustTier>=0?Math.Min(2,AerialFlight.Profile(v).exhaustTier):JetTier(AerialGeometry.LocalBounds(v));
        internal static float JetLength(int tier)=>tier==0?1.8f:tier==1?3.2f:5.2f;
        internal static float JetRadius(int tier)=>tier==0?.22f:tier==1?.4f:.65f;
    }
}
