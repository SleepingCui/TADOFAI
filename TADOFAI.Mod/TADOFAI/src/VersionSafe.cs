using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TADOFAI.Mod
{
    /// <summary>判定语义。数值全部在运行时按枚举名解析，禁止写死。</summary>
    public static class HitMarginCompat
    {
        /// <summary>名字里含 perfect 的全部判定（含 EarlyPerfect / LatePerfect）。</summary>
        private static readonly HashSet<int> PerfectNameValues = new HashSet<int>();

        /// <summary>
        /// 中心「完美」判定（等价于 JipperOverlayer 的 IsPerfectCore）：
        /// 有 XPerfect 的版本 = { PerfectMinus, XPerfect, PerfectPlus }；
        /// 没有的版本 = { Perfect }。不含 EarlyPerfect / LatePerfect（黄绿）与 Auto。
        /// 两个显式名字都解析不到时，退化为「名字含 perfect 且非 early / late」（仍不写死数值）。
        /// </summary>
        private static readonly HashSet<int> PerfectCoreValues = new HashSet<int>();

        private static readonly HashSet<int> ComboBreakValues = new HashSet<int>();
        private static readonly HashSet<int> EarlyValues = new HashSet<int>();
        private static readonly HashSet<int> LateValues = new HashSet<int>();
        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>();

        /// <summary>Auto 判定值，-1 表示未解析到。</summary>
        public static int Auto { get; private set; }

        /// <summary>Midspin 判定值，-1 表示未解析到。</summary>
        public static int Midspin { get; private set; }

        /// <summary>当前版本是否有原生 XPerfect（即 r149+ 的判定布局）。</summary>
        public static bool HasNativeXPerfect { get; private set; }

        /// <summary>EarlyPerfect / LatePerfect 的判定值，无法解析时为 -1（用于 Extended 完美）。</summary>
        private static int EarlyPerfectValue = -1;
        private static int LatePerfectValue = -1;

        /// <summary>当前版本是否存在 X 系判定（XPerfect 等）。</summary>
        public static bool HasXVariant { get; private set; }

        /// <summary>是否成功按枚举名建立映射；false 说明走的是内置兜底表。</summary>
        public static bool ResolvedByName { get; private set; }

        public static void Setup()
        {
            PerfectNameValues.Clear();
            PerfectCoreValues.Clear();
            ComboBreakValues.Clear();
            EarlyValues.Clear();
            LateValues.Clear();
            Names.Clear();
            Auto = -1;
            Midspin = -1;
            HasXVariant = false;
            HasNativeXPerfect = false;
            EarlyPerfectValue = -1;
            LatePerfectValue = -1;
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
                        PerfectNameValues.Add(value);

                    // 断 Combo 的是 fail 系判定：FailMiss / FailOverload / FailedFloor
                    if (lower.IndexOf("fail", StringComparison.Ordinal) >= 0 ||
                        lower.IndexOf("miss", StringComparison.Ordinal) >= 0 ||
                        lower.IndexOf("overload", StringComparison.Ordinal) >= 0)
                        ComboBreakValues.Add(value);

                    if (lower.IndexOf("early", StringComparison.Ordinal) >= 0) EarlyValues.Add(value);
                    if (lower.IndexOf("late", StringComparison.Ordinal) >= 0) LateValues.Add(value);

                    if (isAuto) Auto = value;
                    if (string.Equals(lower, "midspin", StringComparison.Ordinal)) Midspin = value;
                    if (string.Equals(lower, "earlyperfect", StringComparison.Ordinal)) EarlyPerfectValue = value;
                    if (string.Equals(lower, "lateperfect", StringComparison.Ordinal)) LatePerfectValue = value;
                }

                // 中心完美：按 JipperOverlayer 的口径解析，绝不写死数值。
                int xPerfect = ResolveName("XPerfect");
                int perfectMinus = ResolveName("PerfectMinus");
                int perfectPlus = ResolveName("PerfectPlus");
                int perfect = ResolveName("Perfect");

                HasNativeXPerfect = xPerfect >= 0;
                if (HasNativeXPerfect)
                {
                    if (perfectMinus >= 0) PerfectCoreValues.Add(perfectMinus);
                    PerfectCoreValues.Add(xPerfect);
                    if (perfectPlus >= 0) PerfectCoreValues.Add(perfectPlus);
                }
                else if (perfect >= 0)
                {
                    PerfectCoreValues.Add(perfect);
                }

                // 兜底（依旧只按枚举名推，绝不写死数值）：中心完美 = 名字含 perfect 且不属于 early / late 的判定。
                // r150 布局下推出 { PerfectMinus, XPerfect, PerfectPlus }，r148 布局下推出 { Perfect }，
                // 与上面的显式解析结果一致；这样即使某个版本改名，也不会整体丢掉 Combo 统计。
                if (PerfectCoreValues.Count == 0 && PerfectNameValues.Count > 0)
                {
                    foreach (int value in PerfectNameValues)
                    {
                        if (!EarlyValues.Contains(value) && !LateValues.Contains(value))
                            PerfectCoreValues.Add(value);
                    }

                    if (PerfectCoreValues.Count > 0)
                        ModLog.Warn("HitMarginCompat: 未找到 Perfect / XPerfect 等显式判定名，已按「含 perfect 且非 early/late」推定中心完美");
                }

                ResolvedByName = PerfectNameValues.Count > 0 && ComboBreakValues.Count > 0 &&
                                 PerfectCoreValues.Count > 0;
            }
            catch (Exception ex)
            {
                ModLog.Warn("HitMarginCompat: 按枚举名解析失败 (" + ex.Message + ")");
            }

            if (!ResolvedByName)
            {
                // 不写死数值兜底：宁可没有 Combo 数据，也不要给出错误判定。
                // 把实际枚举成员打出来，方便实机核对 ADOFAI 版本。
                ModLog.Error("HitMarginCompat: 无法按枚举名解析 HitMargin，Combo / Miss 统计已禁用；" +
                             "当前 HitMargin = [" + string.Join(", ", Enum.GetNames(typeof(HitMargin))) + "]");
            }
        }

        /// <summary>按枚举名取判定值；该版本没有这个判定时返回 -1（Enum.Parse 未定义名字必然抛异常）。</summary>
        private static int ResolveName(string name)
        {
            try { return (int)Enum.Parse(typeof(HitMargin), name); }
            catch { return -1; }
        }

        /// <summary>
        /// 中心「完美」判定（绿色数字）。有 XPerfect 的版本 = PerfectMinus / XPerfect / PerfectPlus；
        /// 没有的版本 = Perfect。不含 EarlyPerfect / LatePerfect 与 Auto。
        /// </summary>
        public static bool IsPerfectCore(int value)
        {
            return PerfectCoreValues.Contains(value);
        }

        /// <summary>黄绿及以上：中心完美 + EarlyPerfect / LatePerfect（JipperOverlayer 扩展连击口径）。</summary>
        public static bool IsPerfectExtended(int value)
        {
            return IsPerfectCore(value) || value == EarlyPerfectValue || value == LatePerfectValue;
        }

        /// <summary>是否是名字里含 perfect 的判定（宽口径，仅用于展示/诊断）。</summary>
        public static bool MatchesPerfectName(int value)
        {
            return PerfectNameValues.Contains(value);
        }

        public static bool IsMidspin(int value)
        {
            return Midspin >= 0 && value == Midspin;
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
        /// <summary>
        /// 协议能力位。前端据此决定「本局能显示什么」，而不是靠猜。
        /// 必须在 Setup() 之后读取；Setup() 之前只有基础 5 项。
        /// </summary>
        private static readonly List<string> CapabilityList = new List<string>(
            new string[] { "hit", "timing", "accuracy", "bpm", "progress" });

        public static string GameVersion { get; private set; }

        /// <summary>从版本号里解析出的 rXXX；解析不到为 -1。</summary>
        public static int Revision { get; private set; }

        /// <summary>是否存在 scrMarginTracker（r141+ 每个玩家独立判定）。</summary>
        public static bool HasMarginTracker { get; private set; }

        public static bool IsV141OrLater { get; private set; }

        public static bool IsV149OrLater { get; private set; }

        public static string[] Capabilities
        {
            get { return CapabilityList.ToArray(); }
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
            SetupCapabilities();

            ModLog.Info("游戏版本: " + GameVersion + "  (解析 r" + Revision + ")");
            ModLog.Info("API: " + Describe());
            ModLog.Info("HitMargin: " + (HitMarginCompat.ResolvedByName ? "按名字解析" : "兜底表") +
                        "  原生XPerfect=" + HitMarginCompat.HasNativeXPerfect +
                        "  Auto=" + HitMarginCompat.Auto +
                        "  Midspin=" + HitMarginCompat.Midspin);
        }

        public static string Describe()
        {
            return IsV141OrLater ? "v141+" : "v136";
        }

        /// <summary>
        /// 运行时组装能力位。每项都做真探测（而不是「版本号大于 X 就算有」），
        /// 这样前端只要看到 "timeline" 就能放心读 timeline.*，看不到就不去渲染那一块。
        /// </summary>
        private static void SetupCapabilities()
        {
            CapabilityList.Clear();
            CapabilityList.AddRange(new string[] { "hit", "timing", "accuracy", "bpm", "progress" });

            try
            {
                if (HitMarginCompat.HasXVariant) CapabilityList.Add("xperfect");

                // Combo 是「完美连击」口径（只有中心完美才续），并上报 perfectCombo 标志
                CapabilityList.Add("perfectcombo");

                // 判定表：前端把 hitMargins 下标翻译成判定名的前提
                Type helper = FindType("HitMarginHelper");
                if (helper != null) CapabilityList.Add("hitmargins");

                // 关卡元信息（哈希 / 谱师 / 曲师 / 时长 / 楼层数）
                Type levelData = FindType("ADOFAI.LevelData");
                if (levelData != null && LevelDataReadable(levelData)) CapabilityList.Add("mapmeta");

                // 时间轴：音乐时间 / 地图时间
                if (GameRefs.HasSongSource) CapabilityList.Add("timeline");

                // 逐玩家（Coop）
                if (FindType("scrPlayerManager") != null && HasMarginTracker) CapabilityList.Add("coop");

                // 检查点事件
                if (HasMethod(typeof(scrController), "Checkpoint_Enter")) CapabilityList.Add("checkpoint");

                // 暂停 / 帧率
                if (HasMethod(typeof(scrController), "get_paused")) CapabilityList.Add("paused");
                if (HasMethod(typeof(scrController), "get_averageFrameTime") ||
                    HasField(typeof(scrController), "averageFrameTime")) CapabilityList.Add("fps");

                // 判定窗（角度 → 毫秒的边界）
                if (FindType("GCS") != null) CapabilityList.Add("timingwindows");

                CapabilityList.Add("kps");
                CapabilityList.Add("pseudobpm");

                if (GameRefs.HasCheckpointTiles) CapabilityList.Add("checkpointtiles");
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("VersionSafe.SetupCapabilities", "能力探测失败: " + ex.Message);
            }

            ModLog.Info("协议能力: " + string.Join(",", Capabilities));
        }

        private static bool LevelDataReadable(Type levelData)
        {
            return HasField(levelData, "_hash") || HasMethod(levelData, "get_Hash");
        }

        public static bool HasField(Type type, string fieldName)
        {
            if (type == null || string.IsNullOrEmpty(fieldName)) return false;

            try
            {
                return type.GetField(fieldName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static) != null;
            }
            catch
            {
                return false;
            }
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
