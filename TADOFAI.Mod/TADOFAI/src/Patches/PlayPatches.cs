using System;
using HarmonyLib;
using MonsterLove.StateMachine;

namespace TADOFAI.Mod.Patches
{
    /// <summary>游玩过程中的通用 Patch：状态机、进度、Auto 开关。</summary>
    internal static class PlayPatches
    {
        /// <summary>
        /// 成功和失败。ChangeState 有两个重载，必须用参数类型消歧。
        /// </summary>
        [HarmonyPatch(typeof(StateBehaviour), nameof(StateBehaviour.ChangeState), new Type[] { typeof(Enum) })]
        internal static class StateChangePatch
        {
            private static void Postfix(Enum newState)
            {
                switch ((States)newState)
                {
                    case States.Fail2:
                        Collector.RecordDeath();
                        break;
                    case States.Won:
                        Collector.RecordClear();
                        break;
                }
            }
        }

        /// <summary>
        /// 进度刷新点。Coop 下参数不一定代表当前玩家，交给 Collector 按 playerID 区分。
        /// </summary>
        [HarmonyPatch(typeof(scrPlanet), "MoveToNextFloor")]
        internal static class MoveToNextFloorPatch
        {
            private static void Postfix(scrPlanet __instance)
            {
                Collector.UpdateProgress(__instance);
            }
        }

        /// <summary>Auto 开关变化。</summary>
        [HarmonyPatch(typeof(RDC), nameof(RDC.auto), MethodType.Setter)]
        internal static class AutoSetterPatch
        {
            private static void Postfix()
            {
                Collector.RecordAutoChanged();
            }
        }
    }
}
