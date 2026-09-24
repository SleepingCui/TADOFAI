using System;

namespace TADOFAI.Mod.Protocol
{
    /// <summary>状态流里的 play 块。</summary>
    public sealed class PlayState
    {
        public int Seq;
        public float Progress;
        public int Combo;
        public int MaxCombo;
        public float Accuracy;
        public float XAccuracy;
        public float Bpm;
        public float TimingMs;
        public int Misses;
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

        public PlayState Play = new PlayState();

        /// <summary>Coop 时每个玩家的当前 seqID。</summary>
        public readonly int[] PlayerSeq = new int[MaxPlayers];
    }
}
