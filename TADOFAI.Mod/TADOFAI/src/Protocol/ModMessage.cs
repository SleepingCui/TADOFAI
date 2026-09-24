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

    /// <summary>连接后第一帧：声明客户端、版本与能力。</summary>
    public sealed class HelloMessage : ModMessage
    {
        public string ModVersion = "";
        public string GameVersion = "";
        public string[] Capabilities = new string[0];

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
            writer.Name("capabilities").BeginArray();
            for (int i = 0; i < Capabilities.Length; i++) writer.Value(Capabilities[i]);
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

            writer.Name("map").BeginObject();
            writer.Name("songName").Value(snapshot.SongName);
            writer.Name("songAuthor").Value(snapshot.SongAuthor);
            writer.Name("difficulty").Value(snapshot.Difficulty);
            writer.EndObject();

            writer.Name("play").BeginObject();
            writer.Name("seq").Value(snapshot.Play.Seq);
            writer.Name("progress").Value(snapshot.Play.Progress);
            writer.Name("combo").Value(snapshot.Play.Combo);
            writer.Name("maxCombo").Value(snapshot.Play.MaxCombo);
            writer.Name("accuracy").Value(snapshot.Play.Accuracy);
            writer.Name("xAccuracy").Value(snapshot.Play.XAccuracy);
            writer.Name("bpm").Value(snapshot.Play.Bpm);
            writer.Name("timingMs").Value(snapshot.Play.TimingMs);
            writer.Name("misses").Value(snapshot.Play.Misses);
            writer.EndObject();

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
            writer.EndObject();
        }
    }

    /// <summary>通用游戏事件（game.start / game.end / death / state.changed ...）。</summary>
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
