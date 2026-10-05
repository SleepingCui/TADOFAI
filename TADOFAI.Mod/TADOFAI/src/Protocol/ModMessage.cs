using System;

namespace TADOFAI.Mod.Protocol
{
    /// <summary>协议事件名。</summary>
    internal static class GameEventNames
    {
        public const string GameStart = "game.start";
        public const string GameEnd = "game.end";
        public const string MapChanged = "map.changed";
        public const string StateChanged = "state.changed";
        public const string Death = "death";
        public const string Checkpoint = "checkpoint";
    }

    /// <summary>
    /// 协议信封：{ type, version, id, timestamp, data }。
    /// id 用于检测丢失或重复事件；timestamp 是 Mod 采集时刻（单调秒），Core 不使用网络到达时间。
    /// </summary>
    public abstract class ModMessage
    {
        public const int ProtocolVersion = 1;

        public abstract string Type { get; }

        public long Id;
        public double Timestamp;

        public string ToJson()
        {
            JsonWriter writer = new JsonWriter();
            writer.BeginObject();
            writer.Name("type").Value(Type);
            writer.Name("version").Value(ProtocolVersion);
            if (Id > 0) writer.Name("id").Value(Id);
            writer.Name("timestamp").Value(Timestamp);
            writer.Name("data");
            WriteData(writer);
            writer.EndObject();
            return writer.ToString();
        }

        protected abstract void WriteData(JsonWriter writer);
    }

    /// <summary>
    /// 连接后第一帧：声明客户端、版本、能力与判定表。
    /// 判定表放在这里而不是每帧状态里：判定名/权重/分值一局之内不变，前端只需在连接时读一次，
    /// 就能把 state.players[].hitMargins（下标数组）和 hit.judgement 翻译成显示文本与颜色。
    /// </summary>
    public sealed class HelloMessage : ModMessage
    {
        public string ModVersion = "";
        public string GameVersion = "";
        public string[] Capabilities = new string[0];

        /// <summary>下标 = HitMargin 枚举值。</summary>
        public string[] HitMarginNames = new string[0];

        /// <summary>下标 = HitMargin 枚举值；精度权重。</summary>
        public double[] HitMarginWeights = new double[0];

        /// <summary>下标 = HitMargin 枚举值；XScore 权重（XPerfect=2，±Perfect=1，其余 0）。</summary>
        public int[] HitMarginXScores = new int[0];

        /// <summary>难度名（Lenient / Normal / Strict）。</summary>
        public string Difficulty = "";

        public override string Type
        {
            get { return "hello"; }
        }

        protected override void WriteData(JsonWriter writer)
        {
            writer.BeginObject();
            writer.Name("client").Value("tadofai-mod");
            writer.Name("modVersion").Value(ModVersion);
            writer.Name("gameVersion").Value(GameVersion);
            writer.Name("protocolVersion").Value(ProtocolVersion);
            writer.Name("difficulty").Value(Difficulty ?? "");

            writer.Name("capabilities").BeginArray();
            for (int i = 0; i < Capabilities.Length; i++) writer.Value(Capabilities[i]);
            writer.EndArray();

            writer.Name("hitMarginNames").BeginArray();
            for (int i = 0; i < HitMarginNames.Length; i++) writer.Value(HitMarginNames[i] ?? "");
            writer.EndArray();

            writer.Name("hitMarginWeights").BeginArray();
            for (int i = 0; i < HitMarginWeights.Length; i++) writer.Value(HitMarginWeights[i]);
            writer.EndArray();

            writer.Name("hitMarginXScores").BeginArray();
            for (int i = 0; i < HitMarginXScores.Length; i++) writer.Value(HitMarginXScores[i]);
            writer.EndArray();

            writer.EndObject();
        }
    }

    /// <summary>状态流：只保存最新值，覆盖式发送。</summary>
    public sealed class StateMessage : ModMessage
    {
        public GameSnapshot Snapshot;

        public override string Type
        {
            get { return "state"; }
        }

