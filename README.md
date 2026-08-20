# AiTerrainWorkflow

AI 地形编辑流水线 —— 配置驱动的 AI 地形生成工具。Unity Editor 内闭环完成创作，`unity-python-bridge` 仅作按需外围工具。

C# 代码统一使用命名空间 `AiTerrainWorkflow`。当前版本 **v1.3**（写在 `Editor/TerrainEditWorkflowMenu.cs` 的 `Version` 常量，手动维护）。

> 状态标签说明：**[已完成]** = 已实现并可用；**[待开发]** = 已规划、尚未实现；**[待设计]** = 方向已定、细节待设计；**[暂留空]** = 预留位置、内容待填充。

## 设计原则

| 原则 | 说明 |
|---|---|
| 配置驱动 | 工作流最终产出 = **引用美术/模型素材的配置数据**（主配置 `TerrainPaintProjectSO`）；中间图（layerMap/RGB/heightMap）仅为编辑期可视化，**不再是交付物** |
| 数据优先 | 栅格数据一律以 `float[][]` 为最终形态，存为 CSV txt（MapData）；**运行时只读 float[][]，图片仅供人看** |
| 构建端分离 | 运行时/编辑器靠 **TerrainBuilder 组件** 接收主配置构建真实地形（高度/纹理/植被/树 + 实例化摆件） |
| bridge 按需 | `unity-python-bridge` 只是按需取用的外围工具（量尺寸/截图/可选一键构建），**不参与主链路** |

## 完整工作流

```
素材准备 → ①区域编辑 → ②高度 → ③贴图 → ④树木 → ⑤细节 → ⑥摆件 → 烘焙主配置 → ⑦TerrainBuilder 构建 → ⑧运行时/交付
```

| 阶段 | 输入 | 处理 | 产出（载体） | 状态 |
|---|---|---|---|---|
| 0 素材准备 | 美术资产（prefab/贴图/TerrainLayer/模型） | 建素材池；bridge 量尺寸/截预览 | 素材池 + ModelFeatures | 池 **[已完成]**；摆件池/测量回流 **[待设计]** |
| 1 区域编辑 | 手绘语义层 | LayerMap 画布绘制，**每笔完写** | `layerMap`（MapData） | **[已完成]** |
| 2 高度编辑 | layerMap + 每层 heightRange | Perlin 插值 → 归一化 | `height`（MapData）+ min/max | **[已完成]** |
| 3 贴图编辑 | layerMap + 邻接组 + 权重规则 | 距离场 EDT + 随机游走路网 | `distance/occupancy/road`（MapData） | **[已完成]** |
| 4 树木编辑 | layerMap + 树池 + 规则 | 规则 → 密度图/实例 | `treeDensity` + treeRules | **[待开发]**（子界面为占位） |
| 5 细节编辑 | layerMap + 细节池 + 规则 | 规则 → 密度图 | `detailDensity` + detailRules | **[待开发]**（子界面为占位） |
| 6 摆件编辑 | layerMap + ObjectGroup + 规则 | 均匀撒点 + 过滤 + 贴地 | placedObjects 实例列表（结构化字段） | **[待开发]**（规则字段 **[暂留空]**） |
| 7 构建 | 主配置（SO + 全部 MapData） | TerrainBuilder 七步构建 | 真实 TerrainData + 摆件 GameObject | **[待开发]**（alphamap 算法 **[待设计]**） |
| 8 运行时 | 主配置（TextAsset → float[][]） | 动态构建 / 加载已烘焙场景 | 运行时地形 | **[待设计]** |

## 阶段详述

### 阶段 0 · 素材准备 [池：已完成；摆件池/测量回流：待设计]

- 已有素材池（全局，写入主配置）：`naturalTerrainLayers`（自然 TerrainLayer）、`roadTerrainLayers`（道路 TerrainLayer）、`treePrefabs`（树）、`detailPrefabs`（细节）。
- **[待开发]** 摆件池：复用 `ObjectGroup`（groupName + GameObject 列表）。
- **[待设计]** bridge 按需测量：`mesh.bounds --placed` 量取素材尺寸写回 `ModelFeatures.md`；`prefab.screenshot` 生成缩略图供窗口显示。

### 阶段 1 · 区域编辑 [已完成]

- 画布尺寸 = 主配置 `mapResolution`（创建配置时单选 128/256/512/1024；工作流配置页可改，改动后需重新绘制/烘焙）。
- 绘制工具：圆形画笔（单击/拖拽直线条带）、矩形填充、三角形填充、擦除、撤销（32 步）。
- **每画完一笔即写 `MapData/layerMap.txt`**：直线=抬笔时；矩形/三角=画出完整图形后；撤销后同步写。
- Layer0 恒为透明过渡层，其余 15 个预设色可改颜色/名称（颜色解析为层 ID 只此一步，后续流程不接触颜色）。

