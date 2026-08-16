# UI 美术资产来源（art/panels 批次，2026-08-16）

> 本批次为**候选（candidate）美术资产**，尚未进入 `../../asset-ledger.json`，因此**不是运行时默认资产**。
> 进入账本 / 提升为运行时默认，须仓库所有者在 Draft PR 审查中确认许可证口径后进行（本轮不合并，正是该检查点）。

## 生成方式（FACT）

- 工具：WorkBuddy 内置 ImageGen（文生图，text-to-image），quality=high。
- 日期：2026-08-16。
- 操作者：本项目（MingSim / SuperZhao666）通过其 AI 助手生成。
- 后处理（确定性）：用 Pillow 12.3.0 对原图做右下角裁剪（保留左 84% × 上 89%），
  移除 ImageGen 输出强制携带的「AI生成 WORKBUDDY」署名水印。
  裁剪脚本：`artifacts/ui-art-staging/crop_watermark.py`（本机，未入库）。
  原始未裁剪图保留于 `artifacts/ui-art-staging/`（本机，gitignored）备查。
- 提示词统一约束：明确要求「无任何文字 / 字母 / 中文字符 / 字形」，以规避伪字形。

## 资产清单

| 文件 | 尺寸(裁剪后) | 原始尺寸 | 用途 | 提示词要点 |
| --- | --- | --- | --- | --- |
| endgame-review.png | 1021×740 | 1216×832 | 终局复盘插画 | 明代书房复盘：案上账册与展开的复盘卷轴、笔、砚、朱砂印、暖油灯；纸本水墨+工笔 |
| guide-banner.png | 1021×740 | 1216×832 | 新手引导横幅 | 引导场景：手持毛笔指向展开的简化地图卷轴，绢面案几， inviting |
| minister-portrait.png | 860×911 | 1024×1024 | 人物肖像（引导/人物卡） | 明代官员白描工笔肖像：乌纱帽、深色圆领、朱砂领口，素绢底 |

## 许可证（待所有者确认 → DESIGN/OPEN）

- 沿用本仓库既有 ImageGen 先例（`backgrounds/ming-imperial-study-desk-map.png`，见 `../../ASSET_PROVENANCE.md` §5.1）
  的「PROJECT_GENERATED」口径：**本项目生成作品，随仓库 MIT 再分发**。
- ⚠️ 差异披露：先例用的是 OpenAI ImageGen（引 OpenAI 条款「user owns all Output」）；
  本批次用的是 **WorkBuddy ImageGen**，其 Output 授权条款与 OpenAI 不同，且输出带强制「AI生成 WORKBUDDY」水印。
- ⚠️ 水印与 AI 内容标识：裁剪掉了强制水印；游戏 UI 已自带「艺术合成背景」来源标注（MainUi），
  但是否满足《人工智能生成合成内容标识办法》对显性标识的要求，**由所有者判断**。
  若需保留显性 AI 标识，可改用未裁剪原图或在 UI 上叠加标识。
- **结论**：本批次在 Draft PR 中以候选形式提交，供所有者确认上述许可证与标识口径后，再决定是否入账本/提升默认。

## 已知 P2

- P2-1：裁剪为一次性脚本，未并入 `tools/ui/ming_ui_assets.py` 幂等管线；若批次保留，应形式化进管线以保证可复建。
- P2-2：本批次暂不入 `asset-ledger.json`（候选），待所有者确认许可证后再登记。
