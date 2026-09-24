# TADOFAI Core

ADOFAI 本地数据服务与 Web 应用宿主，对应 `TADOFAI-本体开发指南.md` 与
`TADOFAI-本体与插件系统完整框架方案.md` 的本体部分。

Core 不读取游戏内存：Mod 采集数据 → WebSocket → Core 维护状态、分发事件 →
WebUI / Overlay 插件 / OBS 消费数据。

```text
ADOFAI Mod
    ↓ ws://127.0.0.1:37125/ws/mod
TADOFAI Core
    ├─ 协议校验    core/protocol.py
    ├─ 状态存储    core/state_store.py      只保留最新值
    ├─ 事件总线    core/event_bus.py        每客户端独立队列
    ├─ 状态广播    core/broadcaster.py      按 stateRate 合并发送
    ├─ 插件管理    core/plugin_manager.py   扫描 / 校验 / 静态路由
    └─ 健康诊断    core/health.py
         ↓ ws://127.0.0.1:37125/ws/client
    插件页面（Overlay）→ OBS 浏览器源
```

## 1. 目录结构

```text
TADOFAI/
├─ core/                    服务本体
│  ├─ app.py                FastAPI 装配、lifespan、静态挂载
│  ├─ config.py             ConfigManager：读配置 + 原子写入
│  ├─ models.py             Pydantic 模型（对外 camelCase）
│  ├─ protocol.py           信封解析、版本/长度/NaN 校验、序列化
│  ├─ state_store.py        最新状态
│  ├─ event_bus.py          订阅者队列 + 事件/状态分发
│  ├─ broadcaster.py        状态广播循环
│  ├─ mod_gateway.py        /ws/mod
│  ├─ client_gateway.py     /ws/client
│  ├─ plugin_manager.py     插件扫描与文件解析
│  ├─ services.py           服务容器、消息分发、生命周期
│  ├─ health.py             /api/health 数据源
│  └─ logging_setup.py      控制台 + 滚动文件日志
├─ api/                     HTTP 路由
│  ├─ routes_health.py      /api/health
│  ├─ routes_state.py       /api/state、/api/clients
│  ├─ routes_plugins.py     /api/plugins、/api/plugin/{id}/manifest、/overlay/*
│  └─ routes_config.py      GET/POST /api/config
├─ web/
│  ├─ app/                  管理页面（/app/）
│  └─ sdk/                  插件 SDK（/sdk/）
├─ plugins/                 前端插件
│  └─ example-overlay/      OBS 用示例 Overlay
├─ schemas/                 event-v1 / state-v1 的 JSON Schema
├─ config/config.json       默认配置（首次运行也会自动生成）
├─ logs/                    运行日志（自动创建）
├─ requirements.txt
└─ test.py                  独立的协议接收测试脚本（只打印收到的消息）
```

## 2. 快速开始

```bat
python -m venv venv
venv\Scripts\pip install -r requirements.txt
venv\Scripts\python -m core.app
```

或者用 uvicorn（CLI 参数优先于 config.json）：

```bat
venv\Scripts\python -m uvicorn core.app:app --host 127.0.0.1 --port 37125
```

启动后：

| 用途 | 地址 |
| --- | --- |
| 管理页面 | http://127.0.0.1:37125/app/ |
| 示例 Overlay（OBS 浏览器源） | http://127.0.0.1:37125/overlay/example-overlay/ |
| Mod 上行端点 | ws://127.0.0.1:37125/ws/mod |
| 插件客户端端点 | ws://127.0.0.1:37125/ws/client |

Mod 的 `Settings.xml` 里 `ServerUrl` 默认就是 `ws://127.0.0.1:37125/ws/mod`，不用改。

