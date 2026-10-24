using System.Linq;
using System.Reflection;
using Alta.StatSystem;
using HarmonyLib;

namespace TavernLib.Patches;

[HarmonyPatch]
public class StatNamePatch
{
    private const uint CrippleDamageProtectionHash = 44538;
    private static bool done;

    [HarmonyPatch(typeof(StatManager), "Awake"), HarmonyPrefix]
    public static void RenameCrippleDamageProtection()
    {
        if (done)
        {
            return;
        }
        StatDefinition cripple = StatDefinition.All.FirstOrDefault(def => def.Hash == CrippleDamageProtectionHash);
        if (cripple == null)
        {
            return;
        }
        done = true;
        if (cripple.Name == "damageprotection")
        {
            typeof(StatDefinition).GetField("statName", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(cripple, "crippledamageprotection");
        }
    }
}