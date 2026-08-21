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
| 层级配置 | `LayerConfigSO`（ScriptableObject）：单个语义层的配置（颜色 / 名称 / 权重 / 高度范围 / 道路参数 / 最小离路距离 / 构建时参数：密度、scale）；数量 2~16，从属于主配置。 |

## 注意 · 距离语义

- **map 与 terrain 的尺寸无确定关系**：map（栅格，尺寸 = 主配置 `mapResolution`）与实际 Terrain 的尺寸没有固定换算；map 中每个点与 terrain 中每个点存在映射关系，**可理解为 map 是 terrain 的俯视图**。
- **单位约定**：提到距离为 **（整数 / 像素）** 时，指它们在 map 上（或映射到 map 上）的距离；提到距离为 **（float / 米）** 时，指它们在 terrain 上（或映射到 terrain 上）的距离。换算系数 = `TerrainPaintConfig.worldPerPixel`（= Terrain 世界尺寸 / 图片分辨率）。
- 例：`distance`（R 通道）为像素距离（map 上）；`offRoad` 与 `treeRoadDistanceLimit` / `detailRoadDistanceLimit` 为米（terrain 上）。

## 完整工作流

```
素材准备 → ①区域编辑 → ②高度 → ③贴图 → ④摆件(暂留空) → ⑤树木 → ⑥细节 → ⑦TerrainBuilder 构建 → ⑧运行时/交付
```

| 阶段 | 输入 | 处理 | 产出（载体） | 状态 |
|---|---|---|---|---|
| 0 素材准备 | 美术资产（prefab/贴图/TerrainLayer/模型） | 建素材池；bridge 量尺寸/截预览 | 素材池 + ModelFeatures | 池 **[已完成]**；摆件池/测量回流 **[待设计]** |
| 1 区域编辑 | 手绘语义层 | LayerMap 画布绘制，**每笔完写** | `layerMap`（MapData） | **[已完成]** |
| 2 高度编辑 | layerMap + 每层 heightRange | Perlin 插值 → 真实高度 | `height`（MapData） | **[已完成]** |
| 3 贴图编辑 | layerMap + 邻接组 + 权重规则 | 距离场 EDT + 随机游走路网 + 离路距离场 | `distance/occupancy/road/offRoad`（MapData） | **[已完成]** |
| 4 摆件编辑 | layerMap + ObjectGroup | —（功能暂留空，后续设计） | — | **[暂留空]** |
| 5 树木编辑 | layerMap + 树池 + 每层密度/scale/离路限制 | 配置密度/scale/离路限制；**构建时按区块动态生成位置** | 每层密度/scale/离路限制（层 Config）；位置不存储 | **[进行中]**（配置编辑可用；生成在 M3） |
| 6 细节编辑 | layerMap + 细节池 + 每层密度/scale/离路限制 | 配置密度/scale/离路限制；**构建时按区块动态生成位置** | 每层密度/scale/离路限制（层 Config）；位置不存储 | **[进行中]**（配置编辑可用；生成在 M3） |
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
- **透明区域（未绘制位置 = Layer0 / 层ID = -1）[后续在全局配置补充设置，当前不处理]**：不属于任何语义层的空白位置。其下游行为（高度默认值 / 贴图权重 / 是否生成物体）**后续统一在全局配置中增加透明区域设置项**再落实；当前各阶段对 -1 暂按既有默认处理（高度平地、贴图全 0、不生成位置），不做专门设计、不单独落地规则。

### 阶段 2 · 高度编辑 [已完成]

- 逐像素按所在层的 `heightRange`，用 Perlin 噪声（`heightSeed` + `heightScale` 频率）插值生成**真实高度**。
- 真实高度直接写入 `MapData/height.txt`（float[][]，**不归一化**）；**范围不持久化**，由显示 / 构建时遍历数据现算（`ToTexture` 统计后以 `out` 传出）。
- 预览图由窗口用 `MapDataTextureUtils.ToTexture` 生成，**不落盘**。
- 平滑参数（`smoothStep` 步长 / `smoothIterations` 迭代，十字线均值滤波）已加入配置与窗口，**暂未参与运算**，后续接入 `BakeHeightData`。

