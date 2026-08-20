# AiTerrainWorkflow

AI 地形编辑工作流 —— 独立工具仓库，存放地形编辑相关的算法与工具类。

C# 代码统一使用命名空间 `AiTerrainWorkflow`。

## 目录结构

```
Utils/
├── UniformPointGenerator.cs    # 均匀分布随机点生成（网格抖动 Jittered Grid）
└── ObjectGroup.cs              # ObjectGroup ScriptableObject（组名 + GameObject 列表）

LayerEditor/                    # 地形贴图工作流（层次图 → 高度图 → 距离场 → 路网 → 贴图/树木/细节）
├── LayerMap.cs                 # 层图数据：CPU 读写图片 + 圆形/矩形/三角绘制算法
├── LayerPalette.cs             # 层级预设色：15 个内置色 + 默认名称（Layer0 恒为透明）
├── LayerConfigSO.cs            # 每层一个的层级配置 SO（颜色/名称 + 各子界面权重 + 道路/高度参数）
├── TerrainPaintProjectSO.cs    # 总配置 SO（全局配置 + 层级 SO 列表 + 各池 + 邻接组 + 结果）
├── TerrainRoadGen.cs           # 核心算法：层ID解析/邻接组分/距离场R/随机游走G+B/RGB合成/高度烘焙
└── Editor/
    └── LayerEditorWindow.cs    # 六子界面工作流窗口（Tools / Terrain Edit Workflow / Open Terrain Paint Workflow）

Editor/
└── TerrainEditWorkflowMenu.cs  # 菜单栏工具（Tools / Terrain Edit Workflow）

TerrainGeneratorConfigs/        # 本地配置资产（每个配置一个子文件夹，全部不进版本库，见 .gitignore）
ModelFeatures.md                # 模型特征记录（常用预制体的类型/尺寸/外形/方向，供地形搭建参考）
```

## 菜单栏工具（Tools / Terrain Edit Workflow）

| 菜单项 | 功能 |
|---|---|
| `Log Version` | Console 打印当前工具版本号 |
| `Open Terrain Paint Workflow` | 打开地形贴图工作流窗口 |

版本号写在 `Editor/TerrainEditWorkflowMenu.cs` 的 `Version` 常量中（当前 **v1.3**）；有变更时手动同步更新。

## 地形贴图工作流窗口（六子界面）

改造自原 LayerEditor 绘画窗口。**窗口本身不存储持久数据**——所有信息从总 SO（`TerrainPaintProjectSO`）加载，修改直接写入 SO；编辑器顶部 ObjectField 选择/创建配置（EditorPrefs 记住上次使用的配置）。

顶部工具栏（靠右）依次为：**工作流配置 / 区域编辑 / 高度编辑 / 贴图编辑 / 树木编辑 / 细节编辑**。

| 子界面 | 布局 | 内容 |
|---|---|---|
| ① 工作流配置 | 整页（无分栏） | 工作流图（层次图 / RGB 结果图）、Layer 数量（2~16）、各层颜色/名称编辑（**Layer0 颜色锁定为完全透明**）、Terrain 字段（仅窗口内临时，不入 SO） |
| ② 区域编辑 | 左右分栏 | 左：全局配置（层次图已在工作流配置管理，此处占位提示）+ 层级配置（颜色/名称只读）；右：层次图画布绘制（圆形/矩形/三角 + 擦除 + 撤销） |
| ③ 高度编辑 | 左右分栏 | 左：全局配置（高度 seed/scale + 烘焙结果只读 min/max/图）+ 层级配置（每层高度范围 Vector2）；右：**烘焙高度图** |
| ④ 贴图编辑 | 左右分栏 | 左：全局配置（随机游走参数 + 贴图种子 + 自然/道路 TerrainLayer 池 + **邻接组** + R 通道 Max 只读）+ 层级配置（贴图混合权重 + 道路生成参数）；右：**计算距离场 + 路网**（RGB） |
| ⑤ 树木编辑 | 左右分栏 | 左：全局配置（树木 Prefab 池）+ 层级配置（每层树木生成权重）；右：占位（后续实现树木放置） |
| ⑥ 细节编辑 | 左右分栏 | 左：全局配置（细节 Prefab 池）+ 层级配置（每层细节生成权重）；右：占位（后续实现细节放置） |

**左右分栏说明**：编辑子界面左侧窄栏为「全局配置 + 层级配置」拼成的整体（**共同滚动**），右侧宽栏为「信息生成」（该子界面的核心功能），无需页签切换。

**创建新地形配置**：输入名称后自动创建 `TerrainGeneratorConfigs/<名称>/` 子文件夹，内含总 SO + 默认 16 个层级 SO（Layer0 透明 + 其余 15 色取自 LayerPalette 预设），Layer 数量可在工作流配置中调整为 2~16。

## 配置架构（ScriptableObject）

