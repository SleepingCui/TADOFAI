/*!
 * example-overlay —— 渲染逻辑 · 华丽版
 *
 * 数据全部来自 TadofaiClient 的订阅回调，这里只负责画。
 *
 * 抗高频的三条原则（50000 BPM 下每秒可能有 800+ 次命中，state 帧也有 60Hz）：
 *   1. 固定节点：命中路径上绝不创建/查询 DOM；判定分布与逐玩家行只在 hello /
 *      玩家数变化时建一次，之后只改文本与 transform；
 *   2. 合并写：命中统一攒到下一帧（requestAnimationFrame）只写一次；
 *   3. 值不变不写：state 帧里大量字段是重复的，比较后再决定要不要碰 DOM。
 *
 * 能力位：字段是否可信由 hello.capabilities 决定（见 renderHello）。
 * 老版本游戏缺少某个成员时 Mod 不会上报该能力位，对应区块自动隐藏，不会显示假数据。
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

  /** JipperOverlayer Settings.ComboColorMax 的默认值：基础模式下 combo 达到它就取渐变终点 */
  var COMBO_COLOR_MAX = 1000;
  /** 结算横幅停留时长 */
  var BANNER_MS = 4000;
  /** 检查点提示停留时长 */
  var NOTICE_MS = 1600;
  /** Combo 里程碑间隔 */
  var MILESTONE_STEP = 50;
  /** 协议上限：逐玩家最多 8 行 */
  var MAX_PLAYER_ROWS = 8;

  /* ---------------- 固定 DOM 引用 ---------------- */

  var el = {
    body: document.body,
    overlay: document.getElementById('overlay'),
    combo: document.getElementById('combo'),
    comboTitle: document.getElementById('combo-title'),
    song: document.getElementById('song'),
    gameState: document.getElementById('game-state'),
    difficulty: document.getElementById('difficulty'),
    mapInfo: document.getElementById('map-info'),
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
    bannerText: document.getElementById('banner-text'),
    notice: document.getElementById('x-notice'),

    musicTime: document.getElementById('x-music-time'),
    musicLen: document.getElementById('x-music-len'),
    mapTime: document.getElementById('x-map-time'),
    mapLen: document.getElementById('x-map-len'),
    kps: document.getElementById('x-kps'),
    bpmTile: document.getElementById('x-bpm-tile'),
    bpmExtra: document.getElementById('x-bpm-extra'),
    caps: document.getElementById('x-caps'),
    histTotal: document.getElementById('x-hist-total'),
    hist: document.getElementById('x-hist'),
    pure: document.getElementById('x-pure'),
    xScore: document.getElementById('x-xscore'),
    maxScore: document.getElementById('x-maxscore'),
    potential: document.getElementById('x-potential'),
    judged: document.getElementById('x-judged'),
    remaining: document.getElementById('x-remaining'),
    deaths: document.getElementById('x-deaths'),
    checkpoints: document.getElementById('x-checkpoints'),
    window: document.getElementById('x-window'),
    playersCard: document.getElementById('x-players-card'),
    playerCount: document.getElementById('x-player-count'),
    players: document.getElementById('x-players')
  };

  /** 上次写入 DOM 的值 / 状态：相同就跳过写入 */
  var last = {
    combo: null,
    comboTitle: null,
    comboColor: null,
    song: null,
    gameState: null,
    difficulty: null,
    mapInfo: null,
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
    judgementClass: null,

    musicTime: null,
    musicLen: null,
    mapTime: null,
    mapLen: null,
    kps: null,
    bpmTile: null,
    bpmExtra: null,
    caps: null,
    histTotal: null,
    pure: null,
    xScore: null,
    maxScore: null,
    potential: null,
    judged: null,
    remaining: null,
    deaths: null,
    checkpoints: null,
    window: null,
    playerCount: null,
    playersCardHidden: null
  };

  /** 值没变就不写 DOM，返回是否真的写了 */
  function write(node, key, text) {
    if (!node) return false;
    if (last[key] === text) return false;
    last[key] = text;
    node.textContent = text;
    return true;
  }

  /** class 也做一次比较，避免每帧都碰 classList */
  function setClass(node, key, name, on) {
    if (!node) return false;
    if (last[key] === on) return false;
    last[key] = on;
    node.classList.toggle(name, on);
    return true;
  }

  /** 切换节点的 style.display，只在状态真的变了才写 */
  function setHidden(node, key, hidden) {
    if (!node) return false;
    if (last[key] === hidden) return false;
    last[key] = hidden;
    node.style.display = hidden ? 'none' : '';
    return true;
  }

  /** 内联样式：值不变就不写，避免 60Hz 反复触发样式重算 */
  function setStyle(node, key, prop, value) {
    if (!node) return false;
    if (last[key] === value) return false;
    last[key] = value;
    node.style[prop] = value;
    return true;
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

  /* ---------------- 握手信息（hello） ---------------- */

  /** 最近一次 hello 声明的判定表与能力位 */
  var hello = {
    received: false,
    capabilities: [],
    names: [],
    weights: [],
    xScores: []
  };

  /** 能力位 → 简短中文标签，没见过的能力位原样显示 */
  var CAPABILITY_LABELS = {
    hit: '命中',
    timing: '手感',
    accuracy: '准确率',
    bpm: 'BPM',
    progress: '进度',
    xperfect: 'X完美',
    hitmargins: '判定表',
    mapmeta: '关卡信息',
    timeline: '时间轴',
    coop: 'Coop',
    checkpoint: '检查点',
    paused: '暂停',
    fps: '帧率',
    timingwindows: '判定窗',
    kps: 'KPS',
    pseudobpm: '伪BPM',
    checkpointtiles: '检查点格',
    perfectcombo: '完美连击'
  };

  function renderHello(data) {
    var d = data || {};
    hello.received = true;
    hello.capabilities = P.capabilities(d);
    hello.names = Array.isArray(d.hitMarginNames) ? d.hitMarginNames.slice() : [];
    hello.weights = Array.isArray(d.hitMarginWeights) ? d.hitMarginWeights.slice() : [];
    hello.xScores = Array.isArray(d.hitMarginXScores) ? d.hitMarginXScores.slice() : [];

    var labels = [];
    for (var i = 0; i < hello.capabilities.length; i++) {
      var name = hello.capabilities[i];
      labels.push(CAPABILITY_LABELS[name] || name);
    }
    write(el.caps, 'caps', labels.length ? labels.join(' · ') : '--');

    // 判定名到位后重建直方图；hello 也可能因为 Mod 重连而再次到达
    buildHistogram(hello.names);
    if (client.state) renderState(client.state);
  }

  /* ---------------- 判定分布 ---------------- */

  var hist = {
    names: [],
    rows: [],
    builtFor: -1
  };

  function buildHistogram(names) {
    var list = Array.isArray(names) ? names : [];
    if (hist.builtFor === list.length) return;

    el.hist.innerHTML = '';
    hist.rows = [];

    if (!list.length) {
      var empty = document.createElement('div');
      empty.className = 'x-empty';
      empty.textContent = '等待数据…';
      el.hist.appendChild(empty);
      hist.builtFor = -1;
      hist.names = [];
      return;
    }

    for (var i = 0; i < list.length; i++) {
      var cls = P.judgementClass(list[i]) || 'j-plain';

      var row = document.createElement('div');
      row.className = 'x-bar-row ' + cls;

      var key = document.createElement('span');
      key.className = 'x-bar-k';
      key.textContent = P.judgementLabel(list[i]);

      var track = document.createElement('span');
      track.className = 'x-bar-track';
      var fill = document.createElement('span');
      fill.className = 'x-bar-fill';
      track.appendChild(fill);

      var value = document.createElement('span');
      value.className = 'x-bar-v';
      value.textContent = '0';

      row.appendChild(key);
      row.appendChild(track);
      row.appendChild(value);
      el.hist.appendChild(row);

      hist.rows.push({ fill: fill, value: value, count: -1, ratio: null });
    }

    hist.names = list.slice();
    hist.builtFor = list.length;
  }

  /** counts 是「下标 = HitMargin 枚举值」的数组，和 hello.hitMarginNames 对齐 */
  function renderHistogram(counts) {
    if (!Array.isArray(counts) || !counts.length) return;
    if (hist.builtFor !== counts.length) return;

    var max = 0;
    var total = 0;
    var values = [];
    var i;
    for (i = 0; i < counts.length; i++) {
      var c = Math.max(0, Math.round(P.num(counts[i])));
      values.push(c);
      total += c;
      if (c > max) max = c;
    }

    for (i = 0; i < hist.rows.length; i++) {
      var row = hist.rows[i];
      if (row.count !== values[i]) {
        row.count = values[i];
        row.value.textContent = String(values[i]);
      }
      var ratio = max > 0 ? (values[i] / max).toFixed(3) : '0.000';
      if (row.ratio !== ratio) {
        row.ratio = ratio;
        row.fill.style.transform = 'scaleX(' + ratio + ')';
      }
    }

    write(el.histTotal, 'histTotal', total > 0 ? '共 ' + total + ' 次' : '');
  }

  /* ---------------- 逐玩家 ---------------- */

  var playerRows = [];

  function buildPlayerRows() {
    for (var i = 0; i < MAX_PLAYER_ROWS; i++) {
      var row = document.createElement('div');
      row.className = 'x-player';

      var name = document.createElement('span');
      name.className = 'x-player-name';
      name.textContent = 'P' + i;

      var bar = document.createElement('span');
      bar.className = 'x-player-bar';
      var fill = document.createElement('span');
      fill.className = 'x-player-fill';
      bar.appendChild(fill);

      var acc = document.createElement('span');
      acc.className = 'x-player-acc';
      acc.textContent = '--';

      var combo = document.createElement('span');
      combo.className = 'x-player-combo';
      combo.textContent = '0x';

      var tag = document.createElement('span');
      tag.className = 'x-player-tag';
      tag.textContent = '';

      row.appendChild(name);
      row.appendChild(bar);
      row.appendChild(acc);
      row.appendChild(combo);
      row.appendChild(tag);
      row.style.display = 'none';

      el.players.appendChild(row);
      playerRows.push({ row: row, fill: fill, acc: acc, combo: combo, tag: tag, cache: {} });
    }
  }

  /** 逐玩家的每一项都用行内 cache 比较，不能借用全局 last（那个 key 是共享的） */
  function rowWrite(entry, key, node, text) {
    if (entry.cache[key] === text) return;
    entry.cache[key] = text;
    node.textContent = text;
  }

  function setRowHidden(entry, hidden) {
    if (entry.cache.hidden === hidden) return;
    entry.cache.hidden = hidden;
    entry.row.style.display = hidden ? 'none' : '';
  }

  function renderPlayers(data) {
    var list = Array.isArray(data.players) ? data.players : [];
    var count = Math.max(1, Math.round(P.num(data.playerCount, list.length || 1)));

    // 单人模式：主面板已经显示同等信息，避免重复占位（卡片隐藏，但每行状态照旧维护，
    // 免得从 Coop 退回单人的那一刻残留上一局的旧行）
    setHidden(el.playersCard, 'playersCardHidden', count < 2);
    write(el.playerCount, 'playerCount', count > 1 ? count + ' 人' : '');

    for (var i = 0; i < playerRows.length; i++) {
      var entry = playerRows[i];
      var visible = count > 1 && i < count && i < list.length;
      setRowHidden(entry, !visible);
      if (!visible) continue;

      var player = list[i] || {};
      var color = P.playerColor(player.color);
      if (entry.cache.color !== color) {
        entry.cache.color = color;
        entry.row.style.setProperty('--pc', color || '#8fd6ff');
      }

      var progress = P.num(player.progress);
      if (progress < 0) progress = 0;
      if (progress > 1) progress = 1;
      var ratio = progress.toFixed(4);
      if (entry.cache.progress !== ratio) {
        entry.cache.progress = ratio;
        entry.fill.style.transform = 'scaleX(' + ratio + ')';
      }

      rowWrite(entry, 'acc', entry.acc, P.formatPercent(player.accuracy));
      rowWrite(entry, 'combo', entry.combo, Math.max(0, Math.round(P.num(player.combo))) + 'x');

      var tags = [];
      if (player.auto === true) tags.push('AUTO');
      if (player.purePerfect === true) tags.push('纯完美');
      if (P.num(player.deaths) > 0) tags.push('死 ' + P.formatInt(player.deaths));
      if (P.num(player.xScore) > 0) tags.push('X ' + P.formatInt(player.xScore));
      rowWrite(entry, 'tag', entry.tag, tags.join(' · '));
    }
  }

  /* ---------------- 状态帧渲染 ---------------- */

  var comboPulseToggle = { value: false };
  var comboPulseTimer = { timer: 0 };

  function renderState(raw) {
    var data = raw || {};
    var play = data.play || {};
    var map = data.map || {};
    var timeline = data.timeline || {};

    /* --- 主面板 --- */

    var combo = Math.max(0, Math.round(P.num(play.combo)));
    var comboText = String(combo);
    var comboChanged = write(el.combo, 'combo', comboText);

    setClass(el.combo, 'comboHot', 'hot', combo >= 100 && combo < 300);
    setClass(el.combo, 'comboBlaze', 'blaze', combo >= 300);

    if (comboChanged && combo > 0) {
      replay(el.combo, comboPulseToggle, 'pulse-a', 'pulse-b');
      scheduleCleanup(el.combo, ['pulse-a', 'pulse-b'], COMBO_PULSE_MS, comboPulseTimer);
    }

    // JipperOverlayer 的 Combo 格式：标题在「完美 / 连击」之间切换，数值按完美率红→黄→绿渐变。
    write(el.comboTitle, 'comboTitle', P.comboTitle(play.perfectCombo));

    var playerList = data.players || [];
    var first = playerList.length ? playerList[0] : null;
    var tooCount = first ? P.tooJudgementCount(first.hitMargins, hello.names) : 0;

    setStyle(el.combo, 'comboColor', 'color', P.comboColor({
      combo: combo,
      seq: P.num(play.seq, NaN),
      startSeq: P.num(play.startSeq, NaN),
      too: tooCount,
      purePerfect: play.purePerfect,
      max: COMBO_COLOR_MAX
    }));

    var song = P.str(map.songName, '');
    var songAuthor = P.str(map.songAuthor, '') || P.str(map.artist, '');
    write(el.song, 'song', song ? (songAuthor ? song + ' — ' + songAuthor : song) : '未载入曲目');

    write(el.difficulty, 'difficulty', P.difficultyLabel(map.difficultyName, map.difficulty));

    // 关卡标识：谱师 / 格数 / 倍率 / 哈希（本地成绩与标签都以哈希为键）
    var info = [];
    if (P.str(map.author, '')) info.push('谱 ' + map.author);
    if (P.num(map.floorCount) > 0) info.push(P.formatInt(map.floorCount) + ' 格');
    var pitch = P.num(map.pitch, 1);
    if (pitch > 0 && pitch !== 1) info.push('×' + P.formatNumber(pitch, 2));
    if (P.str(map.id, '')) info.push(String(map.id).slice(0, 8));
    write(el.mapInfo, 'mapInfo', info.length ? info.join(' · ') : '--');

    write(el.gameState, 'gameState', P.gameStateLabel(data.gameState));

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

    // BPM 优先用判定格自身的 BPM（tileBpm），缺失时回退到下位兼容字段
    var tileBpm = P.num(play.tileBpm) || P.num(play.bpm);
    write(el.bpm, 'bpm', tileBpm > 0 ? tileBpm.toFixed(1) : '--');

    write(el.timing, 'timing', P.formatTiming(play.timingMs));
    write(el.misses, 'misses', String(Math.max(0, Math.round(P.num(play.misses)))));

    /* --- 时间轴 --- */

    write(el.musicTime, 'musicTime', P.formatClock(timeline.musicTime));
    write(el.musicLen, 'musicLen', '/ ' + P.formatClock(timeline.musicLength));
    write(el.mapTime, 'mapTime', P.formatClock(timeline.mapTime));
    write(el.mapLen, 'mapLen', '/ ' + P.formatClock(timeline.mapLength));

    var kps = P.num(play.kps);
    write(el.kps, 'kps', kps > 0 ? P.formatNumber(kps, 1) : '--');

    write(el.bpmTile, 'bpmTile', tileBpm > 0 ? tileBpm.toFixed(1) : '--');
    var pseudo = P.num(play.pseudoBpm);
    var currentBpm = P.num(play.currentBpm);
    var extraText = pseudo > 0 ? ('伪 ' + pseudo.toFixed(1)) : (currentBpm > 0 ? ('当前 ' + currentBpm.toFixed(1)) : '伪 --');
    write(el.bpmExtra, 'bpmExtra', extraText);

    /* --- X-Score / 判定明细 --- */

    write(el.xScore, 'xScore', P.formatInt(play.xScore));
    write(el.maxScore, 'maxScore', P.formatInt(play.maxXScore));
    write(el.potential, 'potential', P.formatInt(play.xScorePotential));
    write(el.judged, 'judged', P.formatInt(play.judged));
    write(el.remaining, 'remaining', '剩 ' + P.formatInt(play.remaining));
    write(el.deaths, 'deaths', P.formatInt(play.deaths));
    write(el.checkpoints, 'checkpoints', 'CP ' + P.formatInt(play.checkpoints));
    write(el.pure, 'pure', play.purePerfect === true ? '纯完美' : '');

    var tw = data.timingWindows;
    var windowText = '--';
    if (tw && typeof tw === 'object') {
      windowText = 'C ' + P.formatNumber(tw.counted, 1) +
        ' · P ' + P.formatNumber(tw.perfect, 1) +
        ' · P+ ' + P.formatNumber(tw.pure, 1) +
        ' · X ' + P.formatNumber(tw.xPerfect, 1) + ' ms';
    }
    write(el.window, 'window', windowText);

    /* --- 判定分布 + 逐玩家 --- */

    var me = Array.isArray(data.players) && data.players.length ? data.players[0] : null;
    if (me && Array.isArray(me.hitMargins)) renderHistogram(me.hitMargins);
    renderPlayers(data);

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

    // 判定名先过 SDK 的中文标签（和判定分布用同一套名字，各版本枚举差异也由 SDK 兜住）
    var judgementText = P.judgementLabel(P.str(data.judgement, '?'));
    var timingText = P.formatTiming(data.timingMs);

    write(el.hitJudgement, 'judgement', judgementText);
    write(el.hitTiming, 'hitTiming', timingText);
    // 残影同步文本（只在文本变化时写，通常和本体一致）
    el.hitEcho.textContent = judgementText + '  ' + timingText;

    // 判定分级配色：走 SDK 的映射，判定名各版本不同也认得住
    var jc = P.judgementClass(data.judgement);
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

  /* ---------------- 事件提示 ---------------- */

  var noticeTimer = 0;

  function showNotice(text) {
    if (!el.notice) return;
    el.notice.textContent = text;
    el.notice.classList.remove('show');
    void el.notice.offsetWidth;
    el.notice.classList.add('show');

    window.clearTimeout(noticeTimer);
    noticeTimer = window.setTimeout(function () {
      noticeTimer = 0;
      el.notice.classList.remove('show');
    }, NOTICE_MS);
  }

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

  buildPlayerRows();

  var client = new window.TadofaiClient({
    state: true,
    // hello 必须订阅：能力位与判定名表只在握手帧里，
    // Core 会在订阅后补发最近一次 hello（插件通常晚于 Mod 连上）。
    events: ['hello', 'hit', 'game.end', 'checkpoint']
  });

  client.on('state', renderState);
  client.on('hello', renderHello);
  client.on('hit', onHit);
  client.on('game.end', renderBanner);
  client.on('checkpoint', function (data) {
    var d = data || {};
    showNotice('检查点 ' + P.formatInt(d.combo) + ' 连击');
  });
  client.on('map.changed', function (data) {
    var d = data || {};
    showNotice(P.str(d.detail, '') || '地图变更');
  });

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
