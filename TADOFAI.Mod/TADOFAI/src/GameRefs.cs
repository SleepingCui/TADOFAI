using System;
using System.Reflection;
using UnityEngine;

namespace TADOFAI.Mod
{
    public struct MapMeta
    {
        public string SongName;
        public string SongAuthor;
        public int Difficulty;
    }

    /// <summary>
    /// 集中读取游戏对象。所有访问都做空引用保护，绑定失败只记录告警，不让 Mod 崩溃。
    /// </summary>
    public static class GameRefs
    {
        public static scrController Controller
        {
            get { return scrController.instance; }
        }

        public static scrConductor Conductor
        {
            get { return scrConductor.instance; }
        }

        public static scrLevelMaker LevelMaker
        {
            get { return scrLevelMaker.instance; }
        }

        /// <summary>r340 没有 scrMistakesManager.instance，要从 controller 上取。</summary>
        public static scrMistakesManager Mistakes
        {
            get
            {
                scrController controller = Controller;
                return controller == null ? null : controller.mistakesManager;
            }
        }

        public static scrPlayerManager Players
        {
            get { return scrPlayerManager.instance; }
        }

        public static int CurrentSeqID
        {
            get
            {
                scrController controller = Controller;
                return controller == null ? 0 : controller.currentSeqID;
            }
        }

        public static scrFloor CurrentFloor
        {
            get
            {
                scrController controller = Controller;
                return controller == null ? null : controller.currFloor;
            }
        }

        public static float PercentComplete
        {
            get
            {
                scrController controller = Controller;
                return controller == null ? 0f : controller.percentComplete;
            }
        }

        public static bool IsAuto
        {
            get
            {
                try { return RDC.auto; }
                catch { return false; }
            }
        }

        public static bool IsPracticeMode
        {
            get
            {
                try { return GCS.practiceMode; }
                catch { return false; }
            }
        }

