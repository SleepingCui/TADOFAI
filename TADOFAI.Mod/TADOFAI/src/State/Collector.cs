using System;
using TADOFAI.Mod.Protocol;

namespace TADOFAI.Mod
{
    /// <summary>
    /// 把游戏事件规范化成版本无关的数据。
    /// 所有方法都在 Unity 主线程（Harmony Postfix / Update）调用，不能阻塞、不能抛异常。
    /// </summary>
    public static class Collector
    {
        /// <summary>刚开局这段时间内的 EndScene 视为「开始阶段的噪声」，直接忽略。</summary>
        private const double StartGraceSeconds = 0.25;

        private static readonly object Gate = new object();
        private static readonly ModState State = new ModState();

        private static float _lastTimingMs;
        private static bool _hasTiming;

        /// <summary>开始新一局（练习模式是否上报由 Settings.TrackPracticeMode 决定）。</summary>
        public static void StartGame(int seqID)
        {
            int seq = seqID > 0 ? seqID : GameRefs.CurrentSeqID;
            MapMeta meta = GameRefs.ReadMapMeta();

            lock (Gate)
            {
                State.ResetForNewGame();
                State.Seq = seq;
                State.Auto = GameRefs.IsAuto;
                State.Practice = GameRefs.IsPracticeMode;
                State.NoFail = GameRefs.IsNoFail;
                State.SongName = meta.SongName;
                State.SongAuthor = meta.SongAuthor;
                State.Difficulty = meta.Difficulty;
                State.StartedAt = Transport.NowSeconds;

                _lastTimingMs = 0f;
                _hasTiming = false;
            }

            ModLog.Info("游戏开始 seq=" + seq + " song=" + meta.SongName);
            Transport.EnqueueEvent(new GameEventMessage(GameEventNames.GameStart, seq, 0));

            if (!string.IsNullOrEmpty(meta.SongName))
                Transport.EnqueueEvent(new GameEventMessage(GameEventNames.MapChanged, seq, 0));
        }

        /// <summary>
        /// 补充的启动点：有些模式在 scnGame.Play 前后还没有完整实例，这里做去重后补一次。
        /// </summary>
        public static void TryStartGame()
        {
            lock (Gate)
            {
                if (State.InGame) return;
            }

            StartGame(GameRefs.CurrentSeqID);
        }

        /// <summary>场景结束 / 回到选曲界面。</summary>
        public static void EndScene()
        {
            int seq;
            double startedAt;

            lock (Gate)
            {
                if (!State.InGame) return;
                if (Transport.NowSeconds - State.StartedAt < StartGraceSeconds) return;

                seq = State.Seq;
                startedAt = State.StartedAt;
                State.InGame = false;
                State.GameState = "idle";
                State.Combo = 0;
            }

            ModLog.Info("场景结束 seq=" + seq);
            Transport.EnqueueEvent(new GameEventMessage(GameEventNames.GameEnd, seq, 0));
        }

        /// <summary>记录一次命中，并自己维护 Combo（不依赖游戏内部 Combo 字段）。</summary>
        public static void RecordHit(HitMargin hit, int player)
        {
            if (!Settings.Current.SendHits) return;

            int value = (int)hit;
            int seq;
            int combo;
            int misses;
            float timing;
            bool miss;

            lock (Gate)
            {
                seq = GameRefs.CurrentSeqID;
                if (seq <= 0) seq = State.Seq;
                State.Seq = seq;

                miss = HitMarginCompat.BreaksCombo(value);

                if (miss)
                {
                    State.Combo = 0;
                    State.Misses++;
                }
                else if (!HitMarginCompat.IsAuto(value))
                {
                    State.Combo++;
                    if (State.Combo > State.MaxCombo) State.MaxCombo = State.Combo;
                }

                if (_hasTiming) State.TimingMs = _lastTimingMs;

                combo = State.Combo;
                misses = State.Misses;
                timing = State.TimingMs;

                if (player >= 0 && player < State.PlayerSeq.Length) State.PlayerSeq[player] = seq;
            }

            HitMessage message = new HitMessage();
            message.Player = player;
            message.Seq = seq;
            message.Judgement = HitMarginCompat.ToJudgement(value);
            message.TimingMs = timing;
            message.Combo = combo;
            message.Miss = miss;
            message.Id = Transport.NextId();

            Transport.EnqueueEvent(message);

            if (!miss && Settings.Current.SendProgress) UpdatePlayerProgress(player);
        }

        /// <summary>更新准确率。</summary>
        public static void UpdateAccuracy(int player)
        {
            if (!Settings.Current.ShowAccuracy) return;

            float accuracy;
            float xAccuracy;

            try
            {
                // 首选从玩家对象取 tracker，取不到再退回 marginTrackers 数组
                scrMarginTracker tracker = GameRefs.GetPlayerTracker(player);
                if (tracker == null)
                {
                    scrMarginTracker[] trackers = GameRefs.MarginTrackers;
                    if (trackers == null || player < 0 || player >= trackers.Length) return;
                    tracker = trackers[player];
                }

                if (tracker == null) return;

                accuracy = tracker.percentAcc;
                xAccuracy = tracker.percentXAcc;
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("Collector.UpdateAccuracy", "读取准确率失败: " + ex.Message);
                return;
            }

            if (!NumUtil.IsFinite(accuracy)) return;

            lock (Gate)
            {
                State.Accuracy = NumUtil.NormalizeAccuracy(accuracy);
                State.XAccuracy = NumUtil.NormalizeAccuracy(xAccuracy);
            }
        }

