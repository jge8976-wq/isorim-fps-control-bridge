using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Grammar;

namespace HSK.FpsBridge
{
    // Social acts in first person. Facing a person or animal within reach (not hostile), T opens a small menu: greet,
    // hug, pat on the head, chat; the mouse wheel chooses, T again does it, Esc closes. Everything goes through the
    // game's own interactions (speech bubble, social log, opinion and mood thoughts):
    //   - greet: a small opinion lift both ways;
    //   - hug / head pat: accepted by those who like the pawn well enough (lovers, family and children always), giving
    //     both a mood lift and the recipient a better opinion; refused otherwise (the refuser thinks a little less of it);
    //   - chat: the game's own chitchat (sometimes a deep talk between colonists), animal chat with animals;
    //   - animals: hug or pat lifts the pawn's mood.
    // A person or animal of the colony stops for a moment and turns to face the pawn; the view leans in for a hug or a
    // pat. The new interaction and thought defs are made in code at startup (no XML), so saves that mention them load.
    internal static class Social
    {
        private enum Act { Greet, Hug, Pat, Chat }
        private const int ActCount = 4;
        private static string Name(int index) => (Act)index switch {
            Act.Greet => L.T("打招呼", "Greet"), Act.Hug => L.T("抱一抱", "Hug"),
            Act.Pat => L.T("摸摸头", "Head pat"), _ => L.T("闲聊", "Chat")
        };
        private const float Reach = 2.4f, MenuSeconds = 8f, TouchReach = 1.6f;   // a hug or a pat needs arm's length

        private static InteractionDef greetDef, hugDef, patDef, refuseDef;
        private static ThoughtDef greetSocial, hugSocial, patSocial, refusedSocial, hugMood, patMood, petMood;
        public static bool Ready { get; private set; }

        private static bool menu;
        private static int choice;
        private static Pawn target, near;
        private static float menuSince;
        private static int nearFrame = -1;
        private static float gestureAt = -10f;
        private static Act gesture;

        public static bool MenuOpen => menu;

        // ---- defs (once, at startup)

        public static void Build()
        {
            if (Ready) return;
            greetSocial = Social_("FPS_Greeted", L.T("打过招呼", "greeted me"), L.T("和我打了招呼。", "Said hello to me."), 0f, 2f, .5f);
            hugSocial = Social_("FPS_Hugged", L.T("抱过我", "hugged me"), L.T("给了我一个拥抱。", "Gave me a hug."), 0f, 5f, 1f);
            patSocial = Social_("FPS_HeadPatted", L.T("摸过我的头", "patted my head"), L.T("摸了摸我的头。", "Patted me on the head."), 0f, 3f, 1f);
            refusedSocial = Social_("FPS_RefusedTouch", L.T("想动手动脚", "too touchy"), L.T("想和我有身体接触，我不太乐意。", "Wanted to touch me. I didn't like it."), 0f, -4f, 1f);
            hugMood = Mood("FPS_HugMood", L.T("拥抱", "hug"), L.T("一个温暖的拥抱。", "A warm hug."), 3f, .5f);
            patMood = Mood("FPS_HeadPatMood", L.T("被摸了头", "head pat"), L.T("被人摸了摸头，感觉还不错。", "Someone patted my head. That felt nice."), 2f, .5f);
            petMood = Mood("FPS_PetAnimalMood", L.T("撸了动物", "petted an animal"), L.T("抱了抱、摸了摸小动物，心情变好了。", "Hugged and petted an animal. I feel better."), 3f, .5f);
            InteractionDef chit = InteractionDefOf.Chitchat;
            greetDef = Interaction("FPS_Greet", L.T("打招呼", "Greet"), "Things/Mote/SpeechSymbols/Chitchat", greetSocial, chit, L.T("[INITIATOR_nameDef]和[RECIPIENT_nameDef]打了个招呼。", "[INITIATOR_nameDef] greeted [RECIPIENT_nameDef]."));
            hugDef = Interaction("FPS_Hug", L.T("拥抱", "hug"), "Things/Mote/SpeechSymbols/KindWords", hugSocial, chit, L.T("[INITIATOR_nameDef]抱了抱[RECIPIENT_nameDef]。", "[INITIATOR_nameDef] hugged [RECIPIENT_nameDef]."));
            patDef = Interaction("FPS_HeadPat", L.T("摸头", "head pat"), "Things/Mote/SpeechSymbols/KindWords", patSocial, chit, L.T("[INITIATOR_nameDef]摸了摸[RECIPIENT_nameDef]的头。", "[INITIATOR_nameDef] patted [RECIPIENT_nameDef] on the head."));
            refuseDef = Interaction("FPS_RefuseTouch", L.T("拒绝接触", "refused touch"), "Things/Mote/SpeechSymbols/Chitchat", refusedSocial, chit, L.T("[RECIPIENT_nameDef]躲开了[INITIATOR_nameDef]伸过来的手。", "[RECIPIENT_nameDef] pulled away from [INITIATOR_nameDef]'s hand."));
            Ready = true;
        }

