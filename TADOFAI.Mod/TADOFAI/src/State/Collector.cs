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

        /// <summary>逐玩家块的采样间隔。判定直方图 / XScore 之类不需要每帧读，20Hz 足够且省反射。</summary>
        private const double PlayerSampleInterval = 0.05;

        /// <summary>KPS 滑窗容量（只需覆盖最近 1 秒）。</summary>
        private const int KpsWindow = 64;

        private static readonly object Gate = new object();
        private static readonly ModState State = new ModState();
        private static readonly double[] HitTimes = new double[KpsWindow];

        private static float _lastTimingMs;
        private static bool _hasTiming;
        private static int _hitMarginSlots;
        private static double _lastPlayerSample;
        private static int _hitTimeCount;
        private static int _hitTimeHead;

        /// <summary>HitMargin 枚举值个数（缓存）。</summary>
        private static int HitMarginSlots
        {
            get
            {
                if (_hitMarginSlots > 0) return _hitMarginSlots;
                _hitMarginSlots = GameRefs.HitMarginTypeCount;
                return _hitMarginSlots;
            }
        }

        /// <summary>开始新一局（练习模式是否上报由 Settings.TrackPracticeMode 决定）。</summary>
        public static void StartGame(int seqID)
        {
            int seq = seqID > 0 ? seqID : GameRefs.CurrentSeqID;
            MapMeta meta = GameRefs.ReadMapMeta();

            // 全部读取放在锁外：反射与关卡遍历都可能耗时，不该挡住发送线程取快照。
            int slots = HitMarginSlots;
            string mapId = GameRefs.MapId;
            string artist = GameRefs.MapArtist;
            string author = GameRefs.MapAuthor;
            string difficultyName = GameRefs.DifficultyName;
            float mapBpm = (float)GameRefs.MapBpm;
            float pitch = GameRefs.SongPitch;
            int floorCount = GameRefs.FloorCount;
            int playerCount = GameRefs.PlayerCount;
            float startProgress = GameRefs.PercentComplete;
            float marginScale = GameRefs.GetPlayerMarginScale(0);

            GameRefs.RefreshLevelCache();
            double mapLength = GameRefs.MapLength;

            lock (Gate)
            {
                State.ResetForNewGame(slots);

                State.Seq = seq;
                State.Auto = GameRefs.IsAuto;
                State.Practice = GameRefs.IsPracticeMode;
                State.NoFail = GameRefs.IsNoFail;
                State.SongName = meta.SongName;
                State.SongAuthor = meta.SongAuthor;
                State.Difficulty = meta.Difficulty;
                State.StartedAt = Transport.NowSeconds;

                State.MapId = mapId;
                State.MapArtist = artist;
                State.MapAuthor = author;
                State.DifficultyName = difficultyName;
                State.FloorCount = floorCount;
                State.MapBpm = mapBpm;
                State.SongPitch = pitch;
                State.MapLength = mapLength;

                State.PlayerCount = NumUtil.ClampInt(playerCount, 1, GameSnapshot.MaxPlayers);
                State.StartSeq = seq;
                State.StartProgress = NumUtil.Clamp01(startProgress);
                State.MarginScale = marginScale;

                _lastTimingMs = 0f;
                _hasTiming = false;
                _hitTimeCount = 0;
                _hitTimeHead = 0;
                _lastPlayerSample = 0.0;
            }

            ModLog.Info("游戏开始 seq=" + seq + " song=" + meta.SongName + " players=" + playerCount);
            Transport.EnqueueEvent(new GameEventMessage(GameEventNames.GameStart, seq, 0));

            if (!string.IsNullOrEmpty(meta.SongName))
                Transport.EnqueueEvent(new GameEventMessage(GameEventNames.MapChanged, seq, 0));

            RefreshPlayers(true);
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

            lock (Gate)
            {
                if (!State.InGame) return;
                if (Transport.NowSeconds - State.StartedAt < StartGraceSeconds) return;

                seq = State.Seq;
                State.InGame = false;
                State.GameState = "idle";
                State.Combo = 0;
            }

            GameRefs.InvalidateLevelCache();

            ModLog.Info("场景结束 seq=" + seq);
            Transport.EnqueueEvent(new GameEventMessage(GameEventNames.GameEnd, seq, 0));
        }

        /// <summary>记录一次命中，并自己维护 Combo（不依赖游戏内部 Combo 字段）。</summary>
        public static void RecordHit(HitMargin hit, int player)
        {
            int value = (int)hit;
            int seq;
            int combo;
            int misses;
            float timing;
            bool miss;
            bool perfect;
            bool perfectCombo;
            bool emit;

            lock (Gate)
            {
                seq = GameRefs.CurrentSeqID;
                if (seq <= 0) seq = State.Seq;
                State.Seq = seq;

                miss = HitMarginCompat.BreaksCombo(value);
                bool isAuto = HitMarginCompat.IsAuto(value);
                bool isMidspin = HitMarginCompat.IsMidspin(value);
                perfect = HitMarginCompat.IsPerfectCore(value);

                // Combo 只统计「中心完美」（JipperOverlayer 的 perfect combo 口径）：
                //   中心完美            → +1
                //   中旋 / Auto         → 不变（既不加也不清零）
                //   其余任何判定（含 miss）→ 清零，并把本局标记为「非完美连击」
                if (perfect)
                {
                    State.Combo++;
                    if (State.Combo > State.MaxCombo) State.MaxCombo = State.Combo;
                }
                else if (isMidspin || isAuto)
                {
                    // 不动
                }
                else
                {
                    State.Combo = 0;
                    State.PerfectCombo = false;
                }

                if (miss) State.Misses++;

                if (_hasTiming) State.TimingMs = _lastTimingMs;

                combo = State.Combo;
                misses = State.Misses;
                timing = State.TimingMs;
                perfectCombo = State.PerfectCombo;

                if (player >= 0 && player < State.PlayerSeq.Length) State.PlayerSeq[player] = seq;

                TrackPlayerHit(player, value, miss, seq);
                NoteHitTime(Transport.NowSeconds);

                emit = Settings.Current.SendHits;
            }

            if (!emit) return;

            HitMessage message = new HitMessage();
            message.Player = player;
            message.Seq = seq;
            message.Judgement = HitMarginCompat.ToJudgement(value);
            message.TimingMs = timing;
            message.Combo = combo;
            message.Miss = miss;
            message.Perfect = perfect;
            message.PerfectCombo = perfectCombo;
            message.Id = Transport.NextId();

            lock (Gate)
            {
                if (player >= 0 && player < State.Players.Length)
                {
                    PlayerTrack track = State.Players[player];
                    message.XScore = track.XScore;
                    message.Judged = track.Judged;
                    message.PurePerfect = track.PurePerfect;
                }
            }

            Transport.EnqueueEvent(message);

            if (!miss && Settings.Current.SendProgress) UpdatePlayerProgress(player);
        }

        /// <summary>记录一次检查点（scrController.Checkpoint_Enter）。</summary>
        public static void RecordCheckpoint()
        {
            int seq;
            int checkpoints;

            lock (Gate)
            {
                seq = State.Seq;
                checkpoints = GameRefs.CheckpointsUsed;
                State.Checkpoints = checkpoints;
            }

            GameEventMessage message = new GameEventMessage(GameEventNames.Checkpoint, seq, 0);
            message.Detail = "checkpoints=" + checkpoints;
            Transport.EnqueueEvent(message);
        }

        /// <summary>更新准确率（命中时立即刷新，避免等 20Hz 采样）。</summary>
        public static void UpdateAccuracy(int player)
        {
            if (!Settings.Current.ShowAccuracy) return;

            scrMarginTracker tracker = GetTracker(player);
            if (tracker == null) return;

            float accuracy = Reflect.GetFloat(tracker, "percentAcc", 0f);
            float xAccuracy = Reflect.GetFloat(tracker, "percentXAcc", 0f);
            if (!NumUtil.IsFinite(accuracy) && !NumUtil.IsFinite(xAccuracy)) return;

            float normalized = NumUtil.NormalizeAccuracy(accuracy);
            float normalizedX = NumUtil.NormalizeAccuracy(xAccuracy);

            lock (Gate)
            {
                if (player == 0)
                {
                    State.Accuracy = normalized;
                    State.XAccuracy = normalizedX;
                }

                if (player >= 0 && player < State.Players.Length)
                {
                    State.Players[player].Accuracy = normalized;
                    State.Players[player].XAccuracy = normalizedX;
                }
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

                if (player >= 0 && player < State.Players.Length && seq > 0)
                {
                    State.Players[player].Seq = seq;
                }
            }
        }

        /// <summary>每帧采样一次连续变化的状态（时间轴 / 进度 / 准确率 / BPM），由 Main.Update 调用。</summary>
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
                bool paused = GameRefs.IsPaused;
                float fps = GameRefs.Fps;
                int playerCount = GameRefs.PlayerCount;
                double musicTime = GameRefs.MusicTime;
                double musicLength = GameRefs.MusicLength;
                double mapTime = GameRefs.MapTime;
                double mapLength = GameRefs.MapLength;
                double now = Transport.NowSeconds;

                lock (Gate)
                {
                    if (seq > 0) State.Seq = seq;
                    if (NumUtil.IsFinite(progress)) State.Progress = NumUtil.Clamp01(progress);
                    State.Auto = auto;
                    State.Paused = paused;
                    State.Fps = fps;
                    State.PlayerCount = NumUtil.ClampInt(playerCount, 1, GameSnapshot.MaxPlayers);
                    State.MusicTime = musicTime;
                    State.MusicLength = musicLength;
                    State.MapTime = mapTime;
                    State.MapLength = mapLength;
                    State.Kps = ComputeKps(now);
                }
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("Collector.Sample", "采样状态失败: " + ex.Message);
            }

            UpdateBpm();

            // 逐玩家块（判定直方图 / XScore / 检查点 / 判定窗）按 20Hz 刷新：
            // 这些量在前端是「面板数值」，不需要 60Hz，省下来的反射开销留给命中路径。
            bool due;
            double current = Transport.NowSeconds;
            lock (Gate)
            {
                due = current - _lastPlayerSample >= PlayerSampleInterval;
                if (due) _lastPlayerSample = current;
            }

            if (due) RefreshPlayers(false);
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

        #region 内部

        private static void TrackPlayerHit(int player, int value, bool miss, int seq)
        {
            if (player < 0 || player >= State.Players.Length) return;

            PlayerTrack track = State.Players[player];
            track.Seq = seq;

            // 与全局 Combo 同一口径：只有中心完美才续连击。
            if (HitMarginCompat.IsPerfectCore(value))
            {
                track.Combo++;
                if (track.Combo > track.MaxCombo) track.MaxCombo = track.Combo;
            }
            else if (!HitMarginCompat.IsMidspin(value) && !HitMarginCompat.IsAuto(value))
            {
                track.Combo = 0;
                track.PerfectCombo = false;
            }

            if (value >= 0 && value < track.HitMargins.Length) track.HitMargins[value]++;

            scrMarginTracker tracker = GetTracker(player);
            if (tracker != null)
            {
                track.Judged = Reflect.GetInt(tracker, "playerHitMarginCount", track.Judged + 1);
                track.XScore = Reflect.GetInt(tracker, "xScore", track.XScore);
                track.MaxXScore = Reflect.GetInt(tracker, "maxXScore", track.MaxXScore);
                track.Deaths = Reflect.GetInt(tracker, "deaths", track.Deaths);
                track.PurePerfect = Reflect.ToBool(Reflect.Call(tracker, "IsAllPurePerfect"), track.PurePerfect);
            }
            else
            {
                track.Judged++;
            }
        }

        /// <summary>
        /// 逐玩家块刷新。force=true 用于开局（必须立刻有一帧完整数据）。
        /// </summary>
        private static void RefreshPlayers(bool force)
        {
            int slots = HitMarginSlots;
            int playerCount;
            lock (Gate)
            {
                playerCount = State.PlayerCount;
            }
            playerCount = NumUtil.ClampInt(playerCount, 1, GameSnapshot.MaxPlayers);

            for (int i = 0; i < playerCount; i++)
            {
                scrMarginTracker tracker = GetTracker(i);
                if (tracker == null) continue;

                float accuracy = Reflect.GetFloat(tracker, "percentAcc", 0f);
                float xAccuracy = Reflect.GetFloat(tracker, "percentXAcc", 0f);
                int xScore = Reflect.GetInt(tracker, "xScore", 0);
                int maxXScore = Reflect.GetInt(tracker, "maxXScore", 0);
                int deaths = Reflect.GetInt(tracker, "deaths", 0);
                int judged = Reflect.GetInt(tracker, "playerHitMarginCount", 0);
                float playerProgress = Reflect.GetFloat(tracker, "percentComplete", 0f);
                bool purePerfect = Reflect.ToBool(Reflect.Call(tracker, "IsAllPurePerfect"), false);
                bool isAuto = GameRefs.GetPlayerAuto(i);
                int seq = GameRefs.GetPlayerSeq(i);
                float marginScale = GameRefs.GetPlayerMarginScale(i);
                int remaining = GameRefs.CountRemainingHitFloors(seq);
                int[] counts = GameRefs.GetPlayerHitCounts(i, slots);
                string color = GameRefs.GetPlayerColorHex(i);

                lock (Gate)
                {
                    PlayerTrack track = State.Players[i];

                    if (seq > 0) track.Seq = seq;
                    if (NumUtil.IsFinite(accuracy)) track.Accuracy = NumUtil.NormalizeAccuracy(accuracy);
                    if (NumUtil.IsFinite(xAccuracy)) track.XAccuracy = NumUtil.NormalizeAccuracy(xAccuracy);

                    track.XScore = xScore;
                    track.MaxXScore = maxXScore;
                    track.Deaths = deaths;
                    track.Judged = judged > 0 ? judged : track.Judged;
                    track.Remaining = remaining;
                    track.PurePerfect = purePerfect;
                    track.Auto = isAuto;
                    track.Color = color ?? string.Empty;
                    track.MarginScale = NumUtil.IsFinite(marginScale) && marginScale > 0f ? marginScale : 1f;

                    if (counts != null && counts.Length == track.HitMargins.Length)
                    {
                        for (int k = 0; k < counts.Length; k++) track.HitMargins[k] = counts[k];
                    }

                    // 玩家 0 的进度以 scrController.percentComplete 为准（它就是主玩家进度）；
                    // Coop 其余玩家只有 tracker.percentComplete 可用。
                    if (i == 0 && State.Progress > 0f) track.Progress = State.Progress;
                    else if (num_finite(playerProgress)) track.Progress = NumUtil.Clamp01(playerProgress);

                    if (i == 0) State.MarginScale = track.MarginScale;
                }
            }

            // 判定窗 / 伪 BPM / 关卡长度 只在这一次采样里刷新
            int currentSeq;
            lock (Gate)
            {
                currentSeq = State.Seq;
            }

            double[] windows = GameRefs.TimingWindowMs;
            float[] degrees = GameRefs.TimingWindowDegrees;
            double denom = GameRefs.TimingDenom;
            float pseudoBpm = GameRefs.PseudoBpm(currentSeq);
            float mapBpm = (float)GameRefs.MapBpm;
            float pitch = GameRefs.SongPitch;
            int checkpoints = GameRefs.CheckpointsUsed;
            int floorCount = GameRefs.FloorCount;
            double mapLength = GameRefs.MapLength;
            int checkpointTiles = GameRefs.CheckpointTileCount;
            string mapId = GameRefs.MapId;
            string difficultyName = GameRefs.DifficultyName;

            lock (Gate)
            {
                State.TimingCounted = windows[0];
                State.TimingPerfect = windows[1];
                State.TimingPure = windows[2];
                State.TimingXPerfect = windows[3];
                State.TimingDenom = denom;
                State.TimingDegrees = new double[] { degrees[0], degrees[1], degrees[2], degrees[3] };

                State.PseudoBpm = pseudoBpm;
                State.Checkpoints = checkpoints;
                if (mapBpm > 0f) State.MapBpm = mapBpm;
                if (NumUtil.IsFinite(pitch) && pitch > 0f) State.SongPitch = pitch;
                if (floorCount > 0) State.FloorCount = floorCount;
                if (mapLength > 0.0) State.MapLength = mapLength;
                State.CheckpointTileCount = checkpointTiles;
                if (!string.IsNullOrEmpty(mapId)) State.MapId = mapId;
                if (!string.IsNullOrEmpty(difficultyName)) State.DifficultyName = difficultyName;
            }

            if (force)
            {
                ModLog.Debug("逐玩家块已建立 players=" + playerCount);
            }
        }

        private static scrMarginTracker GetTracker(int player)
        {
            try
            {
                scrMarginTracker tracker = GameRefs.GetPlayerTracker(player);
                if (tracker != null) return tracker;

                scrMarginTracker[] trackers = GameRefs.MarginTrackers;
                if (trackers == null || player < 0 || player >= trackers.Length) return null;
                return trackers[player];
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("Collector.GetTracker", "读取判定追踪器失败: " + ex.Message);
                return null;
            }
        }

        private static bool num_finite(float value)
        {
            return NumUtil.IsFinite(value);
        }

        private static void NoteHitTime(double now)
        {
            HitTimes[_hitTimeHead] = now;
            _hitTimeHead = (_hitTimeHead + 1) % KpsWindow;
            if (_hitTimeCount < KpsWindow) _hitTimeCount++;
        }

        private static float ComputeKps(double now)
        {
            int count = 0;
            for (int i = 0; i < _hitTimeCount; i++)
            {
                if (now - HitTimes[i] <= 1.0) count++;
            }
            return count;
        }

        private static void UpdatePlayerProgress(int player)
        {
            int seq = GameRefs.GetPlayerSeq(player);
            if (seq <= 0) return;

            lock (Gate)
            {
                if (player >= 0 && player < State.PlayerSeq.Length) State.PlayerSeq[player] = seq;
                if (seq > State.Seq) State.Seq = seq;
                if (player >= 0 && player < State.Players.Length) State.Players[player].Seq = seq;
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

        #endregion
    }
}