- **总 SO（`TerrainPaintProjectSO`）**——字段按子界面用 Header 划分：
  - 通用：`layers`（层级 SO 列表，2~16 个）
  - 区域编辑：`layerMap`（层次图）
  - 高度编辑：`heightSeed` / `heightScale`（噪声参数）、`heightMin` / `heightMax`（烘焙自动写入）、`heightMap`（高度图）
  - 贴图编辑：`config`（`TerrainPaintConfig`：roadStep / walkStartTries / walkCandidateCount / startCoverStopSamples / walkSeed / maxStepsPerPath / gApplySpacing / noiseScale / worldPerPixel）、`naturalSeed` / `roadSeed`、`naturalTerrainLayers` / `roadTerrainLayers`（两个 TerrainLayer 池）、`adjacencyGroups`（`List<List<int>>` 邻接组）
  - 树木编辑：`treePrefabs`（Prefab 池）
  - 细节编辑：`detailPrefabs`（Prefab 池）
  - 计算结果：`groupMaxD`（每组合层距离场最大值）、`rMax`（R 通道全局最大值）、`resultTexture`（RGB 结果图）
- **层级 SO（`LayerConfigSO`）**——每个层级一个：
  - 区域编辑：`color` / `layerName`（**只能在 Inspector 修改**）
  - 贴图编辑：`naturalLayerWeights` / `roadLayerWeights`（权重列表，索引 = 对应 TL 池 id，值 = 权重，0 = 不纳入）、`generateRoad`、`roadWidth`、`roadSpacingMin`、`roadFinalRemap`
  - 高度编辑：`heightRange`（Vector2，min/max）
  - 树木编辑：`treeWeights`；细节编辑：`detailWeights`（索引 = 对应 Prefab 池 id，值 = 生成权重，0 = 不生成）
- **存储**：全部配置资产在 `LayerEditor/TerrainGeneratorConfigs/` 下按配置分子文件夹，**本地项目数据，不进版本库**（`.gitignore` 忽略）。

## 核心算法（TerrainRoadGen）

链路（详见桌面设计文档《混合距离场与路面生成工具_设计文档(2).md》）：

1. **层ID解析** `ParseLayerIds`：层次图颜色 → 层ID 整数数组（颜色解析只此一步，后续流程不接触颜色）。
2. **组合层级分组** `GroupLayers`：按全局 `adjacencyGroups`（`List<List<int>>` 邻接组）分组（仅 `generateRoad=true` 层）；未出现在任何组的有效层自动单独成组；**同一层级跨组重复会报 Error 并阻断计算**。
3. **距离场 R** `ComputeR`：对组合层区域做 Felzenszwalb 欧氏距离变换；多组合层叠加取 max 后，求**全局 `rMax`** 并归一化 `r/rMax` 写入 R 通道（**恢复公式：`r = R * rMax`**，R 为归一化值 [0,1]）。
4. **随机游走** `GenerateRoads`：每个组合层独立生成路网——候选点必须在起点同组合层（跨组跳过）；按 R 加权选点；**防卷曲**（锚点与新点距离 > `gApplySpacing` 才批量回填 G 胶囊，半径 = 沿途所在层 `roadSpacingMin`）；闭环合并（末点附近历史点接入网络）；结束对路径所有边统一填 B 胶囊（半径 = 所在层 `roadWidth`）。G = 占用/间隔缓冲（防绕圈 + 密度控制），B = 路面硬掩码。
5. **合成** `ComposeRgb`：一张 RGB 图（R=距离场红，G=占用绿，B=路面蓝），结果存 `result_RGB.png`。
6. **高度图烘焙** `BakeHeightMap`：逐像素按所在层 `heightRange`，用 Perlin 噪声（`heightSeed` + `heightScale` 频率）在范围内插值生成高度数组 → 统计实际 min/max 写入 `heightMin`/`heightMax` → 归一化 `(h-min)/(max-min)` 写入高度图 R 通道（**恢复公式：`h = R*(max-min)+min`**），结果存 `heightMap.png`。

参数语义与默认值见设计文档；EDT 算法已独立验证。

## LayerMap（绘制核心类）

CPU 可读写 RGBA32 图片：`FillCircle`（圆形画笔）、`DrawLine`（拖拽直线条带）、`FillRect`（矩形填充）、`FillTriangle`（三角填充）；完全覆盖 alpha=1 不模糊；撤销快照 32 步；`SavePng`/`LoadPng`/`Resize`。不依赖 UnityEditor，供窗口与后续复用。

## ModelFeatures.md

模型特征记录：常用预制体的**类型 / 尺寸 / 外形 / 放置规则**，供 AI 地形搭建摆放时参考。尺寸统一用 bridge `mesh-bounds --placed` 视觉尺寸（x宽 × y高 × z深）。已覆盖 Bonfires / Crystals / Props / Timber / Tower / Tree / Vines / Grass / Rock / Stone&Cliff / Wall。
