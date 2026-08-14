# Ming.Godot

Godot 4.6 .NET 客户端：可交互的“御案 + 活地图”原型。入口场景是
`scenes/ui_preview.tscn`（根节点挂真实 C# `MainUi`，子节点 `MapView` 挂真实 C# `MapView`）。

## 当前能做什么

1. 御书房 / 御案是总界面：`backgrounds/ming-imperial-study-desk-map.png` 作空间基底，
   待办奏疏按只读队列摆成案上实体，不推进时间、不写世界状态；
2. 案上舆图支持滚轮放大进入全屏策略地图、拖动平移、点击选点，西侧边界 clamp，
   LOD 分级显示城镇标签；
3. 御案 ↔ 舆图使用真实 Tween 连续转场，转场期间透明输入阻断层锁住鼠标；
4. 只读奏疏 DTO（默认 DESIGN，未接 Simulation），点击奏疏展开只读正文；
5. 地图清单校验失败时“失败关闭”：只显示中性错误牌，不伪造边界、路线或节点。

## 地图与资源契约

- 正式地图：`assets/maps/generated/ming_1629*/map-manifest.json`（2400×1600 权威画布）。
- `MapView.cs` 只读消费清单；清单画布必须是 2400×1600，否则 fail-closed（无部分状态）。
- 界面显示底图（UI 纸色派生图）的路径 + SHA-256 + 尺寸在 `MapView.cs` 代码侧构成
  权威契约，加载前先校验磁盘字节；篡改 / 缺文件 / 错哈希一律 fail-closed。
- 资产许可与可重建性见 `assets/ui/generated/ming_ui_v2/ASSET_PROVENANCE.md` 与
  `asset-ledger.json`；重建命令见同目录 `README.md`。

## 架构边界

`Ming.Godot.csproj` 只直接引用 `Ming.Application`。正式世界变化仍须经过应用层、
权限/能力校验和模拟内核；Godot 节点只做展示与交互。`MapView.cs` /
`MainUi.cs` 不依赖 `Ming.Domain` / `Ming.Simulation`，也不推进 `GameTime`。

## 自动验收

- `tests/ui_headless_acceptance.gd`、`tests/map_input_acceptance.gd`、
  `tests/main_ui_transition_acceptance.gd`：headless 逻辑验收（含负例）；
- `tests/viewport_input_acceptance.gd`：真实 Viewport 输入分发验收
  （点击 / 拖拽 / 滚轮 / 转场锁 / 遮挡层）。注意：headless 显示服务器会丢弃合成输入，
  该脚本需用 `--rendering-driver dummy`（真实窗口 + 哑渲染）运行；
- `tests/ui_visual_capture.gd`：真实 OpenGL 窗口截图工具（`--state=`、`--size=`、
  `--output=`）。
