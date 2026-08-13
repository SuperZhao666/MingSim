# Hartwell 1391 明代近似行政区数据

| 字段 | 内容 |
| --- | --- |
| 数据集 | Hartwell China Historical GIS / Ming Dynasty Provinces in 1391 |
| 作者 | Robert Hartwell；Harvard Dataverse/WorldMap 发布 |
| 数据集 DOI | https://doi.org/10.7910/DVN/29302 |
| 数据集许可 | Creative Commons Zero v1.0 Universal（CC0 1.0） |
| 服务项 | https://www.arcgis.com/home/item.html?id=5960a2c871244b9e95a3085fcab46bee |
| 查询接口 | https://services7.arcgis.com/iEMmryaM5E3wkdnU/ArcGIS/rest/services/Ming_Dynasty_Provinces_in_1391/FeatureServer/0 |
| 下载日期 | 2026-08-13 |
| GeoJSON SHA-256 | `91C19F832FA308D9119B425B2C115917AAABE3FE0E26D68FAEE0D91C09E427EE` |

## 必须知道的局限

这不是 1629 年精确快照。数据集说明明确把它称作“1391 年近似省区”；Hartwell 方法还使用现代县级单元进行合并、拆分和近似绘制。因此：

1. 它可以替代错误的现代中华人民共和国省级行政区，作为**早期明代法定行政骨架候选**；
2. 它不能直接证明 1629 年每条边界；
3. 后建的贵州、辽东实际控制、奴儿干/乌思藏/朵甘等名义与实际关系必须重新审核；
4. 游戏场景只会显式列入经过 `source-ledger.csv` 审核的要素；未列入的 Hartwell 多边形不会自动显示；
5. 该层只表达历史区域，不自动生成游戏路线、相邻、所有权或军事控制。