        protected override void WriteData(JsonWriter writer)
        {
            GameSnapshot snapshot = Snapshot;
            if (snapshot == null)
            {
                writer.Null();
                return;
            }

            writer.BeginObject();
            writer.Name("connected").Value(snapshot.Connected);
            writer.Name("gameState").Value(snapshot.GameState);
            writer.Name("droppedEvents").Value(snapshot.DroppedEvents);
            writer.Name("auto").Value(snapshot.Auto);
            writer.Name("practice").Value(snapshot.Practice);
            writer.Name("noFail").Value(snapshot.NoFail);
            writer.Name("paused").Value(snapshot.Paused);
            writer.Name("fps").Value(snapshot.Fps);
            writer.Name("playerCount").Value(snapshot.PlayerCount);

            MapState map = snapshot.Map ?? new MapState();
            writer.Name("map").BeginObject();
            writer.Name("id").Value(map.Id ?? "");
            writer.Name("songName").Value(map.SongName ?? "");
            writer.Name("artist").Value(map.Artist ?? "");
            writer.Name("songAuthor").Value(map.SongAuthor ?? "");
            writer.Name("author").Value(map.Author ?? "");
            writer.Name("difficulty").Value(map.Difficulty);
            writer.Name("difficultyName").Value(map.DifficultyName ?? "");
            writer.Name("floorCount").Value(map.FloorCount);
            writer.Name("bpm").Value(map.Bpm);
            writer.Name("pitch").Value(map.Pitch);
            writer.Name("duration").Value(map.Duration);
            writer.Name("checkpointTiles").Value(map.CheckpointTiles);
            writer.EndObject();

            TimelineState timeline = snapshot.Timeline ?? new TimelineState();
            writer.Name("timeline").BeginObject();
            writer.Name("musicTime").Value(timeline.MusicTime);
            writer.Name("musicLength").Value(timeline.MusicLength);
            writer.Name("mapTime").Value(timeline.MapTime);
            writer.Name("mapLength").Value(timeline.MapLength);
            writer.EndObject();

            TimingWindowsState timing = snapshot.Timing;
            writer.Name("timingWindows");
            if (timing == null || timing.Denom <= 0.0)
            {
                // 关卡还没算出速度/倍率时宁可发 null，也不要发一组假的判定窗。
                writer.Null();
            }
            else
            {
                writer.BeginObject();
                writer.Name("counted").Value(timing.Counted);
                writer.Name("perfect").Value(timing.Perfect);
                writer.Name("pure").Value(timing.Pure);
                writer.Name("xPerfect").Value(timing.XPerfect);
                writer.Name("denom").Value(timing.Denom);
                writer.Name("degrees").BeginArray();
                double[] degrees = timing.Degrees ?? new double[0];
                for (int i = 0; i < degrees.Length; i++) writer.Value(degrees[i]);
                writer.EndArray();
                writer.EndObject();
            }

            PlayState play = snapshot.Play ?? new PlayState();
            writer.Name("play").BeginObject();
            writer.Name("seq").Value(play.Seq);
            writer.Name("progress").Value(play.Progress);
            writer.Name("combo").Value(play.Combo);
            writer.Name("maxCombo").Value(play.MaxCombo);
            writer.Name("perfectCombo").Value(play.PerfectCombo);
            writer.Name("accuracy").Value(play.Accuracy);
            writer.Name("xAccuracy").Value(play.XAccuracy);
            writer.Name("bpm").Value(play.Bpm);
            writer.Name("tileBpm").Value(play.TileBpm);
            writer.Name("currentBpm").Value(play.CurrentBpm);
            writer.Name("pseudoBpm").Value(play.PseudoBpm);
            writer.Name("kps").Value(play.Kps);
            writer.Name("timingMs").Value(play.TimingMs);
            writer.Name("misses").Value(play.Misses);
            writer.Name("judged").Value(play.Judged);
            writer.Name("remaining").Value(play.Remaining);
            writer.Name("xScore").Value(play.XScore);
            writer.Name("maxXScore").Value(play.MaxXScore);
            writer.Name("xScorePotential").Value(play.XScorePotential);
            writer.Name("deaths").Value(play.Deaths);
            writer.Name("checkpoints").Value(play.Checkpoints);
            writer.Name("purePerfect").Value(play.PurePerfect);
            writer.Name("startSeq").Value(play.StartSeq);
            writer.Name("startProgress").Value(play.StartProgress);
            writer.Name("marginScale").Value(play.MarginScale);
            writer.EndObject();

            PlayerState[] players = snapshot.Players;
            writer.Name("players").BeginArray();
            if (players != null)
            {
                for (int i = 0; i < players.Length; i++)
                {
                    PlayerState p = players[i] ?? new PlayerState();
                    writer.BeginObject();
                    writer.Name("player").Value(p.Player);
                    writer.Name("seq").Value(p.Seq);
                    writer.Name("progress").Value(p.Progress);
                    writer.Name("combo").Value(p.Combo);
                    writer.Name("maxCombo").Value(p.MaxCombo);
                    writer.Name("perfectCombo").Value(p.PerfectCombo);
                    writer.Name("accuracy").Value(p.Accuracy);
                    writer.Name("xAccuracy").Value(p.XAccuracy);
                    writer.Name("xScore").Value(p.XScore);
                    writer.Name("maxXScore").Value(p.MaxXScore);
                    writer.Name("deaths").Value(p.Deaths);
                    writer.Name("judged").Value(p.Judged);
                    writer.Name("remaining").Value(p.Remaining);
                    writer.Name("purePerfect").Value(p.PurePerfect);
                    writer.Name("auto").Value(p.Auto);
                    writer.Name("color").Value(p.Color ?? "");
                    writer.Name("marginScale").Value(p.MarginScale);
                    writer.Name("hitMargins").BeginArray();
                    int[] counts = p.HitMargins ?? new int[0];
                    for (int k = 0; k < counts.Length; k++) writer.Value(counts[k]);
                    writer.EndArray();
                    writer.EndObject();
                }
            }
            writer.EndArray();

            // 兼容旧前端：主玩家序号的扁平数组仍然发送。
            writer.Name("playerSeq").BeginArray();
            for (int i = 0; i < snapshot.PlayerSeq.Length; i++) writer.Value(snapshot.PlayerSeq[i]);
            writer.EndArray();

            writer.EndObject();
        }
    }