### 阶段 2 · 高度编辑 [已完成]

- 逐像素按所在层的 `heightRange`，用 Perlin 噪声（`heightSeed` + `heightScale` 频率）插值生成高度。
- 统计实际 min/max 写入主配置 `heightMin/heightMax`；归一化到 [0,1] 后写入 `MapData/height.txt`（float[][]）。
- 恢复公式：`h = r * (max - min) + min`。预览图由窗口用 `MapDataTextureUtils.ToTexture` 生成，**不落盘**。

### 阶段 3 · 贴图编辑 [已完成]

- 链路：`ParseLayerIds`（色→层ID）→ `GroupLayers`（邻接组，冲突阻断）→ `ComputeR`（Felzenszwalb 欧氏距离场，全局 rMax 归一化）→ `GenerateRoads`（随机游走，G=占用/间隔缓冲，B=路面硬掩码）。
- 结果写入三个 MapData key：`distance`（R）/ `occupancy`（G）/ `road`（B）。
- **alphamap 最终权重不落盘**：由 TerrainBuilder 在构建时用噪声生成（见阶段 7）。各层只保留权重规则（`naturalLayerWeights` / `roadLayerWeights`，索引 = 对应池 id）。

### 阶段 4 · 树木编辑 [待开发]（当前子界面为占位）

- 规划：每层 `treeRules[]`：`{ treePrefab(池引用), weight, density, scaleMin/Max, slopeLimit°, heightRangeFilter }`。
- 产出形态 **[待拍板]**：密度图 `treeDensity`（float[][]，构建时按种子撒点，可复现、数据小，推荐）或烘焙实例列表 `treeInstances`（精确可控、体积大）。

### 阶段 5 · 细节编辑 [待开发]（当前子界面为占位）

- 规划：每层 `detailRules[]`：`{ detailPrototype(池引用), weight, density(0~16), noiseScale }` → `detailDensity`（MapData）。

### 阶段 6 · 摆件编辑 [待开发]（规则字段暂留空）

- 规划：每层 `propRules[]`：`{ objectGroup, count/密度, spacingMin, alignToTerrain, randomRotation, scaleRange, slopeLimit }`。
- 生成：`UniformPointGenerator`（网格抖动均匀分布）在层掩码内生成候选 → 过滤（层内/间距/坡度）→ 贴地（高度图插值或射线）→ `placedObjects: List<{prefab, pos, rot, scale}>`（结构化字段，**不进 MapData**，非二维栅格）。

### 阶段 7 · TerrainBuilder 构建 [待开发]（alphamap 算法待设计）

规划七步：

```
1 PrepareTerrain  尺寸/分辨率/材质（terrainSpec）
2 ApplyHeight     SetHeights(height 数据)
3 ApplyAlphamap   ⭐构建时生成权重（见下）
4 ApplyDetail     按 detailDensity/规则 → SetDetailLayer
5 ApplyTrees       按 treeDensity/规则 → SetTreeInstances
6 PlaceProps       按 placedObjects/规则 → 实例化 GameObject
7 PostProcess      碰撞、静态标记、光照贴图参数
```

- 双模式：**编辑器烘焙**（写 TerrainData 资产，可保存，为推荐主路径）/ **运行时构建**（Awake 动态构建，程序化场景）。
- **ApplyAlphamap 草案**：逐像素 `L = layerMap[p]`，`base = road[p]>0.5 ? roadLayerWeights[L] : naturalLayerWeights[L]`；对权重>0 的层叠加独立 Perlin 噪声打破条带（`w[i] = base[i] × (1 - blendSoft + blendSoft × n)`），可选按 `distance` 做层边界渐变，归一化 Σw=1 → SetAlphamaps。参数（seed 策略 / noiseScale / blendSoft / 是否距离场过渡）**[待设计]**。

### 阶段 8 · 运行时 [待设计]

- 运行时只读 float[][]（主配置 `mapDataFiles` 持 TextAsset 引用，随构建打包）；图片永不参与运行时。
- 形态 **[待设计]**：加载已烘焙场景（零构建开销）或 TerrainBuilder 运行时动态构建。

## MapData 存储层 [已完成]

