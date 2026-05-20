# GTA V 自然语言自动驾驶 Mod 开发文档

## 项目概述

在 GTA V 中开发一个**自然语言多点导航自动驾驶 mod**。利用 GTA V 原生 AI 驾驶 API（`TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE`），配合嵌入式 LLM（LlamaSharp + GGUF）实现自然语言导航解析，支持多点途经、红绿灯合规、自动刹车停靠。

### 核心理念

**直接利用 GTA V 自身的自动驾驶 API**，在上面包装一层自然语言理解（NLU）+ 多点导航管理。NPC 式驾驶体验：任务只发一次，仅卡住时重发。

---

## 技术栈

| 层级 | 技术 |
|------|------|
| Mod 加载 | Script Hook V |
| 脚本 API | Script Hook V .NET Enhanced v3 (C#, .NET Framework 4.8) |
| 序列化 | Newtonsoft.Json (JSON over TCP) |
| NLU 模型 | LlamaSharp 0.11.2 + Qwen 0.5B GGUF (嵌入式，无需外部进程) |
| 通信 | TCP localhost (端口 21556) |

---

## 目录结构

```
GTA/
├── README.md
├── .gitignore
├── PLAN.md
│
└── phase1_rule_based/
    └── GTA5AutoPilot/
        ├── GTA5AutoPilot.csproj
        ├── EntryPoint.cs              # SHVDN 入口，OnTick 主循环 + 键位
        ├── Configuration.cs           # 全局可调参数
        ├── SensorData.cs              # 传感器数据结构体
        ├── DrivingCommand.cs          # 控制指令结构体
        ├── WaypointManager.cs         # 途经点队列 & 导航状态机
        ├── Networking/
        │   └── TcpCommandServer.cs    # TCP JSON 指令接收
        ├── Modules/
        │   ├── VehicleController.cs   # 车辆底层控制
        │   ├── PathNavigator.cs       # 道路节点导航 (stub)
        │   ├── EntityDetector.cs      # 周围实体检测
        │   ├── CollisionPredictor.cs  # 碰撞风险评估
        │   └── LlamaNavigator.cs      # LLM 导航解析 (LlamaSharp 嵌入式)
        ├── Debug/
        │   └── DebugOverlay.cs        # 屏幕调试信息 (默认关闭)
        └── NativeWrappers/
            └── PathfindingUtils.cs    # (stubbed — SHVDN v3 不支持)
```

---

## 架构详解

### 数据流

```
User
    │  Num9 → 键盘输入 "去机场然后回家"
    ▼
OnScreenKeyboard (GTA 原生)
    │
    ▼
LlamaNavigator.cs (后台线程)
    ├─► LlamaSharp + Qwen 0.5B GGUF → 提取地名列表
    └─► 内置 Gazetteer (30+ 地名) → 模糊匹配 → 坐标
    │
    ▼
WaypointManager.LoadWaypoints + StartNavigation
    │
    ▼
EntryPoint.OnTick
    │ 每帧执行:
    ├─► WaypointManager.State == Driving
    │     ├─► TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE (首发一次，卡住才重发)
    │     ├─► 进度检测 (每6s) → 区分等红灯 vs 真卡住
    │     └─► 距离检测 → 到达?
    │           ├─ 非最终站 → 刹车循环 → Waiting → Num7继续 / Num8停止
    │           └─ 最终站 → 刹车停稳 → 显示"到达终点"
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

## 关键驾驶参数

| 参数 | 值 | 说明 |
|------|-----|------|
| AiDrivingSpeed | 25f (90 km/h) | 巡航速度 |
| Aggressiveness | 0.15 | 驾驶激进程度 |
| AiDrivingFlags | `1|2|8|16|32|64|128|2048` | 见下方说明 |
| DestinationArrivalRadius | 25f | 到达判定距离 |
| AiTaskReissueInterval | 300 | 安全兜底重发间隔 |

### Driving Flags 说明

| Flag | 值 | 作用 |
|------|-----|------|
| 1 | 1 | 前方有车停止 |
| 2 | 2 | 前方有行人停止 |
| 8 | 8 | 只避开空车（不绕排队车辆） |
| 16 | 16 | 避开行人 |
| 32 | 32 | 避开障碍物 |
| 64 | 64 | 不绕玩家行人 |
| 128 | **128** | **停红灯**（Enhanced 版确认） |
| 2048 | 2048 | 到达时可停车 |

---

## 键位绑定

| 按键 | 功能 |
|------|------|
| **NumPad0** | 开关自动驾驶 |
| **NumPad1** | 继续到下一站 |
| **NumPad2** | 停止导航 |
| **NumPad3** | 按住显示途经队列 |
| **NumPad9** | NLU 自然语言输入（需下载 GGUF 模型） |
| **小数点 (.)** | 紧急停止 |

---

## 自然语言导航 (Num9)

代码已就绪，唯一阻塞项：**下载 Qwen 0.5B GGUF 模型文件**，放到 `D:\Epic Games\GTAVEnhanced\scripts\models\qwen-0.5b-q4.gguf`。

下载后，在游戏中按 **Num9** → 弹出输入框 → 输入 "去机场然后回家" → 自动解析地名加入队列。

---

## 构建 & 部署 (Windows)

### 环境
- **游戏**: GTA V Enhanced (Epic Games) @ `D:\Epic Games\GTAVEnhanced`
- **脚本钩子**: ScriptHookVDotNet Enhanced v3

### 构建命令
```bash
PATH="/c/Program Files (x86)/dotnet:$PATH" dotnet build -c Release "d:/GTA_drive/gta5-autopilot/phase1_rule_based/GTA5AutoPilot/GTA5AutoPilot.csproj"
```

### 部署命令
```bash
cp "d:/GTA_drive/gta5-autopilot/phase1_rule_based/GTA5AutoPilot/bin/Release/net48/GTA5AutoPilot.dll" "D:/Epic Games/GTAVEnhanced/scripts/"
```

### 依赖
- .NET Framework 4.8
- x86 .NET SDK 8.0.420
- Newtonsoft.Json 13.0.3 (NuGet)
- LlamaSharp 0.11.2 + Backend.Cpu (NuGet)
- llama.dll (AVX2) @ scripts/

---

## 已知限制

| 限制 | 说明 |
|------|------|
| 地图多选途经点 | GTA V 同时只能有一个地图标记，脚本在暂停界面不运行 |
| 地名覆盖 | 内置 ~30 个常见地标，需手动扩展 Gazetteer |
| 小模型精度 | Qwen 0.5B 可能偶尔误识别地名 |
| AI 驾驶质量 | 完全依赖 GTA V 原生 AI，选道/绕路不可精确控制 |
| 路径寻找 API | Enhanced v3 全部不可用，完全依赖原生 AI 驾驶 |
| LLM 模型 | 需用户自行下载 GGUF 文件 |
