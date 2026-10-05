using System;

namespace TADOFAI.Mod.Protocol
{
    /// <summary>状态流里的 play 块。字段名与上报键一一对应（见 StateMessage.WriteData）。</summary>
    public sealed class PlayState
    {
        public int Seq;
        public float Progress;
        public int Combo;
        public int MaxCombo;

        /// <summary>
        /// Combo 是否仍是「完美连击」：本局到目前为止只出现过中心完美判定。
        /// 一旦出现非完美判定即永久为 false（下一局恢复）。前端据此在「完美 / 连击」标题间切换。
        /// </summary>
        public bool PerfectCombo = true;

        public float Accuracy;
        public float XAccuracy;
        public float Bpm;
        public float TimingMs;
        public int Misses;

        // ---- 以下为「网页端复刻 overlay」新增 ----

        /// <summary>谱面标称 BPM（LevelData.bpm）。</summary>
        public float TileBpm;

        /// <summary>当前节拍器 BPM（变速谱面下会与 TileBpm 不同）。</summary>
        public float CurrentBpm;

        /// <summary>最近 1 秒内 Mod 观察到的命中次数。</summary>
        public float Kps;

        /// <summary>按最近若干命中格时间跨度折算的等效 BPM。</summary>
        public float PseudoBpm;

        /// <summary>已判定格数（各判定计数之和）。</summary>
        public int Judged;

        /// <summary>剩余玩家命中格数。</summary>
        public int Remaining;

        /// <summary>X 分数（来自 scrMarginTracker.xScore）。</summary>
        public int XScore;

        /// <summary>本谱面 X 分数上限。</summary>
        public int MaxXScore;

        /// <summary>按当前已判定 + 剩余格推算的 X 分数潜力。</summary>
        public int XScorePotential;

        /// <summary>本局死亡数。</summary>
        public int Deaths;

        /// <summary>本局已用检查点数。</summary>
        public int Checkpoints;

        /// <summary>是否保持纯完美。</summary>
        public bool PurePerfect;

        /// <summary>本局起始格（练习/检查点开局时非 0）。</summary>
        public int StartSeq;

        /// <summary>本局起始进度百分比。</summary>
        public float StartProgress;

        /// <summary>玩家贴合度缩放。判定窗与「本地记录键」都要用到。</summary>
        public float MarginScale;
    }

    /// <summary>状态流里的 map 块（关卡元信息 + 本地记录要用的键）。</summary>
    public sealed class MapState
    {
        public string Id = "";
        public string SongName = "";
        public string Artist = "";
        public string SongAuthor = "";
        public string Author = "";
        public int Difficulty;
        public string DifficultyName = "";
        public int FloorCount;
        public float Bpm;
        public float Pitch = 1f;

        /// <summary>谱面总时长（最后一格 entryTime，秒）。</summary>
        public double Duration;

        /// <summary>谱面里预先摆了几个检查点砖。</summary>
        public int CheckpointTiles;
    }

    /// <summary>状态流里的 timeline 块：三套时间轴。</summary>
    public sealed class TimelineState
    {
        /// <summary>歌曲已播放秒数（AudioSource.time）。</summary>
        public double MusicTime;

        /// <summary>歌曲总长度。0 表示未知。</summary>
        public double MusicLength;

        /// <summary>地图时间轴位置（songposition_minusi + addoffset）。</summary>
        public double MapTime;

        /// <summary>谱面总时长（最后一格 entryTime）。</summary>
        public double MapLength;
    }

    /// <summary>状态流里的 timingWindows 块，单位毫秒。全部为 0 表示无法换算。</summary>
    public sealed class TimingWindowsState
    {
        public double Counted;
        public double Perfect;
        public double Pure;
        public double XPerfect;

        /// <summary>换算分母（ms = 度 * 1000 / denom），前端可据此自行复核。</summary>
        public double Denom;

        /// <summary>角度边界（度）：Counted / Perfect / Pure / XPerfect。</summary>
        public double[] Degrees = new double[4];
    }

    /// <summary>Coop 时的单个玩家状态。</summary>
    public sealed class PlayerState
    {
        public int Player;
        public int Seq;
        public float Progress;
        public int Combo;
        public int MaxCombo;
        public float Accuracy;
        public float XAccuracy;
        public int XScore;
        public int MaxXScore;
        public int Deaths;
        public int Judged;
        public int Remaining;
        public bool PurePerfect;
        public bool PerfectCombo = true;
        public bool Auto;
        public string Color = "";
        public float MarginScale = 1f;

        /// <summary>判定直方图，下标 = HitMargin 枚举值（名字见 hello.hitMarginNames）。</summary>
        public int[] HitMargins = new int[0];
    }

    /// <summary>状态快照。只保存最新值，由 Transport 按 Settings.StateRate 合并发送。</summary>
    public sealed class GameSnapshot
    {
        public const int MaxPlayers = 8;

        public bool Connected;
        public string GameState = "idle";
        public string SongName = "";
        public string SongAuthor = "";
        public int Difficulty;
        public bool Auto;
        public bool Practice;
        public bool NoFail;
        public long DroppedEvents;

        // ---- 以下为「网页端复刻 overlay」新增 ----

        public bool Paused;
        public float Fps;
        public int PlayerCount = 1;

        public MapState Map = new MapState();
        public TimelineState Timeline = new TimelineState();
        public TimingWindowsState Timing = new TimingWindowsState();
        public PlayState Play = new PlayState();

        /// <summary>逐玩家状态；长度 = PlayerCount。</summary>
        public PlayerState[] Players = new PlayerState[0];

        /// <summary>Coop 时每个玩家的当前 seqID。保留给老插件，不保证与 Players 同步更新。</summary>
        public readonly int[] PlayerSeq = new int[MaxPlayers];
    }
}