### 阶段 3 · 贴图编辑 [已完成]

- 链路：`ParseLayerIds`（色→层ID）→ `GroupLayers`（邻接组，冲突阻断）→ `ComputeR`（Felzenszwalb 欧氏距离场，输出**像素距离真实值**）→ `GenerateRoads`（随机游走，G=占用/间隔缓冲，B=路面硬掩码）→ `ComputeOffRoad`（语义层拼合区域内到最近道路的距离，**米**）。
- 结果写入四个 MapData key：`distance`（R，**像素距离真实值**）/ `occupancy`（G）/ `road`（B）/ `offRoad`（**米**：语义层区域（不含 Layer0）内到最近道路的距离，道路处=0、区域外=0）。**范围不持久化**：预览 RGB 图的 R 通道由数据现算 max 归一化，构建时同样现算。
- **alphamap 最终权重不落盘**：由 TerrainBuilder 在构建时用噪声生成（见阶段 7）。各层只保留权重规则（`naturalLayerWeights` / `roadLayerWeights`，索引 = 对应池 id）。

### 阶段 4 · 摆件编辑 [暂留空]

- 位于树木编辑之前；**功能暂留空，后续设计**。规划时复用 `ObjectGroup`（groupName + GameObject 列表）与 `UniformPointGenerator`。

### 阶段 5 · 树木编辑 [进行中]（配置编辑已可用；位置由构建端动态生成）

- 每层配置新增字段（均为**构建时参数**，位置本身不存储）：
  - `treeDensity`（float，个/平方米）
  - `treeScale`（Vector2，随机缩放范围 min~max）
  - `treeRoadDistanceLimit`（float，**米**，默认 3；距最近道路（offRoad）小于该值不生成树木，0 = 不限制）
- **位置不烘焙、不落盘**（LayerConfigSO / MapData 均不存位置列表）。
- **构建时按区块动态生成**（TerrainBuilder 驱动）：主配置 `treeChunkSize`（区块尺寸）/ `treeVisibleDistance`（可见距离）决定区块划分与激活半径；`SetCameraPosition(Vector2)` 时，区块中心进入可见距离的区块，按该区块内各层掩码 + `treeDensity` 均匀采样位置（区块 seed = 全局 `TreeSeed` ⊕ 区块 index，可复现）→ 过滤 `road >= 0.5`（不能生成在路上）且 `offRoad < treeRoadDistanceLimit`（不能离路太近；limit=0 时不限制）→ 按该层 `treeWeights` 权重随机选原型、scale 在 `treeScale` 内随机 → 从**对象池**实例化 GameObject（name=池 key、`HideInHierarchy` 隐藏；高度 = Terrain.SampleHeight）。区块离开可见距离则整区块物体回收进对象池。**任何时刻只持有当前区块的物体，禁止一次性生成 / 加载全图位置列表**（对接区块管理器）。

### 阶段 6 · 细节编辑 [进行中]（配置编辑已可用；位置由构建端动态生成）

- 与树木编辑同构：每层 `detailDensity`（个/平方米）/ `detailScale`（Vector2）/ `detailRoadDistanceLimit`（float，**米**，默认 1，小于树的 treeRoadDistanceLimit；距最近道路小于该值不生成细节，0 = 不限制）。
- **位置同样不存储、构建时按区块动态生成**（TerrainBuilder 驱动，逻辑同树木）：主配置 `detailChunkSize` / `detailVisibleDistance`；`SetCameraPosition` 时按区块掩码 + `detailDensity` 生成位置 → 过滤 `road >= 0.5` 且 `offRoad < detailRoadDistanceLimit`（limit=0 时不限制）→ 按 `detailWeights` 权重随机选原型 → **对象池实例化**（高度 = Terrain.SampleHeight）；区块离开可见距离则整区块回收。**任何时刻只持有当前区块的物体**。

> 注：**所有 seed 均为全局 seed（无每层 seed）**。树木/细节位置**不存储**（构建时按区块动态生成）；密度/scale/离路限制/权重存各层 Config，不落 MapData。

### 阶段 7 · TerrainBuilder 构建 [待开发]（alphamap 算法待设计）

