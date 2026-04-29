# GTA V 自动驾驶 Mod 开发文档

## 项目概述

在 GTA V 中开发一个自动驾驶 mod，分两个阶段：

- **Phase 1（基于规则）**：利用游戏内部 API 数据——道路节点、红绿灯状态、周围实体等，实现基于规则的自驾驶系统
- **Phase 2（基于视觉）**：基于纯摄像头画面的端到端自动驾驶，对标市面上视觉大模型的自动驾驶效果

---

## 技术栈

### Phase 1

| 层级 | 技术 |
|------|------|
| Mod 加载 | Script Hook V (C++ .asi 插件) |
| 脚本 API | Script Hook V .NET (C#, .NET Framework 4.8) |
| 构建 | Visual Studio 2022 / MSBuild |
| UI | LemonUI |
| 序列化 | Google.Protobuf + Newtonsoft.Json |

### Phase 2

| 层级 | 技术 |
|------|------|
| 语言 | Python 3.10+ |
| 帧捕获 | DXGI Desktop Duplication API (C++ .asi 插件) |
| 计算机视觉 | OpenCV 4.x |
| 深度学习 | PyTorch 2.x |
| 数据管线 | NumPy, h5py, pandas |
| 进程通信 | TCP + Protobuf |

---

## 目录结构

```
GTA/
├── README.md
├── .gitignore
│
├── phase1_rule_based/
│   ├── GTA5AutoPilot.sln
│   └── GTA5AutoPilot/
│       ├── GTA5AutoPilot.csproj
│       ├── EntryPoint.cs              # SHVDN 入口，OnTick 主循环
│       ├── Configuration.cs           # 全局可调参数
│       ├── SensorData.cs              # 传感器数据结构体定义
│       ├── DrivingCommand.cs          # 控制指令结构体
│       ├── DecisionEngine.cs          # 决策状态机核心
│       ├── Modules/
│       │   ├── VehicleController.cs   # 车辆底层控制
│       │   ├── PathNavigator.cs       # 道路节点导航
│       │   ├── LaneKeeper.cs          # 车道保持 PID
│       │   ├── EntityDetector.cs      # 周围实体检测
│       │   ├── CollisionPredictor.cs  # 碰撞风险评估
│       │   ├── TrafficLightDetector.cs # 红绿灯状态检测
│       │   ├── SpeedGovernor.cs       # 速度调控
│       │   └── IntersectionHandler.cs # 路口处理
│       ├── Telemetry/
│       │   ├── TelemetryExporter.cs   # TCP 遥测导出
│       │   └── telemetry.proto
│       ├── Debug/
│       │   ├── DebugOverlay.cs        # 屏幕调试信息
│       │   └── DebugCommands.cs       # 键盘快捷键
│       └── NativeWrappers/
│           ├── TrafficLightUtils.cs   # 红绿灯检测工具
│           ├── PathfindingUtils.cs    # 导航工具
│           └── VehicleControlUtils.cs # 控制工具
│
├── phase2_vision/
│   ├── pyproject.toml
│   ├── gta_driving/
│   │   ├── capture/          # 帧捕获 & 数据接收
│   │   ├── perception/       # OpenCV + YOLO 感知
│   │   ├── policy/           # PyTorch 模型 & 训练
│   │   ├── control/          # 控制指令 & 安全过滤
│   │   ├── data_pipeline/    # 数据录制 & 增强
│   │   └── utils/            # 工具函数
│   ├── scripts/              # CLI 入口脚本
│   └── tests/                # 测试
│
└── bridge/
    └── proto/
        └── telemetry.proto   # 共享通信协议
```

---

## Phase 1：基于规则的自动驾驶

### 架构概览

每帧 (~16ms, 60FPS) 执行一次完整的感知-规划-控制循环：

```
Frame Tick
  │
  ├─► PathNavigator      → 获取下一个路点 + 道路朝向
  ├─► EntityDetector     → 扫描周围车辆/行人
  ├─► TrafficLightDetector → 检测红绿灯状态
  ├─► CollisionPredictor → 计算 TTC 碰撞风险
  ├─► LaneKeeper         → 车道中心偏移纠正
  ├─► SpeedGovernor      → 根据路段/路况计算目标速度
  ├─► IntersectionHandler → 路口检测 & 转弯规划
  │
  ├─► DecisionEngine     → FSM 状态转移 → 输出 DrivingCommand
  │
  └─► VehicleController  → 执行油门/刹车/转向
```

### 决策状态机 (FSM)

```
                    ┌──────────┐
                    │ CRUISING │ ←─────────────────────────┐
                    └─────┬────┘                           │
                          │                                │
          ┌───────────────┼───────────────┐                │
          │               │               │                │
          ▼               ▼               ▼                │
    ST0PPING_AT      EVADING         TURNING               │
    _LIGHT           (避障)          (转弯)                │
          │               │               │                │
          ▼               └───────────────┼────────────────┘
    WAITING_AT                            │
    _LIGHT                                │
          │                               │
          └───────────────────────────────┘
```

**状态说明：**

| 状态 | 触发条件 | 行为 |
|------|---------|------|
| CRUISING | 默认状态 | 按路点行驶，保持车道和速度 |
| STOPPING_AT_LIGHT | 前方检测到红灯/黄灯 | 减速至停车线前 |
| WAITING_AT_LIGHT | 已停在红灯前 | 等待变绿 |
| EVADING | TTC < 阈值，碰撞风险高 | 紧急刹车或变道 |
| TURNING | 到达需转弯的路口 | 减速并执行左转/右转 |
| STUCK | 长时间未移动 | 尝试倒车摆脱 |
| EMERGENCY_STOP | 碰撞迫在眉睫 | 全力刹车 |

### 关键 GTA V Native 函数

**导航 & 道路：**
- `GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING(out pos, out heading)` — 最近道路节点
- `GET_VEHICLE_NODE_PROPERTIES(out density, out flags)` — 节点属性
- `GET_CLOSEST_ROAD(...)` — 道路车道信息
- `GENERATE_DIRECTIONS_TO_COORD(...)` — GPS 导航方向

**车辆控制：**
- `SET_VEHICLE_FORWARD_SPEED(vehicle, speed)` — 设定前进速度 (m/s)
- `SET_VEHICLE_STEER_BIAS(vehicle, value)` — 转向偏置
- `TASK_VEHICLE_TEMP_ACTION(driver, vehicle, action, time)` — 急刹/加速

**实体检测：**
- `World.GetNearbyVehicles(pos, radius)` — 周围车辆
- `World.GetNearbyPeds(pos, radius)` — 周围行人
- `GET_ENTITY_SPEED(entity)` — 实体速度

**红绿灯（最大难点）：**
- GTA V 原生 API 对红绿灯状态几乎没有可靠支持
- 方法 A：扫描交通灯 prop 模型 (model hash)，通过 bone 状态判断红/绿
- 方法 B：在已知路口位置做信号周期追踪
- 回退：检测失败的交叉口视为停车标志

---

## Phase 2：视觉感知 + 规则决策（混合架构，修订版）

### 核心理念

**不替代 Phase 1，而是用视觉感知增强它。** 感知从摄像头画面提取，决策和控制复用 Phase 1 的成熟 FSM。

```
旧方案: Camera → [端到端神经网络] → steer/throttle/brake  (废弃)
新方案: Camera → [视觉感知管线] → 检测结果 → [Phase 1 FSM] → 控制
```

### 为什么新方案更好

| 维度 | 旧方案（端到端） | 新方案（感知+规则） |
|------|-----------------|-------------------|
| 数据需求 | 100-500 小时驾驶数据 | 几千张标注截图 |
| 训练难度 | 学习驾驶策略（极难） | 学习目标检测（成熟技术） |
| 预训练模型 | 不可用 | YOLO 可直接迁移 |
| 可解释性 | 黑盒 | 能看到检测到了什么 |
| 安全兜底 | 需要额外安全层 | Phase 1 FSM 天然兜底 |

### 数据流

```
GTA V 第三人称画面 (车尾视角)
        │
        ▼
Python 视觉感知管线 (10-20 FPS)
        │
        ├─► Lane Detector (OpenCV Canny+Hough)  → 车道线位置、偏移
        ├─► Object Detector (YOLOv8 fine-tuned) → 车辆、行人 bbox + 距离估算
        └─► Traffic Light Classifier (CNN)      → 红/黄/绿/无
        │
        ▼
VisionPerceptionBridge → SensorData 格式
        │
        ▼ (TCP JSON)
C# VisionDataReceiver
        │
        ▼
[Phase 1 DecisionEngine FSM] (已有，不变)
        │
        ▼
[Phase 1 VehicleController] (已有，不变)
```

### 视觉感知 vs 游戏 API 分工

| 感知项 | 来源 | 说明 |
|--------|------|------|
| 前方车辆/行人 | 视觉 (YOLO) | 可能比游戏 API 更通用 |
| 红绿灯状态 | 视觉 (CNN) | 可能比 prop bone 扫描更可靠 |
| 车道线位置 | 视觉 (OpenCV) | 直接检测，不依赖节点朝向 |
| 道路导航/路径 | 游戏 API | 视觉无法替代 GPS |
| 路口检测 | 游戏 API | 视觉不可行 |
| 距离估算 | 视觉 (bbox 几何) | 精度 ±5m，够用 |

### PerceptionMode 切换

按 Numpad6 可循环切换三种感知模式：
- **GameAPI**：Phase 1 默认，全部用游戏 API
- **Vision**：实体检测和红绿灯用视觉，导航保留游戏 API
- **Hybrid**：视觉为主，视觉失败时自动回退到游戏 API

### 训练数据方案
- 运行 Phase 1 自动驾驶 → 记录截图 + Phase 1 的 SensorData（自动标注）
- YOLO fine-tune：用 Phase 1 EntityDetector 结果作为 bbox ground truth
- 红绿灯分类器：用 Phase 1 TrafficLightDetector 结果作为标签
- 数据量目标：10-20 条路线 × 5 分钟 ≈ 60k-120k 帧

---

## 实施步骤

| 步骤 | 内容 | 状态 |
|------|------|------|
| 1-6 | Phase 1 完整实现 + 代码 review | ✅ 已完成 |
| 7 | 数据采集管线（collect_dataset.py + 屏幕抓取） | ✅ 代码就绪 |
| 8 | 视觉感知管线（YOLO + 车道 + 红绿灯） | ✅ 代码就绪 |
| 9 | YOLO fine-tune + 红绿灯分类器训练 | ⏳ 待运行 |
| 10 | 端到端测试（视觉感知 → FSM → 控制） | ⏳ 待测试 |

---

## 可行性评估

| 难度 | 事项 |
|------|------|
| ✅ | 高速公路车道跟随 |
| ✅ | 车辆/行人视觉检测（YOLO 开箱即用） |
| ✅ | 红绿灯视觉识别（可能比 Phase 1 bone 扫描更可靠） |
| ✅ | 数据采集（Phase 1 自动标注，无需人工） |
| ⚠️ | 城区车道检测（传统 CV 可能不够） |
| ⚠️ | 单目距离估算（精度 ±5m） |
| ❌ | 纯视觉导航/GPS（不可行，保留游戏 API） |

---

## 依赖安装

### GTA V 环境（Windows）
1. 安装 [Script Hook V](http://www.dev-c.com/gta5/scripthookv/)
2. 安装 [Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases)
3. 将编译后的 `GTA5AutoPilot.dll` 放入 `GTA5/scripts/` 目录

### Python 环境
```bash
cd phase2_vision
python -m venv .venv
source .venv/bin/activate  # Windows: .venv\Scripts\activate
pip install torch torchvision opencv-python h5py numpy pandas \
            protobuf pillow pyyaml tensorboard
```