        public static bool IsNoFail
        {
            get
            {
                try
                {
                    scrController controller = Controller;
                    return controller != null && controller.noFail;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>谱面 BPM（r340 的 scrConductor.bpm 是 float）。</summary>
        public static double Bpm
        {
            get
            {
                scrConductor conductor = Conductor;
                return conductor == null ? 0.0 : conductor.bpm;
            }
        }

        /// <summary>歌曲音高（AudioSource.pitch）。</summary>
        public static float SongPitch
        {
            get
            {
                try
                {
                    scrConductor conductor = Conductor;
                    if (conductor == null || conductor.song == null) return 1f;

                    float pitch = conductor.song.pitch;
                    if (!NumUtil.IsFinite(pitch) || pitch <= 0f) return 1f;
                    return pitch;
                }
                catch
                {
                    return 1f;
                }
            }
        }

        public static int FloorCount
        {
            get
            {
                try
                {
                    scrLevelMaker levelMaker = LevelMaker;
                    if (levelMaker == null || levelMaker.listFloors == null) return 0;
                    return levelMaker.listFloors.Count;
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>玩家数量（单人 1，Coop 大于 1）。</summary>
        public static int PlayerCount
        {
            get
            {
                try
                {
                    scrPlayerManager manager = Players;
                    if (manager == null || manager.allPlayers == null) return 1;
                    return manager.allPlayers.Length;
                }
                catch
                {
                    return 1;
                }
            }
        }

        /// <summary>某个玩家的判定追踪器（准确率来源）。</summary>
        public static scrMarginTracker GetPlayerTracker(int player)
        {
            if (player < 0) return null;

            try
            {
                scrPlayerManager manager = Players;
                if (manager == null || manager.allPlayers == null) return null;
                if (player >= manager.allPlayers.Length) return null;

                scrPlayer target = manager.allPlayers[player];
                return target == null ? null : target.marginTracker;
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("GameRefs.GetPlayerTracker", "读取判定追踪器失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 全量判定追踪器数组。r340 里 marginTrackers 是静态字段，下标即玩家序号。
        /// </summary>
        public static scrMarginTracker[] MarginTrackers
        {
            get
            {
                try
                {
                    return scrMistakesManager.marginTrackers;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 绑定游戏对象。进入游戏前 instance 可能为空，这里只做一次能力自检并记录告警。
        /// </summary>
        public static void Bind()
        {
            try
            {
                if (scrController.instance == null)
                    ModLog.Info("GameRefs: scrController.instance 尚未就绪（进入游戏后自动可用）");

                if (scrConductor.instance == null)
                    ModLog.Info("GameRefs: scrConductor.instance 尚未就绪");

                if (scrLevelMaker.instance == null)
                    ModLog.Info("GameRefs: scrLevelMaker.instance 尚未就绪");
            }
            catch (Exception ex)
            {
                ModLog.Warn("GameRefs 绑定失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 读取地图信息。不同版本字段名不一致，这里按候选名依次尝试，读不到就留空。
        /// </summary>
        public static MapMeta ReadMapMeta()
        {
            MapMeta meta = new MapMeta();

            try
            {
                scrConductor conductor = Conductor;
                object song = conductor != null ? (object)conductor.song : null;

                meta.SongName = ReadFirstString(song, "songName", "levelName", "songTitle", "name");
                meta.SongAuthor = ReadFirstString(song, "author", "artist", "songAuthor");
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("GameRefs.ReadMapMeta", "读取地图信息失败: " + ex.Message);
            }

            if (string.IsNullOrEmpty(meta.SongName))
                meta.SongName = ReadFirstStaticString("GCNS.songName", "ADOBase.songName", "scnGame.songName");

            if (string.IsNullOrEmpty(meta.SongAuthor))
                meta.SongAuthor = ReadFirstStaticString("GCNS.songAuthor", "ADOBase.songAuthor", "scnGame.songAuthor");

            meta.Difficulty = ReadFirstStaticInt("GCNS.difficulty", "ADOBase.difficulty", "scnGame.difficulty");

            if (meta.SongName == null) meta.SongName = string.Empty;
            if (meta.SongAuthor == null) meta.SongAuthor = string.Empty;
            return meta;
        }

        /// <summary>
        /// 读取指定玩家的当前 Floor seqID；用于 Coop。
        /// 不能假设 MoveToNextFloor 的参数代表当前玩家，必须按 playerID 区分。
        /// </summary>
        public static int GetPlayerSeq(int player)
        {
            try
            {
                scrPlayerManager manager = Players;
                if (manager == null || manager.allPlayers == null) return 0;
                if (player < 0 || player >= manager.allPlayers.Length) return 0;

                scrPlayer target = manager.allPlayers[player];
                if (target == null) return 0;

                scrFloor floor = target.currFloor;
                if (floor != null) return floor.seqID;

                if (target.planetarySystem != null &&
                    target.planetarySystem.chosenPlanet != null &&
                    target.planetarySystem.chosenPlanet.currfloor != null)
                {
                    return target.planetarySystem.chosenPlanet.currfloor.seqID;
                }

                return 0;
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("GameRefs.GetPlayerSeq", "读取 Coop 进度失败: " + ex.Message);
                return 0;
            }
        }

        private static string ReadFirstString(object target, params string[] memberNames)
        {
            if (target == null || memberNames == null) return null;

            Type type = target.GetType();
            for (int i = 0; i < memberNames.Length; i++)
            {
                try
                {
                    FieldInfo field = type.GetField(memberNames[i],
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null && field.FieldType == typeof(string))
                    {
                        string value = field.GetValue(target) as string;
                        if (!string.IsNullOrEmpty(value)) return value;
                    }

                    PropertyInfo property = type.GetProperty(memberNames[i],
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (property != null && property.PropertyType == typeof(string) && property.CanRead)
                    {
                        string value = property.GetValue(target, null) as string;
                        if (!string.IsNullOrEmpty(value)) return value;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static string ReadFirstStaticString(params string[] typeDotMembers)
        {
            if (typeDotMembers == null) return null;

            for (int i = 0; i < typeDotMembers.Length; i++)
            {
                string value = ReadStaticMember(typeDotMembers[i]) as string;
                if (!string.IsNullOrEmpty(value)) return value;
            }

            return null;
        }

        private static int ReadFirstStaticInt(params string[] typeDotMembers)
        {
            if (typeDotMembers == null) return 0;

            for (int i = 0; i < typeDotMembers.Length; i++)
            {
                object value = ReadStaticMember(typeDotMembers[i]);
                if (value is int) return (int)value;
            }

            return 0;
        }

        private static object ReadStaticMember(string typeDotMember)
        {
            if (string.IsNullOrEmpty(typeDotMember)) return null;

            int separator = typeDotMember.IndexOf('.');
            if (separator <= 0) return null;

            Type type = VersionSafe.FindType(typeDotMember.Substring(0, separator));
            if (type == null) return null;

            string memberName = typeDotMember.Substring(separator + 1);

            try
            {
                FieldInfo field = type.GetField(memberName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field != null) return field.GetValue(null);

                PropertyInfo property = type.GetProperty(memberName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property != null && property.CanRead) return property.GetValue(null, null);
            }
            catch
            {
            }

            return null;
        }
    }
}