规划步骤（对外**只暴露一个构建函数 `Build()`**，构建时机由实际项目按需调用，不内置双模式）：

> 树木 / 细节**不在此一次性构建**：`Build()` 只初始化区块管理器（`ChunkUpdateManager`）与对象池，之后由 **`SetCameraPosition(Vector2)`** 按观察点流式生成 / 回收（见阶段 5/6）。

- **构建触发入口**：`Build(projectConfig, terrain)` 为 public，调用时机由调用方决定。窗口「工作流配置」子界面已提供「目标 Terrain」字段（`_terrainField`，窗口会话内临时、不保存 SO），可作为编辑器构建入口的 UI 锚点；运行时亦可由其它代码（如挂载 `TerrainBuilder` 的组件的 Awake）直接调用 `Build()`。⚠️ 当前窗口**尚未接线「构建」按钮**——需补一个调用 `terrainBuilder.Build(_project, _terrainField)` 的按钮，或依赖外部 / 运行时调用。

```
1 PrepareTerrain  尺寸/分辨率/材质（terrainSpec）
2 ApplyHeight     遍历 height 现算 min/max → 归一化 [0,1] → SetHeights
3 ApplyAlphamap   ⭐构建时生成权重（见下）
4 ApplyDetail     按区块动态生成位置（密度/seed/过滤）→ 对象池实例化
5 ApplyTrees       按区块动态生成位置（密度/seed/过滤）→ 对象池实例化
6 PlaceProps       按摆件数据（摆件编辑后续设计）→ 实例化 GameObject
7 PostProcess      碰撞、静态标记、光照贴图参数
```

- **ApplyAlphamap 草案**：逐像素 `L = layerMap[p]`，`base = road[p]>0.5 ? roadLayerWeights[L] : naturalLayerWeights[L]`；对权重>0 的层叠加独立 Perlin 噪声打破条带（`w[i] = base[i] × (1 - blendSoft + blendSoft × n)`），可选按 `distance`（构建时现算归一化）做层边界渐变，归一化 Σw=1 → SetAlphamaps。参数（noiseScale / blendSoft / 是否距离场过渡）**[待设计]**；**seed 均为全局 seed**。
- **L = -1（透明区域）的权重方案 [待设计]**：默认纹理权重或全 0，见阶段 1 的透明区域特殊处理。

### 阶段 8 · 运行时 [待设计]

- 运行时只读 float[][]（主配置 `mapDataFiles` 持 TextAsset 引用，随构建打包）；图片永不参与运行时。
- 形态 **[待设计]**：TerrainBuilder 暴露 `Build()`，具体在 Awake / 场景加载 / 手动调用，由实际项目按需接入；树木/细节由 **`SetCameraPosition(Vector2)`** 按观察点流式生成 / 回收（区块管理器驱动，见阶段 5/6）。

## MapData 存储层 [已完成]

- 接口（主配置 `TerrainPaintProjectSO` 上）：`ReadMap(key)→float[][]` / `WriteMap(key, float[][])` / `DeleteMap(key)` / `HasMap(key)`。
- 文件：`Assets/ai-unity-terrain-edit-workflow/LayerEditor/TerrainGeneratorConfigs/<配置>/MapData/{key}.txt`。
- 格式：CSV，手写解析（无第三方库）。首行元数据头 `#key=...;w=...;h=...`（解析器跳过 `#` 行）；数值 **F3 三位小数**、InvariantCulture（跨平台一致）。
- 引用：主配置持 `mapDataFiles`（`key + TextAsset`），随 SO 打进构建；**编辑器读直接读磁盘文件（保最新）**，**运行时走 TextAsset**。
- 辅助：`MapDataTextureUtils`（float[][]↔Texture2D，仅编辑期显示/采集）。
- key 约定（共 6 个）：`layerMap / height / distance / occupancy / road / offRoad` **[已完成]**。树木/细节位置**不存储**（构建时按区块动态生成，见阶段 5/6）。

## 目录结构

