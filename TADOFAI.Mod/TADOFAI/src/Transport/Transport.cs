using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
                        await PumpAsync(socket, token).ConfigureAwait(false);
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
