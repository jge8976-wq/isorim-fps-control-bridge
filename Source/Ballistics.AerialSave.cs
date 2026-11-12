using Verse;
namespace HSK.FpsBridge
{
    internal static partial class Ballistics
    {
        public static void ExposeAerialMissile(Projectile __instance)
        {
            bool owns=shots.TryGetValue(__instance,out Shot s)&&s.aerialTurn>0;
            if(Scribe.mode==LoadSaveMode.Saving&&!owns)return;
            Scribe_Values.Look(ref owns,"fpsAerialMissileR14",false);if(!owns)return;
            if(s==null){s=new Shot{kind=Kind.Round};shots[__instance]=s;}
            float x=s.pos.X,y=s.pos.Y,z=s.pos.Z,vx=s.vel.X,vy=s.vel.Y,vz=s.vel.Z;
            Scribe_Values.Look(ref x,"fpsAirX");Scribe_Values.Look(ref y,"fpsAirY");Scribe_Values.Look(ref z,"fpsAirZ");
            Scribe_Values.Look(ref vx,"fpsAirVx");Scribe_Values.Look(ref vy,"fpsAirVy");Scribe_Values.Look(ref vz,"fpsAirVz");
            Scribe_Values.Look(ref s.aerialTurn,"fpsAirTurn");Scribe_Values.Look(ref s.startY,"fpsAirStartY");
            Scribe_Values.Look(ref s.muzzleY,"fpsAirMuzzleY");Scribe_Values.Look(ref s.flown,"fpsAirFlown");
            Scribe_Values.Look(ref s.airLaunched,"fpsAirLaunched",true);
            Scribe_References.Look(ref s.launcher,"fpsAirOwner");Scribe_References.Look(ref s.aerialTarget,"fpsAirTarget");
            s.pos=new V3(x,y,z);s.vel=new V3(vx,vy,vz);s.gravity=0;
            if(Scribe.mode==LoadSaveMode.PostLoadInit){s.lastTick=Find.TickManager.TicksGame;FloorSpace.OwnFlight(__instance);}
        }
    }
}
