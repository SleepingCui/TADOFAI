using TADOFAI.Mod.Protocol;

namespace TADOFAI.Mod
{
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
        public bool Auto;
        public bool Practice;
        public bool NoFail;
        public string SongName = "";
        public string SongAuthor = "";
        public int Difficulty;
        public double StartedAt;
        public readonly int[] PlayerSeq = new int[GameSnapshot.MaxPlayers];

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

            snapshot.Play.Seq = Seq;
            snapshot.Play.Progress = Progress;
            snapshot.Play.Combo = Combo;
            snapshot.Play.MaxCombo = MaxCombo;
            snapshot.Play.Accuracy = Accuracy;
            snapshot.Play.XAccuracy = XAccuracy;
            snapshot.Play.Bpm = Bpm;
            snapshot.Play.TimingMs = TimingMs;
            snapshot.Play.Misses = Misses;

            for (int i = 0; i < PlayerSeq.Length; i++) snapshot.PlayerSeq[i] = PlayerSeq[i];

            return snapshot;
        }

        public void ResetForNewGame()
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
            SongName = string.Empty;
            SongAuthor = string.Empty;
            Difficulty = 0;

            for (int i = 0; i < PlayerSeq.Length; i++) PlayerSeq[i] = 0;
        }
    }
}
