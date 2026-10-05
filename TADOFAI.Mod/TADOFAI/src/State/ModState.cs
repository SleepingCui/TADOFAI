using TADOFAI.Mod.Protocol;

namespace TADOFAI.Mod
{
    /// <summary>单个玩家的可变状态（Collector 写、ToSnapshot 拷）。</summary>
    internal sealed class PlayerTrack
    {
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

        /// <summary>
        /// 本局到目前为止是否一直是「完美连击」（只出现过中心完美判定）。
        /// 一旦出现非完美判定就永久为 false，直到下一局；对应 JipperOverlayer 的
        /// ComboTitle 在「完美 / 连击」之间切换的那个粘性开关。
        /// </summary>
        public bool PerfectCombo = true;

        public bool Auto;
        public string Color = "";
        public float MarginScale = 1f;

        /// <summary>判定直方图，下标 = HitMargin 枚举值。</summary>
        public int[] HitMargins = new int[0];

        public PlayerState ToState()
        {
            PlayerState state = new PlayerState();
            state.Player = -1;
            state.Seq = Seq;
            state.Progress = Progress;
            state.Combo = Combo;
            state.MaxCombo = MaxCombo;
            state.Accuracy = Accuracy;
            state.XAccuracy = XAccuracy;
            state.XScore = XScore;
            state.MaxXScore = MaxXScore;
            state.Deaths = Deaths;
            state.Judged = Judged;
            state.Remaining = Remaining;
            state.PurePerfect = PurePerfect;
            state.PerfectCombo = PerfectCombo;
            state.Auto = Auto;
            state.Color = Color ?? string.Empty;
            state.MarginScale = MarginScale;

            int length = HitMargins == null ? 0 : HitMargins.Length;
            int[] copy = new int[length];
            for (int i = 0; i < length; i++) copy[i] = HitMargins[i];
            state.HitMargins = copy;
            return state;
        }

        public void Reset(int hitMarginSlots)
        {
            Seq = 0;
            Progress = 0f;
            Combo = 0;
            MaxCombo = 0;
            Accuracy = 0f;
            XAccuracy = 0f;
            XScore = 0;
            MaxXScore = 0;
            Deaths = 0;
            Judged = 0;
            Remaining = 0;
            PurePerfect = false;
            PerfectCombo = true;
            Auto = false;
            Color = string.Empty;
            MarginScale = 1f;

            if (HitMargins == null || HitMargins.Length != hitMarginSlots)
                HitMargins = new int[hitMarginSlots];
            else
                for (int i = 0; i < HitMargins.Length; i++) HitMargins[i] = 0;
        }
    }

    /// <summary>
    /// Mod 内的可变状态。所有读写都在 Gate 锁内完成，跨线程读取只通过 ToSnapshot 拷贝。
    /// </summary>
    internal sealed class ModState
    {
        public bool InGame;
        public string GameState = "idle";
        public int Seq;
        public float Progress;
        public int Combo;
        public int MaxCombo;
        public float Accuracy;
        public float XAccuracy;
        public float Bpm;
        public float TimingMs;
        public int Misses;

        /// <summary>本局是否一直是完美连击（只出现过中心完美判定）。</summary>
        public bool PerfectCombo = true;

        public bool Auto;
        public bool Practice;
        public bool NoFail;
        public string SongName = "";
        public string SongAuthor = "";
        public int Difficulty;
        public double StartedAt;
        public readonly int[] PlayerSeq = new int[GameSnapshot.MaxPlayers];

        // ---- 关卡元信息（本地记录键 / 时间轴相关） ----
        public string MapId = "";
        public string MapArtist = "";
        public string MapAuthor = "";
        public string DifficultyName = "";
        public int FloorCount;
        public float MapBpm;
        public float SongPitch = 1f;

        /// <summary>谱面里预先摆了几个检查点砖（与「本局用掉几个」不同）。</summary>
        public int CheckpointTileCount;

        // ---- 时间轴 ----
        public double MusicTime;
        public double MusicLength;
        public double MapTime;
        public double MapLength;

        // ---- 会话 ----
        public bool Paused;
        public float Fps;
        public int Checkpoints;
        public float Kps;

        // ---- 派生 ----
        public float PseudoBpm;
        public float MarginScale = 1f;
        public int StartSeq;
        public float StartProgress;

        // ---- 判定窗 ----
        public double TimingCounted;
        public double TimingPerfect;
        public double TimingPure;
        public double TimingXPerfect;
        public double TimingDenom;
        public double[] TimingDegrees = new double[4];

        /// <summary>逐玩家状态。长度固定为 GameSnapshot.MaxPlayers，快照里按 PlayerCount 截取。</summary>
        public readonly PlayerTrack[] Players;
        public int PlayerCount = 1;

        public ModState()
        {
            Players = new PlayerTrack[GameSnapshot.MaxPlayers];
            for (int i = 0; i < Players.Length; i++) Players[i] = new PlayerTrack();
        }

