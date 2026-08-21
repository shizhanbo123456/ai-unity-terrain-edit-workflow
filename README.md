# AiTerrainWorkflow

AI 地形编辑流水线 —— 配置驱动的 AI 地形生成工具。Unity Editor 内闭环完成创作，`unity-python-bridge` 仅作按需外围工具。

C# 代码统一使用命名空间 `AiTerrainWorkflow`。当前版本 **v1.3**（写在 `Editor/TerrainEditWorkflowMenu.cs` 的 `Version` 常量，手动维护）。

> 状态标签说明：**[已完成]** = 已实现并可用；**[待开发]** = 已规划、尚未实现；**[待设计]** = 方向已定、细节待设计；**[暂留空]** = 预留位置、内容待填充。

## 设计原则

| 原则 | 说明 |
|---|---|
| 配置驱动 | 工作流最终产出 = **引用美术/模型素材的配置数据**（主配置 `TerrainPaintProjectSO`）；中间图（layerMap/RGB/高度预览）仅为编辑期可视化，**不再是交付物** |
| 数据优先 | 栅格数据一律以 `float[][]` 为最终形态，存为 CSV txt（MapData）；**运行时只读 float[][]，图片仅供人看** |
| 构建端分离 | 运行时/编辑器靠 **TerrainBuilder 组件** 接收主配置构建真实地形（高度/纹理/植被/树 + 实例化摆件） |
| bridge 按需 | `unity-python-bridge` 只是按需取用的外围工具（量尺寸/截图/可选一键构建），**不参与主链路** |

## 名词解释

| 名词 | 含义 |
|---|---|
| 工作流项目 | `TerrainGeneratorConfigs` 目录下的一个文件夹；一个文件夹 = 一套完整的地形工作流配置（含主配置 SO + 各层级 SO + MapData）。 |
| 主配置 | `TerrainPaintProjectSO`（ScriptableObject）：地形工作流的总配置，聚合素材池 / 规则 / 邻接组 / mapResolution / MapData 接口，是编辑器窗口与 `TerrainBuilder` 的单一数据入口。 |
| 层级配置 | `LayerConfigSO`（ScriptableObject）：单个语义层的配置（颜色 / 名称 / 权重 / 高度范围 / 道路参数 / 最小离路距离，以及后续的树 / 细节位置列表）；数量 2~16，从属于主配置。 |

## 注意 · 距离语义

- **map 与 terrain 的尺寸无确定关系**：map（栅格，尺寸 = 主配置 `mapResolution`）与实际 Terrain 的尺寸没有固定换算；map 中每个点与 terrain 中每个点存在映射关系，**可理解为 map 是 terrain 的俯视图**。
- **单位约定**：提到距离为 **（整数 / 像素）** 时，指它们在 map 上（或映射到 map 上）的距离；提到距离为 **（float / 米）** 时，指它们在 terrain 上（或映射到 terrain 上）的距离。换算系数 = `TerrainPaintConfig.worldPerPixel`（= Terrain 世界尺寸 / 图片分辨率）。
- 例：`distance`（R 通道）为像素距离（map 上）；`offRoad` 与 `treeRoadDistanceLimit` / `detailRoadDistanceLimit` 为米（terrain 上）。

## 完整工作流

```
素材准备 → ①区域编辑 → ②高度 → ③贴图 → ④摆件(暂留空) → ⑤树木 → ⑥细节 → 烘焙主配置 → ⑦TerrainBuilder 构建 → ⑧运行时/交付
```

