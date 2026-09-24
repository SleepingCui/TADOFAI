using HarmonyLib;
using UnityEngine.UI;

namespace TADOFAI.Mod.Patches
{
    /// <summary>游戏内 Debug 文本。</summary>
    internal static class DebugPatches
    {
        /// <summary>返回 false 跳过原方法；返回 true 继续执行原方法。</summary>
        [HarmonyPatch(typeof(scrShowIfDebug), "Update")]
        internal static class HideDebugTextPatch
        {
            private static bool Prefix(Text ___txt)
            {
                if (!Settings.Current.HideDebugText) return true;
                if (___txt == null) return true;

                ___txt.enabled = false;
                return false;
            }
        }
    }
}
