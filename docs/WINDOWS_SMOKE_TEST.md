# Windows 打通测试（协议 v2）

目的：用最小代价确认"外部程序 → TCP → mod → 游戏 → 事件回传"这条线在真游戏里成立，再往上写 Agent。

本轮 C# 改动**没有编译过**。第 1 步很可能出现编译错误，这是预期内的，把错误信息记下来修掉即可。

## 改了什么

| 文件 | 改动 |
| --- | --- |
| `Networking/TcpCommandServer.cs` | 重写。网络线程只解析并入队，不再调用任何游戏函数；长连接；可向客户端回写 |
| `EntryPoint.cs` | 在 `OnTick` 里执行 TCP 请求并回执；发事件和每秒状态心跳；到达与卡住分离；距离改 2D；计时改为毫秒时钟；行驶中丢车、死亡会上报 |
| `WaypointManager.cs` | 新增 `Stuck` 状态、追加、跳过、重试；站点带 `Id` |
| `Configuration.cs` | 新增一组以毫秒计的看门狗参数 |

保持不变：驾驶任务的下发方式和参数、热键、嵌入式 LlamaSharp 路径、旧版 TCP 消息。

行为变化（需要留意）：

- 卡住不再被当作到达并移出队列。离目标 40 米内停住 18 秒算"就近到达"（事件里 `near: true`）；其他位置连续 90 秒没有位移进入 `stuck`，等 Num1 重试或 Num2 停止。
- 任务被 Num0 暂停后，Num2 现在可以停止并清空它（以前无效）。
- TCP 的 `set_waypoints` 在行驶中到达时，会先停掉旧的驾驶任务再换队列（以前旧任务会继续跑）。
- 事件会追加写入 `scripts/GTA5AutoPilot.log`。

用到的、原代码里没出现过的 SHVDN 成员只有一个：`Script.Aborted` 事件（脚本重载时释放端口）。如果它在你的 SHVDN 版本里不存在，删掉构造函数里的 `Aborted += OnAborted;` 一行即可。

## 步骤

每一步记录：通过 / 失败 / 未测，以及日志文件名。

| # | 操作 | 预期 | 结果 |
| --- | --- | --- | --- |
| 1 | 按 README 的命令编译并部署 DLL | 编译通过 | |
| 2 | 进游戏，不连任何客户端，用地图选点 + Num0 跑一次两站路线 | 与改动前的基线一致：能出发、到站停车、Num1 继续、终点停止 | |
| 3 | `python tools/smoke_test.py state` | 打印出 `ok: true` 和当前坐标 | |
| 4 | `python tools/smoke_test.py negative` | 全部 PASS，车不动，队列不变 | |
| 5 | 把车停在路边，`python tools/smoke_test.py capture A`；开到另一处再 `capture B` | `tools/captured_places.json` 里有两个点 | |
| 6 | 开到别处，`python tools/smoke_test.py run A B` | 依次出现 `leg_started` → `arrived` → `continue ok` → `arrived(final)` → `mission_completed`，车确实停在 A 和 B | |
| 7 | 行驶中另开一个终端 `python tools/smoke_test.py stop` | 车的驾驶任务停止，第 6 步的终端显示 `CANCELLED` | |
| 8 | 行驶中打开地图停留 10 秒再关闭 | 期间无心跳；关闭后恢复，不误报卡住 | |
| 9 | 行驶中下车 | 收到 `blocked: no_vehicle`；重新上车后收到 `unblocked` 并继续 | |
| 10 | 把车顶在墙上或开进死路让它动不了 | 约 90 秒后收到 `stuck`，站点仍在队列里 | |
| 11 | 在长红灯前观察 | 不被误判为 `stuck` 或"就近到达" | |
| 12 | 游戏里按 Insert 重载脚本两次，再跑第 3 步 | 端口能重新监听，`state` 正常 | |

第 10、11 步用来标定 `Configuration.cs` 里的阈值，参数表见 [PROTOCOL.md](PROTOCOL.md) 末尾。

## 不依赖游戏的检查（Mac / Windows 都能跑）

```bash
python -m unittest discover -s tests -v                 # 18 个协议测试，对模拟游戏端
python tools/mock_game.py --port 21556 --speedup 20     # 手动起一个模拟游戏端
python tools/smoke_test.py run A B                      # 另开终端，对着它跑
```

这些只能证明客户端和协议规则自洽，不能证明真 mod 的行为。模拟端的校验规则是照着 `EntryPoint.cs` 写的，两边不一致时以真 mod 为准。

## 测完之后

把 `tools/logs/` 里第 6、9、10 步的 `session-*.jsonl` 提交进仓库。下一步模拟端会改成回放这些真实记录，Agent 就在它上面开发。
