using System;
using TADOFAI.Mod;
using TADOFAI.Mod.Loader;
using UMModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace TADOFAI.Loader.UMM
{
    /// <summary>
    /// 把 UMM 的回调和日志转成 IModLoader。
    /// 带 UMM 类型的地方只在这个工程里出现，共享逻辑不感知加载器。
    ///
    /// UMM 的设置窗口：OnGUI 每帧调用（这时在里面画 IMGUI），
    /// OnHideGUI 在窗口关闭时调用（把没落盘的配置写掉）。
    /// </summary>
    public sealed class UnityModLoaderAdapter : IModLoader
    {
        private readonly UMModEntry _entry;

        public UnityModLoaderAdapter(UMModEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            _entry = entry;
            _entry.OnToggle += HandleToggle;
            _entry.OnUpdate += HandleUpdate;
            _entry.OnGUI += HandleGUI;
            _entry.OnHideGUI += HandleHideGUI;
        }

        public string ModPath
        {
            get { return _entry.Path ?? string.Empty; }
        }

        public event Action<bool> OnToggle;

        public event Action<float> OnUpdate;

        public void Log(string message)
        {
            if (_entry.Logger != null) _entry.Logger.Log(message ?? string.Empty);
        }

        public void Warning(string message)
        {
            if (_entry.Logger != null) _entry.Logger.Warning(message ?? string.Empty);
        }

        public void Error(string message)
        {
            if (_entry.Logger != null) _entry.Logger.Error(message ?? string.Empty);
        }

        /// <summary>
        /// UMM 的 OnToggle 是 Func&lt;ModEntry, bool, bool&gt;，返回值表示是否接受该状态。
        /// </summary>
        private bool HandleToggle(UMModEntry entry, bool value)
        {
            Action<bool> handler = OnToggle;
            if (handler != null) handler(value);

            return true;
        }

        private void HandleUpdate(UMModEntry entry, float deltaTime)
        {
            Action<float> handler = OnUpdate;
            if (handler != null) handler(deltaTime);
        }

        private void HandleGUI(UMModEntry entry)
        {
            // 设置窗口可能在 Mod 被禁用时也能打开，让 UI 自己显示状态即可
            SettingsUI.Draw();
        }

        private void HandleHideGUI(UMModEntry entry)
        {
            SettingsUI.SaveOnHide();
        }
    }
}
