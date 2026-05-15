# GTA V 自然语言自动驾驶 Mod 开发文档

## 项目概述

在 GTA V 中开发一个**自然语言多点导航自动驾驶 mod**。用户通过终端对话界面向内置的小模型输入目的地描述（如"先去机场，再去海滩，最后回家"），模型解析出地点和途经顺序后，由游戏内置 AI 驾驶 API 依次开车到达每个途经点，到达后询问用户是否继续。

### 核心理念

**直接利用 GTA V 自身的自动驾驶API**（`TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE`），在上面包装一层自然语言理解（NLU）+ 多点导航管理。

---

## 技术栈

| 层级 | 技术 |
|------|------|
| Mod 加载 | Script Hook V |
| 脚本 API | Script Hook V .NET Enhanced v3 (C#, .NET Framework 4.8) |
| 序列化 | Newtonsoft.Json (JSON over TCP) |
| NLU 模型 | Ollama + Qwen2.5-1.5B (本地运行) |
| NLU 侧车 | Python 3.10+ |
| 通信 | TCP localhost (端口 21556) |

---

## 目录结构

```
GTA/
├── README.md
├── .gitignore
├── PLAN.md
│
├── phase1_rule_based/
│   ├── phase1_status.md
│   └── GTA5AutoPilot/
│       ├── GTA5AutoPilot.csproj
│       ├── EntryPoint.cs              # SHVDN 入口，OnTick 主循环 + 键位
│       ├── Configuration.cs           # 全局可调参数
│       ├── SensorData.cs              # 传感器数据结构体
│       ├── DrivingCommand.cs          # 控制指令结构体
│       ├── DecisionEngine.cs          # 决策状态机 (预留)
│       ├── WaypointManager.cs         # 途经点队列 & 导航状态机 ★
│       ├── Networking/
│       │   └── TcpCommandServer.cs    # TCP JSON 指令接收 ★
│       ├── Modules/
│       │   ├── VehicleController.cs   # 车辆底层控制 (预留)
│       │   ├── PathNavigator.cs       # 道路节点导航 (stub)
│       │   ├── EntityDetector.cs      # 周围实体检测
│       │   ├── CollisionPredictor.cs  # 碰撞风险评估
│       │   ├── LaneKeeper.cs          # 车道保持 PID (预留)
│       │   ├── SpeedGovernor.cs       # 速度调控 (预留)
│       │   ├── TrafficLightDetector.cs # 红绿灯检测 (预留)
│       │   └── IntersectionHandler.cs # 路口处理 (预留)
│       ├── Debug/
│       │   ├── DebugOverlay.cs        # 屏幕调试信息
│       │   └── DebugCommands.cs       # 键盘快捷键
│       ├── NativeWrappers/
│       │   ├── TrafficLightUtils.cs
│       │   ├── PathfindingUtils.cs    # (stubbed — SHVDN v3 不支持)
│       │   └── VehicleControlUtils.cs
│       └── Telemetry/
│           └── TelemetryExporter.cs   # TCP 遥测导出 (预留)
│
└── nlu_sidecar/
    ├── requirements.txt               # ollama
    ├── config.py                      # 模型 & 端口配置
    ├── chat_server.py                 # 终端对话入口
    ├── nlu_engine.py                  # NLU 核心：自然语言 → 途经点
    ├── tcp_client.py                  # TCP 连接 C# mod
    └── gta_gazetteer.json             # GTA V 地名 → 坐标词典
```

★ = 本版本新增/核心文件

---

## 架构详解

### 数据流

```
User Terminal
    │  "先去机场，再去海滩，最后回家"
    ▼
chat_server.py
    │
    ▼
nlu_engine.py
    ├─► Ollama + Qwen2.5-1.5B → 提取地名列表
    └─► gta_gazetteer.json → 地名模糊匹配 → 坐标
    │
    ▼ JSON: {"type":"set_waypoints","waypoints":[...]}
tcp_client.py → TCP 127.0.0.1:21556
    │
    ▼
TcpCommandServer.cs (后台线程)
    │ OnWaypointsReceived 事件
    ▼
WaypointManager.cs
    │ LoadWaypoints + StartNavigation
    ▼
EntryPoint.OnTick
    │ 每帧执行:
    ├─► WaypointManager.State == Driving
    │     ├─► TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE (每2秒)
    │     └─► 距离检测 → 到达?
    │           ├─ 非最终站 → Waiting 状态 → 询问用户 → Num7继续 / Num8停车
    │           └─ 最终站 → 显示"到达终点" → 停止
    │
    └─► WaypointManager.State == Waiting
          └─► 等待按键或TCP指令
```

### 导航状态机

```
                    ┌──────────┐
                    │   IDLE   │
                    └────┬─────┘
                         │ StartNavigation()
                         ▼
                    ┌──────────┐
              ┌────►│ DRIVING  │
              │     └────┬─────┘
              │          │ 到达当前途经点
              │          ▼
              │     ┌──────────┐    是最终站?
              │     │ ARRIVED  │────────► IDLE (导航结束)
              │     └────┬─────┘
              │          │ 非最终站
              │          ▼
              │     ┌──────────┐
              │     │ WAITING  │
              │     └────┬─────┘
              │          │
              │     ┌────┴────┐
              │     │         │
              │  Num7/TCP  Num8/TCP
              │  "continue" "stop"
              │     │         │
              └─────┘         ▼
                            IDLE
```

---

## 关键 GTA V Native 函数

| 函数 | 用途 |
|------|------|
| `TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE` | 核心 AI 驾驶，接受坐标+速度+标志 |
| `CLEAR_PED_TASKS` | 停止自动驾驶 |
| `GET_FIRST_BLIP_INFO_ID` / `GET_BLIP_INFO_ID_COORD` | 读取地图标记点（备用） |
| `World.GetNearbyVehicles` / `World.GetNearbyPeds` | 实体检测（调试用） |

### SHVDN v3 已知限制

所有路径寻找 API (`GET_NTH_CLOSEST_VEHICLE_NODE` 等) 在 Enhanced v3 中会导致崩溃/冻结，因此完全依赖原生 AI 驾驶 API 处理导航和避障。

---

## 键位绑定

| 按键 | 功能 |
|------|------|
| **NumPad0** | 开关自动驾驶 |
| **NumPad1** | 开关调试面板 |
| **NumPad2** | 从地图标记点设目的地（备用） |
| **NumPad7** | **是** — 继续到下一站 |
| **NumPad8** | **否** — 停止导航 |
| **小数点 (.)** | 紧急停止 |

---

## NLU Sidecar 使用说明

### 安装

```bash
# 1. 安装 Ollama
# macOS: brew install ollama
# Windows: https://ollama.com/download

# 2. 拉取小模型
ollama pull qwen2.5:1.5b

# 3. 安装 Python 依赖
cd nlu_sidecar
pip install -r requirements.txt
```

### 使用

```bash
# 终端 1: 启动 GTA V（mod 会自动加载）

# 终端 2: 启动 NLU 对话
python nlu_sidecar/chat_server.py

> 先去洛圣都国际机场，再去威斯普奇海滩，最后回麦克家
正在解析...
已识别 3 个导航点:
  1. 洛圣都国际机场 (-1267, 2995)
  2. 威斯普奇海滩 (-1550, -890)
  3. 麦克家 (-812, 176)
发送到游戏? (回车确认 / n 取消):
已发送到游戏！在游戏中按 Numpad0 启动自动驾驶
```

到达每个途经点后，游戏中会显示提示，按 **NumPad7** 继续或 **NumPad8** 停止。也可以在终端输入 `/yes` 或 `/no`。

---

## 构建 & 部署 (Windows)

### 环境
- **游戏**: GTA V Enhanced (Epic Games) @ `D:\Epic Games\GTAVEnhanced`
- **脚本钩子**: ScriptHookVDotNet Enhanced v3

### 构建命令
```bash
cd phase1_rule_based
PATH="/c/Program Files (x86)/dotnet:$PATH" dotnet build -c Release
```

### 部署命令
```bash
cp bin/Release/net48/GTA5AutoPilot.dll "D:/Epic Games/GTAVEnhanced/scripts/"
cp bin/Release/net48/Newtonsoft.Json.dll "D:/Epic Games/GTAVEnhanced/scripts/"
```

### 依赖
- .NET Framework 4.8
- x86 .NET SDK 8.0.420
- Newtonsoft.Json 13.0.3 (NuGet)

---

## 地名扩展

编辑 `nlu_sidecar/gta_gazetteer.json` 添加更多地名。坐标可在游戏中通过调试面板或第三方工具获取。格式:

```json
{
  "地名": {"x": -1267, "y": 2995, "z": 15}
}
```

---

## 已知限制

| 限制 | 说明 |
|------|------|
| 地名覆盖 | 初始 ~25 个常见地标，需手动扩展 |
| 小模型精度 | Qwen2.5-1.5B 可能偶尔误识别地名 |
| AI 驾驶质量 | 完全依赖 GTA V 原生 AI，可能绕路或卡住 |
| 到达检测 | 纯距离判断 (15m)，无法判断是否真正到达建筑物 |
| 跨平台 | C# mod 仅 Windows，NLU sidecar 可跨平台 |
