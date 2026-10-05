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

  /* ---------------- 能力位 ---------------- */

  /**
   * 从 hello 帧（或 client.hello）取出能力位数组。
   * 前端必须先看这个再决定渲染哪些块：字段出现在列表里就保证可读。
   */
  function capabilities(source) {
    var list = source;
    if (source && typeof source === 'object') list = source.capabilities;
    if (!Array.isArray(list)) return [];
    var out = [];
    for (var i = 0; i < list.length; i++) {
      if (typeof list[i] === 'string' && list[i]) out.push(list[i]);
    }
    return out;
  }

  function hasCapability(source, name) {
    var list = Array.isArray(source) ? source : capabilities(source);
    return list.indexOf(name) >= 0;
  }

  /* ---------------- 判定表 ---------------- */

  /**
   * 判定名 → 中文标签。
   * 名字来自 hello.hitMarginNames（各版本枚举不同），这里只做显示翻译；
   * 认不出来的名字原样返回，绝不丢弃。
   */
  var JUDGEMENT_LABELS = {
    TooEarly: '太早',
    VeryEarly: '很早',
    EarlyPerfect: '稍早',
    Perfect: '完美',
    PerfectMinus: '完美(-)',
    XPerfect: '完美(X)',
    PerfectPlus: '完美(+)',
    LatePerfect: '稍晚',
    VeryLate: '很晚',
    TooLate: '太晚',
    Multipress: '多按',
    OverPress: '连按',
    FailMiss: '失误',
    FailOverload: '过载',
    FailedFloor: '失败',
    Auto: '自动',
    Midspin: '中旋'
  };

  /** 判定名 → CSS class 后缀，供 overlay 上色 */
  var JUDGEMENT_CLASSES = {
    TooEarly: 'j-tooearly',
    VeryEarly: 'j-veryearly',
    EarlyPerfect: 'j-early',
    Perfect: 'j-perfect',
    PerfectMinus: 'j-perfect',
    XPerfect: 'j-xperfect',
    PerfectPlus: 'j-perfect',
    LatePerfect: 'j-late',
    VeryLate: 'j-verylate',
    TooLate: 'j-toolate',
    Multipress: 'j-multi',
    OverPress: 'j-multi',
    FailMiss: 'j-fail',
    FailOverload: 'j-fail',
    FailedFloor: 'j-fail',
    Auto: 'j-auto',
    Midspin: 'j-midspin'
  };

  function judgementLabel(name) {
    var key = str(name);
    if (!key) return '--';
    return JUDGEMENT_LABELS[key] || key;
  }

  function judgementClass(name) {
    var key = str(name);
    if (!key) return '';
    return JUDGEMENT_CLASSES[key] || '';
  }

  /**
   * 把 state.players[].hitMargins（下标数组）展开成 [{index, name, count}]。
   * names 来自 hello.hitMarginNames；names 缺失时 name 为空串，仍然保留下标。
   * onlyNonZero=true（默认）时跳过计数为 0 的项。
   */
  function expandHitMargins(counts, names, onlyNonZero) {
    var out = [];
    if (!Array.isArray(counts)) return out;
    var skipZero = onlyNonZero !== false;
    for (var i = 0; i < counts.length; i++) {
      var count = num(counts[i], 0);
      if (skipZero && count <= 0) continue;
      out.push({
        index: i,
        name: Array.isArray(names) && typeof names[i] === 'string' ? names[i] : '',
        count: Math.round(count)
      });
    }
    return out;
  }

  /* ---------------- 关卡 / 时间轴 ---------------- */

  var DIFFICULTY_LABELS = ['简单', '普通', '困难'];

  /** 难度：优先用 name（Lenient/Normal/Strict），否则按下标映射，再否则返回原值 */
  function difficultyLabel(name, index) {
    var key = str(name);
    if (key === 'Lenient') return DIFFICULTY_LABELS[0];
    if (key === 'Normal') return DIFFICULTY_LABELS[1];
    if (key === 'Strict') return DIFFICULTY_LABELS[2];
    if (key) return key;
    var i = num(index, NaN);
    if (i === i && DIFFICULTY_LABELS[i]) return DIFFICULTY_LABELS[i];
    return '--';
  }

  /** 秒 → m:ss（超过一小时给 h:mm:ss）；非法值返回 '--' */
  function formatClock(seconds, showHours) {
    var n = num(seconds, NaN);
    if (n !== n) return '--';
    var negative = n < 0;
    n = Math.abs(n);
    var total = Math.floor(n);
    var hours = Math.floor(total / 3600);
    var minutes = Math.floor((total % 3600) / 60);
    var secs = total % 60;
    var text;
    if (showHours || hours > 0) {
      text = hours + ':' + pad2(minutes) + ':' + pad2(secs);
    } else {
      text = minutes + ':' + pad2(secs);
    }
    return (negative ? '-' : '') + text;
  }

  /** 秒 → "1:23.45"，用于毫秒级显示 */
  function formatClockMs(seconds) {
    var n = num(seconds, NaN);
    if (n !== n) return '--';
    var negative = n < 0;
    n = Math.abs(n);
    var total = Math.floor(n);
    var minutes = Math.floor(total / 60);
    var secs = total % 60;
    var fraction = Math.floor((n - total) * 100);
    return (negative ? '-' : '') + minutes + ':' + pad2(secs) + '.' + pad2(fraction);
  }

  function pad2(value) {
    return value < 10 ? '0' + value : String(value);
  }

  /** "1 / 1200" 形式的整数比；上限非法时只显示分子 */
  function formatRatio(value, max) {
    var v = num(value, NaN);
    if (v !== v) return '--';
    var a = formatInt(value);
    var b = num(max, NaN);
    if (b !== b || b <= 0) return a;
    return a + ' / ' + formatInt(max);
  }

  /** 玩家颜色 RRGGBB → #RRGGBB；已经是 # 开头则原样返回；空值返回 '' */
  function playerColor(value) {
    var raw = str(value);
    if (!raw) return '';
    if (raw.charAt(0) === '#') return raw;
    if (/^[0-9a-fA-F]{6}$/.test(raw)) return '#' + raw;
    return '';
  }

  /** 毫秒判定窗 → "±45.0ms" */
  function formatWindow(ms, digits) {
    var n = num(ms, NaN);
    if (n !== n || n <= 0) return '--';
    return '±' + n.toFixed(digits == null ? 1 : digits) + 'ms';
  }

  /* ---------------- 完美连击（Combo）---------------- */
  // 口径与 JipperOverlayer 一致：Combo 只统计「中心完美」判定，
  // 标题在「完美」与「连击」之间切换，颜色按完美率走红→黄→绿渐变。

  var COMBO_TITLE = '完美';
  var COMBO_TITLE_ALT = '连击';

  /** JipperOverlayer ColorConfig.JCombo：(0,红) (0.2,黄) (1,绿) */
  var COMBO_STOPS = [
    [0, [255, 0, 0]],
    [0.2, [252, 255, 77]],
    [1, [95, 255, 79]]
  ];

  /** JipperOverlayer ColorConfig.Combo：淡紫 → 紫（单色基础模式） */
  var COMBO_BASE_STOPS = [
    [0, [223, 181, 255]],
    [1, [183, 89, 255]]
  ];

  /** JipperOverlayer ColorConfig.JStatePerfectPlay：金黄 */
  var PERFECT_PLAY_GOLD = [255, 218, 0];

  function stopsToRgb(stops, t) {
    var value = num(t, 0);
    if (value <= 0) value = 0;
    if (value >= 1) value = 1;

    for (var i = 0; i < stops.length - 1; i++) {
      var a = stops[i];
      var b = stops[i + 1];
      if (value >= a[0] && value <= b[0]) {
        var span = b[0] - a[0];
        var k = span <= 0 ? 0 : (value - a[0]) / span;
        return [
          Math.round(a[1][0] + (b[1][0] - a[1][0]) * k),
          Math.round(a[1][1] + (b[1][1] - a[1][1]) * k),
          Math.round(a[1][2] + (b[1][2] - a[1][2]) * k)
        ];
      }
    }
    return stops[stops.length - 1][1];
  }

  function rgbToHex(rgb) {
    function part(v) {
      var s = Math.max(0, Math.min(255, Math.round(v))).toString(16);
      return s.length < 2 ? '0' + s : s;
    }
    return '#' + part(rgb[0]) + part(rgb[1]) + part(rgb[2]);
  }

  /** Combo 标题：完美连击 → 「完美」，本局出现过非完美判定 → 「连击」 */
  function comboTitle(perfectCombo) {
    return bool(perfectCombo, true) ? COMBO_TITLE : COMBO_TITLE_ALT;
  }

  /**
   * Combo 数值颜色，复刻 JipperOverlayer：
   *  - 纯完美（purePerfect）           → 金黄
   *  - 有 seq/startSeq（扩展模式）     → t = combo / (seq - startSeq + too + 1) * 2，红→黄→绿
   *  - 否则（基础模式）                → t = combo / max，淡紫→紫
   * too = TooEarly + TooLate 的次数（可传 undefined，按 0 处理）。
   */
  function comboColor(options) {
    var opts = options || {};
    if (bool(opts.purePerfect, false)) return rgbToHex(PERFECT_PLAY_GOLD);

    var combo = num(opts.combo, 0);
    var seq = num(opts.seq, NaN);
    var startSeq = num(opts.startSeq, NaN);

    if (seq === seq && startSeq === startSeq) {
      var denominator = seq - startSeq + num(opts.too, 0) + 1;
      if (denominator > 0) return rgbToHex(stopsToRgb(COMBO_STOPS, (combo / denominator) * 2));
    }

    var max = num(opts.max, 1000);
    if (max <= 0) max = 1000;
    return rgbToHex(stopsToRgb(COMBO_BASE_STOPS, Math.min(combo, max) / max));
  }

  /** 从判定直方图里数 TooEarly + TooLate（comboColor 的 too 参数）。60Hz 调用，故不分配对象 */
  function tooJudgementCount(counts, names) {
    if (!Array.isArray(counts) || !Array.isArray(names)) return 0;
    var total = 0;
    var len = Math.min(counts.length, names.length);
    for (var i = 0; i < len; i++) {
      if (names[i] === 'TooEarly' || names[i] === 'TooLate') total += Math.round(num(counts[i], 0));
    }
    return total;
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
    gameStateLabel: gameStateLabel,

    capabilities: capabilities,
    hasCapability: hasCapability,

    judgementLabel: judgementLabel,
    judgementClass: judgementClass,
    expandHitMargins: expandHitMargins,

    difficultyLabel: difficultyLabel,
    formatClock: formatClock,
    formatClockMs: formatClockMs,
    formatRatio: formatRatio,
    playerColor: playerColor,
    formatWindow: formatWindow,

    COMBO_TITLE: COMBO_TITLE,
    COMBO_TITLE_ALT: COMBO_TITLE_ALT,
    comboTitle: comboTitle,
    comboColor: comboColor,
    tooJudgementCount: tooJudgementCount
  };
})(typeof window !== 'undefined' ? window : this);
