using System;
using TADOFAI.Mod;
using TADOFAI.Mod.Loader;
using UMModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace TADOFAI.Loader.UMM
{
    /// <summary>
    /// UMM 入口。UMM 会在入口程序集里查找签名匹配的静态 Load(ModEntry) 方法，
    /// 方法名和位置由 info.json 的 EntryMethod 指定。
    ///
    /// </summary>
    public static class UnityModEntry
    {
        public static bool Load(UMModEntry modEntry)
        {
            try
            {
                UnityModLoaderAdapter adapter = new UnityModLoaderAdapter(modEntry);
                ModLog.Bind(adapter);
                Settings.Load(adapter.ModPath);
                Main.Bind(adapter);

                ModLog.Info("TADOFAI.Mod " + Main.ModVersion + " 已加载（等待 UMM 启用）");
                return true;
            }
            catch (Exception ex)
            {
                // 加载失败只返回 false，不抛出，避免影响游戏启动
                ModLog.Error("TADOFAI.Mod 加载失败: " + ex);
                return false;
            }
        }
    }
}