## 3. 接口

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/health` | core 状态、Mod 连接、客户端数、队列与丢包 |
| GET | `/api/state` | 最新状态（与推给插件的字段一致） |
| GET | `/api/plugins` | 插件列表（含不可用的插件与原因） |
| GET | `/api/plugin/{id}/manifest` | 单个插件的 manifest |
| GET | `/api/config` | 当前配置 |
| POST | `/api/config` | 保存配置（校验失败返回 422，`detail` 里有字段信息） |
| GET | `/api/clients` | 每个插件客户端的队列/丢包情况（排查慢客户端用） |
| WS | `/ws/mod` | Mod 上行数据 |
| WS | `/ws/client` | 插件客户端（状态 + 事件） |
| GET | `/app/` | 管理页面 |
| GET | `/overlay/{id}/...` | 插件页面（OBS） |
| GET | `/plugins/{id}/...` | 插件文件别名入口 |
| GET | `/sdk/...` | 插件 SDK |

`/api/health` 关键字段：

```json
{"core":"ok","protocolVersion":1,"modConnected":true,
 "modClient":"tadofai-mod","modVersion":"0.1.0","gameVersion":"3.3.1",
 "capabilities":["hit","timing","accuracy","bpm","progress"],
 "modMessages":464,"modConnects":3,"modDisconnects":1,"rejectedMessages":1,
 "lastModMessageAgoSeconds":0.02,"unknownMessageTypes":{},
 "clientCount":2,"eventQueueSize":0,"maxClientQueueSize":0,
 "droppedEvents":7,"clientDroppedEvents":0,"serializer":"orjson","stateUpdates":812}
```

- `droppedEvents` 是 **Mod 上报**的丢包（Mod 队列满丢最旧）；
- `clientDroppedEvents` 是 **Core 侧**因慢客户端丢的事件（每个客户端互不影响）。

## 4. 协议

信封统一：`{ type, version, id, timestamp, data }`，`data` 内一律 **camelCase**，
`version` 当前为 `1`，不支持的版本会被拒绝并计入 `rejectedMessages`。

- `timestamp`：Mod 上行时是采集时刻；Core 转发给插件时**统一换成 epoch 秒**，
  前端可以直接和 `Date.now()` 对齐；
- `id`：Mod 侧单调递增的事件号，Core 原样转发，插件可用它检测丢失/重复；
  状态帧没有 `id`；
- 校验：NaN / Infinity / 超长字符串（>512）/ 超深嵌套直接拒绝整条消息，
  轻微越界（progress、accuracy、timingMs、bpm）夹到合法区间而不是丢帧；
- 未知字段忽略；未知消息类型记一次警告后**原样转发**，便于插件自行扩展。

### 4.1 Mod → Core（上行）

`hello`（连接后第一帧）、`state`（状态流）、`hit`（逐条）、
`game.start` / `game.end` / `map.changed` / `state.changed` / `death` / `checkpoint`。

完整定义见 `schemas/event-v1.schema.json` 与 `schemas/state-v1.schema.json`。

### 4.2 Core → 插件（下行）

状态帧按 `server.stateRate` 广播（默认 60Hz）：

```json
{"type":"state","version":1,"timestamp":1730000000.5,"data":{
  "connected":true,"stale":false,"gameState":"playing","droppedEvents":0,
  "auto":false,"practice":false,"noFail":false,
  "map":{"songName":"","songAuthor":"","difficulty":0},
  "play":{"seq":421,"progress":0.5231,"combo":128,"maxCombo":200,"accuracy":0.9912,
          "xAccuracy":0.98,"bpm":180.0,"timingMs":-1.37,"misses":2},
  "playerSeq":[421,0,0,0,0,0,0,0],"updatedAt":1730000000.4}}
```

事件帧逐条推送，低延迟优先：

```json
{"type":"hit","version":1,"id":182734,"timestamp":1730000000.789,"data":{
  "player":0,"seq":421,"judgement":"PerfectPlus","timingMs":-1.37,"combo":128,"miss":false}}
```

插件客户端连上后**先发订阅**：

```json
{"type":"subscribe","version":1,"timestamp":1730000000.5,
 "data":{"state":true,"events":["hit","game.start","death"]}}