- 接口（主配置 `TerrainPaintProjectSO` 上）：`ReadMap(key)→float[][]` / `WriteMap(key, float[][])` / `DeleteMap(key)` / `HasMap(key)`。
- 文件：`Assets/ai-unity-terrain-edit-workflow/LayerEditor/TerrainGeneratorConfigs/<配置>/MapData/{key}.txt`。
- 格式：CSV，手写解析（无第三方库）。首行元数据头 `#key=...;w=...;h=...`（解析器跳过 `#` 行）；数值 **F3 三位小数**、InvariantCulture（跨平台一致）。
- 引用：主配置持 `mapDataFiles`（`key + TextAsset`），随 SO 打进构建；**编辑器读直接读磁盘文件（保最新）**，**运行时走 TextAsset**。
- 辅助：`MapDataTextureUtils`（float[][]↔Texture2D，仅编辑期显示/采集）。
- key 约定：`layerMap / height / distance / occupancy / road` **[已完成]**；`treeDensity / detailDensity` **[待开发]**。

## 目录结构

```
Utils/
├── UniformPointGenerator.cs    [已完成] 均匀分布随机点（网格抖动 Jittered Grid，确定性种子可复现）
└── ObjectGroup.cs              [已完成] 摆件组 SO（groupName + GameObject 列表，供摆件编辑复用）

LayerEditor/
├── CsvArrayCodec.cs            [已完成] MapData CSV 手写编解码（元数据头 / F3 / ToJagged / ToFlat）
├── MapDataStore.cs             [已完成] MapData/{key}.txt 文件 IO（写/读/删/存在性）
├── LayerMap.cs                 [已完成] 层图绘制核心（画布 ↔ 层ID float[][]，撤销/填充/线条）
├── LayerPalette.cs             [已完成] 15 预设色（Layer0 恒透明）
├── LayerConfigSO.cs            [已完成] 每层配置（颜色/名称/权重/高度范围/道路参数）
├── TerrainPaintProjectSO.cs    [已完成] 主配置（素材池/规则/邻接组/mapResolution/mapDataFiles + MapData 接口）
├── TerrainRoadGen.cs           [已完成] 核心算法（EDT 距离场 / 随机游走 / RGB 合成 / 高度烘焙 float[][]）
├── TerrainBuilder.cs           [暂留空] 构建组件（阶段 7，待开发）
└── Editor/
    ├── LayerEditorWindow.cs    [已完成] 工作流窗口（六子界面 + 创建向导尺寸单选 + MapData 接线）
    └── MapDataTextureUtils.cs  [已完成] float[][]↔Texture2D（仅显示/采集）

Editor/
└── TerrainEditWorkflowMenu.cs  [已完成] 菜单入口（Tools / Terrain Edit Workflow）

TerrainGeneratorConfigs/        [暂留空] 本地配置资产（gitignored；每个配置一个子文件夹 + MapData/）
ModelFeatures.md                [已完成] 模型特征记录（尺寸统一用 bridge `mesh-bounds --placed` 量取）
```

## 菜单与窗口 [已完成]

- `Tools / Terrain Edit Workflow / Log Version`：打印版本号。
- `Tools / Terrain Edit Workflow / Open Terrain Paint Workflow`：打开工作流窗口。
- 窗口六个子界面：工作流配置（含栅格分辨率） / 区域编辑 / 高度编辑 / 贴图编辑 / 树木编辑 **[占位]** / 细节编辑 **[占位]**。

## 与 unity-python-bridge 的关系 [按需]

- bridge **不参与主链路**，只做按需外围：`mesh.bounds --placed`（量素材尺寸写 ModelFeatures）、`prefab.screenshot`（缩略图）、`terrain.*`（直接读写真实 TerrainData 的命令行通道，共 19 条）。
- 工作流产出高度数据（归一化 0~1）与 bridge `terrain.set_heights` 的 `data` 格式**直接兼容**。
- **[待设计]** 可选增强：把 `TerrainBuilder.Build` 暴露为 bridge 命令（如 `terrainbuilder.build <配置名>`），实现 Python 端一键构建。
- 主链路不依赖 bridge，关掉一切照常。

## 实施里程碑

- **M1 [已完成]** MapData 存储层（CsvArrayCodec / MapDataStore / SO 接口 / TextureUtils / 窗口接线）。
- **M2 [待开发]** 树木 / 细节 / 摆件三个子界面（规则编辑 + 密度图/实例烘焙）。
- **M3 [待开发]** TerrainBuilder 组件（编辑器烘焙 + 运行时构建 + 构建时 alphamap 噪声生成）。
- **M4 [待设计]** bridge 可选集成（一键构建命令）。

## 待拍板事项

1. 树木/细节产出形态：密度图 + 种子（推荐） vs 烘焙实例列表。
2. 摆件编辑是否独立第七个子界面（推荐是）。
3. alphamap 构建时噪声的参数：seed 策略 / noiseScale / blendSoft / 是否用距离场过渡。
4. 主配置是否导出 JSON（跨工具/存档）——暂定不做，SO 为主。
5. TerrainBuilder 双模式确认（编辑器烘焙为主、运行时构建为辅）。
