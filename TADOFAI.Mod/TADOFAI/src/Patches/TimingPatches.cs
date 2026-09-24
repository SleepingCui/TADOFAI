using HarmonyLib;

namespace TADOFAI.Mod.Patches
{
    /// <summary>
    /// Timing 采集。三个入口对应不同版本，缺失的那个 Patch 会由 PatchManager 记录失败并跳过。
    ///
    /// Timing 必须在游戏内 Patch 触发时记录，不能用 WebSocket 到达时间。
    /// 这些 Patch 始终挂载，开关在方法内部判断，避免运行中反复挂载/卸载。
    /// </summary>
    internal static class TimingPatches
    {
        /// <summary>旧版：角度 + bpmTimesSpeed / conductorPitch。</summary>
        [HarmonyPatch(typeof(scrMisc), "GetHitMargin")]
        internal static class GetHitMarginLegacyPatch
        {
            private static void Postfix(
                float hitangle,
                float refangle,
                bool isCW,
                float bpmTimesSpeed,
                float conductorPitch)
            {
                if (!Settings.Current.ShowTiming && !Transport.NeedsTiming) return;
                if (bpmTimesSpeed == 0f || conductorPitch == 0f) return;

                float angle = (hitangle - refangle) * (isCW ? 1f : -1f) * 57.29578f;
                float timing = angle / 180f / bpmTimesSpeed / conductorPitch * 60000f;

                if (NumUtil.IsFinite(timing)) Collector.RecordTiming(timing);
            }
        }

        /// <summary>新版：角度 + floorBpm。</summary>
        [HarmonyPatch(typeof(scrMisc), "GetHitMarginInDeg")]
        internal static class GetHitMarginDegreePatch
        {
            private static void Postfix(
                float hitAngle,
                float refAngle,
                bool clockwise,
                float floorBpm,
                float conductorPitch)
            {
                if (!Settings.Current.ShowTiming && !Transport.NeedsTiming) return;
                if (floorBpm == 0f || conductorPitch == 0f) return;

                float angle = (hitAngle - refAngle) * (clockwise ? 1f : -1f) * 57.29578f;
                float timing = angle / 180f / floorBpm / conductorPitch * 60000f;

                if (NumUtil.IsFinite(timing)) Collector.RecordTiming(timing);
            }
        }

        /// <summary>新版：直接给时间差（秒）。</summary>
        [HarmonyPatch(typeof(scrMisc), "GetHitMarginInSec")]
        internal static class GetHitMarginSecondPatch
        {
            private static void Postfix(double timeDiff)
            {
                if (!Settings.Current.ShowTiming && !Transport.NeedsTiming) return;

                double timing = timeDiff * 1000.0;
                if (NumUtil.IsFinite(timing)) Collector.RecordTiming((float)timing);
            }
        }
    }
}
