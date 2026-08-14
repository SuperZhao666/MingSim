# Ming.Persistence

持久化端口的轻量实现：

- `InMemory\*`：启动演示与测试用的内存适配器；
- `Sqlite\SnapshotCodec.cs` / `Sqlite\SnapshotReflection.cs`：RealtimeSnapshot 的规范化
  二进制编解码与反射桥（不依赖 Microsoft.Data.Sqlite，任何构建都编译）；
- `Sqlite\SqliteCommitStore.cs`：SQLite 单事务提交存储（依赖 Microsoft.Data.Sqlite，
  默认仅在联网 CI 编译，见下）。

## SQLite 单事务提交存储（G2）

`SqliteCommitStore` 同时实现 `IWorldStore` / `IAuditJournal` / `ISnapshotStore`，
把"正式提交（当前状态 + 事件日志追加 + 校验快照）"落成**单个 SQLite 事务**：

- 端口写方法（Commit/Append/Promote）登记本次提交要写的三个面；
- `CommitAll(snapshot)` 用 `BEGIN IMMEDIATE ... COMMIT` 一次性写入
  `world_state`、`event_journal`、`snapshots` 并切换 `world_meta` 指针；
  任一步失败整体回滚，未提交的批次不产生任何效果；
- `RestoreLatest(path, worldId)` 只读恢复：重算覆盖全部内容行的 SHA-256 校验和、
  校验事件日志连续性，任何字节篡改都会抛异常，绝不发布半状态；
- 提交后显式 `wal_checkpoint(TRUNCATE)`，主文件自洽（活动库仍可能出现 -wal/-shm 配套文件）。

架构边界：SQLite 层只做序列化/反序列化与恢复校验，不构造任何 WorldState 可写入口；
快照仍必须交给 `RealtimeSimulationRuntime.Restore` 做权威 canonical hash / payload checksum 校验。

## 条件编译（离线环境说明）

`Microsoft.Data.Sqlite`（MIT）在离线沙箱中无法还原，但 CI（GitHub Actions，联网）可以。
因此 `MingSimEnableSqliteStore` 属性默认在 GitHub Actions 或显式
`-p:MingSimEnableSqliteStore=true` 时启用：启用才引入该包并编译 `SqliteCommitStore.cs`
与 `tests/Ming.SmokeTests/SqliteStoreAcceptance.cs`。本地离线构建保持 0 警告 0 错误，
SQLite 验收由联网 CI 执行；本地已运行快照编解码等价验收（SnapshotCodecAcceptance）。
删除方案：不需要 SQLite 时移除上述属性、PackageReference 与 `Sqlite\SqliteCommitStore.cs` 即可，
其余代码不受影响。
