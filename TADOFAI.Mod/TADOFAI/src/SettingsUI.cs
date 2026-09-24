using System;
using System.Globalization;
using TADOFAI.Mod.Protocol;
using UnityEngine;

namespace TADOFAI.Mod
{
    /// <summary>
    /// Mod 配置界面（Unity IMGUI）。
    /// </summary>
    public static class SettingsUI
    {
        private const float LabelWidth = 200f;
        private const float SliderWidth = 180f;
        private const float ValueWidth = 56f;
        private const float AutoSaveDelay = 1.0f;
        private const float SavedHintDuration = 1.5f;

        private static bool _dirty;
        private static float _changedAt;
        private static float _savedAt = -999f;

        /// <summary>由加载器在它的设置窗口里每帧调用。</summary>
        public static void Draw()
        {
            Settings settings = Settings.Current;

            GUILayout.Label("TADOFAI Mod v" + Main.ModVersion + (Main.IsEnabled ? "" : "（未启用）"));
            GUILayout.Label("游戏 " + VersionSafe.GameVersion
                            + "    API " + VersionSafe.Describe()
                            + "    判定 " + (HitMarginCompat.ResolvedByName ? "按名字解析" : "解析失败，Combo 已禁用"));
            GUILayout.Space(6f);

            DrawConnectionSection(settings);
            GUILayout.Space(6f);
            DrawCollectionSection(settings);
            GUILayout.Space(6f);
            DrawDisplaySection(settings);
            GUILayout.Space(6f);
            DrawLiveSection();
            GUILayout.Space(6f);
            DrawFooter(settings);
        }

        /// <summary>窗口关闭时由加载器调用：把还没落盘的改动写掉。</summary>
        public static void SaveOnHide()
        {
            if (_dirty) SaveNow();
        }

        private static void DrawConnectionSection(Settings settings)
        {
            GUILayout.Label("连接");
            TextRow("  服务地址", ref settings.ServerUrl, 320f);
            ToggleRow("  自动重连", ref settings.AutoReconnect);
            SliderRow("  状态发送频率 (Hz)", ref settings.StateRate, 1f, 120f);
        }

        private static void DrawCollectionSection(Settings settings)
        {
            GUILayout.Label("数据采集");
            ToggleRow("  上报命中判定", ref settings.SendHits);
            ToggleRow("  上报 Timing", ref settings.ShowTiming);
            ToggleRow("  上报准确率", ref settings.ShowAccuracy);
            ToggleRow("  上报 BPM", ref settings.ShowBpm);
            ToggleRow("  上报进度", ref settings.SendProgress);
            ToggleRow("  练习模式也上报", ref settings.TrackPracticeMode);
            SliderIntRow("  事件队列上限", ref settings.MaxQueuedEvents, 64, 16384);
        }

        private static void DrawDisplaySection(Settings settings)
        {
            GUILayout.Label("显示与调试");
            ToggleRow("  隐藏游戏内 Debug 文本", ref settings.HideDebugText);
            ToggleRow("  输出调试日志", ref settings.LogVerbose);
        }

        private static void DrawLiveSection()
        {
            // 只在设置窗口打开时调用，每帧一个快照的开销可以忽略
            GameSnapshot snapshot = Collector.BuildSnapshotMessage().Snapshot;
            PlayState play = snapshot.Play;

            GUILayout.Label("运行状态");
            GUILayout.Label("  连接: " + (snapshot.Connected ? "已连接" : "未连接")
                            + "    端点: " + (string.IsNullOrEmpty(Transport.Endpoint) ? "-" : Transport.Endpoint)
                            + "    丢弃事件: " + snapshot.DroppedEvents.ToString(CultureInfo.InvariantCulture));
            GUILayout.Label("  局内: " + (Collector.IsInGame ? "是" : "否")
                            + "    gameState: " + snapshot.GameState
                            + "    seq: " + play.Seq.ToString(CultureInfo.InvariantCulture)
                            + "    进度: " + Percent(play.Progress)
                            + "    Combo: " + play.Combo.ToString(CultureInfo.InvariantCulture)
                            + "    Miss: " + play.Misses.ToString(CultureInfo.InvariantCulture));
            GUILayout.Label("  准确率: " + Percent(play.Accuracy)
                            + "    X 准确率: " + Percent(play.XAccuracy)
                            + "    BPM: " + play.Bpm.ToString("0.##", CultureInfo.InvariantCulture)
                            + "    Timing: " + play.TimingMs.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " ms");
        }

        private static void DrawFooter(Settings settings)
        {
            bool justSaved = Time.realtimeSinceStartup - _savedAt < SavedHintDuration;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("保存配置", GUILayout.Width(96f))) SaveNow();
            if (GUILayout.Button("重新连接", GUILayout.Width(96f))) Reconnect();
            GUILayout.Label(_dirty ? "有未保存的改动" : (justSaved ? "已保存" : string.Empty));
            GUILayout.EndHorizontal();

            GUILayout.Label("配置文件: " + (string.IsNullOrEmpty(Settings.FilePath) ? "（未初始化）" : Settings.FilePath));

            // 防抖自动保存：拖滑块 / 输入地址不会每次都写盘
            if (_dirty && Time.realtimeSinceStartup - _changedAt > AutoSaveDelay) SaveNow();
        }

        private static void TextRow(string label, ref string value, float width)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            string updated = GUILayout.TextField(value ?? string.Empty, GUILayout.Width(width));
            GUILayout.EndHorizontal();

            if (!string.Equals(updated, value, StringComparison.Ordinal))
            {
                value = updated;
                MarkDirty();
            }
        }

        private static void ToggleRow(string label, ref bool value)
        {
            bool updated = GUILayout.Toggle(value, " " + label);
            if (updated != value)
            {
                value = updated;
                MarkDirty();
            }
        }

        private static void SliderRow(string label, ref float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            float updated = Mathf.Round(GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(SliderWidth)));
            GUILayout.Label(value.ToString("0", CultureInfo.InvariantCulture), GUILayout.Width(ValueWidth));
            GUILayout.EndHorizontal();

            if (updated != value)
            {
                value = updated;
                MarkDirty();
            }
        }

        private static void SliderIntRow(string label, ref int value, int min, int max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            int updated = Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(SliderWidth)));
            GUILayout.Label(value.ToString(CultureInfo.InvariantCulture), GUILayout.Width(ValueWidth));
            GUILayout.EndHorizontal();

            if (updated != value)
            {
                value = updated;
                MarkDirty();
            }
        }

        private static void MarkDirty()
        {
            _dirty = true;
            _changedAt = Time.realtimeSinceStartup;
        }

        private static void SaveNow()
        {
            // 先把非法输入收敛掉，界面上显示的就是真正生效的值
            Settings.Current.Sanitize();
            Settings.Save();
            _dirty = false;
            _savedAt = Time.realtimeSinceStartup;
        }

        private static void Reconnect()
        {
            SaveNow();
            try
            {
                // 服务地址只能靠重连生效
                Transport.Stop();
                Transport.Start();
            }
            catch (Exception ex)
            {
                ModLog.Warn("重新连接失败: " + ex.Message);
            }
        }

        private static string Percent(float value)
        {
            return (value * 100f).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }
    }
}
