# AutoPlay Mod — 杀戮尖塔 2 AI 自动打牌

[English](../README.md)

一款 AI 驱动的杀戮尖塔 2 全自动游玩 Mod——战斗出牌、卡牌奖励、地图导航、事件选择、休息站、商店和宝箱全部自主处理。

支持三个 LLM 提供商（**Claude**、**GPT**、**Gemini**），采用统一的 Agent 架构。Agent 对每个决策进行推理，从经验中学习，并在跨局游戏中积累持久化知识。

## 功能特性

- **全流程自动化** — 处理从开始到死亡/胜利的每个游戏阶段
- **多模型 Agent 模式** — Claude、GPT、Gemini 均支持完整的工具调用对话
- **三层记忆体系** — 局内上下文（当前 run）、战斗反思（近期战斗）、持久化知识（跨 run）
- **战后复盘** — 分析血量损失原因、卡组短板、战略失误
- **后台学习** — 异步提取卡牌/敌人/事件知识，不阻塞游戏
- **局内上下文追踪** — 自动维护 archetype、目标和关键决策，整局不断线
- **脚本模式** — 零延迟 Lua 脚本战斗，可自定义策略
- **随时切换** — 按 F10（可配置）启用/禁用

## 模式说明

### 脚本模式（Script）

使用 Lua 脚本做战斗决策。速度快、确定性强。非战斗决策使用简单启发式规则。

适用场景：测试、快速跑局、低成本运行。

### Agent 模式

使用 LLM 通过多轮工具调用对话为**所有**决策提供智能支持。Agent 可以查询记忆、检查卡组、保存观察，在战斗和非战斗流程中分别维持连续对话线程，局级上下文自动传递。

适用场景：智能对局、策略研究、AI 实验。

**Agent 能力：**
- 阅读卡牌描述、敌人意图和增益/减益效果
- 查询持久化记忆，回忆之前遇到的卡牌、敌人、事件和遗物知识
- 学到新知识后主动保存观察（含推理过程）
- 自动注入上下文：战斗时注入敌人知识，选卡时注入卡牌知识，全程注入 run 策略
- 战后执行结构化复盘（血量损失分析、卡组缺口识别）
- 全局维护局内上下文（archetype、目标、关键决策）——战斗之间不失忆
- 超时时自动回退到脚本策略，下回合恢复 Agent

## 安装与配置

### 前置要求

- 杀戮尖塔 2（v0.99.1+），已启用 Mod 支持
- Agent 模式需要：Claude、GPT 或 Gemini 的 API 密钥

### 安装步骤

将 `AutoPlayMod` 文件夹复制到 STS2 的 mods 目录：

```
{Steam}/steamapps/common/Slay the Spire 2/mods/AutoPlayMod/
```

内容：
```
AutoPlayMod/
├── AutoPlayMod.dll
├── AutoPlayMod.pck
├── MoonSharp.Interpreter.dll
├── mod_manifest.json
├── autoplay_config.json
└── scripts/
    └── default_strategy.lua
```

### 配置文件

编辑 `autoplay_config.json`：

```json
{
  "mode": "agent",
  "script_path": "scripts/default_strategy.lua",
  "toggle_hotkey": "F10",
  "llm_provider": "claude",
  "llm_api_key": "你的API密钥",
  "llm_model": "claude-sonnet-4-6",
  "llm_base_url": "",
  "action_delay_ms": 300,
  "llm_timeout_ms": 60000
}
```

| 字段 | 说明 |
|------|------|
| `mode` | `"script"` 或 `"agent"` |
| `script_path` | Lua 策略脚本路径（脚本模式） |
| `toggle_hotkey` | 切换快捷键（默认 F10） |
| `llm_provider` | `"claude"`、`"gpt"` 或 `"gemini"` — 三者均支持完整 Agent 模式 |
| `llm_api_key` | 你的 API 密钥 |
| `llm_model` | 模型 ID（如 `claude-sonnet-4-6`、`gpt-4o`、`gemini-2.5-flash`） |
| `llm_base_url` | 自定义端点 URL（仅 GPT，用于 Azure/本地代理） |
| `action_delay_ms` | 操作间延迟（毫秒） |
| `llm_timeout_ms` | API 超时（毫秒，默认 60000） |

