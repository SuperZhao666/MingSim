# MyGame / MingSim

这是一个面向长期历史策略推演的基础框架。项目先把“世界模拟”和“游戏引擎界面”分开，目标是让规则可验证、历史可重放、AI 可替换。

> 当前阶段以设计和架构原型为主，不代表游戏功能已经完成。  
> 从这里开始阅读：[MingSim 设计蓝图：先读这里](docs/设计蓝图/00-总导航-先读这里.md)。

## 当前产品与技术定案

- 玩家体验：CK3 式可暂停实时、多档变速；
- 模拟方式：离散游戏时钟 + 不同频率系统 + 到期事件；
- 第一切片：蓝图通过 ADR 暂选 1629“宁远急饷”，只做北京—辽东的最小可玩闭环；精确史实仍待来源账本验证；
- 架构：Godot 表现层与纯 C# 世界模拟分离，内部采用模块化单体；
- 状态边界：Simulation 是唯一权威写者，UI 和 AI 只发命令/意图；
- AI：普通行为走规则/Utility AI，重要人物按重大认知事件调用可选模型；
- 存档目标：SQLite WAL + Current State + Input/Event Journal + verified Snapshot；
- 工程纪律：使用实现和验证功能所需的最少总复杂度，不提前堆框架。

策划原文前部包含旧的季度回合方案；后部已经明确修正为可暂停实时。当前代码仍同时存在旧回合骨架和实时原型，统一工作按[现状差距与实施顺序](docs/设计蓝图/12-现状差距与实施顺序.md)推进。

## 当前已经落地的核心模型

- `WorldState`：世界唯一权威状态；
- `CharacterState`：能力、人格、忠诚、压力、私有记忆；
- `InstitutionState`：机构与能力边界；
- `CapabilityGrant`：角色在资源范围内的授权；
- `FacilityState`、`InventoryState`、`ArmyState`：工坊、库存和军队的真实状态；
- `WorldIntent`：代理或玩家提出的结构化行动；
- `DomainEvent`：已提交事实的审计记录。

## 现有旧回合原型怎么走

```text
加载当前状态
  ↓
冻结回合基准
  ↓
代理只提出 WorldIntent
  ↓
权限、资源、前置条件校验
  ↓
在临时工作区确定性结算
  ↓
不变量检查
  ↓
快照准备与校验
  ↓
原子提交状态 + 追加审计事件
  ↓
切换当前快照指针
```

这条路径已经验证了临时工作区、权限、不变量和“全有或全无”的基本思想；目标架构会把这些思想迁移到实时世界中的短命令/到期批次提交，而不是继续扩张季度回合。

始终有效的规则红线：

> Agent → Tool/Intent → Simulation Kernel → WorldState

不能出现：

> Agent → Database / Agent → 直接修改 WorldState

## 项目结构

| 项目 | 责任 |
| --- | --- |
| `Ming.Domain` | 领域对象、权限、意图、事件；不依赖 Godot、数据库和模型 |
| `Ming.Simulation` | 当前包含旧回合结算与实时调度原型；目标是统一的确定性实时内核 |
| `Ming.Application` | 工作流、存储/审计/快照端口、JSON 剧本加载 |
| `Ming.Agents` | 有限上下文、规则代理、模型供应商抽象 |
| `Ming.Persistence` | 当前的内存存储；以后替换为 SQLite 适配器 |
| `Ming.Cli` | 不启动 Godot 的最小运行入口 |
| `Ming.SmokeTests` | 不依赖第三方测试包的行为冒烟测试 |
| `Ming.Godot` | Godot 4.6 C# 可交互地图/UI 原型；只引用 Application，不承载世界规则 |

## 运行

在安装 .NET 10 SDK 后，从仓库根目录运行：

```powershell
dotnet run --project src/Ming.Cli/Ming.Cli.csproj
dotnet run --project tests/Ming.SmokeTests/Ming.SmokeTests.csproj
```

2026-08-13 基线曾验证：

- .NET SDK `10.0.400`；
- `dotnet build MyGame.sln`：0 警告、0 错误；
- 冒烟测试输出“全部通过”；
- Godot `4.6.stable.mono` 曾以 headless editor 方式加载静态工程。当前新增 C# 交互地图后，必须重新通过构建、headless 交互验收和实际截图，不能沿用旧结论。

这证明旧骨架曾可构建和加载，不证明 SQLite、Godot 与真实 ReadModel/Command 接入、存档恢复或完整玩法已完成。地图制作当前进展见[东亚历史地图纠偏与自动制作方案](docs/地图与UI制作/00-东亚历史地图纠偏与自动制作方案.md)。

## 下一步

不要直接扩张全国系统或接入模型。按蓝图依次：

1. 用特征测试锁住现有行为；
2. 统一 `GameTime`、`WorldVersion`、Scheduler 和状态哈希；
3. 先以内存实现打通“运 5000 石粮到宁远”的完整闭环；
4. 再用 SQLite 单事务替换正式提交与恢复；
5. 接入 Godot 的 Command/ReadModel；
6. 规则 AI 完整可玩后，最后接可选模型。
