using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Classification runs only after the existing runway safety check rejected a sample.
    // It explains that rejection without relaxing any collision or support condition.
    internal static class AerialRunwayDiagnostics
    {
        private static readonly Dictionary<Pawn,(string kind,float next)> reported = new Dictionary<Pawn,(string,float)>();
        private static Game game;
        private static bool Outside(Pawn v,Bounds box)
            => box.min.x<0||box.min.z<0||box.max.x>v.Map.Size.x||box.max.z>FloorSpace.Bands(v.Map)[0].Rows;

        internal static bool FailSurface(Pawn v,AerialRunwayPlan plan,AerialMotor pose,Bounds box,float support,string phase)
            => Fail(v,plan,pose,box,support,Outside(v,box)?"boundary":"surface",phase);

        internal static bool FailClearance(Pawn v,AerialRunwayPlan plan,AerialMotor pose,Bounds box,float support,string phase)
        {
            if(Outside(v,box))return Fail(v,plan,pose,box,support,"boundary",phase);
            if(!AerialCollision.ClearEnvironment(v,pose,v.Map))return Fail(v,plan,pose,box,support,"environment",phase);
            if(!VehicleContacts.Clear(v,pose,v.Map))return Fail(v,plan,pose,box,support,"vehicle",phase);
            foreach(Pawn other in v.Map.mapPawns.AllPawnsSpawned)
            {
                if(other==v||VehicleApi.IsVehicle(other))continue;
                V3 at=FloorSpace.Spot(other);
                if(at.Y>box.max.y+.1f||at.Y+1.8f<box.min.y)continue;
                if(at.X>box.min.x-.3f&&at.X<box.max.x+.3f&&at.Z>box.min.z-.3f&&at.Z<box.max.z+.3f)
                    return Fail(v,plan,pose,box,support,"person",phase,other.ThingID);
            }
            // A concurrent scene change may make the second diagnostic query clear.
            // Keep the original conservative rejection and report it as such.
            return Fail(v,plan,pose,box,support,"changed-scene",phase);
        }

        internal static bool Fail(Pawn v,AerialRunwayPlan plan,AerialMotor pose,Bounds box,float support,string kind,string phase,string blocker=null)
        {
            plan.BlockedAt=pose.Position;plan.BlockKind=kind;
            plan.Reason=kind=="boundary"?L.T("跑道超出地图边界","Runway crosses the map boundary")
                :kind=="surface"?L.T("跑道缺少连续地面或有水面","Runway lacks continuous ground or crosses water")
                :kind=="slope"?L.T("跑道地面存在高度差","Runway ground height is uneven")
                :kind=="environment"?L.T("跑道被建筑、树木或楼板阻挡","Runway blocked by a building, tree or floor slab")
                :kind=="vehicle"?L.T("跑道被其他载具占用","Runway occupied by another vehicle")
                :kind=="person"?L.T("跑道有人员或生物","Runway occupied by a person or creature")
                :L.T("跑道场景正在变化，请重新检查","Runway scene changed; check again");
            if(game!=Current.Game){game=Current.Game;reported.Clear();}
            float now=Time.unscaledTime;
            if(reported.TryGetValue(v,out var prior)&&prior.kind==kind&&now<prior.next)return false;
            if(reported.Count>256)reported.Clear();
            reported[v]=(kind,now+2f);
            IntVec3 cell=FloorSpace.Cell(v.Map,pose.Position,v);
            Log.Message("[FpsBridge] RUNWAY_BLOCK vehicle="+v.ThingID+"; kind="+kind+"; phase="+phase
                +"; sample="+Point(pose.Position.X,pose.Position.Y,pose.Position.Z)+"; cell="+cell
                +"; boxMin="+Point(box.min.x,box.min.y,box.min.z)+"; boxMax="+Point(box.max.x,box.max.y,box.max.z)
                +"; support="+Number(support)+"; runwaySurface="+Number(plan.Surface)+"; yaw="+Number(pose.Yaw)
                +(blocker==null?"":"; blocker="+blocker));
            return false;
        }
        private static string Number(float value)=>value.ToString("0.#####",CultureInfo.InvariantCulture);
        private static string Point(float x,float y,float z)=>Number(x)+","+Number(y)+","+Number(z);
    }
}
