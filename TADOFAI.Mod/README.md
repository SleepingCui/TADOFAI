# TADOFAI.Mod

ADOFAI 数据采集 Mod，对应 `TADOFAI-Unity-Mod开发指南.md` 的 Mod 部分实现。
只负责把游戏内部事件规范化并通过 WebSocket 发给 TADOFAI Core，不做网页渲染。

```text
Unity 游戏
  ↓
ADOFAI 内部对象
  ↓
Harmony Patch          ← TADOFAI/src/Patches/
  ↓
Collector（规范化）     ← TADOFAI/src/State/Collector.cs
  ↓
有限队列 + 状态覆盖      ← TADOFAI/src/Transport/Transport.cs
  ↓
独立发送线程 → localhost WebSocket → TADOFAI Core
```

## 1. 工程结构

游戏逻辑放在共享程序集，加载器只负责适配入口（指南 §3）。
`TADOFAI.Mod.dll` 不引用任何加载器类型，加 MelonLoader 时不需要动它。

```text
TADOFAI.Mod/
├─ TADOFAI.Mod.sln
├─ Directory.Build.props         公共配置：net4.8、libs 路径、AssemblySearchPaths
├─ Directory.Build.targets       引用检查（缺 DLL 时给出可执行的提示）
├─ libs/                         游戏程序集 + UnityModManager（见 §2）
├─ TADOFAI/                      共享游戏逻辑 → TADOFAI.Mod.dll
│  ├─ TADOFAI.Mod.csproj
│  └─ src/
│     ├─ Main.cs                 Enable / Disable / Update
│     ├─ PatchManager.cs         注册、启用、按自己的 Harmony ID 卸载
│     ├─ GameRefs.cs             集中读取游戏对象
│     ├─ VersionSafe.cs          版本探测 + HitMarginCompat 判定语义
│     ├─ Settings.cs             Settings.xml（原子写入）
│     ├─ SettingsUI.cs           游戏内配置面板（IMGUI）
│     ├─ NumUtil.cs / ModLog.cs
│     ├─ Loader/IModLoader.cs    加载器无关的入口抽象
│     ├─ State/                  Collector、ModState
│     ├─ Protocol/               消息模型 + 手写 JsonWriter
│     ├─ Transport/Transport.cs  有限队列 + 单线程 WebSocket
│     └─ Patches/                6 组 Patch
└─ TADOFAI.Loader.UMM/           Unity Mod Manager 适配 → TADOFAI.Loader.UMM.dll
   ├─ TADOFAI.Loader.UMM.csproj
   ├─ UnityModEntry.cs           UMM 入口（info.json 的 EntryMethod 指向这里）
   ├─ UnityModLoaderAdapter.cs   把 UMM 回调/日志转成 IModLoader
   └─ info.json                  UMM 清单（随生成复制到输出）
```

再加一个加载器时的做法：新建 `TADOFAI.Loader.MelonLoader`，引用 `TADOFAI.Mod`，
实现 `IModLoader` 并在入口里调 `Main.Bind(adapter)` 即可，共享逻辑零改动。

## 2. 编译

依赖 `libs/`（相对解决方案根，可用 `-p:LibsDir=` 覆盖）：

```text
libs\                         ADOFAI 的 "A Dance of Fire and Ice_Data\Managed" 内容
libs\UnityModManager\         Unity Mod Manager 目录（UnityModManager.dll + 0Harmony.dll）
```

```bash
dotnet build TADOFAI.Mod.sln
dotnet build -p:LibsDir="D:\path\to\libs"
```

或者用一键脚本（构建 + 打包）：

```bat
build.bat            :: Release 构建并打包
build.bat Debug      :: Debug 构建并打包
```

脚本产物：

```text
dist\TADOFAI.Mod-<版本>.zip   版本号取自 info.json 的 "Version"
dist\TADOFAI.Mod\             同一份内容的展开目录
```

zip 里是 `TADOFAI.Mod/` 目录，含 `TADOFAI.Loader.UMM.dll`、`TADOFAI.Mod.dll`、`info.json`
（不含 pdb，需要的话把 `build.bat` 里的 `COPY_PDB` 改成 `1`）。

> `build.bat` 必须保持纯 ASCII：UTF-8 的批处理文件里中途 `chcp 65001` 会让 cmd.exe 错行解析。

说明：

