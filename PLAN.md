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

## Phase 2：基于视觉的自动驾驶

### 数据管线

```
[GTA V 运行 Phase 1 Mod]
        │
        ├──► TCP Protobuf 遥测 ──► Python recorder.py
        │                              │
        └──► Shared Memory 帧 ──► Python frame_server.py
                                       │
                                       ▼
                                HDF5 数据集文件
                                /session_001.h5
                                ├─ /frames      (N, 3, H, W)
                                ├─ /speed       (N,)
                                ├─ /steer       (N,)
                                ├─ /throttle    (N,)
                                ├─ /brake       (N,)
                                └─ /position    (N, 3)
```

### 模型架构

**基线：PilotNet** (NVIDIA 端到端 CNN)
- 输入：前置摄像头图像 (200×66)
- 5 层卷积 + 3 层全连接
- 输出：steer angle + throttle

**推荐：ResNet-18 + CIL** (条件模仿学习)
- ResNet-18 骨干 (ImageNet 预训练，微调)
- 条件分支：straight / left / right 各有独立输出头
- 时序：5 帧 → Transformer Encoder → 控制输出
- 输出：steer、throttle、brake

### 安全过滤 (Safety Filter)

```
Model 输出
    │
    ├─ 碰撞检查：预测的转向/油门是否会导致碰撞？
    │   └─ 是 → 使用 Phase 1 逻辑覆盖
    │
    ├─ 置信度检查：模型输出置信度是否足够？
    │   └─ 否 → 与 Phase 1 输出加权混合
    │
    └─ 物理约束：steer/throttle 是否在物理可行范围内？
        └─ 否 → 裁剪到安全范围
```

### 训练流程

1. **数据收集**：用 Phase 1 自动驾驶，录制 50-100 小时
2. **预处理**：resize 到 200×66，归一化，时序滑窗
3. **数据增强**：亮度/对比度变化、水平翻转、阴影叠加
4. **训练**：AdamW, lr=1e-4, CosineAnnealing, early stopping
5. **评估**：MAE 转向角、干预率（安全过滤触发频率）

---

## 实施步骤

| 步骤 | 内容 | 优先级 |
|------|------|--------|
| 1 | 项目脚手架：创建 VS 解决方案 + Python 骨架 + Protobuf schema | P0 |
| 2 | 车辆控制原语：VehicleController + PathNavigator + LaneKeeper | P0 |
| 3 | 实体检测：EntityDetector + CollisionPredictor | P0 |
| 4 | 红绿灯检测：TrafficLightDetector（最大风险项） | P0 |
| 5 | 路口处理：IntersectionHandler | P0 |
| 6 | 决策引擎集成：DecisionEngine FSM + SpeedGovernor + DebugOverlay | P0 |
| 7 | 遥测录制：TelemetryExporter + C++ 屏幕抓取 + Python recorder | P1 |
| 8 | 感知管线：lane_detector + object_detector + traffic_light_classifier | P1 |
| 9 | 训练管线：models + dataset + trainer | P1 |
| 10 | 实时推理：inference_engine + safety_filter + vehicle_commander | P1 |

---

## 可行性评估

### 可达成的
- ✅ 高速公路车道跟随 (95%+ 可靠性)
- ✅ 自适应巡航和基础避障
- ✅ Phase 1 自动驾驶数据录制
- ✅ PilotNet 级别端到端转向预测

### 有挑战的
- ⚠️ 城区红绿灯检测 (70-85%)
- ⚠️ 复杂城区驾驶（密集车辆/行人）
- ⚠️ 多车型泛化（不同车的转向比、刹车距离不同）

### 极困难的
- ❌ 环岛和复杂立交
- ❌ 雨天+夜间纯视觉驾驶
- ❌ 训练数据未覆盖区域的泛化

### MVP 建议
- 3 条预设路线（高速环路、城区环路、混合路线）
- Phase 1 完整实现并调参
- Phase 2 数据收集可用，离线训练可跑
- 安全过滤始终生效

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
