using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TADOFAI.Mod
{
    /// <summary>
    /// GameRefs 的跨版本扩展：这里集中放「网页端复刻 JipperOverlayer 需要、原版上报里没有」的读取。
    ///
    /// 设计约束：
    ///  1) 一律经 Reflect（反射 + 缓存）访问游戏成员，编译期不新增对游戏类型的硬依赖，
    ///     这样同一个 dll 能同时兼容 r136 / r141 / r148 / r150 等版本；
    ///  2) 任何一项读失败只影响该字段，返回安全默认值，绝不抛异常；
    ///  3) 逐帧调用者必须自己节流 —— 见 Collector 里的逐玩家采样节流。
    /// </summary>
    public static partial class GameRefs
    {
        private const int HitMarginSlots = 16;

        private static int[] _hitFloorSeqs;
        private static double[] _hitFloorTimes;
        private static bool _levelCacheValid;
        private static int _checkpointTileCount;
        private static double _mapLength;
        private static double _lastCacheAttempt = -1.0;

        /// <summary>伪 BPM 的取样窗口（最近的命中格数量）。</summary>
        private const int PseudoBpmWindow = 8;

        #region 关卡元信息

        /// <summary>scnGame.instance.levelData（ADOFAI.LevelData）。用 object 以免硬引用该类型。</summary>
        public static object LevelData
        {
            get
            {
                try
                {
                    scnGame game = scnGame.instance;
                    if (game == null) return null;
                    return Reflect.Get(game, "levelData");
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>谱面哈希（ADOFAI 的 LevelData.Hash）。本地记录（Plays.dat 之类）要用它当键。</summary>
        public static string MapId
        {
            get
            {
                object levelData = LevelData;
                if (levelData == null) return string.Empty;

                string hash = Reflect.GetString(levelData, "Hash", null);
                if (string.IsNullOrEmpty(hash)) hash = Reflect.GetString(levelData, "_hash", null);
                return hash ?? string.Empty;
            }
        }

        /// <summary>曲师（LevelData.artist）。注意与谱师不是同一个字段。</summary>
        public static string MapArtist
        {
            get
            {
                object levelData = LevelData;
                if (levelData == null) return string.Empty;
                return Reflect.GetString(levelData, "artist", string.Empty);
            }
        }

        /// <summary>谱师（LevelData.author）。</summary>
        public static string MapAuthor
        {
            get
            {
                object levelData = LevelData;
                if (levelData == null) return string.Empty;
                return Reflect.GetString(levelData, "author", string.Empty);
            }
        }

        /// <summary>谱面标称 BPM；读不到时退回节拍器当前 BPM。</summary>
        public static double MapBpm
        {
            get
            {
                double bpm = Reflect.GetDouble(LevelData, "bpm", 0.0);
                if (bpm > 0.0 && NumUtil.IsFinite(bpm)) return bpm;

                scrLevelMaker levelMaker = LevelMaker;
                if (levelMaker != null)
                {
                    float forUnity = Reflect.GetFloat(levelMaker, "bpm_forUnityUseOnly", 0f);
                    if (forUnity > 0f && NumUtil.IsFinite(forUnity)) return forUnity;
                }

                return Bpm;
            }
        }

        /// <summary>难度枚举名（Lenient / Normal / Strict）。</summary>
        public static string DifficultyName
        {
            get { return Reflect.GetStaticString(typeof(GCS), "difficulty", string.Empty); }
        }

        /// <summary>
        /// 谱面总时长（最后一格的 entryTime，单位秒）。
        /// 这是「地图时间轴」的右端，与歌曲长度 musicLength 不一定相等。
        /// </summary>
        public static double MapLength
        {
            get
            {
                EnsureLevelCache();
                return _mapLength;
            }
        }

        /// <summary>谱面里带检查点（ffxCheckpoint）的格子数量。</summary>
        public static int CheckpointTileCount
        {
            get
            {
                EnsureLevelCache();
                return _checkpointTileCount;
            }
        }

        #endregion

        #region 时间轴

        /// <summary>歌曲已播放秒数（AudioSource.time）。</summary>
        public static double MusicTime
        {
            get
            {
                try
                {
                    scrConductor conductor = Conductor;
                    if (conductor == null || conductor.song == null) return 0.0;
                    return NumUtil.IsFinite(conductor.song.time) ? conductor.song.time : 0.0;
                }
                catch
                {
                    return 0.0;
                }
            }
        }

        /// <summary>歌曲总长度（AudioSource.clip.length）。</summary>
        public static double MusicLength
        {
            get
            {
                try
                {
                    scrConductor conductor = Conductor;
                    if (conductor == null || conductor.song == null || conductor.song.clip == null) return 0.0;
                    float length = conductor.song.clip.length;
                    return NumUtil.IsFinite(length) && length > 0f ? length : 0.0;
                }
                catch
                {
                    return 0.0;
                }
            }
        }

        /// <summary>
        /// 地图时间轴上的当前位置（秒）= songposition_minusi + addoffset。
        /// 这是「按谱面计时」的口径，变速/变 BPM 谱面下与 MusicTime 会有差异。
        /// </summary>
        public static double MapTime
        {
            get
            {
                try
                {
                    scrConductor conductor = Conductor;
                    if (conductor == null) return 0.0;

                    double position = conductor.songposition_minusi + conductor.addoffset;
                    return NumUtil.IsFinite(position) ? position : 0.0;
                }
                catch
                {
                    return 0.0;
                }
            }
        }

        #endregion

        #region 会话状态

        public static bool IsPaused
        {
            get
            {
                try
                {
                    scrController controller = Controller;
                    if (controller == null) return false;
                    return controller.paused;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>游戏帧率（1 / averageFrameTime）。注意这是游戏自己的帧率，网页端无法改变它。</summary>
        public static float Fps
        {
            get
            {
                try
                {
                    scrController controller = Controller;
                    if (controller == null) return 0f;

                    float average = controller.averageFrameTime;
                    if (!NumUtil.IsFinite(average) || average <= 0f) return 0f;

                    float fps = 1f / average;
                    if (!NumUtil.IsFinite(fps) || fps < 0f || fps > 1000f) return 0f;
                    return fps;
                }
                catch
                {
                    return 0f;
                }
            }
        }

        /// <summary>本局已用检查点数（scrController.checkpointsUsed 静态字段）。</summary>
        public static int CheckpointsUsed
        {
            get { return Reflect.GetStaticInt(typeof(scrController), "checkpointsUsed", 0); }
        }

        /// <summary>本局死亡数（scrController.deaths 静态字段）。</summary>
        public static int Deaths
        {
            get { return Reflect.GetStaticInt(typeof(scrController), "deaths", 0); }
        }

        #endregion

        #region 判定表

        /// <summary>HitMargin 枚举值总数（r340 为 16）。</summary>
        public static int HitMarginTypeCount
        {
            get
            {
                try
                {
                    Array values = Enum.GetValues(typeof(HitMargin));
                    int max = 0;
                    for (int i = 0; i < values.Length; i++)
                    {
                        int index = Convert.ToInt32(values.GetValue(i), CultureInfo.InvariantCulture);
                        if (index + 1 > max) max = index + 1;
                    }
                    return max > 0 ? max : HitMarginSlots;
                }
                catch
                {
                    return HitMarginSlots;
                }
            }
        }

        /// <summary>下标 = HitMargin 枚举值的判定名。前端靠它解释直方图。</summary>
        public static string[] HitMarginNames
        {
            get
            {
                int count = HitMarginTypeCount;
                string[] names = new string[count];
                for (int i = 0; i < count; i++)
                {
                    try { names[i] = ((HitMargin)i).ToString(); }
                    catch { names[i] = "Unknown" + i.ToString(CultureInfo.InvariantCulture); }
                }
                return names;
            }
        }

        private static Type HitMarginHelperType
        {
            get { return VersionSafe.FindType("HitMarginHelper"); }
        }

        /// <summary>
        /// 判定权重（HitMarginHelper.PlayerHitMarginWeights），下标 = 枚举值。
        /// 精度 / XScore 的前端重算要靠它，避免把权重写死在网页里。
        /// </summary>
        public static double[] HitMarginWeights
        {
            get
            {
                double[] weights = new double[HitMarginTypeCount];
                Type helper = HitMarginHelperType;
                if (helper == null) return weights;

                object table = Reflect.GetStatic(helper, "PlayerHitMarginWeights");
                if (table == null) return weights;

                VisitPairs(table, delegate(object key, object value)
                {
                    int index = Reflect.ToInt(key, -1);
                    if (index < 0 || index >= weights.Length) return;
                    weights[index] = Reflect.ToDouble(value, 0.0);
                });
                return weights;
            }
        }

        /// <summary>每个判定贡献的 XScore（HitMarginHelper.HitMarginXScores），下标 = 枚举值。</summary>
        public static int[] HitMarginXScores
        {
            get
            {
                int[] scores = new int[HitMarginTypeCount];
                Type helper = HitMarginHelperType;
                if (helper == null) return scores;

                object table = Reflect.GetStatic(helper, "HitMarginXScores");
                if (table == null) return scores;

                VisitPairs(table, delegate(object key, object value)
                {
                    int index = Reflect.ToInt(key, -1);
                    if (index < 0 || index >= scores.Length) return;
                    scores[index] = Reflect.ToInt(value, 0);
                });
                return scores;
            }
        }

        /// <summary>
        /// 游戏认定的「玩家命中」判定下标集合（HitMarginHelper.PlayerHitMarginTypes）。
        /// 直方图只统计这些，避免把 Midspin / FailedFloor 这类非玩家判定也报上去。
        /// </summary>
        public static int[] PlayerHitMarginIndices
        {
            get
            {
                Type helper = HitMarginHelperType;
                if (helper == null) return new int[0];

                object raw = Reflect.GetStatic(helper, "PlayerHitMarginTypes");
                Array array = raw as Array;
                if (array == null) return new int[0];

                int[] indices = new int[array.Length];
                int used = 0;
                for (int i = 0; i < array.Length; i++)
                {
                    int index = Reflect.ToInt(array.GetValue(i), -1);
                    if (index < 0) continue;
                    indices[used++] = index;
                }

                if (used == indices.Length) return indices;
                int[] trimmed = new int[used];
                Array.Copy(indices, trimmed, used);
                return trimmed;
            }
        }

        /// <summary>
        /// 判定窗的角度边界（度）：[0]=Counted [1]=Perfect [2]=Pure [3]=XPerfect。
        /// 直接上报角度而不是毫秒 —— 毫秒还要除以速度与音高，前端自己换算更透明，
        /// 也不会因为拿不准游戏内部 API 的参数顺序而报错数。
        /// </summary>
        public static float[] TimingWindowDegrees
        {
            get
            {
                float[] degrees = new float[4];
                degrees[0] = Reflect.GetStaticFloat(typeof(GCS), "HITMARGIN_COUNTED", 40f);
                degrees[1] = Reflect.GetStaticFloat(typeof(GCS), "HITMARGIN_PERFECT", 45f);
                degrees[2] = Reflect.GetStaticFloat(typeof(GCS), "HITMARGIN_PURE", 30f);
                degrees[3] = Reflect.GetStaticFloat(typeof(GCS), "HITMARGIN_XPERFECT_ANGLE_ADJUST", 12.5f);
                return degrees;
            }
        }

        /// <summary>
        /// 换算分母：ms = 度 * 1000 / denom。
        /// denom = 3 * bpmTimesSpeed * conductorPitch，bpmTimesSpeed = bpm * speedTrial。
        /// </summary>
        public static double TimingDenom
        {
            get
            {
                double bpm = Bpm;
                if (!NumUtil.IsFinite(bpm) || bpm <= 0.0) return 0.0;

                float speedTrial = 1f;
                try
                {
                    if (Reflect.GetStaticBool(typeof(GCS), "speedTrialMode", false))
                        speedTrial = Reflect.GetStaticFloat(typeof(GCS), "currentSpeedTrial", 1f);
                }
                catch
                {
                    speedTrial = 1f;
                }
                if (!NumUtil.IsFinite(speedTrial) || speedTrial <= 0f) speedTrial = 1f;

                float pitch = SongPitch;
                double denom = 3.0 * (bpm * speedTrial) * pitch;
                return NumUtil.IsFinite(denom) ? denom : 0.0;
            }
        }

        /// <summary>判定窗毫秒 [Counted, Perfect, Pure, XPerfect]；无法换算时返回全 0。</summary>
        public static double[] TimingWindowMs
        {
            get
            {
                double[] result = new double[4];
                double denom = TimingDenom;
                if (denom <= 0.0) return result;

                float[] degrees = TimingWindowDegrees;
                for (int i = 0; i < result.Length; i++)
                {
                    double ms = degrees[i] * 1000.0 / denom;
                    result[i] = NumUtil.IsFinite(ms) && ms > 0.0 ? ms : 0.0;
                }
                return result;
            }
        }

        #endregion

        #region 逐玩家

        public static scrPlayer GetPlayerObject(int player)
        {
            try
            {
                scrPlayerManager manager = Players;
                if (manager == null || manager.allPlayers == null) return null;
                if (player < 0 || player >= manager.allPlayers.Length) return null;
                return manager.allPlayers[player];
            }
            catch
            {
                return null;
            }
        }

        /// <summary>该玩家的贴合度缩放（scrPlayer._marginScale）。</summary>
        public static float GetPlayerMarginScale(int player)
        {
            return Reflect.GetFloat(GetPlayerObject(player), "_marginScale", 1f);
        }

        /// <summary>该玩家的准确进度百分比（scrMarginTracker.percentComplete）。</summary>
        public static float GetPlayerProgress(int player)
        {
            scrMarginTracker tracker = GetPlayerTracker(player);
            if (tracker == null) return 0f;
            return Reflect.GetFloat(tracker, "percentComplete", 0f);
        }

        public static bool GetPlayerAuto(int player)
        {
            return Reflect.GetBool(GetPlayerObject(player), "auto", false);
        }

        /// <summary>该玩家的判定计数数组（scrMarginTracker.hitMarginsCount）。</summary>
        public static int[] GetPlayerHitCounts(int player, int length)
        {
            int[] result = new int[length];
            if (length <= 0) return result;

            scrMarginTracker tracker = GetPlayerTracker(player);
            if (tracker == null) return result;

            Array raw = Reflect.GetArray(tracker, "hitMarginsCount");
            if (raw == null) return result;

            int used = raw.Length < length ? raw.Length : length;
            for (int i = 0; i < used; i++)
            {
                result[i] = Reflect.ToInt(raw.GetValue(i), 0);
            }
            return result;
        }

        /// <summary>该玩家的颜色（PlanetColor.ToRealColor() → #RRGGBB）。</summary>
        public static string GetPlayerColorHex(int player)
        {
            try
            {
                // 不直接引用 PlanetColor（那是游戏内部结构体），整表走反射，缺类型时安静失败。
                Array colors = Reflect.GetStatic(typeof(scrPlayerManager), "playerColors") as Array;
                if (colors == null || player < 0 || player >= colors.Length) return string.Empty;

                object color = Reflect.Call(colors.GetValue(player), "ToRealColor");
                if (color == null) return string.Empty;

                float r = Reflect.GetFloat(color, "r", 0f);
                float g = Reflect.GetFloat(color, "g", 0f);
                float b = Reflect.GetFloat(color, "b", 0f);
                return ToHex(r, g, b);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>该玩家是否处于纯完美（scrMarginTracker.IsAllPurePerfect）。</summary>
        public static bool GetPlayerPurePerfect(int player)
        {
            scrMarginTracker tracker = GetPlayerTracker(player);
            if (tracker == null) return false;
            return Reflect.ToBool(Reflect.Call(tracker, "IsAllPurePerfect"), false);
        }

        /// <summary>该玩家剩余的玩家命中格数（用于 XScore 潜力估计）。</summary>
        public static int GetPlayerRemaining(int player)
        {
            return CountRemainingHitFloors(GetPlayerSeq(player));
        }

        #endregion

        #region 关卡缓存

        /// <summary>
        /// 预排序玩家命中格并统计检查点格数。开局调用一次，之后逐帧只做二分查找。
        /// </summary>
        public static void RefreshLevelCache()
        {
            _levelCacheValid = false;
            _hitFloorSeqs = null;
            _hitFloorTimes = null;
            _checkpointTileCount = 0;
            _mapLength = 0.0;
            _lastCacheAttempt = Time.realtimeSinceStartup;

            try
            {
                scrLevelMaker levelMaker = LevelMaker;
                if (levelMaker == null) return;

                List<scrFloor> floors = levelMaker.listFloors;
                if (floors != null && floors.Count > 0)
                {
                    scrFloor last = floors[floors.Count - 1];
                    if (last != null) _mapLength = last.entryTime;
                }

                Type checkpointType = VersionSafe.FindType("ffxCheckpoint");
                if (checkpointType != null && floors != null)
                {
                    int count = 0;
                    for (int i = 0; i < floors.Count; i++)
                    {
                        scrFloor floor = floors[i];
                        if (floor == null || floor.plusEffects == null) continue;
                        for (int j = 0; j < floor.plusEffects.Count; j++)
                        {
                            object effect = floor.plusEffects[j];
                            if (effect != null && checkpointType.IsInstanceOfType(effect))
                            {
                                count++;
                                break;
                            }
                        }
                    }
                    _checkpointTileCount = count;
                }

                // PlayerHitFloors 是 IReadOnlyList<scrFloor>，用 IList 读以免硬引用集合类型。
                object hitFloors = Reflect.Get(levelMaker, "PlayerHitFloors");
                IList hitList = hitFloors as IList;
                if (hitList != null && hitList.Count > 0)
                {
                    int[] seqs = new int[hitList.Count];
                    double[] times = new double[hitList.Count];
                    for (int i = 0; i < hitList.Count; i++)
                    {
                        object floor = hitList[i];
                        seqs[i] = Reflect.GetInt(floor, "seqID", -1);
                        times[i] = Reflect.GetDouble(floor, "entryTime", 0.0);
                    }
                    Array.Sort(seqs, times);
                    _hitFloorSeqs = seqs;
                    _hitFloorTimes = times;
                }

                // 只有真的读到楼层才算缓存有效；否则下次访问会再试一次
                // （scnGame.Play 触发 StartGame 时楼层偶尔还没建好）。
                _levelCacheValid = floors != null && floors.Count > 0;
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("GameRefs.RefreshLevelCache", "建立关卡缓存失败: " + ex.Message);
            }
        }

        /// <summary>当前 seqID 之后还剩多少个玩家命中格（二分查找，逐帧安全）。</summary>
        public static int CountRemainingHitFloors(int currentSeq)
        {
            int[] seqs = _hitFloorSeqs;
            if (!_levelCacheValid || seqs == null || seqs.Length == 0) return 0;

            return seqs.Length - UpperBound(seqs, currentSeq);
        }

        /// <summary>
        /// 伪 BPM：用最近若干命中格的时间跨度反推「实际过格速率」。
        ///
        /// 高 BPM 谱面常用 90 度格把真实难度藏起来，节拍器 BPM 已经不能反映手感，
        /// 这个值等于「60 / 相邻命中格平均间隔(秒)」。它是派生量，与原版逐角度累加的
        /// 算法不完全一致，但用途相同（给前端一个可显示的等效 BPM）。
        /// </summary>
        public static float PseudoBpm(int currentSeq)
        {
            int[] seqs = _hitFloorSeqs;
            double[] times = _hitFloorTimes;
            if (!_levelCacheValid || seqs == null || times == null) return 0f;

            int last = UpperBound(seqs, currentSeq) - 1;
            if (last < 1) return 0f;

            int first = last - PseudoBpmWindow + 1;
            if (first < 0) first = 0;

            int steps = last - first;
            if (steps <= 0) return 0f;

            double elapsed = times[last] - times[first];
            if (!NumUtil.IsFinite(elapsed) || elapsed <= 0.0) return 0f;

            double bpm = 60.0 * steps / elapsed;
            if (!NumUtil.IsFinite(bpm) || bpm <= 0.0 || bpm > 1000000.0) return 0f;
            return (float)bpm;
        }

        /// <summary>返回第一个 &gt; value 的下标（标准上界二分）。</summary>
        private static int UpperBound(int[] sorted, int value)
        {
            int low = 0;
            int high = sorted.Length;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (sorted[mid] <= value) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        private static void EnsureLevelCache()
        {
            if (_levelCacheValid) return;

            // 关卡还没建好时不要每帧重建（建缓存要遍历全部楼层与特效）。
            double now = Time.realtimeSinceStartup;
            if (_lastCacheAttempt >= 0.0 && now - _lastCacheAttempt < 0.5) return;
            _lastCacheAttempt = now;
            RefreshLevelCache();
        }

        /// <summary>换曲/重开时让缓存失效，下一帧重新建档。</summary>
        public static void InvalidateLevelCache()
        {
            _levelCacheValid = false;
            _lastCacheAttempt = -1.0;
        }

        /// <summary>音乐时间轴是否可读（conductor.song 上必须有 AudioSource 才谈得上时间轴）。</summary>
        public static bool HasSongSource
        {
            get
            {
                try
                {
                    scrConductor conductor = Conductor;
                    return conductor != null && Reflect.Get(conductor, "song") != null;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>谱面里是否存在可识别的检查点砖块（决定 checkpointTiles 有没有意义）。</summary>
        public static bool HasCheckpointTiles
        {
            get { return VersionSafe.FindType("ffxCheckpoint") != null; }
        }

        #endregion

        #region 工具

        private static void VisitPairs(object table, Action<object, object> visit)
        {
            IEnumerable enumerable = table as IEnumerable;
            if (enumerable == null) return;

            foreach (object entry in enumerable)
            {
                if (entry == null) continue;
                object key = Reflect.Get(entry, "Key");
                if (key == null) continue;
                visit(key, Reflect.Get(entry, "Value"));
            }
        }

        private static string ToHex(float r, float g, float b)
        {
            int ri = ChannelToByte(r);
            int gi = ChannelToByte(g);
            int bi = ChannelToByte(b);
            return ri.ToString("X2", CultureInfo.InvariantCulture)
                 + gi.ToString("X2", CultureInfo.InvariantCulture)
                 + bi.ToString("X2", CultureInfo.InvariantCulture);
        }

        private static int ChannelToByte(float value)
        {
            if (!NumUtil.IsFinite(value)) return 0;
            float scaled = value <= 1f ? value * 255f : value;
            int rounded = (int)Math.Round(scaled);
            return NumUtil.ClampInt(rounded, 0, 255);
        }

        #endregion
    }
}
