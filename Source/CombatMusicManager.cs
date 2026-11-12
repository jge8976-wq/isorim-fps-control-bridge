using System;
using System.IO;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace HSK.FpsBridge
{
    internal static class CombatMusicManager
    {
        private static readonly CombatMusicClock clock=new CombatMusicClock();
        private static AudioSource source;
        private static AudioClip clip;
        private static string root, loadedPath;
        private static bool failed;
        private static float duck;
        internal static bool Enabled {get;private set;}
        internal static void Configure(string directory) => root=directory;
        internal static void Install(Harmony h)
        {
            h.Patch(AccessTools.PropertyGetter(typeof(MusicManagerPlay),"CurVolume") ?? throw new MissingMemberException("MusicManagerPlay.CurVolume"),
                postfix:new HarmonyMethod(typeof(CombatMusicManager),nameof(BackgroundVolume)));
            h.Patch(AccessTools.PropertyGetter(typeof(Sample),nameof(Sample.SanitizedVolume)) ?? throw new MissingMemberException("Sample.SanitizedVolume"),
                postfix:new HarmonyMethod(typeof(CombatMusicManager),nameof(AmbientVolume)));
            Enabled=true;
            if(!string.IsNullOrEmpty(root)) LongEventHandler.ExecuteWhenFinished(Preload);
        }
        public static void BackgroundVolume(ref float __result) {if(Enabled) __result*=1f-duck;}
        public static void AmbientVolume(Sample __instance, ref float __result)
        {
            if(Enabled && duck>0 && FpsMod.Settings.combatMusicDuckAmbient && __instance.IsAmbient)
                __result*=1f-.7f*duck;
        }
        private static void Preload()
        {
            if(string.IsNullOrEmpty(root)) return;
            try
            {
                string path=CombatMusicPaths.Resolve(root,FpsMod.Settings.combatMusicPath);
                if(loadedPath==path && clip!=null) return;
                if(loadedPath!=path) failed=false;
                loadedPath=path;
                if(clip!=null)UnityEngine.Object.Destroy(clip);
                clip=null;
                if(!File.Exists(path)) {WarnUnavailable("File missing");return;}
                clip=AudioClip.LoadFromFile(path);
                if(clip==null || clip.length<=.1f) {WarnUnavailable("Empty audio");return;}
                failed=false;
                Log.Message("[FpsBridge] COMBAT_MUSIC_READY; file="+Path.GetFileName(path)+"; seconds="+clip.length.ToString("0.00")+"; channels="+clip.channels);
            }
            catch(Exception e) {WarnUnavailable(e.Message);}
        }
        private static void WarnUnavailable(string reason)
        {
            if(failed)return;
            failed=true;
            Log.Warning("[FpsBridge] Combat music unavailable; background music retained. "+reason);
        }
        internal static void AbilityActivated()
        {
            if(!Enabled || !FpsMod.Settings.combatMusic || FpsMod.Settings.combatMusicVolume<=0) return;
            double now=Time.realtimeSinceStartup;
            if(clock.Active && !clock.Expired(now)) return;
            if(clock.Active) {source?.Stop();clock.Reset();duck=0;}
            Preload();
            if(clip==null || clip.length<=.1f)
            {
                WarnUnavailable("No usable track");
                return;
            }
            try
            {
                if(source==null)
                {
                    var go=new GameObject("HSKFps_CombatMusic");UnityEngine.Object.DontDestroyOnLoad(go);
                    source=go.AddComponent<AudioSource>();source.playOnAwake=false;source.loop=false;source.spatialBlend=0;
                    source.ignoreListenerPause=true;
                }
                // The bundled file already contains original seconds 20-80. Seek only custom tracks.
                float offset=CombatMusicPaths.IsBundled(root,FpsMod.Settings.combatMusicPath) ? 0f
                    : Mathf.Clamp(FpsMod.Settings.combatMusicOffset,0,Mathf.Max(0,clip.length-.1f));
                source.clip=clip;source.pitch=1;source.volume=0;source.time=offset;source.Play();
                if(!source.isPlaying) return;
                clock.Trigger(Time.realtimeSinceStartup,clip.length-offset,60f);
            }
            catch(Exception e) {Release();Log.Warning("[FpsBridge] Combat music off: "+e.Message);}
        }
        internal static void SkillsEnded()
        {
            // A completed/cancelled ability leaves the accepted one-minute track playing.
        }
        internal static void Frame()
        {
            if(!clock.Active) return;
            double now=Time.realtimeSinceStartup;
            if(Current.ProgramState!=ProgramState.Playing || Current.Game==null || source==null) {Release();return;}
            if(!FpsMod.Settings.combatMusic || FpsMod.Settings.combatMusicVolume<=0)clock.FadeOut(now);
            if(clock.Expired(now)) {source.Stop();clock.Reset();duck=0;return;}
            // An early audio-device stop must not let repeated abilities retrigger this minute.
            if(!source.isPlaying) {duck=0;return;}
            duck=clock.Envelope(now);
            source.volume=Mathf.Clamp01(Prefs.VolumeMusic*FpsMod.Settings.combatMusicVolume*duck);
        }
        internal static void Release()
        {
            clock.Reset();duck=0;
            if(source!=null) {source.Stop();UnityEngine.Object.Destroy(source.gameObject);source=null;}
        }
    }
}
