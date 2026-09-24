/*!
 * example-overlay —— 渲染逻辑 · 华丽版
 *
 * 数据全部来自 TadofaiClient 的订阅回调，这里只负责画。
 *
 * 抗高频的三条原则（50000 BPM 下每秒可能有 800+ 次命中，state 帧也有 60Hz）：
 *   1. 固定节点：命中路径上绝不创建/查询 DOM，节点在启动时一次性取好；
 *   2. 合并写：命中统一攒到下一帧（requestAnimationFrame）只写一次；
 *   3. 值不变不写：state 帧里大量字段是重复的，比较后再决定要不要碰 DOM。
 */
(function () {
  'use strict';

  var P = window.TadofaiProtocol;

  /** 命中动画时长，必须和 style.css 里 @keyframes 的 220ms 一致 */
  var HIT_ANIM_MS = 220;
  /** 残影动画时长，比主命中略长一点 */
  var ECHO_ANIM_MS = 260;
  /** Combo 脉冲时长 */
  var COMBO_PULSE_MS = 180;
  /** 结算横幅停留时长 */
  var BANNER_MS = 4000;
  /** Combo 里程碑间隔 */
  var MILESTONE_STEP = 50;

  /* ---------------- 固定 DOM 引用 ---------------- */

  var el = {
    body: document.body,
    overlay: document.getElementById('overlay'),
    combo: document.getElementById('combo'),
    song: document.getElementById('song'),
    gameState: document.getElementById('game-state'),
    difficulty: document.getElementById('difficulty'),
    progressBar: document.getElementById('progress-bar'),
    progressText: document.getElementById('progress-text'),
    barHead: document.getElementById('bar-head'),
    accuracy: document.getElementById('accuracy'),
    xAccuracy: document.getElementById('xaccuracy'),
    bpm: document.getElementById('bpm'),
    timing: document.getElementById('timing'),
    misses: document.getElementById('misses'),
    hit: document.getElementById('hit'),
    hitEcho: document.getElementById('hit-echo'),
    hitJudgement: document.getElementById('hit-judgement'),
    hitTiming: document.getElementById('hit-timing'),
    milestone: document.getElementById('milestone'),
    milestoneText: document.getElementById('milestone-text'),
    banner: document.getElementById('banner'),
    bannerText: document.getElementById('banner-text')
  };

  /** 上次写入 DOM 的值 / 状态：相同就跳过写入 */
  var last = {
    combo: null,
    song: null,
    gameState: null,
    difficulty: null,
    progress: null,
    accuracy: null,
    xAccuracy: null,
    bpm: null,
    timing: null,
    misses: null,
    judgement: null,
    hitTiming: null,
    hitMiss: null,
    clsOffline: null,
    clsStale: null,
    comboHot: null,
    comboBlaze: null,
    judgementClass: null
  };

  /** 值没变就不写 DOM，返回是否真的写了 */
  function write(node, key, text) {
    if (last[key] === text) return false;
    last[key] = text;
    node.textContent = text;
    return true;
  }

  /** class 也做一次比较，避免每帧都碰 classList */
  function setClass(node, key, name, on) {
    if (last[key] === on) return;
    last[key] = on;
    node.classList.toggle(name, on);
  }

  /** PerfectPlus → Perfect Plus：只做视觉分隔 */
  function spacedName(name) {
    return String(name).replace(/([a-z0-9])([A-Z])/g, '$1 $2');
  }

  /** 判定名 → 分级 class（只识别常见几类，其余不额外上色） */
  function judgementClass(name) {
    var n = String(name || '').toLowerCase().replace(/[\s_-]/g, '');
    if (!n) return '';
    if (n.indexOf('perfectplus') !== -1) return 'j-perfectplus';
    if (n.indexOf('perfect') !== -1) return 'j-perfect';
    if (n.indexOf('veryearly') !== -1) return 'j-veryearly';
    if (n.indexOf('verylate') !== -1) return 'j-verylate';
    if (n.indexOf('early') !== -1) return 'j-early';
    if (n.indexOf('late') !== -1) return 'j-late';
    return '';
  }

  /* ---------------- 动画重放（双 class 交替） ---------------- */

  /** 在 node 上重放 name-a / name-b 两个动画：交替切换即可重新触发 */
  function replay(node, toggleState, classA, classB) {
    var next = !toggleState.value;
    toggleState.value = next;
    node.classList.remove(next ? classB : classA);
    node.classList.add(next ? classA : classB);
    return next;
  }

  /** 用一次性定时器移除动画 class，避免残留 */
  function scheduleCleanup(node, classes, ms, holder) {
    window.clearTimeout(holder.timer);
    holder.timer = window.setTimeout(function () {
      holder.timer = 0;
      for (var i = 0; i < classes.length; i++) node.classList.remove(classes[i]);
    }, ms);
  }

  /* ---------------- 状态帧渲染 ---------------- */

  var comboPulseToggle = { value: false };
  var comboPulseTimer = { timer: 0 };

  function renderState(raw) {
    var data = raw || {};
    var play = data.play || {};
    var map = data.map || {};

    // Combo：唯一的大号数字
    var combo = Math.max(0, Math.round(P.num(play.combo)));
    var comboText = String(combo);
    var comboChanged = write(el.combo, 'combo', comboText);

    // Combo 分级配色
    setClass(el.combo, 'comboHot',   'hot',   combo >= 100 && combo < 300);
    setClass(el.combo, 'comboBlaze', 'blaze', combo >= 300);

    // 数值变化时触发脉冲（只在真的变了才触发，state 帧重复推送不会刷）
    if (comboChanged && combo > 0) {
      replay(el.combo, comboPulseToggle, 'pulse-a', 'pulse-b');
      scheduleCleanup(el.combo, ['pulse-a', 'pulse-b'], COMBO_PULSE_MS, comboPulseTimer);
    }

    // 曲名（有作者就带上）
    var song = P.str(map.songName, '');
    var author = P.str(map.songAuthor, '');
    write(el.song, 'song', song ? (author ? song + ' — ' + author : song) : '未载入曲目');

    // 难度：可能是数字也可能是字符串，缺失就显示 --
    var difficulty = map.difficulty;
    write(
      el.difficulty,
      'difficulty',
      (difficulty === undefined || difficulty === null || difficulty === '')
        ? '--'
        : (typeof difficulty === 'number' ? 'Lv.' + difficulty : String(difficulty))
    );

    // 游戏状态：idle / playing / cleared
    write(el.gameState, 'gameState', P.gameStateLabel(data.gameState));

    // 进度：progress 是 0~1 小数，用 transform 推进度条（不触发重排）
    var progress = P.num(play.progress);
    if (progress < 0) progress = 0;
    if (progress > 1) progress = 1;
    var progressKey = progress.toFixed(4);
    if (last.progress !== progressKey) {
      last.progress = progressKey;
      el.progressBar.style.transform = 'scaleX(' + progressKey + ')';
      el.barHead.style.transform = 'translateX(' + (progress * 100) + '%) scaleX(1)';
      el.progressText.textContent = P.formatPercent(progress, 1);
    }

    write(el.accuracy, 'accuracy', P.formatPercent(play.accuracy));
    write(el.xAccuracy, 'xAccuracy', P.formatPercent(play.xAccuracy));

    var bpm = P.num(play.bpm);
    write(el.bpm, 'bpm', bpm > 0 ? bpm.toFixed(1) : '--');

    write(el.timing, 'timing', P.formatTiming(play.timingMs));
    write(el.misses, 'misses', String(Math.max(0, Math.round(P.num(play.misses)))));

    // stale = Mod 已断开，界面压暗并提示
    setClass(el.overlay, 'clsStale', 'stale', data.stale === true || data.connected === false);
  }

  /* ---------------- 命中渲染 ---------------- */

  var pendingHit = null;      // 只保留最新一次命中
  var hitFrame = 0;           // requestAnimationFrame 句柄
  var hitAnimToggle = { value: false };
  var echoAnimToggle = { value: false };
  var hitCleanup = { timer: 0 };
  var echoCleanup = { timer: 0 };
  var lastMilestone = 0;

  function onHit(data) {
    pendingHit = data || {};
    if (!hitFrame) hitFrame = window.requestAnimationFrame(flushHit);
  }

  function flushHit() {
    hitFrame = 0;
    var data = pendingHit;
    pendingHit = null;
    if (!data) return;

    var judgementText = spacedName(P.str(data.judgement, '?'));
    var timingText = P.formatTiming(data.timingMs);

    write(el.hitJudgement, 'judgement', judgementText);
    write(el.hitTiming, 'hitTiming', timingText);
    // 残影同步文本（只在文本变化时写，通常和本体一致）
    el.hitEcho.textContent = judgementText + '  ' + timingText;

    // 判定分级配色
    var jc = judgementClass(data.judgement);
    if (last.judgementClass !== jc) {
      if (last.judgementClass) el.hit.classList.remove(last.judgementClass);
      if (jc) el.hit.classList.add(jc);
      last.judgementClass = jc;
    }

    // miss = 断 Combo，换成醒目红色
    var miss = data.miss === true;
    setClass(el.hit, 'hitMiss', 'miss', miss);

    // 主命中动画 + 残影动画（双 class 交替，不用 reflow）
    replay(el.hit, hitAnimToggle, 'pop-a', 'pop-b');
    replay(el.hit, echoAnimToggle, 'echo-a', 'echo-b');
    scheduleCleanup(el.hit, ['pop-a', 'pop-b'], HIT_ANIM_MS, hitCleanup);
    scheduleCleanup(el.hit, ['echo-a', 'echo-b'], ECHO_ANIM_MS, echoCleanup);
  }

  /* ---------------- Combo 里程碑 ---------------- */

  var milestoneTimer = 0;

  function maybeMilestone(combo) {
    if (combo < MILESTONE_STEP) { lastMilestone = 0; return; }
    var tier = Math.floor(combo / MILESTONE_STEP);
    if (tier <= lastMilestone) return;
    lastMilestone = tier;

    el.milestoneText.textContent = combo + ' COMBO';
    el.milestone.classList.remove('show');
    // 强制重启动画：读一次 offsetWidth（低频事件，可接受）
    void el.milestone.offsetWidth;
    el.milestone.classList.add('show');

    window.clearTimeout(milestoneTimer);
    milestoneTimer = window.setTimeout(function () {
      milestoneTimer = 0;
      el.milestone.classList.remove('show');
    }, 1200);
  }

  // 包一层：在 renderState 之后检查里程碑
  var _renderState = renderState;
  renderState = function (raw) {
    _renderState(raw);
    var play = (raw && raw.play) || {};
    maybeMilestone(Math.max(0, Math.round(P.num(play.combo))));
  };

  /* ---------------- 结算横幅 ---------------- */

  var bannerTimer = 0;

  function renderBanner(data) {
    var d = data || {};
    var parts = [];
    if (d.songName !== undefined && d.songName !== null && d.songName !== '') parts.push(String(d.songName));
    if (d.maxCombo !== undefined && d.maxCombo !== null) parts.push('最大连击 ' + P.formatInt(d.maxCombo));
    if (d.accuracy !== undefined && d.accuracy !== null) parts.push('准确率 ' + P.formatPercent(d.accuracy));
    if (!parts.length) parts.push('本局结束');

    el.bannerText.textContent = parts.join('   ');
    el.banner.classList.remove('show');
    void el.banner.offsetWidth;
    el.banner.classList.add('show');

    window.clearTimeout(bannerTimer);
    bannerTimer = window.setTimeout(function () {
      bannerTimer = 0;
      el.banner.classList.remove('show');
    }, BANNER_MS);

    // 新一局开始，重置里程碑
    lastMilestone = 0;
  }

  /* ---------------- 接入客户端 ---------------- */

  var client = new window.TadofaiClient({
    state: true,
    events: ['hit', 'game.end']
  });

  client.on('state', renderState);
  client.on('hit', onHit);
  client.on('game.end', renderBanner);

  client.on('connection', function (connected) {
    setClass(el.body, 'clsOffline', 'offline', !connected);
    if (connected && client.state) renderState(client.state);
  });

  client.on('error', function (err) {
    if (window.console && window.console.warn) {
      window.console.warn('[overlay]', (err && err.message) ? err.message : err);
    }
  });

  client.connect();
})();