### 使用方法

1. 启动杀戮尖塔 2
2. 开始或继续一局游戏
3. 按 **F10** 切换自动游玩
4. Mod 接管后续所有操作

## 从源码构建

### 前置工具

- .NET 9.0 SDK
- Godot 4.5.1 Mono（导出 PCK）
- 已安装杀戮尖塔 2

### 构建

创建 `local.props` 配置路径后执行 `dotnet build`：

```xml
<Project>
  <PropertyGroup>
    <STS2GamePath>D:\SteamLibrary\steamapps\common\Slay the Spire 2</STS2GamePath>
    <GodotExePath>D:\Godot\Godot.exe</GodotExePath>
  </PropertyGroup>
</Project>
```

### 项目结构

```
mod/
├── Core/                          # 游戏循环与状态机
│   ├── AutoPlayer.cs              # 主控制器：事件驱动战斗 + 阶段检测 poll
│   ├── GamePhase.cs               # Phase 枚举 + GamePhaseDetector
│   ├── PhaseHandler.cs            # 统一阶段处理器
│   ├── RunContext.cs              # 内存中的局上下文
│   ├── RunContextExtractor.cs     # 后台 LLM 自动提取局上下文
│   ├── BattleStateCollector.cs    # 为 Agent 提取战斗状态
│   └── ActionExecutor.cs         # 执行出牌和使用药水
├── Agent/                         # LLM 集成（provider 无关）
│   ├── ILlmClient.cs             # 统一接口：文本 + 工具 + 对话
│   ├── UnifiedGameAgent.cs        # 多轮对话循环 + 互斥锁
│   ├── AgentStrategy.cs          # 战斗策略（含超时回退）
│   ├── GameAgentAdvisor.cs        # 非战斗决策
│   ├── BattleJournal.cs          # 战后复盘（线程安全，后台 AI）
│   ├── Prompts.cs                # 系统提示词
│   ├── ToolDefinitions.cs        # 所有工具定义
│   ├── JsonUtils.cs              # 共享 JSON/Markdown 提取
│   └── Clients/
│       ├── ClaudeClient.cs       # Claude API
│       ├── GptClient.cs          # OpenAI 兼容 API
│       └── GeminiClient.cs       # Google Gemini API
├── Memory/                        # 持久化知识系统
│   ├── MemoryStore.cs            # 文件存储、搜索、注入评分
│   ├── MemoryEntry.cs            # 数据类
│   └── BackgroundMemoryWriter.cs  # 异步提取 + 观察合并
├── Scripting/                     # Lua 脚本引擎
├── Patches/                       # Harmony 补丁
├── Config/
│   └── ModConfig.cs
└── scripts/
    └── default_strategy.lua
```

## 架构设计

### 游戏阶段状态机

Mod 通过读取游戏状态来决定该做什么——不运行自己的游戏循环：

```
GamePhaseDetector.Detect()：
  手牌选择中?     → CombatHandSelect
  覆盖层?         → RewardsScreen / CardRewardSelect / ...
  战斗中?         → CombatPlayerTurn（敌人回合则 Idle）
  地图打开?       → MapScreen
  房间类型?       → EventRoom / RestSite / Shop / TreasureRoom
  否则            → Idle
```

**战斗**由游戏的 `TurnStarted` 事件驱动。**非战斗**由 poll 循环驱动，只在阶段**变化**时行动。单选场景（唯一地图节点、Proceed 按钮）直接处理，不查询 Agent。

### Provider 无关的 Agent

三个 provider 实现相同的 `ILlmClient` 接口——文本补全、单次工具调用、多轮对话：

```
ILlmClient
├── ClaudeClient   (Anthropic API)
├── GptClient      (OpenAI 兼容 API)
└── GeminiClient   (Google Gemini API)
```

