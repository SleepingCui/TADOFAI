# TADOFAI

A Dance of Fire and Ice 的实时数据采集与展示工具链：**游戏内采集 → 本地服务 → Overlay 插件 → OBS**。

用于直播/练习时的实时命中、判定、Timing、Combo、准确率、BPM、进度显示。

```text
ADOFAI 游戏
  ↓ Harmony Patch
TADOFAI.Mod            采集游戏事件，只入队，不做网络 IO（不阻塞游戏线程）
  ↓ ws://127.0.0.1:37125/ws/mod
TADOFAI Core           协议校验 → 状态存储 + 事件总线 → 状态按 60Hz 广播
  ↓ ws://127.0.0.1:37125/ws/client
Overlay 插件 (HTML)    → OBS 浏览器源
```

## 组成

| 目录 | 作用 | 技术栈 |
| --- | --- | --- |
| [`TADOFAI.Mod/`](TADOFAI.Mod/) | 游戏内 Mod：采集并上行数据 | C# / .NET Framework 4.8 / Unity Mod Manager + Harmony |
| [`TADOFAI/`](TADOFAI/) | Core：本地服务、插件系统、WebUI | Python 3.11+ / FastAPI + Uvicorn + Pydantic |

两个子项目各自有 README，细节都在里面。

## 快速开始

### 1. 启动 Core

```bat
cd TADOFAI
python -m venv venv
venv\Scripts\pip install -r requirements.txt
venv\Scripts\python -m core.app
```

### 2. 编译并安装 Mod

```bat
cd TADOFAI.Mod
build.bat
```


### 3. 打开页面

| 用途 | 地址 |
| --- | --- |
| 管理页面（状态、插件、配置） | http://127.0.0.1:37125/app/ |
| 示例 Overlay（OBS 浏览器源，背景透明） | http://127.0.0.1:37125/overlay/example-overlay/ |
| Mod 上行端点 | ws://127.0.0.1:37125/ws/mod |

Mod 的 `ServerUrl` 默认就是 `ws://127.0.0.1:37125/ws/mod`，不用改配置。

## 现在能做什么

- 实时命中判定、Timing、Combo、准确率 / X 准确率、BPM、进度、Miss、曲名
- Coop 按玩家区分进度，命中事件带 `player`
- 游戏内配置面板（UMM 设置窗口），开关即时生效、无需重启
- Core 管理页面：健康状态、实时状态、插件列表、配置编辑
- 示例 Overlay 可直接给 OBS；插件用 SDK 订阅，不用自己写 WebSocket
- 50000 BPM（约 833 次命中/秒）按设计处理：事件逐条发送、状态覆盖式合并、
  限长队列丢最旧并计数、慢客户端互不影响


## 前置条件

**Mod**：.NET SDK（或 VS 2022）+ Unity Mod Manager + 你自己的游戏程序集（放到 `libs/`）。

**Core**：Python 3.11+（开发环境用的是 3.13），依赖见 `TADOFAI/requirements.txt`。


## 许可

[MIT](LICENSE)。
