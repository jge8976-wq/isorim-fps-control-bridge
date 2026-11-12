using System;

namespace HSK.FpsBridge
{
    internal enum VehicleCrewCamera { Shoulder, Driver, Commander, Role }
    // Camera changes never move a pawn between native handlers or change the operator count.
    internal sealed class VehicleCameraCycle
    {
        public int Index { get; private set; }
        public bool Scoped { get; private set; }
        public VehicleCrewCamera Kind => Index == 0 ? VehicleCrewCamera.Shoulder : Index == 1 ? VehicleCrewCamera.Driver
            : Index == 2 ? VehicleCrewCamera.Commander : VehicleCrewCamera.Role;
        public int RoleIndex => Index - 3;
        public void Reset() { Index = 0; Scoped = false; }
        public void Next(int otherRoles)
        { Index = (Index + 1) % (3 + Math.Max(0, otherRoles)); Scoped = false; }
        public void Validate(int otherRoles)
        { if (Index < 0 || Index >= 3 + Math.Max(0, otherRoles)) { Index = 0; Scoped = false; } }
        public bool ToggleScope(bool available)
        { if (!available) return false; Scoped = !Scoped; return true; }
    }
}
