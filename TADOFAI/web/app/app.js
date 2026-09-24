/*!
 * TADOFAI Core 管理面板逻辑
 *
 * 只用 fetch + 原生 DOM，无框架、无构建、无 CDN。
 * 三个轮询：健康 1.5s、状态 1.5s、插件列表 5s；页面不可见时暂停。
 */
(function () {
  'use strict';

  /** protocol.js 里的格式化工具，取不到时下面有等价兜底 */
  var P = window.TadofaiProtocol || null;

  var HEALTH_INTERVAL = 1500;
  var STATE_INTERVAL = 1500;
  var PLUGINS_INTERVAL = 5000;

  var API = {
    health: '/api/health',
    state: '/api/state',
    plugins: '/api/plugins',
    config: '/api/config'
  };

  /* ---------------- DOM 引用 ---------------- */

  function byId(id) { return document.getElementById(id); }

  var el = {
    coreState: byId('core-state'),
    modPill: byId('mod-pill'),
    lastUpdate: byId('last-update'),
    topError: byId('top-error'),

    hCore: byId('h-core'),
    hMod: byId('h-mod'),
    hClients: byId('h-clients'),
    hQueue: byId('h-queue'),
    hDropped: byId('h-dropped'),
    hProto: byId('h-proto'),

    sGame: byId('s-game'),
    sSong: byId('s-song'),
    sSeq: byId('s-seq'),
    sProgress: byId('s-progress'),
    sCombo: byId('s-combo'),
    sAcc: byId('s-acc'),
    sXacc: byId('s-xacc'),
    sBpm: byId('s-bpm'),
    sTiming: byId('s-timing'),
    sMisses: byId('s-misses'),
    sBar: byId('s-bar'),

    pluginList: byId('plugin-list'),
    pluginsUpdated: byId('plugins-updated'),
    pluginsRefresh: byId('plugins-refresh'),

    configForm: byId('config-form'),
    cfgHost: byId('cfg-host'),
    cfgPort: byId('cfg-port'),
    cfgRate: byId('cfg-rate'),
    cfgDir: byId('cfg-dir'),
    cfgReload: byId('cfg-reload'),
    cfgLevel: byId('cfg-level'),
    configMsg: byId('config-msg'),
    configSave: byId('config-save'),
    configReload: byId('config-reload'),
    configSource: byId('config-source')
  };

  /* ---------------- 通用工具 ---------------- */

  /** 文字相同就不写 DOM */
  function setText(node, text) {
    if (!node || node.__text === text) return;
    node.__text = text;
    node.textContent = text;
  }

  function setClass(node, name, on) {
    if (node) node.classList.toggle(name, on);
  }

  function timeText() {
    var now = new Date();
    function pad(n) { return (n < 10 ? '0' : '') + n; }
    return pad(now.getHours()) + ':' + pad(now.getMinutes()) + ':' + pad(now.getSeconds());
  }

  function esc(text) {
    return String(text === null || text === undefined ? '' : text)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  /* 格式化：优先用 SDK 里的实现，保证与 Overlay 显示一致 */
  function num(value, fallback) {
    if (P) return P.num(value, fallback);
    var n = typeof value === 'number' ? value : parseFloat(value);
    return isFinite(n) ? n : (fallback === undefined ? 0 : fallback);
  }

  function pct(value, digits) {
    if (P) return P.formatPercent(value, digits);
    var n = num(value, NaN);
    return n !== n ? '--' : (n * 100).toFixed(digits == null ? 2 : digits) + '%';
  }

  function timing(value) {
    if (P) return P.formatTiming(value);
    var n = num(value, NaN);
    if (n !== n) return '--';
    return (n >= 0 ? '+' : '-') + Math.abs(n).toFixed(2) + 'ms';
  }

  function fixed(value, digits) {
    if (P) return P.formatNumber(value, digits);
    var n = num(value, NaN);
    return n !== n ? '--' : n.toFixed(digits == null ? 2 : digits);
  }

  function intText(value) {
    if (P) return P.formatInt(value);
    var n = num(value, NaN);
    return n !== n ? '--' : String(Math.round(n));
  }

  function gameLabel(state) {
    if (P) return P.gameStateLabel(state);
    return state ? String(state) : '--';
  }

  /* ---------------- 网络 ---------------- */

  /**
   * 把后端错误体转成一行可读文字。
   * detail 可能是字符串，也可能是 FastAPI 422 的数组（[{loc, msg, type}]）。
   */
  function describeDetail(body, status) {
    var fallback = 'HTTP ' + status;
    if (!body || typeof body !== 'object') return fallback;
    var detail = (body.detail !== undefined) ? body.detail : body.message;
    if (detail === undefined || detail === null) return fallback;
    if (typeof detail === 'string') return detail;
    if (Array.isArray(detail)) {
      var parts = [];
      for (var i = 0; i < detail.length; i++) {
        var item = detail[i];
        if (typeof item === 'string') {
          parts.push(item);
        } else if (item && typeof item === 'object') {
          var loc = Array.isArray(item.loc)
            ? item.loc.filter(function (p) { return p !== 'body' && p !== 'query'; }).join('.')
            : '';
          var msg = item.msg || item.message || JSON.stringify(item);
          parts.push(loc ? loc + ': ' + msg : msg);
        }
      }
      return parts.length ? parts.join('；') : fallback;
    }
    return JSON.stringify(detail);
  }

  /** fetch + JSON 解析；HTTP 非 2xx 时把后端的 detail 抛出来 */
  function fetchJson(url, options) {
    return fetch(url, options).then(function (response) {
      return response.text().then(function (text) {
        var body = null;
        if (text) {
          try {
            body = JSON.parse(text);
          } catch (err) {
            body = null;
          }
        }
        if (!response.ok) throw new Error(describeDetail(body, response.status));
        return body;
      });
    });
  }

  /* ---------------- 顶部错误条 ---------------- */

  var errors = {};

  function setError(key, message) {
    if (message) errors[key] = message;
    else delete errors[key];
    renderErrors();
  }

  function renderErrors() {
    var keys = Object.keys(errors);
    if (!keys.length) {
      el.topError.hidden = true;
      el.topError.textContent = '';
      return;
    }
    var lines = [];
    for (var i = 0; i < keys.length; i++) lines.push(errors[keys[i]]);
    el.topError.textContent = lines.join('　|　');
    el.topError.hidden = false;
  }

  /* ---------------- 服务状态 ---------------- */

  function describeCore(core) {
    if (core === undefined || core === null || core === '') return '--';
    if (typeof core === 'boolean') return core ? '正常' : '异常';
    if (typeof core === 'object') return '已就绪';
    return String(core);
  }

  var busy = {};

  function pollHealth() {
    if (busy.health) return Promise.resolve();
    busy.health = true;
    return fetchJson(API.health).then(function (data) {
      var d = data || {};
      setError('health', '');

      setText(el.hCore, describeCore(d.core));
      setText(el.lastUpdate, '更新于 ' + timeText());

      var modKnown = (d.modConnected !== undefined && d.modConnected !== null);
      var modConnected = d.modConnected === true;
      setText(el.hMod, modKnown ? (modConnected ? '已连接' : '未连接') : '--');
      setClass(el.hMod.closest('.field'), 'is-warn', modKnown && !modConnected);

      setText(el.hClients, d.clientCount === undefined || d.clientCount === null ? '--' : intText(d.clientCount));
      setText(el.hQueue, d.eventQueueSize === undefined || d.eventQueueSize === null ? '--' : intText(d.eventQueueSize));
      setText(el.hDropped, d.droppedEvents === undefined || d.droppedEvents === null ? '--' : intText(d.droppedEvents));
      setText(el.hProto, d.protocolVersion === undefined || d.protocolVersion === null ? '--' : String(d.protocolVersion));

      var proto = (d.protocolVersion === undefined || d.protocolVersion === null) ? '' : (' · 协议 v' + d.protocolVersion);
      setText(el.coreState, '已连接' + proto);

      setText(el.modPill, modKnown ? (modConnected ? 'Mod 已连接' : 'Mod 未连接') : 'Mod 状态未知');
      el.modPill.classList.toggle('ok', modConnected);
      el.modPill.classList.toggle('warn', modKnown && !modConnected);
      el.modPill.classList.toggle('bad', false);
    }).catch(function (err) {
      setError('health', '无法获取服务状态：' + err.message);
      setText(el.coreState, 'Core 无响应');
      setText(el.lastUpdate, '刷新失败 ' + timeText());
      setText(el.hMod, '--');
      el.modPill.classList.remove('ok', 'warn');
      el.modPill.classList.add('bad');
      setText(el.modPill, 'Core 离线');
    }).then(function () {
      busy.health = false;
    });
  }

  /* ---------------- 实时状态 ---------------- */

  function pollState() {
    if (busy.state) return Promise.resolve();
    busy.state = true;
    return fetchJson(API.state).then(function (data) {
      var d = data || {};
      var play = d.play || {};
      var map = d.map || {};
      setError('state', '');

      setText(el.sGame, gameLabel(d.gameState));

      var song = (typeof map.songName === 'string' && map.songName) ? map.songName : '';
      var author = (typeof map.songAuthor === 'string' && map.songAuthor) ? map.songAuthor : '';
      setText(el.sSong, song ? (author ? song + ' — ' + author : song) : '--');

      setText(el.sSeq, intText(play.seq));

      var progress = num(play.progress);
      if (progress < 0) progress = 0;
      if (progress > 1) progress = 1;
      setText(el.sProgress, pct(progress, 2));
      el.sBar.style.width = (progress * 100).toFixed(2) + '%';

      setText(el.sCombo, intText(play.combo));
      setText(el.sAcc, pct(play.accuracy));
      setText(el.sXacc, pct(play.xAccuracy));

      var bpm = num(play.bpm);
      setText(el.sBpm, bpm > 0 ? fixed(bpm, 1) : '--');
      setText(el.sTiming, timing(play.timingMs));
      setText(el.sMisses, intText(play.misses));
    }).catch(function (err) {
      setError('state', '无法获取实时状态：' + err.message);
    }).then(function () {
      busy.state = false;
    });
  }

  /* ---------------- 插件列表 ---------------- */

  var lastPluginsHtml = null;

  function pluginStatus(status) {
    var text = (status === null || status === undefined) ? '' : String(status).toLowerCase();
    if (text === 'ok' || text === 'loaded' || text === 'active' || text === 'enabled' || text === 'ready') {
      return { label: '正常', cls: 'ok' };
    }
    if (!text) return { label: '未知', cls: 'warn' };
    return { label: '错误', cls: 'bad' };
  }

  function renderPlugins(list) {
    if (!list) {
      if (lastPluginsHtml === '!error') return;
      lastPluginsHtml = '!error';
      el.pluginList.innerHTML = '<div class="empty">插件列表获取失败，请检查 Core 是否在运行。</div>';
      return;
    }
    if (!list.length) {
      if (lastPluginsHtml === '!empty') return;
      lastPluginsHtml = '!empty';
      el.pluginList.innerHTML = '<div class="empty">没有发现任何插件。</div>';
      return;
    }

    var parts = [];
    for (var i = 0; i < list.length; i++) {
      var plugin = list[i] || {};
      var id = esc(plugin.id);
      var name = esc(plugin.name || plugin.id || '(未命名插件)');
      var version = esc(plugin.version || '--');
      var type = esc(plugin.type || '--');
      var status = pluginStatus(plugin.status);
      var statusRaw = esc(plugin.status === null || plugin.status === undefined ? '--' : plugin.status);
      var error = (typeof plugin.error === 'string' && plugin.error) ? plugin.error : '';

      var link = '';
      if (plugin.id) {
        // 后端会给一个现成的 url（/overlay/{id}/），没有就自己拼
        var href = (typeof plugin.url === 'string' && plugin.url)
          ? plugin.url
          : ('/overlay/' + encodeURIComponent(plugin.id) + '/');
        link = '<a class="link" href="' + esc(href) + '" target="_blank" rel="noopener">打开 Overlay</a>';
      }

      parts.push(
        '<div class="plugin">' +
          '<div class="plugin-main">' +
            '<div class="plugin-name">' + name + ' <span class="plugin-meta"><code>' + id + '</code></span></div>' +
            '<div class="plugin-meta">版本 <code>' + version + '</code> · 类型 <code>' + type +
              '</code> · 状态 <code>' + statusRaw + '</code></div>' +
            (error ? '<div class="plugin-error">' + esc(error) + '</div>' : '') +
          '</div>' +
          '<div class="plugin-side">' +
            '<span class="status ' + status.cls + '">' + status.label + '</span>' +
            link +
          '</div>' +
        '</div>'
      );
    }

    var html = parts.join('');
    if (html === lastPluginsHtml) return;   // 内容没变就不重建 DOM
    lastPluginsHtml = html;
    el.pluginList.innerHTML = html;
  }

  function pollPlugins() {
    if (busy.plugins) return Promise.resolve();
    busy.plugins = true;
    return fetchJson(API.plugins).then(function (data) {
      setError('plugins', '');
      var list = Array.isArray(data) ? data : (data && Array.isArray(data.plugins) ? data.plugins : []);
      renderPlugins(list);

      // 后端会带上 count / usable，顺手显示出来
      var summary = '';
      if (data && !Array.isArray(data) && typeof data.count === 'number') {
        summary = data.count + ' 个插件';
        if (typeof data.usable === 'number') summary += ' · ' + data.usable + ' 个可用';
        summary += ' · ';
      }
      setText(el.pluginsUpdated, summary + '更新于 ' + timeText());
    }).catch(function (err) {
      setError('plugins', '插件列表获取失败：' + err.message);
      renderPlugins(null);
    }).then(function () {
      busy.plugins = false;
    });
  }

  /* ---------------- 配置 ---------------- */

  var currentConfig = null;

  function setConfigMsg(text, kind) {
    setText(el.configMsg, text);
    el.configMsg.classList.toggle('ok', kind === 'ok');
    el.configMsg.classList.toggle('bad', kind === 'bad');
  }

  function deepCopy(value) {
    try {
      return JSON.parse(JSON.stringify(value));
    } catch (err) {
      return null;
    }
  }

  function hasOption(select, value) {
    for (var i = 0; i < select.options.length; i++) {
      if (select.options[i].value === value) return true;
    }
    return false;
  }

  function fillConfigForm(config) {
    var cfg = config || {};
    var server = cfg.server || {};
    var plugins = cfg.plugins || {};
    var logging = cfg.logging || {};

    el.cfgHost.value = (server.host === null || server.host === undefined) ? '' : String(server.host);
    el.cfgPort.value = (server.port === null || server.port === undefined) ? '' : String(server.port);
    el.cfgRate.value = (server.stateRate === null || server.stateRate === undefined) ? '' : String(server.stateRate);
    el.cfgDir.value = (plugins.directory === null || plugins.directory === undefined) ? '' : String(plugins.directory);
    el.cfgReload.checked = plugins.autoReload !== false;

    var level = (logging.level === null || logging.level === undefined) ? 'INFO' : String(logging.level).toUpperCase();
    if (!hasOption(el.cfgLevel, level)) {
      var option = document.createElement('option');
      option.value = level;
      option.textContent = level;
      el.cfgLevel.appendChild(option);
    }
    el.cfgLevel.value = level;
  }

  function loadConfig() {
    setConfigMsg('正在载入配置…', '');
    return fetchJson(API.config).then(function (data) {
      currentConfig = (data && typeof data === 'object' && !Array.isArray(data)) ? data : {};
      fillConfigForm(currentConfig);
      setConfigMsg('配置已载入', 'ok');
      setText(el.configSource, 'GET /api/config · ' + timeText());
    }).catch(function (err) {
      setConfigMsg('配置载入失败：' + err.message, 'bad');
    });
  }

  /** 校验并组装完整配置对象；校验失败返回 {error: '提示'} */
  function buildConfigPayload() {
    var host = el.cfgHost.value.trim();
    if (!host) return { error: '监听地址不能为空' };

    var port = parseInt(el.cfgPort.value, 10);
    if (!isFinite(port) || port < 1 || port > 65535) return { error: '端口必须是 1~65535 之间的整数' };

    var rate = parseInt(el.cfgRate.value, 10);
    if (!isFinite(rate) || rate < 1 || rate > 240) return { error: '状态推送频率必须是 1~240 之间的整数' };

    var directory = el.cfgDir.value.trim();
    if (!directory) return { error: '插件目录不能为空' };

    var level = el.cfgLevel.value;
    if (!level) return { error: '日志级别不能为空' };

    // 在原有配置上改，保留后端可能存在的其它字段
    var config = deepCopy(currentConfig) || {};
    config.server = config.server || {};
    config.plugins = config.plugins || {};
    config.logging = config.logging || {};

    config.server.host = host;
    config.server.port = port;
    config.server.stateRate = rate;
    config.plugins.directory = directory;
    config.plugins.autoReload = !!el.cfgReload.checked;
    config.logging.level = level;

    return { config: config };
  }

  function saveConfig(config) {
    el.configSave.disabled = true;
    setConfigMsg('正在保存…', '');
    return fetchJson(API.config, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(config)
    }).then(function (data) {
      if (data && typeof data === 'object' && !Array.isArray(data) && Object.keys(data).length) {
        // 后端回显了保存后的配置，用它刷新表单
        currentConfig = data;
        fillConfigForm(data);
      } else {
        currentConfig = config;
      }
      setConfigMsg('保存成功' + (data && data.restartRequired ? '（部分改动需要重启服务生效）' : ''), 'ok');
      setText(el.configSource, 'POST /api/config · ' + timeText());
      pollHealth();
    }).catch(function (err) {
      setConfigMsg('保存失败：' + err.message, 'bad');
    }).then(function () {
      el.configSave.disabled = false;
    });
  }

  /* ---------------- 轮询调度 ---------------- */

  var timers = [];

  function tick(fn) {
    if (document.hidden) return;   // 页面不可见时不做无谓的请求
    fn();
  }

  function startPolling() {
    pollHealth();
    pollState();
    pollPlugins();

    timers.push(setInterval(function () { tick(pollHealth); }, HEALTH_INTERVAL));
    timers.push(setInterval(function () { tick(pollState); }, STATE_INTERVAL));
    timers.push(setInterval(function () { tick(pollPlugins); }, PLUGINS_INTERVAL));

    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) {
        pollHealth();
        pollState();
        pollPlugins();
      }
    });
  }

  /* ---------------- 事件绑定 ---------------- */

  el.pluginsRefresh.addEventListener('click', function () { pollPlugins(); });
  el.configReload.addEventListener('click', function () { loadConfig(); });

  el.configForm.addEventListener('submit', function (event) {
    event.preventDefault();
    var result = buildConfigPayload();
    if (result.error) {
      setConfigMsg(result.error, 'bad');
      return;
    }
    saveConfig(result.config);
  });

  window.addEventListener('beforeunload', function () {
    for (var i = 0; i < timers.length; i++) clearInterval(timers[i]);
  });

  /* ---------------- 启动 ---------------- */

  startPolling();
  loadConfig();
})();
