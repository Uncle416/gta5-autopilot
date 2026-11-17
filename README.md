# GTA5 AutoPilot

GTA V Enhanced 多途经点导航原型，正在向「LLM 导航 Agent」推进：用户用自然语言说要去哪、怎么改行程，外部 Agent 解析成经过校验的地点序列，通过 TCP 交给 mod，由游戏原生 NPC AI 依次开过去，并对各种失败给出明确反馈。

**状态更新：2026-11-17。** 本轮新增 TCP 协议 v2、C# mod 改动和外部 Python Agent。**C# 改动没有编译过，也没有进游戏运行；Agent 没有调用过真实的 Qwen / DeepSeek，地点坐标全部未实测。** 下表是唯一的状态口径。

## 当前完成范围

以实际验证记录为准，不把“仓库有代码”当作“功能已跑通”。

| 能力 | 代码 | 验证情况 |
| --- | --- | --- |
| 地图选点、途经点队列、NPC 式驾驶、驾驶风格调节 | 有 | 作者此前在游戏中跑通；本轮改动后未重新运行 |
| TCP 协议 v2：回执、事件、每秒状态心跳、过期指令拒绝（[docs/PROTOCOL.md](docs/PROTOCOL.md)） | 有（C#） | 未编译、未进游戏；18 个协议测试对模拟游戏端通过 |
| 到达与卡住分离、就近到达、追加 / 跳过 / 重试 | 有（C#） | 同上；看门狗阈值（90 秒判卡住、40 米内 18 秒算就近到达）是未实测的起点 |
| 外部导航 Agent（`agent/`，[docs/AGENT.md](docs/AGENT.md)） | 有（Python，仅标准库） | 33 个测试用假模型对模拟游戏端通过；未接真实模型、未连真游戏 |
| 自然语言理解准确率 | 有评测集（60 条） | 无模型的关键词模式 22/60；DeepSeek / Qwen 待测 |
| 地点库 `agent/places.json`（51 个地点） | 有 | 坐标全部未实测，Agent 默认拒绝导航到未实测地点 |
| 嵌入式 NLU（LlamaSharp + GGUF） | 有 | 冻结，不再开发，也未删除 |
| `nlu_sidecar/`（旧的外部 NLU） | 有 | 将由 `agent/` 取代；已知 `extract_yes_no` 子串误判问题未修 |
| 卡住后的阶梯恢复（倒车、放宽驾驶标志）、游戏内输入框转发给 Agent | 无 | 待 Windows 实测后再做 |

旧的 [PLAN.md](PLAN.md) 和 [phase1_status.md](phase1_rule_based/phase1_status.md) 只作历史参考，与本页冲突时以本页和当前源码为准。

## 架构

```text
用户（终端里说话）
  → agent/  外部 Python 进程，与游戏在同一台 Windows 上
       固定说法（停下 / 取消 / 继续 / 跳过 / 重试 / 到哪了）本地精确匹配，不经过模型
       其余交给云端 LLM（DeepSeek / Qwen，OpenAI 兼容接口，两家顺序回退）
       模型只能从 4 个工具里选一个，只给地点 id；坐标由程序查 places.json
       任何路线改动先复述、等用户确认
  → TCP 127.0.0.1:21556（协议 v2，每行一个 JSON）
  → C# mod：只做确定性执行，在游戏脚本线程里改队列、下发驾驶任务、上报事件
  → GTA V 原生 AI 驾驶（TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE）
  → 事件 / 心跳回到 Agent，Agent 转述并决定下一步（自动继续、追问、重试或停下）
```

- 底层驾驶是游戏 NPC AI，不是本项目实现的驾驶算法；地点序列不等于道路路径规划。
- 模型不看游戏画面，项目不宣称 VLM / VLA / 端到端视觉导航。
- 三层状态必须分开：socket 写入成功 ≠ 收到 `ack` ≠ 收到 `arrived` 事件。
- API key 只从环境变量读取（`DEEPSEEK_API_KEY`、`DASHSCOPE_API_KEY`），不要写进代码或提交。

各种失败（未知地点、多义地点、改口、模型超时、断线、无回执、卡住、下车等）的具体处理见 [docs/AGENT.md](docs/AGENT.md) 的对照表。