        private static ThoughtDef Social_(string name, string label, string description, float mood, float opinion, float days)
            => Thought(name, typeof(Thought_MemorySocial), label, description, mood, opinion, days, true);
        private static ThoughtDef Mood(string name, string label, string description, float mood, float days)
            => Thought(name, typeof(Thought_Memory), label, description, mood, 0f, days, false);

        private static ThoughtDef Thought(string name, System.Type cls, string label, string description, float mood, float opinion, float days, bool social)
        {
            var def = new ThoughtDef
            {
                defName = name, label = label, thoughtClass = cls, durationDays = days, stackLimit = 1,
                stackLimitForSameOtherPawn = social ? 1 : -1,
            };
            var stage = new ThoughtStage { label = label, description = description, baseMoodEffect = mood, baseOpinionOffset = opinion };
            stage.PostLoad();
            def.stages.Add(stage);
            Add(def);
            return def;
        }

        private static InteractionDef Interaction(string name, string label, string symbol, ThoughtDef recipientThought, InteractionDef like, string log)
        {
            var def = new InteractionDef { defName = name, label = label, recipientThought = recipientThought, interactionMote = like.interactionMote };
            Traverse.Create(def).Field("symbol").SetValue(symbol);
            var rules = new RulePack();
            Traverse.Create(rules).Field("rulesStrings").SetValue(new List<string> { "r_logentry->" + log });
            Traverse.Create(rules).Field("untranslatedRulesStrings").SetValue(new List<string> { "r_logentry->" + log });
            def.logRulesInitiator = rules;
            Add(def);
            return def;
        }

        private static void Add<T>(T def) where T : Def
        {
            if (DefDatabase<T>.GetNamedSilentFail(def.defName) != null) return;
            def.PostLoad();
            DefDatabase<T>.Add(def);
            def.ResolveReferences();
        }

        // ---- who is in reach

        private static bool Fits(Pawn self, Pawn p)
            => p != null && p != self && p.Spawned && !p.Dead && p.Map == self.Map && !p.HostileTo(self)
               && (p.RaceProps.Humanlike ? p.interactions != null : p.RaceProps.Animal);

        // The person or animal the view faces within reach (nearest to the view's line), cached a few frames.
        public static Pawn Facing(Pawn self)
        {
            if (nearFrame == Time.frameCount) return near;
            nearFrame = Time.frameCount;
            near = null;
            if (self?.Map == null || !Ready) return null;
            V3 at = FloorSpace.Spot(self);
            Vector3 forward = Quaternion.Euler(0f, FirstPerson.Yaw, 0f) * Vector3.forward;
            float best = float.MaxValue;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(self.Position, 3f, true))
            {
                if (!c.InBounds(self.Map)) continue;
                List<Thing> things = c.GetThingList(self.Map);
                for (int i = 0; i < things.Count; i++)
                {
                    if (!(things[i] is Pawn p) || !Fits(self, p)) continue;
                    V3 s = FloorSpace.Spot(p);
                    Vector3 to = new Vector3(s.X - at.X, 0f, s.Z - at.Z);
                    float d = to.magnitude;
                    if (d > Reach || Mathf.Abs(s.Y - at.Y) > 1f) continue;
                    float dot = d < .3f ? 1f : Vector3.Dot(to / d, forward);
                    if (dot < .7f) continue;
                    float score = d * (2f - dot);
                    if (score < best) { best = score; near = p; }
                }
            }
            return near;
        }

