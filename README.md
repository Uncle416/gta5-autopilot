# GTA5 AutoPilot

GTA V Enhanced 的自然语言多点导航自动驾驶 mod。利用游戏原生 AI 驾驶 API，支持红绿灯合规、多途经点导航、驾驶风格调节，以及嵌入式 LLM 自然语言输入。

https://github.com/user-attachments/assets/00000000-0000-0000-0000-000000000000

## 功能

- **原生 AI 驾驶** — 使用 `TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE`，NPC 式驾驶体验
- **多点导航** — 通过地图标记依次添加途经点，自动按顺序到达
- **红绿灯合规** — 等红灯、排队，不乱绕
- **到达自动刹车** — 途经点/终点自动刹停，自然减速
- **驾驶风格调节** — 4 档可调：Sloth / Base / Aggressive / Mad Max，行驶中实时切换
- **NLU 自然语言** — 输入"去机场然后回家"，自动解析地名加入队列（需下载 GGUF 模型）
- **TCP 远程控制** — 外部程序可通过 TCP 发送导航指令

## 环境要求

- **GTA V Enhanced** (Epic Games) v1.0.1013.34
- [Script Hook V](http://www.dev-c.com/gta5/scripthookv/) (Enhanced 版)
- [Script Hook V .NET Enhanced v3](https://github.com/nitanmarcel/ScriptHookVDotNet/releases)

> Steam Enhanced 版理论兼容，未测试。Legacy 版未测试。**不支持 FiveM / GTA Online。**

## 安装

1. 安装 Script Hook V + SHVDN Enhanced v3
2. 将 `scripts/` 目录下的所有文件复制到 `D:\Epic Games\GTAVEnhanced\scripts\`
3. （可选）下载 [Qwen 0.5B GGUF](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF) 模型，放到 `scripts/models/qwen-0.5b-q4.gguf` 以启用 NLU 功能

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

### NLU 自然语言 (需模型)
1. **Num6** 打开输入框
2. 输入 "去机场然后回家" → 回车
3. 自动解析地名并加入队列

## 驾驶风格

| 档位 | Aggressiveness | 说明 |
|------|---------------|------|
| Sloth | 0.0 | 最怂，路口犹豫，绝对不闯红灯 |
| Base | 0.1 | 温和，日常驾驶 |
| Aggressive | 0.15 | 默认，灵活但守规矩 |
| Mad Max | 0.2 | 激进，快速变道 |

行驶中随时按 **Num4/Num5** 切换，立即生效。

## TCP 接口

外部程序可通过 TCP 发送 JSON 指令：

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
- 路径寻找 API 在 Enhanced 版全部不可用，完全依赖原生 AI 驾驶
- 选道/绕路行为不可精确控制，受 GTA V AI 内部逻辑限制

## 构建 (开发者)

```bash
# 需要 .NET SDK 8.0 x86
PATH="/c/Program Files (x86)/dotnet:$PATH" dotnet build -c Release

# 部署
cp bin/Release/net48/GTA5AutoPilot.dll "D:/Epic Games/GTAVEnhanced/scripts/"
```

## License

MIT
