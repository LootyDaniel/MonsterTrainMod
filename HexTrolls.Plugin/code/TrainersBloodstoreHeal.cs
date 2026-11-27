using System;
using System.Reflection;
namespace HexTrolls.Plugin.code
{
    internal static class TrainersBloodstoreHeal
    {
        internal const string StatusId = "hextrolls.plugin_bloodstore";
        internal static void Heal(CharacterState unit, int amount)
        {
            if (unit == null || amount <= 0 || !unit.IsAlive) return;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            // Different Monster Train 2 builds expose different health accessors.
            var t = unit.GetType();
            foreach (string method in new [] {"Heal", "HealHP", "HealHealth"})
            {
                var m = t.GetMethod(method, flags, null, new [] {typeof(int)}, null);
                if (m != null) { m.Invoke(unit, new object[]{amount}); return; }
            }
            int? hp = Read(t,unit,"GetHP","GetHealth","GetCurrentHP","GetCurrentHealth");
            int? max = Read(t,unit,"GetMaxHP","GetMaxHealth");
            if (hp.HasValue && max.HasValue)
            {
                var m = t.GetMethod("SetHealth", flags, null, new [] {typeof(int),typeof(int)}, null);
                if (m != null) m.Invoke(unit, new object[]{Math.Min(max.Value,hp.Value+amount),max.Value});
            }
        }
        private static int? Read(Type t, CharacterState unit, params string[] names)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var name in names)
            {
                var m = t.GetMethod(name,flags,null,Type.EmptyTypes,null);
                if (m != null) try {return Convert.ToInt32(m.Invoke(unit,null));} catch {}
            }
            return null;
        }
    }
}
