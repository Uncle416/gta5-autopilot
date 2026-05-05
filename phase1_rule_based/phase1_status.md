# Phase 1 Status — 2026-05-06

## 当前状态：AI 原生驾驶 API 可用 ✅

`TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE` 在 SHVDN Enhanced v3 中**可以工作**，车子能沿道路自动驾驶到地图标记点。

## 环境

- **游戏**: GTA V Enhanced (Epic Games) v1.0.1013.34 @ `D:\Epic Games\GTAVEnhanced`
- **脚本钩子**: ScriptHookVDotNet Enhanced v3 (Chiheb-Bacha fork) @ `deps/ScriptHookVDotNet3.dll`
- **SDK**: .NET Framework 4.8 + x86 .NET SDK 8.0.420 @ `C:\Program Files (x86)\dotnet\`
- **构建**: `PATH="/c/Program Files (x86)/dotnet:$PATH" dotnet build -c Release`
- **部署**: `cp bin/Release/net48/GTA5AutoPilot.dll "D:/Epic Games/GTAVEnhanced/scripts/"`

## SHVDN v3 关键 API 差异

| 功能 | v2 (Legacy) | v3 (Enhanced) |
|------|-------------|---------------|
| UI 字幕 | `UI.ShowSubtitle()` | `GTA.UI.Screen.ShowSubtitle()` |
| 原生调用 | `Function.Call(Hash.XXX)` | 同，但 Hash 枚举用 v3 DLL 的枚举名，不能用原始整数 |
| `using` | `using static GTA.UI;` | `using GTA.UI;` 和 `using GTA.Native;` |
| Math | `Math.X` | `System.Math.X`（因为 `using GTA.Math;` 冲突） |
| Vector3.Normalize | 返回值 | void（原地修改） |
| Entity.Speed | getter | 无，用 `Function.Call` |
| Ped(int) | 构造函数 | 无，用 `Function.Call<Ped>` |
| 路径寻找 | 正常 | **全部崩溃** → PathfindingUtils stubbed |
| 原生 hash | 原始整数可工作 | **必须用 v3 DLL 的 Hash 枚举名** |

## 文件结构

```
GTA5AutoPilot/
├── EntryPoint.cs          ← 主循环：AI任务调度 + 监控
├── Configuration.cs       ← 可调参数
├── DrivingCommand.cs / SensorData.cs / DecisionEngine.cs
├── Modules/
│   ├── EntityDetector.cs      ← 扫描周围车辆/行人 ✅
│   ├── CollisionPredictor.cs  ← 碰撞风险评估
│   ├── VehicleController.cs   ← 手动驾驶（AI模式不需要）
│   ├── PathNavigator.cs       ← stub
│   └── (其他模块暂未使用)
├── Debug/DebugOverlay.cs  ← 3D标记 + 调试字幕
└── NativeWrappers/        ← PathfindingUtils stubbed
```

## 核心代码路径

**AI 驾驶任务**: [EntryPoint.cs:63-68]
```csharp
Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
    driver, vehicle,
    _destination.X, _destination.Y, _destination.Z,
    20f,    // 速度 m/s
    4|16|32|128|2048,  // 驾驶标志
    10f);   // 停止距离
```

**目的地设置**: [EntryPoint.cs:134-155] — 读取地图标记点 via `Hash.GET_FIRST_BLIP_INFO_ID`

**键位**:
- Num0: 开关自动驾驶
- Num1: 开关调试面板
- Num2: 设定目的地（读取地图标记点）
- Num.: 关闭自动驾驶

## 可微调的 AI 参数

- 速度: 当前 20 m/s → 可根据道路类型动态调整
- 驾驶标志: 4(避车)+16(避物)+32(避人)+128(允许逆行)+2048(允许停车)
- 停止距离: 10m
- 附加 API: `SET_DRIVER_ABILITY` / `SET_DRIVER_AGGRESSIVENESS` / `SET_DRIVE_TASK_CRUISE_SPEED` / `SET_DRIVE_TASK_DRIVING_STYLE`

## 已确认的可用 Hash 枚举名

- `Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE`
- `Hash.CLEAR_PED_TASKS`
- `Hash.SET_VEHICLE_STEER_BIAS`
- `Hash.SET_VEHICLE_FORWARD_SPEED`
- `Hash.TASK_VEHICLE_TEMP_ACTION`
- `Hash.GET_FIRST_BLIP_INFO_ID` / `Hash.DOES_BLIP_EXIST` / `Hash.GET_BLIP_INFO_ID_COORD`

## 已确认不可用的原生

- `GET_SCRIPT_TASK_STATUS` (0x2A8F89B15E8DFA53) — 即使 Hash 枚举存在也报 "can't find native"
- 所有路径寻找 API（GET_NTH_CLOSEST_VEHICLE_NODE 等）— 导致游戏崩溃/冻结

## 下一步方向

Phase 1 → AI 原生驾驶的参数微调：
1. 速度根据道路类型动态调整
2. 驾驶风格（conservative → aggressive）
3. 卡死检测和恢复
4. 调试面板增强