切换 provider 只需改配置文件的 `llm_provider`，不需要改代码。

### 工具调用循环

```
游戏状态 → LLM → 工具调用 → 执行 → 更新状态 → LLM → ...
```

- **查询工具**（内部处理）：`recall_memory`、`save_memory`、`view_deck_full`、`inspect_relics` 等
- **行动工具**（返回调用方）：`play_card`、`end_turn`、`choose_map_node` 等

没有人为的查询次数限制。Agent 按需查询，`recall_memory` 在 6 次后被节流以防止无限搜索。调用方超时（可配置，默认 60s）是兜底。超时后回退到脚本策略打完当前回合，下回合恢复 Agent。

### 三层记忆体系

| 层级 | 作用域 | 生命周期 | 存储 | 示例 |
|------|--------|----------|------|------|
| **局上下文** | 当前 run | 新 run 重置 | 内存 | "正在构建 Strength，需要 AOE" |
| **战斗反思** | 近期战斗 | 最近 20 场 | `battle_journal.json` | "被 Mawler 打掉血因为防御不足" |
| **持久化记忆** | 所有 run | 永久 | `memories/` 目录 | "Bash: 先上易伤再打重击" |

#### 持久化记忆

位于 `%APPDATA%/SlayTheSpire2/AutoPlayMod/memories/`：

```
memories/
├── shared/              # 跨角色共享知识
│   ├── cards/           # 卡牌观察、协同、评分
│   ├── enemies/         # 敌人行为模式、应对策略
│   ├── events/          # 事件结果（以游戏事件 ID 索引）
│   └── relics/          # 遗物评价
├── general/
│   └── strategy.json    # 通用策略（仅一般性结论，无局内细节）
└── {角色名}/
    ├── strategy.json    # 角色专属策略
    ├── archetypes/      # 构建模板
    └── runs/            # 单局总结
```

所有文件为纯 JSON，可自由查看、编辑、删除。修改即时生效。

**自动注入**：战斗时注入敌人知识，选卡时注入卡牌知识，事件时注入事件知识——按相关性评分控制注入量。

**后台提取**：每场战斗后使用游戏中的准确名字（非 LLM 猜测）提取卡牌/敌人观察。当一个实体积累 8 条以上观察时，后台合并任务整合它们并解决矛盾。

**策略过滤**：`strategy.json` 拒绝局内细节（楼层号、血量值），仅接受通用洞察。

### 对话与安全

- **战斗会话**：每场一个线程。战斗中覆盖层（Headbutt、Armaments）在同一线程内处理。
- **非战斗会话**：每个战后流程一个线程（奖励→地图→事件→休息）。
- **互斥锁**：同一时间只有一个 `RunAgentLoop` 执行——无消息交错。
- **超时恢复**：`CleanupAfterInterruption` 回滚到最后完整交换。基础反思立即保存，AI 反思后台运行。
- **并行工具调用**：完整处理——查询结果批量返回，额外调用正确追踪。
- **线程安全反思**：所有 `_reflections` 访问加锁。后台 AI 反思替换基础反思无数据竞争。

### 战后管线

```
战斗结束
├── 立即：保存基础反思（0ms，永不丢失）
├── 后台：AI 反思 → 替换基础 → 提取记忆
│   ├── 卡牌观察（使用游戏状态中的准确名字）
│   ├── 敌人观察
│   ├── 策略笔记（仅通用结论）
│   └── 8+ 观察时自动合并
├── 后台：RunContext 更新（archetype、目标）
└── 若死亡：保存 run 总结到 memories/{角色}/runs/
```

## 支持游戏

如果你喜欢这个 Mod，请在 Steam 上购买 [杀戮尖塔 2](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/) 支持开发者——这是我 2026 年最喜欢的游戏！

## 许可声明

本项目基于 [MIT 许可证](../LICENSE) 开源。杀戮尖塔 2 由 Mega Crit 开发。
