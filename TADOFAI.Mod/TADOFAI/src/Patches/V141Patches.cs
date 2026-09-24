using HarmonyLib;

namespace TADOFAI.Mod.Patches
{
    /// <summary>r141+ 路径：每个玩家独立的 scrMarginTracker。</summary>
    internal static class V141Patches
    {
        [HarmonyPatch(typeof(scrMarginTracker), nameof(scrMarginTracker.AddHit))]
        internal static class AddHitPatch
        {
            private static void Postfix(scrMarginTracker __instance, HitMargin hit)
            {
                int player = VersionSafe.GetPlayerIndex(__instance);
                Collector.RecordHit(hit, player);
            }
        }

        [HarmonyPatch(typeof(scrPlayer), nameof(scrPlayer.Hit))]
        internal static class BpmPatch
        {
            private static void Postfix(scrPlayer __instance)
            {
                Collector.UpdateBpm();
            }
        }
    }

    /// <summary>r141+ 准确率刷新。开关在注册时判定，方法内再快速判断一次。</summary>
    internal static class V141AccuracyPatch
    {
        [HarmonyPatch(typeof(scrMarginTracker), nameof(scrMarginTracker.CalculatePercentAcc))]
        internal static class AccuracyPatch
        {
            private static void Postfix(scrMarginTracker __instance)
            {
                if (!Settings.Current.ShowAccuracy) return;

                int player = VersionSafe.GetPlayerIndex(__instance);
                Collector.UpdateAccuracy(player);
            }
        }
    }
}
