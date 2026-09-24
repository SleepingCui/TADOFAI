using HarmonyLib;

namespace TADOFAI.Mod.Patches
{
    /// <summary>r136 旧路径：命中与准确率都在 scrMistakesManager 上，只有单人。</summary>
    internal static class V136Patches
    {
        [HarmonyPatch(typeof(scrMistakesManager), "AddHit")]
        internal static class AddHitPatch
        {
            private static void Postfix(HitMargin hit)
            {
                Collector.RecordHit(hit, 0);
            }
        }

        [HarmonyPatch(typeof(scrController), "Hit")]
        internal static class BpmPatch
        {
            private static void Postfix()
            {
                Collector.UpdateBpm();
            }
        }
    }

    /// <summary>r136 准确率刷新。</summary>
    internal static class V136AccuracyPatch
    {
        [HarmonyPatch(typeof(scrMistakesManager), "CalculatePercentAcc")]
        internal static class AccuracyPatch
        {
            private static void Postfix()
            {
                if (!Settings.Current.ShowAccuracy) return;
                Collector.UpdateAccuracy(0);
            }
        }
    }
}
