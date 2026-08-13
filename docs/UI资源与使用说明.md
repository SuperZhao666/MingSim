# UI 资源与使用说明

## 先给结论

当前 UI 资源分成三层：

1. `assets/ui/*.svg`：项目自制的图标、印章、线框和装饰。
2. `assets/ui/theme-tokens.json`：颜色、间距、字号和圆角等统一令牌。
3. `assets/ui/generated/*`：本项目生成并仍在使用的纸张、人物；已撤下的旧地图实验样张不进入公开仓库。
4. `assets/maps/generated/ming_1629/*`：由本地、可追溯数据自动构建的东亚物理底图、历史草稿覆盖层和 Godot 清单。

Godot 只负责显示这些资源；财政、军队、工坊、权限和 AI 意图仍然必须经过 `Ming.Application` 与 `Ming.Simulation`。

## 项目自制 SVG

| 文件 | 用途 |
| --- | --- |
| `assets/ui/theme-tokens.json` | 统一颜色、间距、字号和圆角 |
| `assets/ui/imperial-seal.svg` | 红色印章 / 世界标志 |
| `assets/ui/icon-treasury.svg` | 财政入口 |
| `assets/ui/icon-military.svg` | 军事与后勤入口 |
| `assets/ui/icon-memorial.svg` | 奏疏与审计入口 |
| `assets/ui/panel-corner.svg` | 古籍式面板角花 |
| `assets/ui/imperial-ui-wireframe.svg` | 1600×960 的“御案 + 活地图”布局草图 |

## ImageGen 生成的 PNG

| 文件 | 用途 | 当前状态 |
| --- | --- | --- |
| `assets/ui/generated/ming-imperial-paper-background.png` | 全屏宣纸/绢纸底纹，中心留白给面板和文字 | 已生成并接入 Godot 预览 |
| `assets/ui/generated/ming-minister-portrait.png` | 虚构内阁官员人物卡肖像 | 当前未使用；本机可留作复盘，公开仓库排除 |
| `assets/ui/generated/ming-strategy-map-texture.png` | 旧版 ImageGen 战略地图底图 | 本机可留作复盘；公开仓库排除 |

## 已撤下的现代行政区实验样张

| 文件 | 用途 | 当前状态 |
| --- | --- | --- |
| `assets/ui/generated/ming-strategy-map-natural-earth.*` | 现代中国省级边界实验图 | 本机可留作问题复盘；公开仓库排除 |
| `assets/maps/source/natural-earth/admin1/*` | Natural Earth 现代 Admin-1 测试输入 | 本机隔离；公开仓库排除，正式构建器不会读取 |
| `tools/build_real_china_map.py` | 旧数据格式实验工具 | 本机可留作复盘；公开仓库排除 |

它使用了真实的**现代**省级轮廓，所以地理文件并非虚构，但时代和语义是错的。
“数据真实”不等于“适合 1629 年”。

## 当前正式地图流水线

| 文件 | 职责 | 当前状态 |
| --- | --- | --- |
| `content/scenarios/ming_1629/map/map-build.json` | 固定快照、投影、输入和输出 | 已接入 |
| `assets/maps/sources/natural-earth-physical/*` | 公共领域海岸、陆地、主要河流与湖泊 | 只作物理地理 |
| `assets/maps/sources/hartwell-cc0/*` | 1391 年明代行政近似几何 | CC0 草稿基线，不等同精确 1629 边界 |
| `tools/maps/build_east_asia_map.py` | 离线验证并生成全部地图表现资产 | 已实现，可重复构建 |
| `assets/maps/generated/ming_1629/map-manifest.json` | Godot 只读入口 | 已接入交互原型 |
| `assets/maps/generated/ming_1629/build-report.json` | 输入/输出哈希、数量和验证结果 | 构建通过 |

历史边界的纠偏原则和制作流程见
[东亚历史地图纠偏与自动制作方案](地图与UI制作/00-东亚历史地图纠偏与自动制作方案.md)，
具体势力分区与未决证据见
[1629 东亚势力分区与证据计划](地图与UI制作/01-1629东亚势力分区与证据计划.md)。

## Godot 预览

从仓库根目录打开 `project.godot`，运行 `src/Ming.Godot/scenes/ui_preview.tscn`。当前场景是原生 Godot 控件组成的交互原型，可缩放、拖动、点选六个节点并切换图层；整张 ImageGen 概念图不会被当成按钮层。

## 推荐落地顺序

1. 用 `theme-tokens.json` 建立全局 Theme。
2. 先做 `ImperialPanel`、`MemorialCard`、`CharacterCard`、`MetricChip` 四个基础组件。
3. 用 `imperial-ui-wireframe.svg` 和概念图统一风格，但用 Godot 原生控件搭界面结构。
4. 将 `DomainEvent` 映射成奏疏卡片，把查询结果映射成地图标记和人物卡。
5. 最后接入按钮输入和自然语言政令，并让所有政令先变成可校验的 `WorldIntent`。

UI 的视觉顺序要服务于玩法顺序：

```text
当前世界状态 → 待处理事务 → 可执行命令 → 规则失败原因 → 已提交结果
```

不要先做“很会说话的 AI 对话框”，否则玩家看不到财政、库存、权限链和实时推演中的真实变化。
