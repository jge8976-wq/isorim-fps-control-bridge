using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    internal static class QuickTend
    {
        private static Pawn owner;
        private static Job active;
        public static bool For(Pawn p) => p != null && p == owner && active != null && p.CurJob == active;
        public static void Reset() { Cancel(); owner = null; active = null; }
        private static void Cancel()
        {
            if (For(owner)) owner.jobs.EndCurrentJob(JobCondition.InterruptForced);
            active = null;
        }
        internal static bool Eligible(Pawn doctor, Pawn patient, bool nearby = true, bool needsTend = true)
        {
            return doctor?.Spawned == true && patient?.Spawned == true && doctor.Map == patient.Map
                && patient.Faction == Faction.OfPlayer && !patient.Dead && !patient.InAggroMentalState
                && patient.RaceProps.IsFlesh && (!needsTend || patient.health.HasHediffsNeedingTend())
                && FloorSpace.Band(doctor.Map, doctor.Position).Level == FloorSpace.Band(patient.Map, patient.Position).Level
                && doctor.Position.DistanceToSquared(patient.Position) <= (nearby ? WorkControl.Range * WorkControl.Range : 36f);
        }
        internal static Thing MedicineFor(Pawn doctor, Pawn patient)
        {
            if (patient.playerSettings != null && patient.playerSettings.medCare <= MedicalCareCategory.NoMeds) return null;
            Thing best = null; float potency = -1f;
            void Consider(Thing t)
            {
                if (t == null || t.Destroyed || !t.def.IsMedicine || t.stackCount < 1
                    || (patient.playerSettings != null && !patient.playerSettings.medCare.AllowsMedicine(t.def))) return;
                if (t.Spawned && (t.IsForbidden(doctor) || !doctor.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Deadly, 10, 1)
                    || FloorSpace.Band(doctor.Map, t.Position).Level != FloorSpace.Band(doctor.Map, doctor.Position).Level)) return;
                float value = t.GetStatValue(StatDefOf.MedicalPotency);
                if (value > potency) { best = t; potency = value; }
            }
            Consider(doctor.carryTracker?.CarriedThing);
            if (doctor.inventory != null) foreach (Thing t in doctor.inventory.innerContainer) Consider(t);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(doctor.Position, WorkControl.Range, true))
                if (c.InBounds(doctor.Map)) foreach (Thing t in c.GetThingList(doctor.Map)) Consider(t);
            return best;
        }

        internal static string Invalid(Pawn doctor, Pawn patient)
        {
            string reason = InteractionActions.TargetInvalid(doctor, patient);
            if (reason != null) return reason;
            if (patient.Faction != Faction.OfPlayer || patient.Dead || patient.InAggroMentalState || !patient.RaceProps.IsFlesh)
                return L.T("只能治疗自己或己方血肉队友。", "Only yourself or a player-owned flesh ally can be tended.");
            if (!patient.health.HasHediffsNeedingTend()) return L.T("没有需要包扎的伤口。", "No wounds need tending.");
            if (patient.playerSettings?.medCare == MedicalCareCategory.NoCare)
                return L.T("目标医疗策略禁止治疗。", "The patient's care policy forbids tending.");
            if (!doctor.RaceProps.IsFlesh || doctor.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
                || doctor.skills?.GetSkill(SkillDefOf.Medicine)?.TotallyDisabled == true)
                return L.T("该人物不能从事医疗工作。", "This pawn cannot do medical work.");
            if (!doctor.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return L.T("缺少操作能力。", "Manipulation capacity is missing.");
            if (patient != doctor && !doctor.CanReserveAndReach(patient, PathEndMode.ClosestTouch, Danger.Deadly))
                return L.T("无法接近这位队友，或队友已被占用。", "The ally cannot be reached or is reserved.");
            if (DefDatabase<JobDef>.GetNamedSilentFail("FPS_QuickTend") == null)
                return L.T("快捷包扎定义未加载。", "Quick-tend job definition is missing.");
            return null;
        }

        // Explicit target entry point: never replace an invalid/moving ally with the doctor.
        internal static bool Start(Pawn doctor, Pawn patient, Thing medicine = null)
        {
            string reason = Invalid(doctor, patient);
            if (reason != null) return InteractionActions.Reject(reason);
            if (medicine != null)
            {
                if (patient != doctor) return InteractionActions.Reject(L.T("所选药品仅用于本次自我包扎。", "Selected medicine is for this self-tend command."));
                reason = Pocket.UseInvalid(doctor, medicine);
                if (reason != null) return InteractionActions.Reject(reason);
            }
            Job job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamedSilentFail("FPS_QuickTend"), patient);
            if (medicine != null) job.SetTarget(TargetIndex.C, medicine);
            job.draftedTend = true;
            if (!WorkControl.StartExplicit(doctor, job, L.T("包扎：", "Tending: ") + patient.LabelShortCap))
                return InteractionActions.Reject(L.T("现在无法开始包扎。", "Cannot start tending now."));
            owner = doctor; active = WorkControl.ManualJob;
            return true;
        }
        public static void Frame(Pawn p, bool input)
        {
            if (!For(p)) { active = null; owner = p; }
            if (!input) return;
            if (For(p) && (ControlKeys.Held(ControlAction.Forward) || ControlKeys.Held(ControlAction.Backward)
                || ControlKeys.Held(ControlAction.Left) || ControlKeys.Held(ControlAction.Right)
                || ControlKeys.Held(ControlAction.Fire) || ControlKeys.Held(ControlAction.Aim)
                || ControlKeys.Down(ControlAction.Jump) || ControlKeys.Down(ControlAction.NextWeapon)
                || ControlKeys.Down(ControlAction.Reload) || ControlKeys.Down(ControlAction.AutoWork)
                || ControlKeys.Down(ControlAction.ManualWork) || ControlKeys.Down(ControlAction.InteractLeanRight)
                || ControlKeys.Down(ControlAction.UseItem) || ControlKeys.Down(ControlAction.Social))) Cancel();
            if (!ControlKeys.Down(ControlAction.QuickTend)) return;
            if (For(p)) { Cancel(); return; }
            V3 eye = Ballistics.Eye(p), direction = ControlCamera.ShotDirection(p);
            Pawn patient = Ballistics.PickTarget(p, eye, eye + direction * (WorkControl.Range + 1f)).Thing as Pawn;
            if (!Eligible(p, patient)) patient = p;
            Start(p, patient);
        }
        private static void Reject(string text) => Messages.Message(text, MessageTypeDefOf.RejectInput, false);
    }

    // Explicit player treatment uses the game's own pickup, timed work and FinalizeTend toils. The player's
    // permanent self-tend toggle and medical-care policy are never changed by this command.
    public sealed class JobDriver_QuickTend : JobDriver
    {
        private Pawn Patient => job.targetA.Pawn;
        private Job heldPatientJob;
        private bool specificMedicine;
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref specificMedicine, "fpsSpecificMedicine", false);
        }
        public override bool TryMakePreToilReservations(bool errorOnFailed) => Patient == pawn || pawn.Reserve(Patient, job, 1, -1, null, errorOnFailed);
        protected override IEnumerable<Toil> MakeNewToils()
        {
            specificMedicine |= job.targetC.HasThing;
            this.FailOn(() => !QuickTend.Eligible(pawn, Patient, false, false) || pawn.Downed || pawn.InMentalState);
            AddEndCondition(() => Patient?.health?.HasHediffsNeedingTend() == true ? JobCondition.Ongoing : JobCondition.Succeeded);
            AddFinishAction(_ => { ReleasePatient(); ReturnMedicine(); });
            Toil choose = ToilMaker.MakeToil("FPS_ChooseMedicine");
            choose.initAction = () => {
                ReturnMedicine();
                Thing medicine = specificMedicine ? job.targetC.Thing : QuickTend.MedicineFor(pawn, Patient);
                if (specificMedicine)
                {
                    if (medicine == null || medicine.Destroyed || medicine.stackCount < 1)
                    { EndJobWith(JobCondition.Succeeded); return; }
                    bool carried = pawn.carryTracker?.CarriedThing == medicine;
                    bool inventory = pawn.inventory?.Contains(medicine) == true;
                    if (!medicine.def.IsMedicine || (Patient.playerSettings != null && !Patient.playerSettings.medCare.AllowsMedicine(medicine.def))
                        || (!medicine.Spawned && !carried && !inventory)
                        || (medicine.Spawned && (medicine.Map != pawn.Map || medicine.Position.DistanceToSquared(pawn.Position) > WorkControl.Range * WorkControl.Range
                            || FloorSpace.Band(pawn.Map, medicine.Position).Level != FloorSpace.Band(pawn.Map, pawn.Position).Level)))
                    { EndJobWith(JobCondition.Incompletable); return; }
                }
                if (medicine?.Spawned == true && !pawn.Reserve(medicine, job, 10, 1)) medicine = null;
                if (specificMedicine && medicine == null) { EndJobWith(JobCondition.Incompletable); return; }
                job.SetTarget(TargetIndex.B, medicine != null ? new LocalTargetInfo(medicine) : LocalTargetInfo.Invalid);
                job.count = 1;
            };
            yield return choose;
            Toil gotoPatient = Toils_Goto.GotoThing(TargetIndex.A, Patient == pawn ? PathEndMode.OnCell : PathEndMode.ClosestTouch);
            Toil pickup = Toils_Tend.PickupMedicine(TargetIndex.B, Patient);
            yield return Toils_Jump.JumpIf(gotoPatient, () => !job.targetB.IsValid);
            yield return Toils_Jump.JumpIf(pickup, () => pawn.inventory?.Contains(job.targetB.Thing) == true || pawn.carryTracker.CarriedThing == job.targetB.Thing);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.B);
            yield return pickup;
            yield return gotoPatient;
            int ticks = Math.Max(1, Mathf.RoundToInt(600f / Mathf.Max(.05f, pawn.GetStatValue(StatDefOf.MedicalTendSpeed))));
            Toil wait = Patient == pawn ? Toils_General.Wait(ticks)
                : Toils_General.WaitWith(TargetIndex.A, ticks, false, true, false, TargetIndex.A, PathEndMode.ClosestTouch);
            Action start = wait.initAction;
            wait.initAction = () => { start?.Invoke(); if (Patient != pawn) heldPatientJob = Patient.CurJob; };
            wait.AddFinishAction(ReleasePatient);
            wait.WithProgressBarToilDelay(TargetIndex.A).PlaySustainerOrSound(SoundDefOf.Interact_Tend);
            wait.activeSkill = () => SkillDefOf.Medicine;
            wait.FailOn(() => Patient != pawn && !pawn.CanReachImmediate(Patient, PathEndMode.ClosestTouch));
            wait.FailOn(() => job.targetB.Thing?.def.IsMedicine == true && Patient.playerSettings != null
                && !Patient.playerSettings.medCare.AllowsMedicine(job.targetB.Thing.def));
            yield return wait;
            yield return Toils_Tend.FinalizeTend(Patient);
            yield return Toils_Jump.Jump(choose);
        }
        private void ReleasePatient()
        {
            if (heldPatientJob != null && Patient != pawn && Patient?.CurJob == heldPatientJob
                && (heldPatientJob.def == JobDefOf.Wait || heldPatientJob.def == JobDefOf.Wait_MaintainPosture))
                Patient.jobs.EndCurrentJob(JobCondition.InterruptForced);
            heldPatientJob = null;
        }
        private void ReturnMedicine()
        {
            Thing carried = pawn.carryTracker?.CarriedThing;
            if (carried?.def.IsMedicine != true) return;
            // Keep a specifically selected Thing alive; merging could destroy targetC while medicine remains.
            if (pawn.inventory == null || !pawn.carryTracker.innerContainer.TryTransferToContainer(carried, pawn.inventory.innerContainer, canMergeWithExistingStacks: !specificMedicine))
                pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
        }
    }
}