- 目标框架 **net4.8**（UMM 的 `UnityModManager.dll` / `0Harmony.dll` 就是 net4.8 编译的）。
- 默认引用 `libs\Assembly-CSharp340.dll`（r340）。换游戏版本：
  `dotnet build -p:GameAssembly=libs\Assembly-CSharp.dll`
- `libs/` 里有 Mono 版 `System.dll`，会被 MSBuild 当成候选程序集抢占框架引用（表现为 `CS7069 ValueTask`）。
  已在 `Directory.Build.props` 把 `{TargetFrameworkDirectory}` 排到 `AssemblySearchPaths` 最前。
- 游戏程序集全部 `Private=false`，不会复制进输出。

## 3. 安装

`build.bat` 产出的 `dist\TADOFAI.Mod\` 就是可部署的 Mod 目录
（直接 `dotnet build` 的话，等价目录是 `TADOFAI.Loader.UMM\bin\<配置>\`）：

```text
TADOFAI.Mod\
├─ TADOFAI.Loader.UMM.dll     UMM 加载的入口程序集（info.json 的 EntryMethod）
├─ TADOFAI.Mod.dll            共享逻辑（依赖，UMM 从 Mod 目录解析）
└─ info.json                  UMM 清单
```

把整个目录复制到：

```text
<游戏目录>\Mods\TADOFAI.Mod\
```

或直接解压 `dist\TADOFAI.Mod-<版本>.zip` 到 `<游戏目录>\Mods\`，效果相同。

## 4. 协议

信封：`{ type, version, id, timestamp, data }`。`timestamp` 是 Mod 采集时刻的单调秒，
Core 不使用网络到达时间。`id` 只给逐条事件，用于检测丢失 / 重复。

- `hello`：连接后第一帧，含 `client` / `modVersion` / `gameVersion` / `capabilities`。
- `state`：状态流，覆盖式，按 `StateRate`（默认 60Hz）合并发送：

```json
{"type":"state","version":1,"timestamp":12.5,"data":{
  "connected":true,"gameState":"playing","droppedEvents":0,"auto":false,"practice":false,"noFail":false,
  "map":{"songName":"","songAuthor":"","difficulty":0},
  "play":{"seq":421,"progress":0.523,"combo":128,"maxCombo":200,"accuracy":0.9912,"xAccuracy":0.98,
          "bpm":180,"timingMs":-1.37,"misses":2},
  "playerSeq":[421,0,0,0,0,0,0,0]}}