| 阶段 | 输入 | 处理 | 产出（载体） | 状态 |
|---|---|---|---|---|
| 0 素材准备 | 美术资产（prefab/贴图/TerrainLayer/模型） | 建素材池；bridge 量尺寸/截预览 | 素材池 + ModelFeatures | 池 **[已完成]**；摆件池/测量回流 **[待设计]** |
| 1 区域编辑 | 手绘语义层 | LayerMap 画布绘制，**每笔完写** | `layerMap`（MapData） | **[已完成]** |
| 2 高度编辑 | layerMap + 每层 heightRange | Perlin 插值 → 真实高度 | `height`（MapData） | **[已完成]** |
| 3 贴图编辑 | layerMap + 邻接组 + 权重规则 | 距离场 EDT + 随机游走路网 + 离路距离场 | `distance/occupancy/road/offRoad`（MapData） | **[已完成]** |
| 4 摆件编辑 | layerMap + ObjectGroup | —（功能暂留空，后续设计） | — | **[暂留空]** |
| 5 树木编辑 | layerMap + 树池 + 每层密度/scale/离路限制 | 按密度生成位置，**road&lt;0.5 + offRoad≥treeRoadDistanceLimit 过滤** | 每层 `treePositions`（Vector2[]）+ 密度/scale（层 Config） | **[待开发]**（子界面为占位） |
| 6 细节编辑 | layerMap + 细节池 + 每层密度/scale/离路限制 | 按密度生成位置，**road&lt;0.5 + offRoad≥detailRoadDistanceLimit 过滤** | 每层 `detailPositions`（Vector2[]）+ 密度/scale（层 Config） | **[待开发]**（子界面为占位） |
| 7 构建 | 主配置（SO + 全部 MapData） | `TerrainBuilder.Build()`（构建函数单一入口） | 真实 TerrainData + 摆件 GameObject | **[待开发]**（alphamap 算法 **[待设计]**） |
| 8 运行时 | 主配置（TextAsset → float[][]） | 按需调用 Build()（时机由实际项目定） | 运行时地形 | **[待设计]** |

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
- **⚠️ 透明区域（未绘制位置 = Layer0 / 层ID = -1）需要特殊处理 [待设计]**：不属于任何语义层的空白位置，下游各阶段需统一约定其行为，例如：
  - 高度：当前 `BakeHeightData` 对 -1 层按 `heightRange=(0,0)` → 平地；后续可改为可配置的默认高度。
  - 贴图/路网：-1 不在任何组合层内 → 距离场 R 全 0、随机游走不进入、不产生路面、offRoad=0（区域外）。
  - alphamap：-1 像素给默认纹理权重或全 0（构建时再定）。
  - 树木/细节/摆件：透明区域不生成位置。
  - 具体规则待设计确认后统一落实。

### 阶段 2 · 高度编辑 [已完成]

- 逐像素按所在层的 `heightRange`，用 Perlin 噪声（`heightSeed` + `heightScale` 频率）插值生成**真实高度**。
- 真实高度直接写入 `MapData/height.txt`（float[][]，**不归一化**）；**范围不持久化**，由显示 / 构建时遍历数据现算（`ToTexture` 统计后以 `out` 传出）。
- 预览图由窗口用 `MapDataTextureUtils.ToTexture` 生成，**不落盘**。

### 阶段 3 · 贴图编辑 [已完成]

- 链路：`ParseLayerIds`（色→层ID）→ `GroupLayers`（邻接组，冲突阻断）→ `ComputeR`（Felzenszwalb 欧氏距离场，输出**像素距离真实值**）→ `GenerateRoads`（随机游走，G=占用/间隔缓冲，B=路面硬掩码）→ `ComputeOffRoad`（语义层拼合区域内到最近道路的距离，**米**）。
- 结果写入四个 MapData key：`distance`（R，**像素距离真实值**）/ `occupancy`（G）/ `road`（B）/ `offRoad`（**米**：语义层区域（不含 Layer0）内到最近道路的距离，道路处=0、区域外=0）。**范围不持久化**：预览 RGB 图的 R 通道由数据现算 max 归一化，构建时同样现算。
- **alphamap 最终权重不落盘**：由 TerrainBuilder 在构建时用噪声生成（见阶段 7）。各层只保留权重规则（`naturalLayerWeights` / `roadLayerWeights`，索引 = 对应池 id）。

