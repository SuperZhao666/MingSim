# MingSim 协作红线

开始前先阅读任务范围和相关设计文档，并运行 `git status --short --branch`。不要覆盖用户或其他任务的修改。

## 最小正确复杂度

- 选择能正确实现、测试和维护功能的最简单方案；不做代码高尔夫。
- 第一个真实用例优先写具体实现；没有第二个实现或明确外部边界，不创建接口。
- 禁止万能 `Manager`、`ServiceLocator`、`Utils`、Generic Repository。
- 禁止为简单操作堆叠 Factory、Builder、Handler、Dispatcher、Validator。
- 不提交占位实现、注释掉的旧代码、死代码或“以后可能使用”的抽象。
- 优先使用 .NET/Godot 标准能力；新增依赖必须说明必要性、许可证和删除方案。
- 注释解释“为什么”，代码表达“做什么”；保持中文说明适合初学者阅读。

## 架构边界

- `WorldState` 是唯一已提交世界状态，只有 Simulation 可以修改。
- UI 只能读取 ReadModel、提交 Command；Agent/LLM 只能提交结构化 Intent。
- Domain/Simulation 不依赖 Godot、SQLite、HTTP 或模型 SDK。
- 权威游戏时间只有一套 `GameTime + Scheduler`；不得新增第二套时钟或时间推进。
- 不得为了减少代码而跳过权限、前置条件、事务、幂等、不变量或测试。
- 历史内容必须区分 `FACT`、`INFERENCE`、`DESIGN`、`OPEN`；不得用现代行政区冒充历史边界。

## Git 与完成标准

- 一个任务使用一个 Worktree、一个短期分支和一个 Pull Request；不直接在 `main` 开发。
- 只修改任务授权路径，只暂存明确文件；禁止覆盖其他任务分支和普通 force push。
- 没有实际运行相关构建、测试或人工验收，不得声称完成。
- 交付时报告修改文件、验证命令与结果、剩余风险，以及新增抽象为何不可再省。
