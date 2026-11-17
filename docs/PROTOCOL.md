# GTA5AutoPilot TCP 协议 v2

状态：2026-11-17 起草。C# 端实现尚未在 Windows / GTA V 中验证，本文件是两端的约定，实测后如有出入以实测为准并回改本文件。

## 传输

- 地址：`127.0.0.1:21556`（mod 只监听回环地址，Agent 必须和游戏跑在同一台 Windows 上）。
- 编码：UTF-8，每行一个 JSON 对象，以 `\n` 结尾。
- 长连接：客户端连上后保持连接，持续读取。mod 向所有已连接客户端广播事件和状态心跳。最多 8 个客户端。
- mod 只在游戏脚本线程里执行指令。地图、暂停菜单打开时脚本线程挂起，期间没有回执、事件和心跳。**心跳中断不等于断线**，客户端应以 TCP 连接状态和回执超时为准。

## 消息分类

| 方向 | `type` | 含义 |
| --- | --- | --- |
| 客户端 → mod | 见"请求" | 指令，建议都带 `id` |
| mod → 客户端 | `ack` | 对某条请求的回执，只发给发请求的客户端 |
| mod → 所有客户端 | `event` | 导航状态发生变化 |
| mod → 所有客户端 | `state` | 每秒一次的状态心跳 |

三个层次必须分开看待：socket 写入成功 ≠ 收到 `ack` ≠ 收到 `arrived` 事件。

## 请求

所有请求可带 `id`（字符串，回执里原样返回）。控制类请求可带 `mission_id`，与 mod 当前任务不一致时被拒绝（`stale_mission`），用来丢弃过期指令。

### `hello` / `get_state`

```json
{"id": "1", "type": "get_state"}
```

回执里带完整 `state` 对象（字段同状态心跳）。

### `set_route`

```json
{
  "id": "2", "type": "set_route",
  "mission_id": "m-20261117-001",
  "mode": "replace",
  "autostart": true,
  "stops": [
    {"id": "lsia", "name": "洛圣都国际机场", "x": -1037.0, "y": -2737.0, "z": 13.9},
    {"id": "beach", "name": "威斯普奇海滩", "x": -1550.0, "y": -890.0, "z": 5.0}
  ]
}
```

- `mode`：`replace`（默认，清空旧队列，旧任务会收到 `cancelled` 事件）或 `append`（追加到队尾，保留当前任务和 `mission_id`）。
- `mission_id`：`replace` 时成为新任务 ID，省略则由 mod 生成；`append` 时若提供则必须等于当前任务 ID。
- `autostart`：默认 `false`，只装载不出发。
- `stops`：1 到 20 个。每个必须有数值型 `x`、`y`、`z`，且在地图范围内（x ∈ [-4500, 5000]，y ∈ [-4500, 8500]，z ∈ [-200, 1500]）。任何一个不合格则整条请求被拒绝，队列不变。

### 控制指令

| `type` | 前置状态 | 作用 |
| --- | --- | --- |
| `start` | 队列非空，玩家在车内 | 从第一站出发；若任务被热键暂停（Num0 关闭），则恢复 |
| `continue` | `waiting` | 中途站停靠后前往下一站 |
| `retry` | `stuck` | 对当前站重新下发驾驶任务 |
| `skip` | 队列非空 | 放弃当前目标站，前往下一站；没有下一站则任务结束 |
| `stop` | 任意 | 停车并清空队列，幂等 |

### 旧版兼容

`{"type":"set_waypoints","waypoints":[...]}` 等价于 `set_route` + `replace` + 不自动出发。
`{"type":"command","action":"continue"|"stop"}` 仍可用。旧客户端发完即断开，收不到回执。

## 回执 `ack`

```json
{"type": "ack", "id": "2", "ok": true, "mission_id": "m-20261117-001", "nav": "driving"}
{"type": "ack", "id": "2", "ok": false, "error": "invalid_stop", "message": "stops[1]: y out of range"}
```