    /// <summary>事件流：逐条发送，低延迟优先。</summary>
    public sealed class HitMessage : ModMessage
    {
        public int Player;
        public int Seq;
        public string Judgement = "";
        public float TimingMs;
        public int Combo;
        public bool Miss;

        /// <summary>本次命中是否为中心完美判定（决定 Combo 是否继续增长）。</summary>
        public bool Perfect;

        /// <summary>本次命中后 Combo 是否仍是完美连击。</summary>
        public bool PerfectCombo = true;

        public int XScore;
        public int Judged;
        public bool PurePerfect;

        public override string Type
        {
            get { return "hit"; }
        }

        protected override void WriteData(JsonWriter writer)
        {
            writer.BeginObject();
            writer.Name("player").Value(Player);
            writer.Name("seq").Value(Seq);
            writer.Name("judgement").Value(Judgement);
            writer.Name("timingMs").Value(TimingMs);
            writer.Name("combo").Value(Combo);
            writer.Name("miss").Value(Miss);
            writer.Name("perfect").Value(Perfect);
            writer.Name("perfectCombo").Value(PerfectCombo);
            writer.Name("xScore").Value(XScore);
            writer.Name("judged").Value(Judged);
            writer.Name("purePerfect").Value(PurePerfect);
            writer.EndObject();
        }
    }

    /// <summary>通用游戏事件（game.start / game.end / death / checkpoint / state.changed ...）。</summary>
    public sealed class GameEventMessage : ModMessage
    {
        private readonly string _type;

        public int Seq;
        public int Combo;
        public string Detail = "";

        public GameEventMessage(string type, int seq, int combo)
        {
            _type = type;
            Seq = seq;
            Combo = combo;
        }

        public override string Type
        {
            get { return _type; }
        }

        protected override void WriteData(JsonWriter writer)
        {
            writer.BeginObject();
            writer.Name("seq").Value(Seq);
            writer.Name("combo").Value(Combo);
            if (!string.IsNullOrEmpty(Detail)) writer.Name("detail").Value(Detail);
            writer.EndObject();
        }
    }
}
