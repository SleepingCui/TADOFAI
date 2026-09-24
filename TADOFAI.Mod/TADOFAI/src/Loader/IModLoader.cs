using System;

namespace TADOFAI.Mod.Loader
{
    /// <summary>
    /// 加载器抽象。游戏逻辑放在共享程序集里，UMM / MelonLoader 只负责适配入口。
    /// </summary>
    public interface IModLoader
    {
        /// <summary>Mod 目录，配置文件写在这里。</summary>
        string ModPath { get; }

        void Log(string message);

        void Warning(string message);

        void Error(string message);

        /// <summary>加载器切换启用状态。</summary>
        event Action<bool> OnToggle;

        /// <summary>加载器每帧回调，参数为 deltaTime。</summary>
        event Action<float> OnUpdate;
    }
}
