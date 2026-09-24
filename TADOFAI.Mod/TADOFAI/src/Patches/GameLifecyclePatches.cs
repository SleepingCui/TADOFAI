using HarmonyLib;
using UnityEngine.SceneManagement;

namespace TADOFAI.Mod.Patches
{
    /// <summary>游戏生命周期：开始游玩、场景结束与隐藏。</summary>
    internal static class GameLifecyclePatches
    {
        /// <summary>开始游玩。练习模式默认短路（与开发指南一致），可用 TrackPracticeMode 打开。</summary>
        [HarmonyPatch(typeof(scnGame), nameof(scnGame.Play))]
        internal static class GamePlayPatch
        {
            private static void Postfix(int seqID)
            {
                if (GCS.practiceMode && !Settings.Current.TrackPracticeMode) return;
                Collector.StartGame(seqID);
            }
        }

        /// <summary>
        /// 某些模式下 scnGame.Play 前后还没有完整游戏实例，这里做一次补充启动。
        /// </summary>
        [HarmonyPatch(typeof(scrPressToStart), nameof(scrPressToStart.ShowText))]
        internal static class PressToStartPatch
        {
            private static void Postfix()
            {
                Collector.TryStartGame();
            }
        }

        /// <summary>场景淡出。</summary>
        [HarmonyPatch(typeof(scrUIController), nameof(scrUIController.WipeToBlack))]
        internal static class WipeToBlackPatch
        {
            private static void Postfix()
            {
                Collector.EndScene();
            }
        }

        /// <summary>编辑器重置场景（ResetScene 是私有方法，只能按名字 Patch）。</summary>
        [HarmonyPatch(typeof(scnEditor), "ResetScene")]
        internal static class EditorResetScenePatch
        {
            private static void Postfix()
            {
                Collector.EndScene();
            }
        }

        /// <summary>开始加载场景。</summary>
        [HarmonyPatch(typeof(scrController), nameof(scrController.StartLoadingScene))]
        internal static class StartLoadingScenePatch
        {
            private static void Postfix()
            {
                Collector.EndScene();
            }
        }

        /// <summary>Unity 场景卸载（Unity 内部触发 sceneUnloaded 的私有方法）。</summary>
        [HarmonyPatch(typeof(SceneManager), "Internal_SceneUnloaded")]
        internal static class SceneUnloadedPatch
        {
            private static void Postfix()
            {
                Collector.EndScene();
            }
        }
    }
}
