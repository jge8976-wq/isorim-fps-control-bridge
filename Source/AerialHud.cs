using UnityEngine;
using Verse;
namespace HSK.FpsBridge
{
    internal static class AerialHud
    {
        static Window minimap;static Rect original;
        internal static void Layout()
        {
            if(!VehicleControl.On||!AerialFlight.Supports(VehicleControl.Vehicle)){Restore();return;}
            foreach(Window w in Find.WindowStack.Windows)
                if(w.GetType().FullName=="DubsMintMinimap.MainTabWindow_MiniMap")
                {
                    if(minimap!=w){Restore();minimap=w;original=w.windowRect;}
                    Rect rect=w.windowRect;
                    rect.x=Mathf.Max(0,UI.screenWidth-rect.width-12);rect.y=72;w.windowRect=rect;break;
                }
        }
        internal static void Restore(){if(minimap!=null){minimap.windowRect=original;minimap=null;}}
    }
}