| `error` | 含义 |
| --- | --- |
| `bad_json` | 不是合法 JSON 对象，或单行超过 64 KB |
| `unknown_type` | `type` 缺失或不认识 |
| `invalid_route` | `stops` 缺失、为空或超过 20 个，或 `mode` 不合法 |
| `invalid_stop` | 某一站坐标缺失、非数值或越界 |
| `stale_mission` | `mission_id` 与当前任务不一致 |
| `invalid_state` | 当前导航状态下不能执行该指令 |
| `empty_queue` | 队列为空 |
| `no_vehicle` | 玩家不在车内 |
| `player_dead` | 玩家角色不可用或已死亡 |
| `internal_error` | mod 处理请求时抛出异常，`message` 为异常信息 |

`ok: true` 只表示指令已被接受并生效到队列或状态机，不表示车已到达。

## 事件 `event`

公共字段：`event`、`seq`（递增）、`clock_ms`（mod 内部时钟，游戏暂停时不走）、`mission_id`。

| `event` | 附加字段 | 触发时机 |
| --- | --- | --- |
| `route_loaded` | `mode`（`replace` / `append` / `map`）, `queue` | 队列被 `set_route`、旧版指令或地图选点修改 |
| `leg_started` | `stop`, `retry`（第几次重试，0 表示首次）, `remaining` | 开始驶向某一站 |
| `arrived` | `stop`, `near`, `dist_m`, `final`, `next` | 车已停稳。`near: true` 表示没进入到达半径，但已在目标 40 米内停住无法更近 |
| `stuck` | `stop`, `dist_m`, `stationary_ms`, `retries` | 远离目标且长时间没有位移，mod 已停止驾驶任务，等待 `retry` / `skip` / `stop` |
| `skipped` | `stop`, `remaining` | 某一站被跳过 |
| `blocked` | `reason`（`no_vehicle` / `vehicle_destroyed` / `player_dead`） | 行驶中前置条件丢失，任务保留并暂停 |
| `unblocked` | 无 | 前置条件恢复，mod 会重新下发驾驶任务 |
| `paused` / `resumed` | 无 | 玩家用热键关闭 / 重新打开自动驾驶，队列保留 |
| `mission_completed` | `skipped`（被跳过的站数） | 最后一站到达或被跳过 |
| `cancelled` | `reason`（`stop` / `replaced` / `hotkey`） | 任务被取消 |

`stop` 对象形如 `{"id": "lsia", "name": "洛圣都国际机场"}`。

## 状态心跳 `state`

约每秒一次，仅在有客户端连接时发送。

```json
{
  "type": "state", "seq": 42, "clock_ms": 183000,
  "nav": "driving", "autopilot": true, "braking": false,
  "mission_id": "m-20261117-001", "blocked": null,
  "in_vehicle": true, "pos": {"x": -800.1, "y": -1200.5, "z": 20.3}, "speed_mps": 18.2,
  "current": {"id": "lsia", "name": "洛圣都国际机场", "dist_m": 1650.0},
  "queue": [{"id": "lsia", "name": "洛圣都国际机场"}, {"id": "beach", "name": "威斯普奇海滩"}],
  "style": 0.15
}
```

`nav` 取值：`idle`、`driving`、`waiting`（已到中途站，等 `continue`）、`stuck`。距离均为水平（2D）距离。

## 需要在 Windows 上标定的参数

均在 `Configuration.cs`，当前值是未经实测的起点：

| 参数 | 当前值 | 说明 |
| --- | --- | --- |
| `ProgressCheckMs` | 6000 | 进度检查窗口 |
| `TaskSafetyReissueMs` | 30000 | 兜底重发驾驶任务的间隔 |
| `StationaryMoveThreshold` | 5 m | 一个窗口内位移小于它算"没动" |
| `NearStationaryArriveMs` | 18000 | 目标 40 米内连续没动这么久，按"就近到达"处理 |
| `StuckStationaryMs` | 90000 | 连续没动这么久判为卡住。须大于最长红灯和正常堵车时间 |
| `ArrivalBrakeTimeoutMs` | 8000 | 到站刹车超时，超时后强制速度归零 |