## 快速开始

只需要 Python 3.9+，不用装依赖。

```bash
# 测试（不需要游戏，不需要 key）
python -m unittest discover -s tests -v          # 51 个：18 协议 + 33 Agent

# 量一下真实模型的理解准确率（不需要游戏）
export DEEPSEEK_API_KEY=...
export DASHSCOPE_API_KEY=...
python -m agent.eval --provider deepseek --out eval-deepseek.jsonl
python -m agent.eval --provider qwen --out eval-qwen.jsonl

# 对模拟游戏端聊天
python tools/mock_game.py --port 21777 --speedup 20             # 终端 A
python -m agent.cli --port 21777 --allow-unverified             # 终端 B

# 真游戏（Windows，mod 已加载）
python tools/smoke_test.py state                 # 先确认协议打通，完整步骤见 docs/WINDOWS_SMOKE_TEST.md
python -m agent.capture --list                   # 哪些地点还没实测坐标
python -m agent.capture casino                   # 把车停到该地点路边后执行
python -m agent.cli
```

## 仓库结构

| 路径 | 内容 |
| --- | --- |
| `phase1_rule_based/GTA5AutoPilot/` | C# mod（SHVDN v3）。本轮改了 `Networking/TcpCommandServer.cs`、`EntryPoint.cs`、`WaypointManager.cs`、`Configuration.cs` |
| `agent/` | 外部导航 Agent：地点库、LLM 客户端、意图解析、对话与执行、终端、采点、评测 |
| `tools/` | 协议客户端 `gta_client.py`、Windows 打通脚本 `smoke_test.py`、模拟游戏端 `mock_game.py`；运行日志写到 `tools/logs/`（已忽略） |
| `tests/` | 协议与 Agent 的单元 / 集成测试 |
| `docs/` | `PROTOCOL.md` 协议约定、`AGENT.md` Agent 行为、`WINDOWS_SMOKE_TEST.md` Windows 验收表 |
| `nlu_sidecar/` | 旧外部 NLU，待 `agent/` 实测后移除 |

## 下一步

按顺序，每步都要留下可复现的记录（日期、commit、版本、输入、预期、实际、日志位置、通过 / 失败 / 未测）。

1. **Mac，无需游戏**：配好 key 跑 `agent.eval`，记录两家通过率和失败用例，据此调提示词。
2. **Mac，可选**：装 .NET SDK 试编译 C#，先把语法级错误清掉。
3. **Windows**：按 [docs/WINDOWS_SMOKE_TEST.md](docs/WINDOWS_SMOKE_TEST.md) 编译并跑 12 项验收，回传 `tools/logs/session-*.jsonl` 和 `scripts/GTA5AutoPilot.log`；用 `agent.capture` 采常用地点的坐标。
4. 模拟游戏端改为回放真实日志，让开发测试贴近真实行为。
5. mod 内的阶梯恢复；游戏内输入框转发给 Agent；`nlu_sidecar/` 下线。

已知未处理：Agent 持锁等回执时，“停下”最多可能延迟 3 秒；队列里有地图选点（无 id 的站）时只能追加或整体替换，不能重排。

## 两台电脑如何交接

本 README 是共享的状态入口，不依赖聊天历史。

- Mac：整理需求、写代码与文档、跑不需要游戏的测试和评测，提交并推送。
- Windows：先保护本机未提交的工作，再拉取；按验收表实测，把结果、日志和修复提交推送，并更新本页的完成范围与下一步。
- 不要提交 API key、Codex / Claude 配置、登录凭据或本地缓存。

### 最近一次交接

- 日期：2026-11-17（Mac 端，Claude 协助）。
- 本轮：TCP 协议 v2 与对应的 C# 改动；外部导航 Agent、地点库、60 条评测集；`tools/`、`tests/`、`docs/` 三处新文件；README 重写为当前状态。
- 验证：51 个 Python 测试通过（假模型 + 模拟游戏端）。C# 未编译、未进游戏；未调用真实模型；坐标未实测。
- 下一步：见上方“下一步”第 1、3 项。

#### 更早的交接

- 2026-10-22：只更新 README，明确职责、状态、目标与验收步骤；未构建、未运行。

