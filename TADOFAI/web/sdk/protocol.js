/*!
 * TADOFAI 协议常量与校验工具
 *
 * 全局对象：TadofaiProtocol
 * 无依赖、零构建，普通 <script src="/sdk/protocol.js"></script> 引入即可。
 * 字段名与 Core 的 WebSocket 协议（version 1）严格一致，注意大小写。
 */
(function (global) {
  'use strict';

  /** 协议版本，服务端 version !== 1 的消息一律丢弃 */
  var PROTOCOL_VERSION = 1;

  /* ---------------- 消息类型 ---------------- */
  var TYPE_SUBSCRIBE = 'subscribe';
  var TYPE_STATE = 'state';
  var TYPE_HIT = 'hit';

  /* ---------------- 事件类型 ---------------- */
  var EVENT_GAME_START = 'game.start';
  var EVENT_GAME_END = 'game.end';
  var EVENT_DEATH = 'death';
  var EVENT_CHECKPOINT = 'checkpoint';
  var EVENT_MAP_CHANGED = 'map.changed';
  var EVENT_STATE_CHANGED = 'state.changed';

  /** 全部事件类型（不含 state 帧），可用于 subscribe 的默认值 / 校验 */
  var EVENT_TYPES = [
    TYPE_HIT,
    EVENT_GAME_START,
    EVENT_GAME_END,
    EVENT_DEATH,
    EVENT_CHECKPOINT,
    EVENT_MAP_CHANGED,
    EVENT_STATE_CHANGED
  ];

  /** 全部已知消息类型（state 帧 + 事件帧） */
  var MESSAGE_TYPES = [TYPE_STATE].concat(EVENT_TYPES);

  /* ---------------- gameState 取值 ---------------- */
  var GAME_STATE_IDLE = 'idle';
  var GAME_STATE_PLAYING = 'playing';
  var GAME_STATE_CLEARED = 'cleared';
  var GAME_STATES = [GAME_STATE_IDLE, GAME_STATE_PLAYING, GAME_STATE_CLEARED];

  /* ---------------- 取值容错 ---------------- */

  function isFiniteNumber(value) {
    return typeof value === 'number' && isFinite(value);
  }

  /** 安全转数字，缺失 / 非法 / NaN / Infinity 时返回 fallback（默认 0） */
  function num(value, fallback) {
    var def = (fallback === undefined) ? 0 : fallback;
    var n = (typeof value === 'string') ? parseFloat(value) : value;
    return isFiniteNumber(n) ? n : def;
  }

  /** 安全转字符串，非字符串或空串时返回 fallback（默认 ''） */
  function str(value, fallback) {
    var def = (fallback === undefined) ? '' : fallback;
    return (typeof value === 'string' && value) ? value : def;
  }

  /** 安全转布尔，非布尔时返回 fallback（默认 false） */
  function bool(value, fallback) {
    var def = (fallback === undefined) ? false : fallback;
    return (typeof value === 'boolean') ? value : def;
  }

  /* ---------------- 订阅消息 ---------------- */

  /**
   * 构造订阅消息，返回值可直接 JSON.stringify 后发给服务端。
   * @param {{state?: boolean, events?: string[]}} options
   *        state 省略时默认 true；events 省略时为空数组（= 不要事件）
   */
  function buildSubscribe(options) {
    var o = options || {};
    var events = [];
    if (Array.isArray(o.events)) {
      for (var i = 0; i < o.events.length; i++) {
        var name = o.events[i];
        if (typeof name === 'string' && name) events.push(name);
      }
    }
    return {
      type: TYPE_SUBSCRIBE,
      version: PROTOCOL_VERSION,
      timestamp: Date.now() / 1000,
      data: {
        state: o.state !== false,
        events: events
      }
    };
  }

  /* ---------------- 消息解析 ---------------- */

  /**
   * 解析并做基本校验。任何非法输入都返回 null，绝不抛异常。
   * 非法情形：不是合法 JSON、顶层不是对象、缺 type、version !== 1。
   *
   * @param {string|object} raw 服务端发来的原始文本（也容忍已解析的对象）
   * @returns {{type:string, version:number, id:number|null, timestamp:number, data:object, raw:object}|null}
   */
  function parseMessage(raw) {
    var message = raw;

    if (typeof raw === 'string') {
      if (!raw) return null;
      try {
        message = JSON.parse(raw);
      } catch (err) {
        return null;
      }
    }

    if (!message || typeof message !== 'object' || Array.isArray(message)) return null;
    if (typeof message.type !== 'string' || !message.type) return null;
    if (message.version !== PROTOCOL_VERSION) return null;

    var data = message.data;
    if (!data || typeof data !== 'object' || Array.isArray(data)) data = {};

    return {
      type: message.type,
      version: message.version,
      id: isFiniteNumber(message.id) ? message.id : null,
      timestamp: isFiniteNumber(message.timestamp) ? message.timestamp : 0,
      data: data,
      raw: message
    };
  }

  /* ---------------- 显示格式化 ---------------- */

  /**
   * 0~1 的小数 → 百分比字符串（协议里的 progress / accuracy / xAccuracy 都是小数）。
   * 非法值返回 '--'。
   */
  function formatPercent(value, digits) {
    var n = num(value, NaN);
    if (n !== n) return '--';
    return (n * 100).toFixed(digits == null ? 2 : digits) + '%';
  }

  /** 毫秒 → 带正负号、固定小数的字符串，例如 -1.37 → '-1.37ms'；非法值返回 '--' */
  function formatTiming(value, digits) {
    var n = num(value, NaN);
    if (n !== n) return '--';
    var text = Math.abs(n).toFixed(digits == null ? 2 : digits);
    return (n >= 0 ? '+' : '-') + text + 'ms';
  }

  /** 数字 → 固定小数位字符串，非法值返回 '--' */
  function formatNumber(value, digits) {
    var n = num(value, NaN);
    if (n !== n) return '--';
    return n.toFixed(digits == null ? 2 : digits);
  }

  /** 数字 → 整数字符串，非法值返回 '--' */
  function formatInt(value) {
    var n = num(value, NaN);
    if (n !== n) return '--';
    return String(Math.round(n));
  }

  /** gameState → 中文标签，未知值原样返回 */
  function gameStateLabel(state) {
    if (state === GAME_STATE_PLAYING) return '游玩中';
    if (state === GAME_STATE_CLEARED) return '已通关';
    if (state === GAME_STATE_IDLE) return '空闲';
    return typeof state === 'string' && state ? state : '--';
  }

  global.TadofaiProtocol = {
    PROTOCOL_VERSION: PROTOCOL_VERSION,

    TYPE_SUBSCRIBE: TYPE_SUBSCRIBE,
    TYPE_STATE: TYPE_STATE,
    TYPE_HIT: TYPE_HIT,

    EVENT_GAME_START: EVENT_GAME_START,
    EVENT_GAME_END: EVENT_GAME_END,
    EVENT_DEATH: EVENT_DEATH,
    EVENT_CHECKPOINT: EVENT_CHECKPOINT,
    EVENT_MAP_CHANGED: EVENT_MAP_CHANGED,
    EVENT_STATE_CHANGED: EVENT_STATE_CHANGED,

    EVENT_TYPES: EVENT_TYPES,
    MESSAGE_TYPES: MESSAGE_TYPES,

    GAME_STATE_IDLE: GAME_STATE_IDLE,
    GAME_STATE_PLAYING: GAME_STATE_PLAYING,
    GAME_STATE_CLEARED: GAME_STATE_CLEARED,
    GAME_STATES: GAME_STATES,

    isFiniteNumber: isFiniteNumber,
    num: num,
    str: str,
    bool: bool,

    buildSubscribe: buildSubscribe,
    parseMessage: parseMessage,

    formatPercent: formatPercent,
    formatTiming: formatTiming,
    formatNumber: formatNumber,
    formatInt: formatInt,
    gameStateLabel: gameStateLabel
  };
})(typeof window !== 'undefined' ? window : this);
