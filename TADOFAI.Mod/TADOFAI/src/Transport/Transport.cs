using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TADOFAI.Mod.Protocol;

namespace TADOFAI.Mod
{
    /// <summary>
    /// 单一发送线程的 WebSocket 传输。
    ///
    /// Harmony Patch → ConcurrentQueue&lt;ModMessage&gt; → Transport 线程 → WebSocket
    ///
    /// 约束：
    ///  - 游戏线程只入队，不做序列化和网络 IO；
    ///  - 事件逐条发送，状态覆盖式合并，按 Settings.StateRate 限频；
    ///  - 队列有上限，满了丢最旧并计入 droppedEvents；
    ///  - 同一 WebSocket 只由一个线程发送；
    ///  - 断线按 250ms / 500ms / 1s / 2s 退避重连，重连后先补一帧完整状态。
    /// </summary>
    public static class Transport
    {
        /// <summary>单次循环最多连续发送的事件数，避免状态帧被事件流长期饿死。</summary>
        private const int EventBudgetPerLoop = 256;

        /// <summary>接收缓冲区大小；入站帧（目前只有 ping/pong 与其后的空数据）不会大。</summary>
        private const int ReceiveBufferBytes = 4096;

        /// <summary>入站帧上限，超过即丢弃并只告警一次，防止对端灌内存。</summary>
        private const int MaxInboundBytes = 64 * 1024;

        private static readonly ConcurrentQueue<ModMessage> EventQueue = new ConcurrentQueue<ModMessage>();
        private static readonly Stopwatch Uptime = Stopwatch.StartNew();

        private static ModMessage _pendingState;
        private static CancellationTokenSource _cts;
        private static Task _worker;
        private static volatile bool _connected;
        private static bool _coreWantsTiming = true;
        private static long _droppedEvents;
        private static long _nextId;

        public static string Endpoint { get; private set; }

        public static bool IsConnected
        {
            get { return _connected; }
        }

        /// <summary>Core 是否仍需要 Timing。断线时无需采集，避免做无用功。</summary>
        public static bool NeedsTiming
        {
            get { return _connected && _coreWantsTiming; }
        }

        public static long DroppedEvents
        {
            get { return Interlocked.Read(ref _droppedEvents); }
        }

        /// <summary>单调秒，用于事件时间戳（不用网络到达时间）。</summary>
        public static double NowSeconds
        {
            get { return Uptime.Elapsed.TotalSeconds; }
        }

        public static long NextId()
        {
            return Interlocked.Increment(ref _nextId);
        }

        /// <summary>Core 通过控制消息开关 Timing 采集。</summary>
        public static void SetCoreTimingRequest(bool wanted)
        {
            _coreWantsTiming = wanted;
        }

        public static void Start()
        {
            Stop();

            Endpoint = Settings.Current.ServerUrl;
            CancellationTokenSource cts = new CancellationTokenSource();
            _cts = cts;
            _worker = Task.Run(() => RunAsync(cts.Token));

            ModLog.Info("Transport: " + Endpoint);
        }

        public static void Stop()
        {
            CancellationTokenSource cts = _cts;
            _cts = null;
            _worker = null;

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }

            _connected = false;

            ModMessage ignored;
            while (EventQueue.TryDequeue(out ignored)) { }
            Interlocked.Exchange(ref _pendingState, null);
        }

        /// <summary>入队一个逐条发送的事件。游戏线程调用，绝不阻塞。</summary>
        public static void EnqueueEvent(ModMessage message)
        {
            if (message == null) return;

            message.Timestamp = NowSeconds;

            int limit = Settings.Current.MaxQueuedEvents;
            while (EventQueue.Count >= limit)
            {
                ModMessage dropped;
                if (!EventQueue.TryDequeue(out dropped)) break;
                Interlocked.Increment(ref _droppedEvents);
            }

            EventQueue.Enqueue(message);
        }

        /// <summary>发布最新状态，覆盖上一个未发送的状态。</summary>
        public static void PublishState(ModMessage state)
        {
            if (state == null) return;

            state.Timestamp = NowSeconds;
            Interlocked.Exchange(ref _pendingState, state);
        }

