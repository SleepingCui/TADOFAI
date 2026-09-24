using System;
using System.IO;
using System.Xml.Serialization;

namespace TADOFAI.Mod
{
    /// <summary>
    /// Mod 配置。用 XmlSerializer 读写 Settings.xml（随 Mod 目录），
    /// 保存采用「临时文件 + 替换」，避免中断造成配置损坏。
    /// </summary>
    [Serializable]
    public sealed class Settings
    {
        /// <summary>WebSocket 端点，必须指向 Core 的 /ws/mod。</summary>
        public string ServerUrl = "ws://127.0.0.1:37125/ws/mod";

        /// <summary>断线后是否自动重连（250ms / 500ms / 1s / 2s 退避）。</summary>
        public bool AutoReconnect = true;

        /// <summary>状态流合并发送频率（Hz），协议建议 30~120。</summary>
        public float StateRate = 60f;

        /// <summary>事件队列上限，满时丢弃最旧事件并计入 droppedEvents。</summary>
        public int MaxQueuedEvents = 4096;

        /// <summary>是否采集并上报命中判定。</summary>
        public bool SendHits = true;

        /// <summary>是否上报准确率。</summary>
        public bool ShowAccuracy = true;

        /// <summary>是否上报 Timing。</summary>
        public bool ShowTiming = true;

        /// <summary>是否上报 BPM。</summary>
        public bool ShowBpm = true;

        /// <summary>是否上报进度 / Floor / SeqID。</summary>
        public bool SendProgress = true;

        /// <summary>练习模式是否也上报（默认关闭，与开发指南的 practiceMode 短路一致）。</summary>
        public bool TrackPracticeMode = false;

        /// <summary>隐藏游戏内的 Debug 文本。</summary>
        public bool HideDebugText = false;

        /// <summary>输出调试日志（高频，默认关闭）。</summary>
        public bool LogVerbose = false;

        public static Settings Current { get; private set; }

        public static string FilePath { get; private set; }

        static Settings()
        {
            Current = new Settings();
            FilePath = string.Empty;
        }

        public static void Load(string modPath)
        {
            try
            {
                if (string.IsNullOrEmpty(modPath))
                {
                    ModLog.Warn("Settings: ModPath 为空，使用默认配置");
                    Current = new Settings();
                    Current.Sanitize();
                    return;
                }

                FilePath = Path.Combine(modPath, "Settings.xml");
                if (!File.Exists(FilePath))
                {
                    Current = new Settings();
                    Current.Sanitize();
                    Save();
                    return;
                }

                using (FileStream stream = File.OpenRead(FilePath))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(Settings));
                    Settings loaded = serializer.Deserialize(stream) as Settings;
                    if (loaded != null) Current = loaded;
                    else Current = new Settings();
                }

                Current.Sanitize();
            }
            catch (Exception ex)
            {
                ModLog.Warn("Settings: 读取失败，改用默认配置 (" + ex.Message + ")");
                Current = new Settings();
                Current.Sanitize();
            }
        }

        public static void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(FilePath)) return;

                string directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string temp = FilePath + ".tmp";
                using (FileStream stream = File.Create(temp))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(Settings));
                    serializer.Serialize(stream, Current);
                }

                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(temp, FilePath);
            }
            catch (Exception ex)
            {
                ModLog.Warn("Settings: 保存失败 " + ex.Message);
            }
        }

        /// <summary>把外部（用户手改）配置收敛到合法范围。</summary>
        public void Sanitize()
        {
            if (!NumUtil.IsFinite(StateRate)) StateRate = 60f;
            StateRate = (float)NumUtil.Clamp(StateRate, 1.0, 120.0);
            MaxQueuedEvents = NumUtil.ClampInt(MaxQueuedEvents, 64, 65536);

            if (string.IsNullOrEmpty(ServerUrl) ||
                (!ServerUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                 !ServerUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)))
            {
                ModLog.Warn("Settings: ServerUrl 非法，回退到默认端点");
                ServerUrl = "ws://127.0.0.1:37125/ws/mod";
            }
        }
    }
}