        // ---- input (once per frame while the pawn can act)

        public static void Frame(Pawn self)
        {
            if (!Ready) return;
            if (menu)
            {
                if (target == null || Facing(self) != target || Time.unscaledTime - menuSince > MenuSeconds || Input.GetKeyDown(KeyCode.Escape)) { menu = false; target = null; return; }
                if (ControlKeys.Down(ControlAction.Social)) { menu = false; Do(self, target, (Act)choice); target = null; }
                return;
            }
            if (!ControlKeys.Down(ControlAction.Social)) return;
            Pawn p = Facing(self);
            if (p == null) { Messages.Message(L.T("面前没有可以互动的人或动物。", "No one to interact with in front of you."), MessageTypeDefOf.RejectInput, false); return; }
            target = p; menu = true; menuSince = Time.unscaledTime;
        }

        public static void Wheel(float delta)
        {
            if (!menu || Mathf.Abs(delta) < .01f) return;
            choice = (choice + (delta < 0f ? 1 : ActCount - 1)) % ActCount;
            menuSince = Time.unscaledTime;
        }

        public static void Reset() { menu = false; target = null; near = null; gestureAt = -10f; SocialHands.Reset(); }

        // ---- the act

        private static void Do(Pawn self, Pawn p, Act act)
        {
            if (self.interactions == null || !self.interactions.CanInteractNowWith(p))
            { Messages.Message(L.T("现在没法互动（刚互动过、对方睡着了，或者离得太远）。", "Cannot interact now (just did, they are asleep, or too far away)."), MessageTypeDefOf.RejectInput, false); return; }
            bool animal = p.RaceProps.Animal;
            bool touch = act == Act.Hug || act == Act.Pat;
            if (touch)
            {
                V3 a = FloorSpace.Spot(self), b = FloorSpace.Spot(p);
                if (new Vector2(a.X - b.X, a.Z - b.Z).magnitude > TouchReach) { Messages.Message(L.T("再靠近一点才够得着。", "Get a little closer to reach."), MessageTypeDefOf.RejectInput, false); return; }
            }
            bool done;
            string said;
            if (animal)
            {
                done = self.interactions.TryInteractWith(p, act == Act.Hug ? hugDef : act == Act.Pat ? patDef : act == Act.Greet ? greetDef : InteractionDefOf.AnimalChat);
                if (done && (act == Act.Hug || act == Act.Pat)) self.needs?.mood?.thoughts?.memories?.TryGainMemory(petMood);
                said = Name((int)act) + L.T("：", ": ") + p.LabelShort;
            }
            else if (act == Act.Hug || act == Act.Pat)
            {
                if (!Welcomes(p, self, act))
                {
                    self.interactions.TryInteractWith(p, refuseDef);
                    Messages.Message(p.LabelShort + L.T(" 躲开了。", " pulled away."), MessageTypeDefOf.RejectInput, false);
                    Log.Message("[FpsBridge] SOCIAL refused act=" + act + "; by=" + p.thingIDNumber);
                    return;
                }
                done = self.interactions.TryInteractWith(p, act == Act.Hug ? hugDef : patDef);
                if (done)
                {
                    ThoughtDef mood = act == Act.Hug ? hugMood : patMood;
                    p.needs?.mood?.thoughts?.memories?.TryGainMemory(mood, self);
                    if (act == Act.Hug) self.needs?.mood?.thoughts?.memories?.TryGainMemory(hugMood, p);
                }
                said = Name((int)act) + L.T("：", ": ") + p.LabelShort;
            }
            else
            {
                InteractionDef def = act == Act.Greet ? greetDef
                    : (self.IsColonist && p.IsColonist && Rand.Chance(.15f) ? InteractionDefOf.DeepTalk : InteractionDefOf.Chitchat);
                done = self.interactions.TryInteractWith(p, def);
                said = (def == InteractionDefOf.DeepTalk ? L.T("深入交谈", "Deep talk") : Name((int)act)) + L.T("：", ": ") + p.LabelShort;
            }
            if (!done) { Messages.Message(L.T("对方现在没法回应。", "They cannot respond right now."), MessageTypeDefOf.RejectInput, false); return; }
            Pause(self, p);
            gesture = act; gestureAt = Time.unscaledTime;
            if (touch) SocialHands.Begin(self, p, act == Act.Hug);   // the hands reach out (first person)
            Messages.Message(said, MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] SOCIAL act=" + act + "; with=" + p.thingIDNumber + "; animal=" + animal);
        }