```

- `state` 为 `true` 才收状态帧；`events` 里写 `"*"` 表示全部事件；
- 连上时默认就订阅状态（不发 subscribe 也能立刻看到画面），
  收到 subscribe 后会**立刻补一份完整状态**，页面刷新不用等下一个变化；
- 状态没变化时不再重复发快照，改为 1 秒一次心跳。

### 4.3 断线与恢复

Mod 断开：`connected=false` + `stale=true`，保留最后状态等重连（§17）。
Mod 自动重连后恢复正常推送；插件侧断线由 SDK 按 250ms / 500ms / 1s / 2s 退避重连。

## 5. 插件开发

```text
plugins/example-overlay/
├─ manifest.json
├─ index.html
├─ main.js
├─ style.css
└─ assets/
```

manifest 字段：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | ✓ | 插件唯一 id，只允许字母数字与 `. _ -`，建议与目录名一致 |
| `name` | ✓ | 显示名 |
| `version` | ✓ | 插件版本 |
| `apiVersion` | ✓ | 高于本端支持的 `1` 会被标记为不可用 |
| `entry` | ✓ | 入口文件，必须存在于插件目录内 |
| `type` | | `overlay` / `panel` / `widget`，默认 `overlay` |
| `permissions` | | 仅作描述，第一版不执行任何插件代码 |
| `defaultSize` | | OBS 建议尺寸 |

页面里通过 SDK 订阅，不要自己写 WebSocket：

```html
<script src="/sdk/protocol.js"></script>
<script src="/sdk/tadofai-client.js"></script>
<script>
  const client = new TadofaiClient({ state: true, events: ["hit", "game.end"] });
  client.on("state", state => { /* 进度、Combo、准确率、BPM ... */ });
  client.on("hit", hit => { /* 判定 + timing 动画 */ });
  client.on("connection", connected => document.body.classList.toggle("offline", !connected));
</script>
```

SDK 负责自动重连、状态缓存、事件订阅、协议版本与错误处理；
插件不能直接读文件系统，只能通过 Core 提供的这些数据。

扫描规则：任何插件坏掉（缺 manifest / 字段缺失 / id 非法 / 入口不存在 / apiVersion 过高）
只会被标记 `status=error` 并附上原因，不会影响 Core 和其他插件；
`plugins.autoReload` 打开时 `GET /api/plugins` 会按需重扫。
所有插件文件访问都做目录穿越校验。

## 6. 配置

`config/config.json`（首次运行自动生成，也可以在管理页面里改）：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `server.host` | `127.0.0.1` | 只监听本机；改它**需要重启进程** |
| `server.port` | `37125` | 同上 |
| `server.stateRate` | `60` | 状态广播频率（1~240），改完立即生效 |
| `plugins.directory` | `plugins` | 相对项目根或绝对路径，改完立即重扫 |
| `plugins.autoReload` | `true` | 列表接口按需重扫插件目录 |
| `logging.level` | `INFO` | 改完立即生效 |

- 插件配置单独放 `config/plugins/{plugin-id}.json`，插件自己不直接读文件；
- 写盘一律「临时文件 + 替换」，中断不会留下半截 JSON；
- 源码运行时配置/日志在项目目录下；打包成 exe 后自动改用用户数据目录，
  也可以用 `TADOFAI_CONFIG_DIR` / `TADOFAI_LOGS_DIR` 指定。

## 7. 稳定性与安全

- 默认只绑定 `127.0.0.1`，不对外暴露；
- **没有开启 CORS**：浏览器同源策略会挡住其他网站读本机服务的返回，这是有意的；
- 每个插件客户端独立队列（默认 4096，满时丢最旧并计数），慢客户端不影响别人；
- 状态只保留最新值（单槽覆盖），60Hz 下不会积压；
- Mod 接收循环只做「更新状态 + 入队」，绝不等待插件发送；
- 单帧上限 256KB（`ws_max_size`），超长/NaN/超深嵌套直接拒绝；
- 插件异常不影响 Core 启动和运行；没有 Mod 时 WebUI 也能正常打开；
- 日志限长（单条 2000 字符）且不记录每一次命中，避免高 BPM 下日志成为瓶颈。