```

- `hit`：逐条事件，低延迟优先：`player` / `seq` / `judgement` / `timingMs` / `combo` / `miss`。
- `game.start` / `game.end` / `map.changed` / `state.changed` / `death` / `checkpoint`。

所有数值出口都过 `NumUtil`：NaN / Infinity 写 `null`，字符串截断到 512 字符。

## 5. 线程与高频

```text
Harmony Postfix → ConcurrentQueue<ModMessage> → Transport 线程 → ClientWebSocket
```

- Patch 里只采集 + 入队，不做网络 IO、不序列化；
- 状态用 `Interlocked.Exchange` 覆盖旧值，事件队列上限 `MaxQueuedEvents`，满了丢最旧并计入 `droppedEvents`；
- 同一 WebSocket 只有一个发送线程；单次循环最多连发 256 个事件，避免状态帧被饿死；
- 断线按 250ms / 500ms / 1s / 2s 退避重连，重连后先补一帧完整状态；
- 所有 Patch 异常只禁用对应功能，不让 Mod 退出。

50000 BPM（约 833 events/s）下 Patch 路径无锁竞争、无分配热点。

## 6. Patch 清单

| 分组 | 目标 | 说明 |
| --- | --- | --- |
| GameLifecyclePatches | `scnGame.Play`、`scrPressToStart.ShowText`、`scrUIController.WipeToBlack`、`scnEditor.ResetScene`、`scrController.StartLoadingScene`、`SceneManager.Internal_SceneUnloaded` | 开局 / 结束 / 场景切换 |
| PlayPatches | `StateBehaviour.ChangeState(Enum)`、`scrPlanet.MoveToNextFloor`、`RDC.auto` setter | 死亡 / 通关 / 进度 / Auto |
| V141Patches | `scrMarginTracker.AddHit`、`scrPlayer.Hit` | r141+ 命中与 BPM |
| V141AccuracyPatch | `scrMarginTracker.CalculatePercentAcc` | 准确率（注册时按开关门控） |
| V136Patches | `scrMistakesManager.AddHit`、`scrController.Hit` | r136 旧路径 |
| TimingPatches | `scrMisc.GetHitMargin` / `GetHitMarginInDeg` / `GetHitMarginInSec` | 始终挂载，开关在方法内部判断 |
| DebugPatches | `scrShowIfDebug.Update` | 隐藏 Debug 文本 |

版本选择：`VersionSafe` 用**能力探测**（反射找 `scrMarginTracker`、`scrMisc.GetHitMarginInSec`）
而不是版本号字符串，探测不到的类型对应的 Patch 在应用时失败并被跳过，只禁用该功能。
实际 r340 上会被跳过的是 `scrMisc.GetHitMargin` 和两个 r136 Patch（该版本已不存在这些方法）。

## 7. 判定语义

`HitMarginCompat` 在启动时按**枚举名**建立映射，绝不写死数值（不同版本判定枚举会位移）：

- 完美核心（保持 Combo）：名字含 `perfect` 且不是 `Auto`；
  r340 上即 `EarlyPerfect` / `PerfectMinus` / `PerfectPlus` / `LatePerfect` / `XPerfect`；
- 断 Combo：名字含 `fail` / `miss` / `overload`；
  r340 上即 `FailMiss` / `FailOverload` / `FailedFloor`；
- `Multipress` / `OverPress` / `Midspin` 按「非完美但不中断 Combo」处理；
- `Auto` 不参与 Combo 计数。

如果名字解析失败，**不做数值兜底**，直接禁用 Combo / Miss 统计并报错，避免给出错误判定。
`judgement` 字段直接使用游戏枚举名（如 `PerfectPlus`、`FailMiss`），Core 侧按字符串处理。

> 注意：r340 的 `HitMargin` 没有单独的 `Perfect`，而是 `PerfectMinus` / `PerfectPlus`。
> 上面的分类是本 Mod 的策略，若要和游戏内显示完全一致，按需调整这一处即可。

## 8. Combo / 准确率来源

- Combo 与 Miss 由 `Collector` 自己维护，不读游戏内部 Combo 字段；
- 准确率读 `scrMarginTracker.percentAcc` / `percentXAcc`：优先
  `scrPlayerManager.instance.allPlayers[player].marginTracker`，兜底
  `scrMistakesManager.marginTrackers[player]`（r340 里该字段是静态的）；
  数值按 0~1 与 0~100 两种版本差异做了归一（`NumUtil.NormalizeAccuracy`）；
- BPM 用 `scrFloor.entryTime` 相邻差计算（`60 / (delta / pitch)`），异常值回退到 `scrConductor.bpm`；
- Coop 下 `MoveToNextFloor` 的参数不代表当前玩家，按 `scrPlanet.player.playerID` 区分。

## 9. 配置

游戏内：UMM 主界面（默认 `Ctrl+F10`）→ Mods → TADOFAI → 设置，打开的就是 `SettingsUI` 画的面板。

- **所有开关即时生效，不用重启。** Patch 全部无条件挂载，数值判断放在 Patch 方法内部读
  `Settings.Current`；用 `PatchManager.Register` 的 gate 在启动时决定挂不挂，会让改动要重启才生效。
- 服务地址改完要点「重新连接」：WebSocket 已经建连后不会自己换目标。
- 面板底部是实时状态（连接 / 丢弃事件 / seq / Combo / 准确率 / BPM / Timing），排查时不用另开工具。
- 改动 1 秒防抖自动写盘，关闭面板时也写盘；也可以手动点「保存配置」。

配置文件：`<游戏目录>\Mods\TADOFAI.Mod\Settings.xml`（首次启动生成，手改也行）：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `ServerUrl` | `ws://127.0.0.1:37125/ws/mod` | 只接受 `ws://` / `wss://` |
| `AutoReconnect` | `true` | 断线退避重连 |
| `StateRate` | `60` | 状态合并发送频率，1~120 |
| `MaxQueuedEvents` | `4096` | 事件队列上限，64~65536 |
| `SendHits` / `ShowAccuracy` / `ShowTiming` / `ShowBpm` / `SendProgress` | `true` | 各数据源开关 |
| `TrackPracticeMode` | `false` | 练习模式是否上报（默认短路，与指南一致） |
| `HideDebugText` | `false` | 隐藏游戏内 Debug 文本 |
| `LogVerbose` | `false` | 调试日志 |

保存前会跑一次 `Sanitize()`：非法 `ServerUrl` 回退默认、`StateRate` 夹到 1~120、
`MaxQueuedEvents` 夹到 64~65536，所以面板上看到的就是真正生效的值。
