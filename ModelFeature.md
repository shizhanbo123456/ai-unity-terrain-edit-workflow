# ModelFeature（备用 Prefab 特征登记）

> 用途：登记工具根目录 `Generated/Prefabs/` 下**已处理并确认效果**的备用 Prefab 特征，供散布 / 摆件 / 定点摆放时参考。
> 规则：处理备用 Prefab 并**查看生成后的效果**（打开 Prefab / 场景视图，或查看模型截图）后，必须在下方登记该 Prefab 的特征；**文件不存在时创建本文件**。特征变化时更新对应条目。详见 README「阶段 0 · 素材准备」。

## 字段说明

| 字段 | 含义 |
|---|---|
| 名称 | 备用 Prefab 文件名（`Assets/ai-unity-terrain-edit-workflow/Generated/Prefabs/<name>.prefab`） |
| 类型 | 语义分类（Tree / Rock / Grass / Building / Prop / …），供生成组目标匹配 |
| 尺寸 | 处理后的世界尺寸（长 × 宽 × 高，米），可与 `mesh.bounds` / `prefab.bounds` 量测结果对照 |
| 外形 | 简单描述（如「圆锥树冠 + 直干」「长方体外墙 + 坡屋顶」） |
| 落位 | 模型底部中心是否落在根节点原点；若否，已做的子物体修正说明 |
| Billboard | 模式（None / CrossPlanes / FaceCamera / YawOnly）及原因——树木等「看个大概」可启用；房屋等不规则对象必须 None |
| 摆放建议 | 适合的分布形式 / 旋转策略 / 离路范围 / 密度等（供生成组配置参考） |

## 登记表

| 名称 | 类型 | 尺寸(长×宽×高, m) | 外形 | 落位 | Billboard | 摆放建议 |
|---|---|---|---|---|---|---|
| Tree_A_1 | Tree | 2.0 × 2.0 × 6.0 | 圆锥树冠 + 直干 | 底部中心 = 原点，无需修正 | CrossPlanes | 散布：离路 2~10m，等值线旋转，密度偏高 |
| Rock_A10 | Rock | 3.0 × 2.5 × 1.8 | 不规则岩块，顶面略平 | 底部中心 = 原点 | None | 散布：离路 ≥1m，随机旋转，可团簇 |
| House_A | Building | 8.0 × 6.0 × 4.5 | 长方体外墙 + 坡屋顶 | 底部中心 = 原点 | None | 定点 / 摆件：远离道路，固定朝向，不启用 Billboard |

> 示例条目仅作格式参考；实际条目在每次处理 / 确认效果后逐条登记或更新。
