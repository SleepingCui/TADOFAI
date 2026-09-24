using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TADOFAI.Mod
{
    /// <summary>判定语义。数值全部在运行时按枚举名解析，禁止写死。</summary>
    public static class HitMarginCompat
    {
        private static readonly HashSet<int> PerfectCoreValues = new HashSet<int>();
        private static readonly HashSet<int> ComboBreakValues = new HashSet<int>();
        private static readonly HashSet<int> EarlyValues = new HashSet<int>();
        private static readonly HashSet<int> LateValues = new HashSet<int>();
        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>();

        /// <summary>Auto 判定值，-1 表示未解析到。</summary>
        public static int Auto { get; private set; }

        /// <summary>当前版本是否存在 X 系判定（XPerfect 等）。</summary>
        public static bool HasXVariant { get; private set; }

        /// <summary>是否成功按枚举名建立映射；false 说明走的是内置兜底表。</summary>
        public static bool ResolvedByName { get; private set; }

        public static void Setup()
        {
            PerfectCoreValues.Clear();
            ComboBreakValues.Clear();
            EarlyValues.Clear();
            LateValues.Clear();
            Names.Clear();
            Auto = -1;
            HasXVariant = false;
            ResolvedByName = false;

            try
            {
                Type type = typeof(HitMargin);
                foreach (string name in Enum.GetNames(type))
                {
                    int value = (int)Enum.Parse(type, name);
                    Names[value] = name;

                    string lower = name.ToLowerInvariant();
                    bool isAuto = string.Equals(lower, "auto", StringComparison.Ordinal);

                    if (lower.StartsWith("x", StringComparison.Ordinal)) HasXVariant = true;

                    if (!isAuto && lower.IndexOf("perfect", StringComparison.Ordinal) >= 0)
                        PerfectCoreValues.Add(value);

                    // 断 Combo 的是 fail 系判定：FailMiss / FailOverload / FailedFloor
                    if (lower.IndexOf("fail", StringComparison.Ordinal) >= 0 ||
                        lower.IndexOf("miss", StringComparison.Ordinal) >= 0 ||
                        lower.IndexOf("overload", StringComparison.Ordinal) >= 0)
                        ComboBreakValues.Add(value);

                    if (lower.IndexOf("early", StringComparison.Ordinal) >= 0) EarlyValues.Add(value);
                    if (lower.IndexOf("late", StringComparison.Ordinal) >= 0) LateValues.Add(value);

                    if (isAuto) Auto = value;
                }

                ResolvedByName = PerfectCoreValues.Count > 0 && ComboBreakValues.Count > 0;
            }
            catch (Exception ex)
            {
                ModLog.Warn("HitMarginCompat: 按枚举名解析失败 (" + ex.Message + ")");
            }

            if (!ResolvedByName)
            {
                // 不写死数值兜底：宁可没有 Combo 数据，也不要给出错误判定。
                ModLog.Error("HitMarginCompat: 无法按枚举名解析 HitMargin，Combo / Miss 统计已禁用；请核对当前游戏版本");
            }
        }

        /// <summary>判定是否为保持 Combo 的「完美核心」判定（Auto 不算）。</summary>
        public static bool IsPerfectCore(int value)
        {
            return PerfectCoreValues.Contains(value);
        }

        /// <summary>判定是否断 Combo（Miss / Overload 系）。</summary>
        public static bool BreaksCombo(int value)
        {
            return ComboBreakValues.Contains(value);
        }

        public static bool IsMiss(int value)
        {
            return ComboBreakValues.Contains(value);
        }

        public static bool IsAuto(int value)
        {
            return Auto >= 0 && value == Auto;
        }

        public static bool IsEarly(int value)
        {
            return EarlyValues.Contains(value);
        }

        public static bool IsLate(int value)
        {
            return LateValues.Contains(value);
        }

        /// <summary>是否是 X 系判定（X 准确率模式）。</summary>
        public static bool IsX(int value)
        {
            string name;
            return Names.TryGetValue(value, out name) &&
                   name.StartsWith("x", StringComparison.Ordinal);
        }

        /// <summary>判定名，直接使用游戏枚举名，未知返回 Unknown。</summary>
        public static string ToJudgement(int value)
        {
            string name;
            return Names.TryGetValue(value, out name) ? name : "Unknown";
        }

    }

    /// <summary>游戏版本与能力探测。所有判断优先用「能力探测」，版本号只作为补充。</summary>
    public static class VersionSafe
    {
        private static readonly string[] CapabilityList = new string[] { "hit", "timing", "accuracy", "bpm", "progress" };

        public static string GameVersion { get; private set; }

        /// <summary>从版本号里解析出的 rXXX；解析不到为 -1。</summary>
        public static int Revision { get; private set; }

        /// <summary>是否存在 scrMarginTracker（r141+ 每个玩家独立判定）。</summary>
        public static bool HasMarginTracker { get; private set; }

        public static bool IsV141OrLater { get; private set; }

        public static bool IsV149OrLater { get; private set; }

        public static string[] Capabilities
        {
            get { return CapabilityList; }
        }

        public static void Setup()
        {
            GameVersion = "unknown";
            Revision = -1;

            HasMarginTracker = FindType("scrMarginTracker") != null;
            bool hasHitMarginInSec = HasMethod(FindType("scrMisc"), "GetHitMarginInSec");

            string raw = ReadGameVersionString();
            if (!string.IsNullOrEmpty(raw)) GameVersion = raw;
            Revision = ParseRevision(raw);

            IsV141OrLater = HasMarginTracker || Revision >= 141;
            IsV149OrLater = hasHitMarginInSec || Revision >= 149;

            HitMarginCompat.Setup();

            ModLog.Info("游戏版本: " + GameVersion + "  (解析 r" + Revision + ")");
            ModLog.Info("API: " + Describe());
            ModLog.Info("HitMargin: " + (HitMarginCompat.ResolvedByName ? "按名字解析" : "兜底表") +
                        "  XPerfect=" + HitMarginCompat.HasXVariant);
        }

        public static string Describe()
        {
            return IsV141OrLater ? "v141+" : "v136";
        }

        /// <summary>取判定所属玩家序号；失败返回 0。</summary>
        public static int GetPlayerIndex(scrMarginTracker tracker)
        {
            if (tracker == null) return 0;

            // 有些版本在 tracker 上直接带 playerID
            int id = ReadIntMember(tracker, "playerID");
            if (id >= 0) return id;

            try
            {
                // 首选：按玩家列表逐个比对 marginTracker
                scrPlayerManager manager = GameRefs.Players;
                if (manager != null && manager.allPlayers != null)
                {
                    for (int i = 0; i < manager.allPlayers.Length; i++)
                    {
                        scrPlayer player = manager.allPlayers[i];
                        if (player != null && ReferenceEquals(player.marginTracker, tracker)) return i;
                    }
                }

                // 兜底：按 marginTrackers 数组下标
                scrMarginTracker[] trackers = GameRefs.MarginTrackers;
                if (trackers != null)
                {
                    for (int i = 0; i < trackers.Length; i++)
                    {
                        if (ReferenceEquals(trackers[i], tracker)) return i;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("VersionSafe.GetPlayerIndex", "定位玩家序号失败: " + ex.Message);
            }

            return 0;
        }

        /// <summary>跨程序集按类型名查找（不依赖编译期引用，便于兼容缺失类型）。</summary>
        public static Type FindType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            try
            {
                System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    try
                    {
                        Type found = assemblies[i].GetType(typeName, false);
                        if (found != null) return found;
                    }
                    catch
                    {
                        // 个别程序集反射失败不影响其他程序集
                    }
                }
            }
            catch
            {
            }

            try
            {
                return Type.GetType(typeName, false);
            }
            catch
            {
                return null;
            }
        }

        public static bool HasMethod(Type type, string methodName)
        {
            if (type == null || string.IsNullOrEmpty(methodName)) return false;

            try
            {
                return type.GetMethod(methodName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static) != null;
            }
            catch
            {
                return false;
            }
        }

        private static int ReadIntMember(object target, string memberName)
        {
            if (target == null || string.IsNullOrEmpty(memberName)) return -1;

            try
            {
                Type type = target.GetType();

                System.Reflection.FieldInfo field = type.GetField(memberName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(int)) return (int)field.GetValue(target);

                System.Reflection.PropertyInfo property = type.GetProperty(memberName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (property != null && property.PropertyType == typeof(int) && property.CanRead)
                    return (int)property.GetValue(target, null);
            }
            catch
            {
            }

            return -1;
        }

        private static int ParseRevision(string version)
        {
            if (string.IsNullOrEmpty(version)) return -1;

            try
            {
                Match match = Regex.Match(version, @"r(\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    int revision;
                    if (int.TryParse(match.Groups[1].Value, out revision)) return revision;
                }
            }
            catch
            {
            }

            return -1;
        }

        /// <summary>按候选成员依次尝试读取游戏版本字符串。</summary>
        private static string ReadGameVersionString()
        {
            try
            {
                string version = UnityEngine.Application.version;
                if (!string.IsNullOrEmpty(version)) return version;
            }
            catch
            {
            }

            string[] candidates = new string[]
            {
                "GCNS.gameVersion", "ADOBase.gameVersion", "PersistentData.gameVersion", "scnGame.gameVersion"
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string value = ReadStaticStringMember(candidates[i]);
                if (!string.IsNullOrEmpty(value)) return value;
            }

            return null;
        }

        private static string ReadStaticStringMember(string typeDotMember)
        {
            if (string.IsNullOrEmpty(typeDotMember)) return null;

            int separator = typeDotMember.IndexOf('.');
            if (separator <= 0) return null;

            Type type = FindType(typeDotMember.Substring(0, separator));
            if (type == null) return null;

            string memberName = typeDotMember.Substring(separator + 1);

            try
            {
                System.Reflection.FieldInfo field = type.GetField(memberName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static);
                if (field != null && field.GetValue(null) != null)
                    return field.GetValue(null).ToString();

                System.Reflection.PropertyInfo property = type.GetProperty(memberName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static);
                if (property != null && property.CanRead)
                {
                    object value = property.GetValue(null, null);
                    if (value != null) return value.ToString();
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
