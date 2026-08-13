# Godot 御案地图原型验证报告

## 验证对象

- 工程：`src/Ming.Godot/Ming.Godot.csproj`
- 主场景：`src/Ming.Godot/scenes/ui_preview.tscn`
- 地图控件：`src/Ming.Godot/scripts/MapView.cs`
- 页面连接：`src/Ming.Godot/scripts/MainUi.cs`
- 自动验收：`src/Ming.Godot/tests/ui_headless_acceptance.gd`
- 视觉验收：`src/Ming.Godot/tests/ui_visual_capture.gd`

## 架构检查

- Godot 项目使用 `Godot.NET.Sdk/4.6.0`；
- Godot 项目只直接引用 `Ming.Application`，不直接引用 `Ming.Domain` 或 `Ming.Simulation`；
- 地图 UI 只读取 `map-manifest.json`，不修改地图输入和构建器；
- UI 中的政令确认只形成待校验 Intent，不直接修改世界状态；
- 玩家可见场景不引用现代中国省级行政区实验图。
- 总览与辽西细节分别读取真实 manifest；任何清单、纹理或坐标契约失败都停止绘制历史层。

## 自动验证命令

```powershell
$RepoRoot = (Resolve-Path .).Path
$GodotExe = '<你的 Godot 4.6 Mono console 可执行文件路径>'
dotnet restore MyGame.sln
dotnet build MyGame.sln --no-restore
dotnet run --project tests/Ming.SmokeTests/Ming.SmokeTests.csproj --configuration Release --no-build
& $GodotExe --headless --editor --path $RepoRoot --rendering-method gl_compatibility --audio-driver Dummy --quit
& $GodotExe --headless --path $RepoRoot --rendering-method gl_compatibility --audio-driver Dummy --script res://src/Ming.Godot/tests/ui_headless_acceptance.gd
```

1600×960 实机截图使用 Windows 显示驱动，而不是 headless 渲染：

```powershell
# 东亚概览
& $GodotExe --path $RepoRoot --display-driver windows --rendering-method gl_compatibility --audio-driver Dummy --write-movie artifacts/screenshots/godot-ui-overview-dual-manifest/frame.png --fixed-fps 10 --disable-vsync --log-file artifacts/screenshots/godot-ui-overview-dual-manifest/godot.log --script res://src/Ming.Godot/tests/ui_visual_capture.gd

# 辽西细节
& $GodotExe --path $RepoRoot --display-driver windows --rendering-method gl_compatibility --audio-driver Dummy --write-movie artifacts/screenshots/godot-ui-liaoxi-dual-manifest/frame.png --fixed-fps 10 --disable-vsync --log-file artifacts/screenshots/godot-ui-liaoxi-dual-manifest/godot.log --script res://src/Ming.Godot/tests/ui_visual_capture.gd -- --focus-liaoxi
```

## 自动验收覆盖

1. 主场景和 C# 脚本可加载；
2. 御案、地图、详情、暂停/倍速、图层、提示和政令控件都存在；
3. 总览与辽西两个真实清单都通过六节点、五条稳定路线及已知端点契约，纹理存在且日期来自 manifest；
4. 日期使用公元快照，不做未经校核的农历年号换算；
5. 场景不引用现代省界实验图，不含回合制用语；
6. 京师、通州等六节点逐一通过真实 `_GuiInput` 鼠标按下/抬起事件点选；京师/通州命中区不重叠；
7. 拖动边界受限，不会把细节地图甩出视口；标签避免重叠与上下裁切；
8. `DESIGN 候选路线` 开关实际控制候选路线，不再用固定百分比圆弧冒充补给量；标题、图例、18日、72%、现代锚点、演示时钟均带 DESIGN/OPEN/未建模语义；
9. 清单、纹理或 `map_x/map_y` 缺失时失败关闭，`LoadedFromManifest=false` 且不显示伪造节点或路线。

本轮实际运行时，总览和辽西两个 `map-manifest.json` 都存在。初始状态保持全东亚概览、缩放 1；点击宁远急报后切换到同源辽西细节清单，并仅按节点可读间距适度放大。辽西纹理不再把东亚总图强行放大，因此没有原先的像素块问题。

本轮实测结果：.NET `restore/build/test` 均退出码 0，build 为 0 警告、0 错误；Godot 资源导入退出码 0；headless 自动验收退出码 0，全部 PASS；两条 Windows MovieWriter 截图命令均退出码 0，各生成 7 帧。

## 人工验收提示

运行项目后，按以下顺序体验：点击“宁远急报” → 滚轮缩放地图 → 拖动地图 → 切换两个图层 → 点选六节点 → 比较补给方案 → 拟定结构化政令 → 确认提交校验 → 解除暂停并切换速度。

已实际检查最后一帧：

- 东亚概览：`artifacts/screenshots/godot-ui-overview-dual-manifest/frame00000006.png`
- 辽西细节：`artifacts/screenshots/godot-ui-liaoxi-dual-manifest/frame00000006.png`

早先截图超时来自 `--headless --write-movie` 的无窗口渲染路径；MovieWriter 改用 `--display-driver windows` 后稳定退出。当前没有挂起的 Godot 进程，也没有一次性临时脚本；`ui_visual_capture.gd` 被保留为可重复视觉验收工具。截图目录中的逐帧 PNG、WAV 和日志属于验收产物，不参与游戏运行。

仍未完成：这些地图边界和数值仍是 OPEN/DESIGN 资料，不是完成的 1629 多政权史实图；暂停、倍速和时钟尚未接入 Simulation 权威时间，政令按钮也仍是交互原型。
