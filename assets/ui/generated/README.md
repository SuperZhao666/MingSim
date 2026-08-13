# UI 视觉素材

这些素材服务于 MingSim 当前的“明代御案”视觉方向，存放在这里是为了让 Godot
预览场景可以直接引用。PNG 是本项目生成的表现层素材；战略地图现在改为使用
由可追溯数据构建的正式地图则放在 `assets/maps/generated/ming_1629/`，不再混入本目录。

| 文件 | 用途 | 生成约束 |
| --- | --- | --- |
| `ming-imperial-paper-background.png` | 全屏宣纸/绢纸底纹，中心留白给信息面板 | 不生成文字、印章、Logo 或 UI 控件 |
| `ming-minister-portrait.png` | 官员人物卡肖像 | 当前未使用；本机可留作对照，公开仓库排除 |
| `ming-strategy-map-natural-earth.*` | 已撤下的现代中国省级行政区实验样张 | 本机可留作错误复盘；公开仓库排除，不再接入预览 |
| `ming-strategy-map-texture.png` | 旧版 ImageGen 战略地图底图 | 本机可留作对照；公开仓库排除，不再作为预览入口 |

## 使用方式

- Godot 预览入口：仓库根目录的 `project.godot`。
- 预览场景：`src/Ming.Godot/scenes/ui_preview.tscn`。
- 图片是表现层资源，不承载世界状态；地图、人物属性和实时结算仍必须来自 .NET 核心层。
- 纸张和肖像由本项目内置 ImageGen 生成；正式东亚地图的物理数据、历史草稿数据与来源记录位于 `assets/maps/sources/`，不要再使用旧 `assets/maps/source/natural-earth/admin1/` 现代行政数据。
- 公开仓库中仍在使用的原创视觉素材按根目录 MIT 许可证发布；本机对照稿不进入版本基线。
- `tools/build_real_china_map.py` 只保留为现代数据格式实验工具。正式东亚历史地图的纠偏和新流水线见 `docs/地图与UI制作/00-东亚历史地图纠偏与自动制作方案.md`。