---

## 现有操作参考

以下保留原项目使用入口，不代表每一项已完成端到端验证。驾驶风格只是传给原生 AI 的配置，不保证交通行为。

## 环境要求

- **GTA V Enhanced** (Epic Games) v1.0.1013.34
- [Script Hook V](http://www.dev-c.com/gta5/scripthookv/) (Enhanced 版)
- [Script Hook V .NET Enhanced v3](https://github.com/nitanmarcel/ScriptHookVDotNet/releases)

> Steam Enhanced 版理论兼容，未测试。Legacy 版未测试。**不支持 FiveM / GTA Online。**

## 安装（原有说明，需 Windows 复核）

1. 安装 Script Hook V + SHVDN Enhanced v3
2. 将 `scripts/` 目录下的所有文件复制到 `D:\Epic Games\GTAVEnhanced\scripts\`
3. （可选）下载 [Qwen 0.5B GGUF](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF) 模型，放到 `scripts/models/qwen-0.5b-q4.gguf` 用于验证嵌入式 NLU；下载模型不代表链路已跑通

## 键位

| 按键 | 功能 |
|------|------|
| **Num0** | 开关自动驾驶 |
| **Num1** | 继续到下一站 |
| **Num2** | 停止导航 |
| **Num3** | 按住显示途经队列 |
| **Num4** | 驾驶风格 -（更怂） |
| **Num5** | 驾驶风格 +（更猛） |
| **Num6** | NLU 自然语言输入 |
| **小数点 (.)** | 紧急停止 |

## 使用流程

### 添加途经点
1. 打开地图 → 左键点位置 → **Enter 确认** → 关闭地图
2. 标记自动加入队列，小地图 GPS 自动更新
3. 重复以上步骤添加更多途经点

### 开始导航
1. 上车 → **Num0** 开启自动驾驶
2. 到达途经点后自动刹停，屏幕提示 **Num1 继续 / Num2 停止**
3. 到达最终目的地后自动停止

### 游戏内 NLU 输入框（旧入口，走嵌入式 NLU，已冻结）

自然语言导航请用外部 Agent（见上方“快速开始”）。游戏内输入框目前不会转发给 Agent。

1. **Num6** 打开输入框
2. 输入 "去机场然后回家" → 回车
3. 预期解析地名并加入队列；需核验解析结果、加载行为及实际驾驶结果

## 驾驶风格

| 档位 | Aggressiveness | 说明 |
|------|---------------|------|
| Sloth | 0.0 | 保守驾驶配置；实际交通行为需验证 |
| Base | 0.1 | 温和，日常驾驶 |
| Aggressive | 0.15 | 默认配置；交通合规不作保证 |
| Mad Max | 0.2 | 激进，快速变道 |

行驶中随时按 **Num4/Num5** 切换，立即生效。

## TCP 接口

协议 v2（带回执、事件和状态心跳，代码已写、待 Windows 验证）见 [docs/PROTOCOL.md](docs/PROTOCOL.md)。下面的旧版消息仍然兼容：

```
端口: 21556
```

```json
{"type": "set_waypoints", "waypoints": [{"name": "机场", "x": -1267, "y": 2995, "z": 15}]}
{"type": "command", "action": "continue"}
{"type": "command", "action": "stop"}
```

## 已知问题

- GTA V 同时只能保留一个地图标记，无法在地图内多选途经点（需反复开关地图添加）
- 当前工程的道路节点导航相关实现为 stub；实际驾驶依赖原生 AI，不据此推断所有版本的 API 均不可用
- 选道/绕路行为不可精确控制，受 GTA V AI 内部逻辑限制

## 构建 (开发者)

```bash
# 在 Windows Git Bash 中，从仓库根目录进入项目目录
cd phase1_rule_based/GTA5AutoPilot
# 原项目使用 .NET SDK 8.0 x86；需先配置 deps/ScriptHookVDotNet3.dll
PATH="/c/Program Files (x86)/dotnet:$PATH" dotnet build -c Release

# 部署
cp bin/Release/net48/GTA5AutoPilot.dll "D:/Epic Games/GTAVEnhanced/scripts/"
```