```
Utils/
├── UniformPointGenerator.cs    [已完成] 均匀分布随机点（网格抖动 Jittered Grid，确定性种子可复现）
├── ObjectGroup.cs              [已完成] 摆件组 SO（groupName + GameObject 列表，供摆件编辑复用）
└── ChunkUpdateManager.cs       [已完成] 区块更新管理器（激活/失活区块集合，MoveTo 驱动；供 TerrainBuilder 树木/细节流式生成）

LayerEditor/
├── CsvArrayCodec.cs            [已完成] MapData CSV 手写编解码（元数据头 / F3 / ToJagged / ToFlat）
├── MapDataStore.cs             [已完成] MapData/{key}.txt 文件 IO（写/读/删/存在性）
├── LayerMap.cs                 [已完成] 层图绘制核心（画布 ↔ 层ID float[][]，撤销/填充/线条）
├── LayerPalette.cs             [已完成] 15 预设色（Layer0 恒透明）
├── LayerConfigSO.cs            [已完成] 每层配置（颜色/名称/权重/高度范围/道路参数/最小离路距离/构建时参数；树/细节位置不存储）
├── TerrainPaintProjectSO.cs    [已完成] 主配置（素材池/规则/邻接组/mapResolution/mapDataFiles + MapData 接口）
├── TerrainRoadGen.cs           [已完成] 核心算法（EDT 距离场 / 随机游走 / RGB 合成 / 高度烘焙 float[][]）
├── TerrainBuilder.cs           [进行中] 构建组件（阶段 7：树木/细节区块化对象池生成已实现；高度/纹理/摆件待开发）
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
- 窗口六个子界面：工作流配置（含栅格分辨率） / 区域编辑 / 高度编辑 / 贴图编辑 / 摆件编辑 **[暂留空]** / 树木编辑 **[进行中]**（生成配置编辑可用）/ 细节编辑 **[进行中]**（生成配置编辑可用）。

## 与 unity-python-bridge 的关系 [按需]

- bridge **不参与主链路**，只做按需外围：`mesh.bounds --placed`（量素材尺寸写 ModelFeatures）、`prefab.screenshot`（缩略图）、`terrain.*`（直接读写真实 TerrainData 的命令行通道，共 19 条）。
- 工作流产出高度数据（**真实高度**，构建/桥接时现算归一化到 0~1）可与 bridge `terrain.set_heights` 的 `data` 格式对接。
- **[待设计]** 可选增强：把 `TerrainBuilder.Build` 暴露为 bridge 命令（如 `terrainbuilder.build <配置名>`），实现 Python 端一键构建。
- 主链路不依赖 bridge，关掉一切照常。

## 实施里程碑

- **M1 [已完成]** MapData 存储层（CsvArrayCodec / MapDataStore / SO 接口 / TextureUtils / 窗口接线）。
- **M2 [已完成]** 树木 / 细节子界面配置编辑（每层密度/scale/离路限制/权重）；位置**构建时按区块动态生成**已并入 M3（TerrainBuilder 对象池生成，road&lt;0.5 + offRoad≥对应 limit 过滤）。
- **M3 [待开发]** TerrainBuilder 组件（`Build()` 单一构建函数 + **树木/细节区块化对象池生成（SetCameraPosition 驱动）** + 构建时 alphamap 噪声生成）。
- **M4 [暂留空]** 摆件编辑（位于树木之前，后续设计）。
- **M5 [待设计]** bridge 可选集成（一键构建命令）。

## 待拍板事项（已收敛）

1. ~~树木/细节产出形态~~ → **已定**：每层存构建时参数（密度(个/㎡) + scale(Vector2) + 离路限制）；**位置不存储，构建时按区块动态生成**（禁止全图位置列表驻留，对接区块管理器）；road&lt;0.5 + offRoad≥limit 过滤；构建时按全局 seed + 层权重选原型。
2. ~~摆件编辑~~ → **已定**：位于树木编辑之前，功能暂留空，后续设计。
3. alphamap 构建时噪声参数（noiseScale / blendSoft / 是否用距离场过渡）——构建时再定。
4. ~~导出 JSON~~ → **已定**：不导出。
5. ~~TerrainBuilder 双模式~~ → **已定**：只暴露 `Build()`，构建时机由实际项目按需调用。
6. seed 策略 → **已定**：全部为全局 seed（TreeSeed / DetailSeed 等），无每层 seed。
