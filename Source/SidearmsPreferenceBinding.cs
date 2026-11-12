#nullable disable
using System;
using System.Collections;
using System.Reflection;

namespace HSK.FpsBridge
{
    // Optional dependency boundary. No game or Simple Sidearms assembly is required to test this binding.
    internal sealed class SidearmsPreferenceBinding
    {
        private readonly MethodInfo memoryFor, force, unforce, ranged, melee;
        private readonly ConstructorInfo pairConstructor;
        private readonly PropertyInfo remembered;
        private readonly FieldInfo mode;
        private readonly object rangedMode, meleeMode;

        public SidearmsPreferenceBinding(Type memoryType, Type pawnType, Type defType)
        {
            Type pair = memoryType.Assembly.GetType("SimpleSidearms.rimworld.ThingDefStuffDefPair", true);
            pairConstructor = pair.GetConstructor(new[] { defType, defType })
                ?? throw new MissingMethodException(pair.FullName, ".ctor(ThingDef,ThingDef)");
            memoryFor = Require(memoryType, "GetMemoryCompForPawn", true, memoryType, pawnType, typeof(bool));
            force = Require(memoryType, "SetWeaponAsForced", false, typeof(void), pair, typeof(bool));
            unforce = Require(memoryType, "UnsetForcedWeapon", false, typeof(void), typeof(bool));
            ranged = Require(memoryType, "SetRangedWeaponTypeAsDefault", false, typeof(void), pair);
            melee = Require(memoryType, "SetMeleeWeaponTypeAsPreferred", false, typeof(void), pair);
            remembered = memoryType.GetProperty("RememberedWeapons", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMemberException(memoryType.FullName, "RememberedWeapons");
            if (!typeof(IList).IsAssignableFrom(remembered.PropertyType)) throw new MissingMemberException("RememberedWeapons must implement IList");
            mode = memoryType.GetField("primaryWeaponMode", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingFieldException(memoryType.FullName, "primaryWeaponMode");
            if (!mode.FieldType.IsEnum || mode.IsInitOnly) throw new MissingFieldException("primaryWeaponMode signature");
            rangedMode = Enum.Parse(mode.FieldType, "Ranged");
            meleeMode = Enum.Parse(mode.FieldType, "Melee");
        }

        private static MethodInfo Require(Type type, string name, bool isStatic, Type result, params Type[] args)
        {
            var m = type.GetMethod(name, BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, args, null);
            if (m == null || m.ReturnType != result) throw new MissingMethodException(type.FullName, name);
            return m;
        }

        // Apply only after a successful equip. Native Sidearms serialization owns these preferences.
        public bool Apply(object pawn, object def, object stuff, bool isMelee, bool drafted)
        {
            object memory = memoryFor.Invoke(null, new[] { pawn, (object)false });
            if (memory == null) return false;
            object pair = pairConstructor.Invoke(new[] { def, stuff });
            var list = (IList)remembered.GetValue(memory);
            if (list == null) throw new InvalidOperationException("Simple Sidearms memory list is null");
            // Sidearms remembers weapon type + material, not individual item quality/ThingID.
            if (!list.Contains(pair)) list.Add(pair);
            unforce.Invoke(memory, new object[] { false });
            unforce.Invoke(memory, new object[] { true });
            (isMelee ? melee : ranged).Invoke(memory, new[] { pair });
            mode.SetValue(memory, isMelee ? meleeMode : rangedMode);
            // Match the native drafted click: other weapons may be better, but the player's selection wins.
            if (drafted) force.Invoke(memory, new[] { pair, (object)true });
            return true;
        }
    }
}