        /// <summary>按当前 Floor 与下一 Floor 的 entryTime 计算 BPM。</summary>
        public static void UpdateBpm()
        {
            if (!Settings.Current.ShowBpm) return;

            double bpm = 0.0;

            try
            {
                scrFloor current = GameRefs.CurrentFloor;
                scrFloor next = current != null ? current.nextfloor : null;

                if (current != null && next != null)
                {
                    double delta = next.entryTime - current.entryTime;
                    if (delta > 0.0 && NumUtil.IsFinite(delta))
                    {
                        double pitch = GameRefs.SongPitch;
                        if (!NumUtil.IsFinite(pitch) || pitch <= 0.0) pitch = 1.0;
                        bpm = 60.0 / (delta / pitch);
                    }
                }

                if (bpm <= 0.0 || !NumUtil.IsFinite(bpm)) bpm = GameRefs.Bpm;
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("Collector.UpdateBpm", "读取 BPM 失败: " + ex.Message);
                return;
            }

            if (bpm <= 0.0 || !NumUtil.IsFinite(bpm)) return;

            lock (Gate)
            {
                State.Bpm = (float)NumUtil.Clamp(bpm, 0.0, 1000000.0);
            }
        }

        /// <summary>记录 Timing（毫秒）。必须在游戏内 Patch 触发时调用，不能用网络到达时间。</summary>
        public static void RecordTiming(float milliseconds)
        {
            if (!NumUtil.IsFinite(milliseconds)) return;

            float clamped = (float)NumUtil.Clamp(milliseconds, -1000.0, 1000.0);

            lock (Gate)
            {
                _lastTimingMs = clamped;
                _hasTiming = true;
                State.TimingMs = clamped;
            }
        }

        public static void RecordDeath()
        {
            int seq;
            lock (Gate)
            {
                seq = State.Seq;
            }

            ModLog.Info("死亡 seq=" + seq);
            Transport.EnqueueEvent(new GameEventMessage(GameEventNames.Death, seq, 0));
        }

        public static void RecordClear()
        {
            int seq;
            int combo;

            lock (Gate)
            {
                seq = State.Seq;
                combo = State.MaxCombo;
                State.GameState = "cleared";
            }

            ModLog.Info("通关 seq=" + seq + " maxCombo=" + combo);
            Transport.EnqueueEvent(new GameEventMessage(GameEventNames.GameEnd, seq, combo));
        }

        public static void RecordAutoChanged()
        {
            int seq;
            bool auto;

            lock (Gate)
            {
                State.Auto = GameRefs.IsAuto;
                auto = State.Auto;
                seq = State.Seq;
            }

            GameEventMessage message = new GameEventMessage(GameEventNames.StateChanged, seq, 0);
            message.Detail = "auto=" + auto;
            Transport.EnqueueEvent(message);
        }

        /// <summary>
        /// 来自 scrPlanet.MoveToNextFloor 的进度刷新。
        /// Coop 下必须按 planet 的 playerID 区分，不能当成主玩家。
        /// </summary>
        public static void UpdateProgress(scrPlanet planet)
        {
            if (!Settings.Current.SendProgress) return;
            if (planet == null) return;

            int player = ReadPlanetPlayerId(planet);
            int seq = 0;

            try
            {
                scrFloor floor = planet.currfloor;
                if (floor != null) seq = floor.seqID;
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("Collector.UpdateProgress", "读取 Floor 失败: " + ex.Message);
                return;
            }

            lock (Gate)
            {
                if (player >= 0 && player < State.PlayerSeq.Length) State.PlayerSeq[player] = seq;
                if (seq > State.Seq) State.Seq = seq;
            }
        }

        /// <summary>每帧采样一次连续变化的状态（进度 / 准确率 / BPM），由 Main.Update 调用。</summary>
        public static void Sample()
        {
            lock (Gate)
            {
                if (!State.InGame) return;
            }

            try
            {
                int seq = GameRefs.CurrentSeqID;
                float progress = GameRefs.PercentComplete;
                bool auto = GameRefs.IsAuto;

                lock (Gate)
                {
                    if (seq > 0) State.Seq = seq;
                    if (NumUtil.IsFinite(progress)) State.Progress = NumUtil.Clamp01(progress);
                    State.Auto = auto;
                }
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("Collector.Sample", "采样状态失败: " + ex.Message);
            }

            UpdateAccuracy(0);
            UpdateBpm();
        }

        /// <summary>构造状态消息。跨线程读取安全（拷贝快照）。</summary>
        public static StateMessage BuildSnapshotMessage()
        {
            GameSnapshot snapshot;

            lock (Gate)
            {
                snapshot = State.ToSnapshot();
            }

            snapshot.Connected = Transport.IsConnected;
            snapshot.DroppedEvents = Transport.DroppedEvents;

            StateMessage message = new StateMessage();
            message.Snapshot = snapshot;
            return message;
        }

        public static bool IsInGame
        {
            get
            {
                lock (Gate)
                {
                    return State.InGame;
                }
            }
        }

        private static void UpdatePlayerProgress(int player)
        {
            int seq = GameRefs.GetPlayerSeq(player);
            if (seq <= 0) return;

            lock (Gate)
            {
                if (player >= 0 && player < State.PlayerSeq.Length) State.PlayerSeq[player] = seq;
                if (seq > State.Seq) State.Seq = seq;
            }
        }

        private static int ReadPlanetPlayerId(scrPlanet planet)
        {
            try
            {
                // r340：scrPlanet.player 直接指向所属玩家
                scrPlayer owner = planet.player;
                if (owner != null) return owner.playerID;
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("Collector.ReadPlanetPlayerId", "定位 Planet 所属玩家失败: " + ex.Message);
            }

            return 0;
        }
    }
}
