using HarmonyLib;

namespace TADOFAI.Mod.Patches
{
    /// <summary>
    /// 检查点事件。
    /// 协议里 checkpoint 早就定义好了（ModMessage / event-v1.schema.json / core/models.py），
    /// 但一直没有任何地方真的发过它。scrController.Checkpoint_Enter() 是玩家踩到检查点那一刻，
    /// 比比对 seqID 变化更准——同一格可以重复进入，游戏自己已经做过判定。
    /// </summary>
    internal static class CheckpointPatches
    {
        [HarmonyPatch(typeof(scrController), "Checkpoint_Enter")]
        internal static class CheckpointEnterPatch
        {
            private static void Postfix()
            {
                Collector.RecordCheckpoint();
            }
        }
    }
}