        private static async Task RunAsync(CancellationToken token)
        {
            int attempt = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (ClientWebSocket socket = new ClientWebSocket())
                    {
                        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);

                        await socket.ConnectAsync(new Uri(Endpoint), token).ConfigureAwait(false);

                        attempt = 0;
                        ModLog.ResetOnceKeys();
                        _connected = true;
                        ModLog.Info("Transport: 已连接 Core");

                        await SendAsync(socket, BuildHello(), token).ConfigureAwait(false);

                        // 发送与接收必须同时在跑：ClientWebSocket 只在 ReceiveAsync 里处理
                        // PING/PONG 控制帧，只发不收的话服务端会在 ping 超时后断开（见 ReceiveLoopAsync）。
                        using (CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            Task pump = PumpAsync(socket, session.Token);
                            Task receive = ReceiveLoopAsync(socket, session.Token);

                            await Task.WhenAny(pump, receive).ConfigureAwait(false);

                            session.Cancel();
                            try
                            {
                                await Task.WhenAll(pump, receive).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                if (token.IsCancellationRequested) throw;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 连不上是常态（Core 没开），同一个错误只提示一次
                    if (!token.IsCancellationRequested)
                        ModLog.WarnOnce("Transport.Connect", "Transport: 连接异常 " + ex.Message);
                }
                finally
                {
                    _connected = false;
                }

                if (token.IsCancellationRequested || !Settings.Current.AutoReconnect) break;

                int delay = BackoffDelay(attempt);
                attempt++;

                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static async Task PumpAsync(ClientWebSocket socket, CancellationToken token)
        {
            double interval = 1.0 / Math.Max(1.0, Settings.Current.StateRate);
            // 连接后立刻补一帧完整状态
            double lastStateSent = NowSeconds - interval;

            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                bool didWork = false;

                int budget = EventBudgetPerLoop;
                ModMessage evt;
                while (budget > 0 && EventQueue.TryDequeue(out evt))
                {
                    budget--;
                    await SendAsync(socket, evt, token).ConfigureAwait(false);
                    didWork = true;
                }

                if (NowSeconds - lastStateSent >= interval)
                {
                    ModMessage state = Interlocked.Exchange(ref _pendingState, null);
                    if (state != null)
                    {
                        await SendAsync(socket, state, token).ConfigureAwait(false);
                        lastStateSent = NowSeconds;
                        didWork = true;
                    }
                }

                if (!didWork)
                    await Task.Delay(1, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 读取循环。两个作用：
        /// 1) ClientWebSocket 只在 ReceiveAsync 内部处理 PING/PONG 控制帧。只发不收时
        ///    uvicorn 会在 ws_ping_interval + ws_ping_timeout（默认 20s + 20s = 40 秒）后
        ///    判定客户端失联并断开，表现为「每隔 40 秒断开重连一次」。
        /// 2) 给 Core → Mod 的控制消息留一条通道（Core 目前不发，收到只记日志）。
        /// </summary>
        private static async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = new byte[ReceiveBufferBytes];

            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;

                try
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ModLog.Info("Transport: 对端关闭了连接");
                    return;
                }

                // 收满一帧（控制帧由 ReceiveAsync 内部处理，这里拿到的都是数据帧）
                MemoryStream frame = new MemoryStream();
                if (result.Count > 0) frame.Write(buffer, 0, result.Count);

                while (!result.EndOfMessage)
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.Count > 0) frame.Write(buffer, 0, result.Count);

                    if (frame.Length > MaxInboundBytes)
                    {
                        ModLog.WarnOnce("Transport.Inbound", "Transport: 入站帧超过 " + MaxInboundBytes + " 字节，已丢弃");
                        frame.SetLength(0);
                        break;
                    }
                }

                if (frame.Length > 0)
                {
                    ModLog.Debug("Transport: 收到 " + frame.Length + " 字节入站数据（当前未被 Core 使用）");
                }
            }
        }

        private static async Task SendAsync(ClientWebSocket socket, ModMessage message, CancellationToken token)
        {
            byte[] payload;

            try
            {
                payload = Encoding.UTF8.GetBytes(message.ToJson());
            }
            catch (Exception ex)
            {
                // 序列化失败只丢弃当前消息，不影响游戏线程
                Interlocked.Increment(ref _droppedEvents);
                ModLog.WarnOnce("Transport.Serialize", "Transport: 序列化失败，已丢弃 " + message.Type + " (" + ex.Message + ")");
                return;
            }

            try
            {
                await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, token)
                            .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _droppedEvents);
                ModLog.WarnOnce("Transport.Send", "Transport: 发送失败 " + ex.Message);
                throw; // 交给外层重连
            }
        }

        private static ModMessage BuildHello()
        {
            HelloMessage hello = new HelloMessage();
            hello.Id = NextId();
            hello.Timestamp = NowSeconds;
            hello.ModVersion = Main.ModVersion;
            hello.GameVersion = VersionSafe.GameVersion;
            hello.Capabilities = VersionSafe.Capabilities;
            hello.Difficulty = GameRefs.DifficultyName;
            hello.HitMarginNames = GameRefs.HitMarginNames;
            hello.HitMarginWeights = GameRefs.HitMarginWeights;
            hello.HitMarginXScores = GameRefs.HitMarginXScores;
            return hello;
        }

        private static int BackoffDelay(int attempt)
        {
            switch (attempt)
            {
                case 0: return 250;
                case 1: return 500;
                case 2: return 1000;
                default: return 2000;
            }
        }
    }
}
