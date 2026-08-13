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

## 独立审查与合并门禁

- 开发任务只能推送分支并创建 Draft PR；禁止审查、批准、合并或删除自己的分支。
- PR 创建后标记 `review:pending` 并停止；构建和自测通过不能代替独立审查。
- 总控必须安排另一个只读审查任务检查完整 diff、实际测试结果、任务范围和架构红线。
- 代码 PR 必查正确性、失败原子性、权限、幂等、不变量、确定性、恢复和缺失边界测试。
- UI/地图/史料 PR 还必须检查真实运行画面、来源、许可证以及 `FACT` / `DESIGN` / `OPEN` 语义。
- 存在未解决的 P0 或 P1 时标记 `review:changes-requested`，禁止合并；修复后必须重新审查。
- 审查通过后由总控标记 `review:passed`；此后任何新提交都会使通过结果失效，必须复审。
- 只有总控可以在 `review:passed` 且验证通过后执行 Squash merge 和删除远程分支。
- 下游任务只能依赖“独立审查通过并已合并”的 PR，不能只看分支存在、测试通过或 PR 已创建。
