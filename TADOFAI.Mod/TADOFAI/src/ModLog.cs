using System;
using System.Collections.Generic;
using TADOFAI.Mod.Loader;

namespace TADOFAI.Mod
{
    /// <summary>
    /// 统一日志出口。加载器未绑定时退回 UnityEngine.Debug。
    /// 高频路径不要直接调用，避免高 BPM 下日志本身成为瓶颈。
    /// </summary>
    public static class ModLog
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> WarnedOnce = new HashSet<string>();
        private static IModLoader _loader;

        public static void Bind(IModLoader loader)
        {
            _loader = loader;
        }

        public static void Info(string message)
        {
            try
            {
                if (_loader != null) _loader.Log(message);
                else UnityEngine.Debug.Log("[TADOFAI.Mod] " + message);
            }
            catch
            {
                // 日志失败不能影响游戏
            }
        }

        public static void Warn(string message)
        {
            try
            {
                if (_loader != null) _loader.Warning(message);
                else UnityEngine.Debug.LogWarning("[TADOFAI.Mod] " + message);
            }
            catch
            {
            }
        }

        public static void Error(string message)
        {
            try
            {
                if (_loader != null) _loader.Error(message);
                else UnityEngine.Debug.LogError("[TADOFAI.Mod] " + message);
            }
            catch
            {
            }
        }

        public static void Debug(string message)
        {
            if (!Settings.Current.LogVerbose) return;
            Info(message);
        }

        /// <summary>同一个 key 只告警一次，避免每帧/每次命中重复刷屏。</summary>
        public static void WarnOnce(string key, string message)
        {
            lock (Gate)
            {
                if (!WarnedOnce.Add(key)) return;
            }
            Warn(message);
        }

        public static void ResetOnceKeys()
        {
            lock (Gate)
            {
                WarnedOnce.Clear();
            }
        }
    }
}
