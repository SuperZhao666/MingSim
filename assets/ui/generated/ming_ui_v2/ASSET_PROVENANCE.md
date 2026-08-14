# Ming UI V2 资产来源与再分发证据（ASSET PROVENANCE）

本文件是 `asset-ledger.json` 中每条 `license_decision` 引用的仓库内证据。原则：

- 每张交付 PNG 必须有可复核的**作者/工具、日期、生成标识或来源 URL、明确授权**；
- 无法补证、无法重建的旧批次图已从交付清单移除，不再作为运行时默认；
- `OPEN` 不进入本账本；账本工具会拒绝任何仍带 `OPEN` 的条目（`entry_map` 校验）。

## 1. 本仓库许可基线

- 仓库 LICENSE：MIT（Copyright (c) 2026 SuperZhao666）。
- 本项目代码（含 `tools/ui/ming_ui_assets.py`）按 MIT 授权；由该代码确定性生成的 PNG
  属于"本项目生成作品"，再分发权利由仓库 LICENSE 覆盖，证据即本文件 + 生成器源码。

## 2. 程序化生成的 8 张源图集（PROJECT_ORIGINAL_MIT）

以下 8 张 `source/*-source.png` 全部由 `tools/ui/ming_ui_assets.py` 的
`render_procedural_source()` 用 Pillow 12.3.0 确定性绘制（纸、墨、朱砂、竹木配色），
生成标识统一为 `PROCEDURAL_GENERATION_ID = "ming-paper-ink-procedural-v2"`，
日期 2026-08-14。作者=本项目（SuperZhao666 / MingSim）。无任何第三方素材参与，
因此再分发授权 = 仓库 MIT。

| 源图集 | 尺寸 | 绘制内容 |
| --- | --- | --- |
| source/functional-paper-icons-source.png | 2079×756 | 五枚纸本水墨功能图标（军务/钱粮/消息/旨意/奏疏） |
| source/memorial-paper-states-source.png | 3230×258 | 五态奏疏纸页（normal/hover/pressed/selected/disabled） |
| source/primary-paper-states-source.png | 2040×156 | 五态纸签操作按钮 |
| source/seal-paper-states-source.png | 2088×239 | 四态朱批纸签按钮（右置朱砂方印） |
| source/small-paper-parts-source.png | 1254×1254 | 九枚小零件（分隔线/勾选框/焦点环/滚动条/开关/提示框） |
| source/speed-bamboo-states-transparent.png | 1774×887 | 四枚竹简倍速题签 |
| source/status-paper-tags-transparent.png | 1774×887 | 六枚状态纸签（FACT/DESIGN/OPEN/选中/急报/警告） |
| source/tab-paper-states-source.png | 2205×185 | 五态页签 |

重建方法（幂等、两次构建字节一致）：

    python tools/ui/ming_ui_assets.py verify        # 校验当前文件与账本
    python tools/ui/ming_ui_assets.py repeatability # 两次临时构建，字节必须一致

## 3. 从源图集确定性切出的 43 张派生图（DERIVED_FROM_PROJECT_ORIGINAL_MIT_SOURCE）

`buttons/`、`tabs/`、`badges/`、`icons/`、`memorials/`、`parts/`、`speed/` 共 43 张
均为 `PROVEN_EXACT_CROPS` 中记录的像素级精确切图矩形（含一像素 Alpha 收边），
切片坐标就写在生成器源码里，可逐张复核；其再分发授权随源图集 = MIT。

## 4. 正式地图的 UI 纸色派生图（DERIVED_FROM_FORMAL_MAP）

- `maps/ming_1629-physical.png`、`maps/ming_1629_liaoxi-physical.png`
- 输入：`assets/maps/generated/ming_1629*/physical-base.png` 与 map-manifest.json。
- 物理底图数据来源为 Natural Earth（公共领域）：
  https://www.naturalearthdata.com/about/terms-of-use/ —— Natural Earth 声明其数据为公共领域，
  可自由复制、修改、分发。UI 纸色处理（parchment-muted-v1）由本项目工具完成，
  坐标/尺寸不变，仅作呈现，不构成历史或仿真拓扑。
- 重建：`python tools/ui/ming_ui_assets.py derive-map --dry-run ...`（只算哈希，不写仓库）。

## 5. 成品类

### 5.1 御书房底图（PROJECT_GENERATED / built-in ImageGen）

- `backgrounds/ming-imperial-study-desk-map.png`
- 工具：ChatGPT 内置 ImageGen；日期 2026-08-14；生成 ID：`exec-46924c8c-6d4a-46cf-94ed-b17ed57ffa25`；
  操作：precise-object-edit（移除伪字形与机械/金属器物）。
- 授权依据：OpenAI 服务条款（https://openai.com/policies/terms-of-use/）规定用户对其
  Output 拥有所有权（"as between you and OpenAI, you own all Output"）；本项目为生成者
  本人（SuperZhao666）使用，故可随仓库 MIT 再分发。该 ID 与编辑记录已在账本中登记，
  本目录无法仅凭文件重演编辑，因此账本 operation 标记为 `precise_object_edit`（不可重建）。

### 5.2 册页九宫格（PROJECT_ORIGINAL_MIT）

- `cards/ming-booklet-paper-ninepatch.png`：由 `render_procedural_final()` 确定性绘制，
  右上折角完全落在 StyleBoxTexture 固定区（右 330 / 上 210）内，中央纸面可安全拉伸。

## 6. 已移除的不可补证旧批次

上一轮遗留的一批 ImageGen 源图集与预览（如 `source/functional-icons-source.png`、
`backgrounds/ming-imperial-study-dawn.png`、`frames/*` 等）没有可复核的生成 ID 记录，
无法补证，已从工作树删除，不进入交付清单。运行时默认只消费本文件覆盖的 55 张资产。