### 阶段 4 · 摆件编辑 [暂留空]

- 位于树木编辑之前；**功能暂留空，后续设计**。规划时复用 `ObjectGroup`（groupName + GameObject 列表）与 `UniformPointGenerator`。

### 阶段 5 · 树木编辑 [待开发]（当前子界面为占位）

- 每层配置新增字段：
  - `treeDensity`（float，个/平方米）
  - `treeScale`（Vector2，随机缩放范围 min~max）
  - `treeRoadDistanceLimit`（float，**米**，默认 3；距最近道路（offRoad）小于该值不生成树木，0 = 不限制）
  - `treePositions`（Vector2[]，归一化位置列表，烘焙产物，**存于该层 LayerConfigSO**）
- 烘焙流程：按该层掩码 + 密度生成均匀位置（复用 `UniformPointGenerator`，全局 seed 可复现）→ **过滤 `road >= 0.5`（不能生成在路上）且 `offRoad < treeRoadDistanceLimit`（不能离路太近；limit=0 时不限制）的位置** → 写入 `layer.treePositions`。
- **构建时**（TerrainBuilder.ApplyTrees）：遍历 `treePositions`，用**全局 `TreeSeed`** + 该层 `treeWeights`（树池权重）随机决定每个位置放树池中的哪个原型；scale 在 `treeScale` 范围内随机。

### 阶段 6 · 细节编辑 [待开发]（当前子界面为占位）

- 与树木编辑同构：每层 `detailDensity`（个/平方米）/ `detailScale`（Vector2）/ `detailRoadDistanceLimit`（float，**米**，默认 1，小于树的 treeRoadDistanceLimit；距最近道路小于该值不生成细节，0 = 不限制）/ `detailPositions`（Vector2[]，过滤条件同树木：road&lt;0.5 且 offRoad≥detailRoadDistanceLimit）。
- **构建时**：用**全局 `DetailSeed`** + 该层 `detailWeights`（细节池权重）随机选原型。

> 注：**所有 seed 均为全局 seed（无每层 seed）**。位置列表存于各层 Config（非 MapData 栅格），密度/scale 不落 MapData。

### 阶段 7 · TerrainBuilder 构建 [待开发]（alphamap 算法待设计）

规划步骤（对外**只暴露一个构建函数 `Build()`**，构建时机由实际项目按需调用，不内置双模式）：

```
1 PrepareTerrain  尺寸/分辨率/材质（terrainSpec）
2 ApplyHeight     遍历 height 现算 min/max → 归一化 [0,1] → SetHeights
3 ApplyAlphamap   ⭐构建时生成权重（见下）
4 ApplyDetail     按各层 detailPositions + DetailSeed/权重 → SetDetailLayer
5 ApplyTrees       按各层 treePositions + TreeSeed/权重 → SetTreeInstances
6 PlaceProps       按摆件数据（摆件编辑后续设计）→ 实例化 GameObject
7 PostProcess      碰撞、静态标记、光照贴图参数
```

- **ApplyAlphamap 草案**：逐像素 `L = layerMap[p]`，`base = road[p]>0.5 ? roadLayerWeights[L] : naturalLayerWeights[L]`；对权重>0 的层叠加独立 Perlin 噪声打破条带（`w[i] = base[i] × (1 - blendSoft + blendSoft × n)`），可选按 `distance`（构建时现算归一化）做层边界渐变，归一化 Σw=1 → SetAlphamaps。参数（noiseScale / blendSoft / 是否距离场过渡）**[待设计]**；**seed 均为全局 seed**。
- **L = -1（透明区域）的权重方案 [待设计]**：默认纹理权重或全 0，见阶段 1 的透明区域特殊处理。

### 阶段 8 · 运行时 [待设计]

- 运行时只读 float[][]（主配置 `mapDataFiles` 持 TextAsset 引用，随构建打包）；图片永不参与运行时。
- 形态 **[待设计]**：TerrainBuilder 暴露 `Build()`，具体在 Awake / 场景加载 / 手动调用，由实际项目按需接入。