        public GameSnapshot ToSnapshot()
        {
            GameSnapshot snapshot = new GameSnapshot();

            snapshot.GameState = string.IsNullOrEmpty(GameState) ? "idle" : GameState;
            snapshot.SongName = SongName ?? string.Empty;
            snapshot.SongAuthor = SongAuthor ?? string.Empty;
            snapshot.Difficulty = Difficulty;
            snapshot.Auto = Auto;
            snapshot.Practice = Practice;
            snapshot.NoFail = NoFail;
            snapshot.Paused = Paused;
            snapshot.Fps = Fps;
            snapshot.PlayerCount = PlayerCount;

            snapshot.Map.Id = MapId ?? string.Empty;
            snapshot.Map.SongName = SongName ?? string.Empty;
            snapshot.Map.Artist = MapArtist ?? string.Empty;
            snapshot.Map.SongAuthor = SongAuthor ?? string.Empty;
            snapshot.Map.Author = MapAuthor ?? string.Empty;
            snapshot.Map.Difficulty = Difficulty;
            snapshot.Map.DifficultyName = DifficultyName ?? string.Empty;
            snapshot.Map.FloorCount = FloorCount;
            snapshot.Map.Bpm = MapBpm;
            snapshot.Map.Pitch = SongPitch;
            snapshot.Map.Duration = MapLength;
            snapshot.Map.CheckpointTiles = CheckpointTileCount;

            snapshot.Timeline.MusicTime = MusicTime;
            snapshot.Timeline.MusicLength = MusicLength;
            snapshot.Timeline.MapTime = MapTime;
            snapshot.Timeline.MapLength = MapLength;

            snapshot.Timing.Counted = TimingCounted;
            snapshot.Timing.Perfect = TimingPerfect;
            snapshot.Timing.Pure = TimingPure;
            snapshot.Timing.XPerfect = TimingXPerfect;
            snapshot.Timing.Denom = TimingDenom;
            snapshot.Timing.Degrees = TimingDegrees == null ? new double[4] : (double[])TimingDegrees.Clone();

            PlayState play = snapshot.Play;
            play.Seq = Seq;
            play.Progress = Progress;
            play.Combo = Combo;
            play.MaxCombo = MaxCombo;
            play.Accuracy = Accuracy;
            play.XAccuracy = XAccuracy;
            play.Bpm = Bpm;
            play.TimingMs = TimingMs;
            play.Misses = Misses;
            play.PerfectCombo = PerfectCombo;
            play.CurrentBpm = Bpm;
            play.TileBpm = MapBpm;
            play.Kps = Kps;
            play.PseudoBpm = PseudoBpm;
            play.StartSeq = StartSeq;
            play.StartProgress = StartProgress;
            play.MarginScale = MarginScale;
            play.Checkpoints = Checkpoints;

            int count = NumUtil.ClampInt(PlayerCount, 1, GameSnapshot.MaxPlayers);
            PlayerState[] players = new PlayerState[count];
            int judged = 0;
            int remaining = 0;
            for (int i = 0; i < count; i++)
            {
                PlayerTrack track = Players[i];
                PlayerState state = track.ToState();
                state.Player = i;
                players[i] = state;
                judged += track.Judged;
                remaining += track.Remaining;
            }
            snapshot.Players = players;

            play.Judged = judged;
            play.Remaining = remaining;
            play.XScore = Players[0].XScore;
            play.MaxXScore = Players[0].MaxXScore;
            play.XScorePotential = Players[0].XScore + RemainingXScore(remaining);
            play.Deaths = Players[0].Deaths;
            play.PurePerfect = Players[0].PurePerfect;

            for (int i = 0; i < PlayerSeq.Length; i++) snapshot.PlayerSeq[i] = PlayerSeq[i];

            return snapshot;
        }

        /// <summary>
        /// X 分数潜力：每个完美格 +2。这里按剩余格全部完美外推上限，
        /// 精确值由前端按 hello.hitMarginXScores 自行重算。
        /// </summary>
        private static int RemainingXScore(int remaining)
        {
            if (remaining <= 0) return 0;
            return remaining * 2;
        }

        public void ResetForNewGame(int hitMarginSlots)
        {
            InGame = true;
            GameState = "playing";
            Seq = 0;
            Progress = 0f;
            Combo = 0;
            MaxCombo = 0;
            Accuracy = 0f;
            XAccuracy = 0f;
            Bpm = 0f;
            TimingMs = 0f;
            Misses = 0;
            PerfectCombo = true;
            Auto = false;
            Practice = false;
            NoFail = false;
            SongName = string.Empty;
            SongAuthor = string.Empty;
            Difficulty = 0;

            MapId = string.Empty;
            MapArtist = string.Empty;
            MapAuthor = string.Empty;
            DifficultyName = string.Empty;
            FloorCount = 0;
            MapBpm = 0f;
            SongPitch = 1f;
            CheckpointTileCount = 0;

            MusicTime = 0.0;
            MusicLength = 0.0;
            MapTime = 0.0;
            MapLength = 0.0;

            Paused = false;
            Fps = 0f;
            Checkpoints = 0;
            Kps = 0f;

            PseudoBpm = 0f;
            MarginScale = 1f;
            StartSeq = 0;
            StartProgress = 0f;

            TimingCounted = 0.0;
            TimingPerfect = 0.0;
            TimingPure = 0.0;
            TimingXPerfect = 0.0;
            TimingDenom = 0.0;
            TimingDegrees = new double[4];

            PlayerCount = 1;
            for (int i = 0; i < Players.Length; i++) Players[i].Reset(hitMarginSlots);
            for (int i = 0; i < PlayerSeq.Length; i++) PlayerSeq[i] = 0;
        }
    }
}
