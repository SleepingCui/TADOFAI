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
- Combo 是**完美连击**：只有中心完美判定「完美 / X 完美 / ± 完美」才 +1，中旋与 Auto 不变，其余清零；
  标题在「完美 / 连击」之间切换、数值按完美率红→黄→绿渐变（与 JipperOverlayer 的显示格式一致）
- 判定分布直方图、X-Score 与满值 / 潜力值、判定格数（已判 / 剩余）、死亡数、检查点数、纯完美
- 时间轴（音乐时间 / 地图时间 / 两者总长）、KPS、瓦片 BPM / 当前 BPM / 伪 BPM、暂停、游戏帧率
- 判定窗毫秒（Counted / Perfect / Pure / X-Perfect），由 Mod 按当前 BPM 与倍速实时算出
- 关卡信息：谱面哈希、曲师、谱师、格数、时长、判定难度、起始位置、倍速、判定宽度
- Coop 逐玩家数据（进度 / 准确率 / X-Score / 死亡 / 判定分布 / 颜色），单人局同一结构
- 判定名与权重由 Mod 在 `hello` 里上报，网页端不写死任何判定数值，跨游戏版本自动适配
- 过检查点事件（`checkpoint`）；能力位（`capabilities`）让插件知道当前游戏版本支持哪些数据
- 游戏内配置面板（UMM 设置窗口），开关即时生效、无需重启
- Core 管理页面：健康状态、实时状态、插件列表、配置编辑
- 连接长期保持：Mod 侧有接收循环处理 WebSocket 控制帧，不会因为不回 PONG 被 Core 每 40 秒断开一次
- 示例 Overlay 可直接给 OBS；插件用 SDK 订阅，不用自己写 WebSocket
- 50000 BPM（约 833 次命中/秒）按设计处理：事件逐条发送、状态覆盖式合并、
  限长队列丢最旧并计数、慢客户端互不影响

### 已知边界

下面这些是**游戏进程内的行为**，靠上报无法在网页端复刻，Mod 仍会自己在游戏里处理：

- 隐藏游戏内 Debug 文本、去掉 beta 水印、改写等级名、Auto 文本重排、注入自定义字体
  （`HideDebugText` 等 Harmony 补丁改的是游戏自己的 UI 对象）；
- 上报的 `fps` 是**游戏**帧率，不是网页帧率；
- 判定面板配色暂不联动游戏内设置，由前端 SDK 固定给出。

判定窗的参数含义依赖游戏版本，所以 Mod 同时上报角度边界与分母，网页端可自行复核换算。


## 前置条件

**Mod**：.NET SDK（或 VS 2022）+ Unity Mod Manager + 你自己的游戏程序集（放到 `libs/`）。

**Core**：Python 3.11+（开发环境用的是 3.13），依赖见 `TADOFAI/requirements.txt`。


## 许可

[MIT](LICENSE)。