## MapData 存储层 [已完成]

- 接口（主配置 `TerrainPaintProjectSO` 上）：`ReadMap(key)→float[][]` / `WriteMap(key, float[][])` / `DeleteMap(key)` / `HasMap(key)`。
- 文件：`Assets/ai-unity-terrain-edit-workflow/LayerEditor/TerrainGeneratorConfigs/<配置>/MapData/{key}.txt`。
- 格式：CSV，手写解析（无第三方库）。首行元数据头 `#key=...;w=...;h=...`（解析器跳过 `#` 行）；数值 **F3 三位小数**、InvariantCulture（跨平台一致）。
- 引用：主配置持 `mapDataFiles`（`key + TextAsset`），随 SO 打进构建；**编辑器读直接读磁盘文件（保最新）**，**运行时走 TextAsset**。
- 辅助：`MapDataTextureUtils`（float[][]↔Texture2D，仅编辑期显示/采集）。
- key 约定（共 6 个）：`layerMap / height / distance / occupancy / road / offRoad` **[已完成]**。树木/细节的位置列表**不存 MapData**（存于各层 Config，见阶段 5/6）。

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
├── LayerConfigSO.cs            [已完成] 每层配置（颜色/名称/权重/高度范围/道路参数/最小离路距离；树/细节位置列表字段 **[待开发]**）
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
- 窗口六个子界面：工作流配置（含栅格分辨率） / 区域编辑 / 高度编辑 / 贴图编辑 / 摆件编辑 **[暂留空]** / 树木编辑 **[占位]** / 细节编辑 **[占位]**。

## 与 unity-python-bridge 的关系 [按需]

- bridge **不参与主链路**，只做按需外围：`mesh.bounds --placed`（量素材尺寸写 ModelFeatures）、`prefab.screenshot`（缩略图）、`terrain.*`（直接读写真实 TerrainData 的命令行通道，共 19 条）。
- 工作流产出高度数据（**真实高度**，构建/桥接时现算归一化到 0~1）可与 bridge `terrain.set_heights` 的 `data` 格式对接。
- **[待设计]** 可选增强：把 `TerrainBuilder.Build` 暴露为 bridge 命令（如 `terrainbuilder.build <配置名>`），实现 Python 端一键构建。
- 主链路不依赖 bridge，关掉一切照常。

## 实施里程碑

- **M1 [已完成]** MapData 存储层（CsvArrayCodec / MapDataStore / SO 接口 / TextureUtils / 窗口接线）。
- **M2 [待开发]** 树木 / 细节子界面（每层密度/scale/位置列表烘焙，road&lt;0.5 + offRoad≥treeRoadDistanceLimit / detailRoadDistanceLimit 过滤）。
- **M3 [待开发]** TerrainBuilder 组件（`Build()` 单一构建函数 + 构建时 alphamap 噪声生成）。
- **M4 [暂留空]** 摆件编辑（位于树木之前，后续设计）。
- **M5 [待设计]** bridge 可选集成（一键构建命令）。

## 待拍板事项（已收敛）

1. ~~树木/细节产出形态~~ → **已定**：每层 Vector2 位置列表 + 密度(个/㎡) + scale(Vector2)；road&lt;0.5 过滤；构建时按全局 seed + 层权重选原型。
2. ~~摆件编辑~~ → **已定**：位于树木编辑之前，功能暂留空，后续设计。
3. alphamap 构建时噪声参数（noiseScale / blendSoft / 是否用距离场过渡）——构建时再定。
4. ~~导出 JSON~~ → **已定**：不导出。
5. ~~TerrainBuilder 双模式~~ → **已定**：只暴露 `Build()`，构建时机由实际项目按需调用。
6. seed 策略 → **已定**：全部为全局 seed（TreeSeed / DetailSeed 等），无每层 seed。
