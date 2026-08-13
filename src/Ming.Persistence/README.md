# Ming.Persistence

当前目录只放持久化端口的轻量实现：

- `InMemoryWorldStore`：用于启动演示和测试；
- `InMemoryAuditJournal`：证明事件是只追加的；
- `InMemorySnapshotStore`：演示快照校验通过后才移动当前指针。

下一步接 SQLite 时，建议仍然保持 `IWorldStore`、`IAuditJournal` 和 `ISnapshotStore` 三个边界，
不要让 `Microsoft.Data.Sqlite` 的类型渗透到 Domain 或 Simulation 项目里。
