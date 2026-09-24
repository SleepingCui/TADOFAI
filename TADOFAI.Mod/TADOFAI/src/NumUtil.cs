using System;

namespace TADOFAI.Mod
{
    /// <summary>
    /// 数值守卫。协议要求：NaN / Infinity / 越界值一律不允许进入事件或状态。
    /// 注意 net472 没有 double.IsFinite，这里自己实现。
    /// </summary>
    internal static class NumUtil
    {
        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        /// <summary>有限值检查 + 兜底值。</summary>
        public static double Or(double value, double fallback)
        {
            return IsFinite(value) ? value : fallback;
        }

        public static float Or(float value, float fallback)
        {
            return IsFinite(value) ? value : fallback;
        }

        /// <summary>
        /// ADOFAI 的 percentAcc 在不同版本里可能是 0~1 或 0~100，统一归一到 0~1。
        /// </summary>
        public static float NormalizeAccuracy(float value)
        {
            if (!IsFinite(value)) return 0f;
            double normalized = value;
            if (normalized > 1.5) normalized /= 100.0;
            return Clamp01((float)normalized);
        }
    }
}