        // Close enough for a hug or a pat: lovers and family, children, or a good enough opinion.
        private static bool Welcomes(Pawn p, Pawn self, Act act)
        {
            if (p.relations == null) return true;
            if (p.DevelopmentalStage.Juvenile() || LovePartnerRelationUtility.LovePartnerRelationExists(p, self) || p.relations.FamilyByBlood.Contains(self)) return true;
            return p.relations.OpinionOf(self) >= (act == Act.Hug ? 0 : 10);
        }

        // The colony's own people and animals stop a moment and turn to the pawn.
        private static void Pause(Pawn self, Pawn p)
        {
            p.rotationTracker?.FaceTarget(self);
            bool ours = p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony;
            if (!ours || p.Downed || p.InMentalState || p.jobs == null || p.Drafted) return;
            Job wait = JobMaker.MakeJob(JobDefOf.Wait, 120);
            p.jobs.StartJob(wait, JobCondition.InterruptForced, null, resumeCurJobAfterwards: true);
            p.rotationTracker?.FaceTarget(self);
        }

        // ---- the view: leaning in for a hug or a pat, a nod for a greeting

        public static Vector3 EyeOffset
        {
            get
            {
                float t = Time.unscaledTime - gestureAt;
                float span = gesture == Act.Hug ? SocialHands.HugSeconds : gesture == Act.Pat ? SocialHands.PatSeconds : .9f;
                if (t < 0f || t > span) return Vector3.zero;
                float k = Mathf.Sin(t / span * Mathf.PI);
                Vector3 forward = Quaternion.Euler(0f, FirstPerson.Yaw, 0f) * Vector3.forward;
                switch (gesture)
                {
                    case Act.Hug: return forward * .35f * k + Vector3.down * .12f * k;
                    case Act.Pat: return forward * .18f * k;
                    case Act.Greet: return Vector3.down * .05f * k;
                    default: return Vector3.zero;
                }
            }
        }

        // ---- HUD

        public static void DrawHud(Pawn self)
        {
            if (!Ready) return;
            float x = UI.screenWidth * .5f, y = UI.screenHeight * .5f;
            GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
            try
            {
                if (menu && target != null)
                {
                    float w = 170f, rowH = 26f, top = y + 40f;
                    Widgets.DrawBoxSolid(new Rect(x + 30f, top - 26f, w, rowH * ActCount + 32f), new Color(0f, 0f, 0f, .6f));
                    Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.MiddleLeft; GUI.color = new Color(.8f, .8f, .8f);
                    Widgets.Label(new Rect(x + 38f, top - 24f, w - 10f, 20f), target.LabelShortCap + L.T(" · 滚轮选择 · T 确定", " · wheel to choose · T to confirm"));
                    Text.Font = GameFont.Small;
                    for (int i = 0; i < ActCount; i++)
                    {
                        Rect row = new Rect(x + 30f, top + i * rowH, w, rowH);
                        if (i == choice) Widgets.DrawBoxSolid(row, new Color(.35f, .6f, 1f, .45f));
                        GUI.color = i == choice ? Color.white : new Color(.85f, .85f, .85f);
                        Widgets.Label(new Rect(row.x + 10f, row.y, row.width - 10f, row.height), Name(i));
                    }
                    return;
                }
                Pawn p = Facing(self);
                if (p == null) return;
                Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperCenter; GUI.color = new Color(1f, .85f, .95f);
                Widgets.Label(new Rect(x - 200f, y + 70f, 400f, 24f), L.T("T 互动：", "T interact: ") + p.LabelShortCap);
            }
            finally { Text.Font = f0; Text.Anchor = a0; GUI.color = c0; }
        }
    }
}
