/*!
 * TADOFAI 插件客户端 SDK
 *
 * 全局类：TadofaiClient —— 封装 /ws/client 的连接、订阅、自动重连与事件分发。
 * 依赖：protocol.js（必须先引入，否则本文件直接报错退出）。
 *
 * 用法：
 *   const client = new TadofaiClient({ state: true, events: ["hit", "game.start"] });
 *   client.on("state", state => {});
 *   client.on("hit", event => {});
 *   client.on("connection", connected => {});
 *   client.connect();
 */
(function (global) {
  'use strict';

  var P = global.TadofaiProtocol;
  if (!P) throw new Error('[TadofaiClient] 缺少依赖：请先引入 /sdk/protocol.js');

  /** 重连退避：250ms → 500ms → 1s → 2s，之后稳定在 2s */
  var RECONNECT_DELAYS = [250, 500, 1000, 2000];

  /** 页面用 file:// 打开、拿不到 location.host 时的兜底地址 */
  var FALLBACK_HOST = '127.0.0.1:37125';

  /** 默认连接地址：ws://<当前页面 host>/ws/client（https 页面自动用 wss://） */
  function defaultUrl() {
    var loc = global.location;
    var host = (loc && loc.host) ? loc.host : FALLBACK_HOST;
    var secure = !!(loc && loc.protocol === 'https:');
    return (secure ? 'wss://' : 'ws://') + host + '/ws/client';
  }

  /**
   * @param {{url?: string, state?: boolean, events?: string[], autoReconnect?: boolean}} options
   */
  function TadofaiClient(options) {
    var opts = options || {};

    this.url = opts.url || defaultUrl();
    this.wantState = opts.state !== false;
    this.wantEvents = Array.isArray(opts.events)
      ? opts.events.filter(function (name) { return typeof name === 'string' && name; })
      : [];
    this.autoReconnect = opts.autoReconnect !== false;

    /** 最近一次状态快照（只读缓存，可能为 null） */
    this.state = null;
    /** 当前是否已连上 */
    this.connected = false;
    /** 已发送消息数（订阅消息会重复计入） */
    this.sentMessages = 0;
    /** 已接收消息数 */
    this.receivedMessages = 0;
    /** 最近一条事件帧的消息 id，配合 droppedEventIds 估算丢包 */
    this.lastEventId = null;
    /** 按 id 连续性估算出的事件丢包数量 */
    this.droppedEventIds = 0;
    /** 最近一条完整消息的信封（含 id / timestamp） */
    this.lastMessage = null;

    this._handlers = {};          // type -> [callback]
    this._ws = null;
    this._reconnectTimer = null;
    this._reconnectAttempt = 0;
    this._closed = false;

    var self = this;
    this._onUnload = function () { self.close(); };
    if (global.addEventListener) global.addEventListener('beforeunload', this._onUnload);
  }

  /* ---------------- 事件订阅 ---------------- */

  /** 注册回调，支持 state / connection / error / 任意事件名；同一类型可注册多个 */
  TadofaiClient.prototype.on = function (type, callback) {
    if (typeof type !== 'string' || typeof callback !== 'function') return this;
    if (!this._handlers[type]) this._handlers[type] = [];
    this._handlers[type].push(callback);
    return this;
  };

  /** 注销回调：不传 callback 时清空该类型的全部回调 */
  TadofaiClient.prototype.off = function (type, callback) {
    var list = this._handlers[type];
    if (!list) return this;
    if (!callback) {
      delete this._handlers[type];
      return this;
    }
    var index = list.indexOf(callback);
    if (index >= 0) list.splice(index, 1);
    return this;
  };

  /** 分发：回调内部抛出的异常不能打断其它回调，统一走 error */
  TadofaiClient.prototype._dispatch = function (type, data, message) {
    var list = this._handlers[type];
    if (!list || !list.length) return;
    // 先取长度再遍历：回调里 on/off 不会影响本次分发
    for (var i = 0, len = list.length; i < len; i++) {
      var callback = list[i];
      if (typeof callback !== 'function') continue;
      try {
        callback.call(this, data, message);
      } catch (err) {
        this._emitError(err);
      }
    }
  };

  /** 错误回调；没有注册任何 error 回调时只打日志，不抛异常 */
  TadofaiClient.prototype._emitError = function (err, raw) {
    var list = this._handlers['error'];
    if (!list || !list.length) {
      if (global.console && global.console.warn) {
        global.console.warn('[TadofaiClient]', (err && err.message) ? err.message : err);
      }
      return;
    }
    for (var i = 0, len = list.length; i < len; i++) {
      try {
        list[i].call(this, err, raw);
      } catch (inner) {
        if (global.console && global.console.warn) global.console.warn('[TadofaiClient]', inner);
      }
    }
  };

  /* ---------------- 连接生命周期 ---------------- */

  TadofaiClient.prototype.connect = function () {
    this._closed = false;

    var WS = global.WebSocket;
    if (!WS) {
      this._emitError(new Error('当前环境不支持 WebSocket'));
      return this;
    }
    // 正在连接或已连上时不重复建连
    if (this._ws && (this._ws.readyState === 0 || this._ws.readyState === 1)) return this;

    this._clearReconnectTimer();

    var self = this;
    var ws;
    try {
      ws = new WS(this.url);
    } catch (err) {
      this._emitError(err);
      this._scheduleReconnect();
      return this;
    }
    this._ws = ws;

    ws.onopen = function () {
      if (self._ws !== ws) return;
      self._reconnectAttempt = 0;
      self._setConnected(true);
      // 每次连上都重新订阅：服务端连上后会先推一份状态快照，这里马上把订阅补上
      self.subscribe();
    };

    ws.onmessage = function (event) {
      if (self._ws !== ws) return;
      self._handleRaw(event.data);
    };

    ws.onerror = function () {
      if (self._ws !== ws) return;
      // 浏览器不提供错误细节，onnclose 随后会触发重连
      self._emitError(new Error('WebSocket 连接错误'));
    };

    ws.onclose = function () {
      if (self._ws !== ws) return;
      self._ws = null;
      self._setConnected(false);
      if (!self._closed && self.autoReconnect) self._scheduleReconnect();
    };

    return this;
  };

  /** 关闭连接：清理定时器、摘掉 beforeunload，不再重连 */
  TadofaiClient.prototype.close = function () {
    this._closed = true;
    this._clearReconnectTimer();

    if (global.removeEventListener && this._onUnload) {
      global.removeEventListener('beforeunload', this._onUnload);
    }

    var ws = this._ws;
    this._ws = null;
    if (ws) {
      ws.onopen = ws.onmessage = ws.onerror = ws.onclose = null;
      try {
        if (ws.readyState === 0 || ws.readyState === 1) ws.close();
      } catch (err) {
        /* 关闭时的异常忽略即可 */
      }
    }

    this._setConnected(false);
    return this;
  };

  /** 发送当前订阅消息（connect 后自动调用，也可手动重发） */
  TadofaiClient.prototype.subscribe = function () {
    return this.send(P.buildSubscribe({ state: this.wantState, events: this.wantEvents }));
  };

  /** 发送任意消息，返回是否发送成功 */
  TadofaiClient.prototype.send = function (payload) {
    var ws = this._ws;
    if (!ws || ws.readyState !== 1) return false;
    try {
      ws.send(typeof payload === 'string' ? payload : JSON.stringify(payload));
      this.sentMessages++;
      return true;
    } catch (err) {
      this._emitError(err);
      return false;
    }
  };

  /** socket 是否处于打开状态 */
  TadofaiClient.prototype.isOpen = function () {
    return !!(this._ws && this._ws.readyState === 1);
  };

  TadofaiClient.prototype._setConnected = function (value) {
    if (this.connected === value) return;
    this.connected = value;
    this._dispatch('connection', value);
  };

  TadofaiClient.prototype._clearReconnectTimer = function () {
    if (this._reconnectTimer) {
      global.clearTimeout(this._reconnectTimer);
      this._reconnectTimer = null;
    }
  };

  TadofaiClient.prototype._scheduleReconnect = function () {
    if (this._closed || this._reconnectTimer) return;
    var index = Math.min(this._reconnectAttempt, RECONNECT_DELAYS.length - 1);
    var delay = RECONNECT_DELAYS[index];
    this._reconnectAttempt++;

    var self = this;
    this._reconnectTimer = global.setTimeout(function () {
      self._reconnectTimer = null;
      self.connect();
    }, delay);
  };

  /* ---------------- 消息处理 ---------------- */

  TadofaiClient.prototype._handleRaw = function (raw) {
    try {
      if (typeof raw !== 'string') {
        this._emitError(new Error('收到非文本消息，已忽略'), raw);
        return;
      }
      this.receivedMessages++;

      var message = P.parseMessage(raw);
      if (!message) {
        // 非法 JSON / 非对象 / 缺 type / version 不匹配
        this._emitError(new Error('收到非法消息（JSON 解析失败或协议版本不匹配）'), raw);
        return;
      }
      this.lastMessage = message;

      if (message.type === P.TYPE_STATE) {
        // 先更新缓存再回调：页面刷新后可以先用这份快照顶上
        this.state = message.data;
        this._dispatch(P.TYPE_STATE, message.data, message);
        return;
      }

      // 逐条事件：id 单调递增，用它估算丢包
      if (message.id !== null) {
        if (this.lastEventId !== null && message.id > this.lastEventId + 1) {
          this.droppedEventIds += message.id - this.lastEventId - 1;
        }
        if (this.lastEventId === null || message.id > this.lastEventId) {
          this.lastEventId = message.id;
        }
      }
      this._dispatch(message.type, message.data, message);
    } catch (err) {
      // 兜底：任何意外都不允许冒泡成未捕获异常
      this._emitError(err, raw);
    }
  };

  global.TadofaiClient = TadofaiClient;
})(typeof window !== 'undefined' ? window : this);
