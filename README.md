# AiTerrainWorkflow

AI 地形编辑流水线 —— 配置驱动的 AI 地形生成工具。Unity Editor 内闭环完成创作；**依赖 `unity-python-bridge`（bridge 仓库），使用本仓库时默认即可使用 bridge**（见「依赖 · unity-python-bridge」小节）。

C# 代码统一使用命名空间 `AiTerrainWorkflow`。当前版本 **v1.4**（写在 `Editor/TerrainEditWorkflowMenu.cs` 的 `Version` 常量，手动维护）。

> 状态标签说明：**[已完成]** = 已实现并可用；**[待开发]** = 已规划、尚未实现；**[待设计]** = 方向已定、细节待设计；**[暂留空]** = 预留位置、内容待填充。

## 设计原则

| 原则 | 说明 |
|---|---|
| 配置驱动 | 工作流最终产出 = **引用美术/模型素材的配置数据**（主配置 `TerrainPaintProjectSO`）；中间图（layerMap/RGB/高度预览）仅为编辑期可视化，**不再是交付物** |
| 数据优先 | 栅格数据一律以 `float[][]` 为最终形态；编辑器计算结果持久化为 CSV txt（MapData），运行时计算结果只留在本次构建的内存中；图片仅供人看 |
| 单一生成核心 | 完整生成算法与 `TerrainBuilder` 均为运行时代码；编辑器预览和 Player 构建必须调用同一套公开生成入口，不维护两份算法 |
| 编辑器只做适配 | `Editor` 目录只负责配置编辑、资产读写、撤销、按钮与可视化预览；不容纳实际地形生成规则 |
| 构建端分离 | 运行时/编辑器都靠 **TerrainBuilder 组件** 接收主配置构建真实地形（高度/纹理/散布/摆件/定点） |
| 依赖 bridge | 本仓库**依赖** `unity-python-bridge`（bridge 仓库）：C# 命令挂在它的 `BridgeCommand` 框架上，Python CLI 使用其 `unity_bridge` 包；**使用本仓库时默认即可使用 bridge**。主创作链路在 Editor 内闭环，bridge 按需取用（量尺寸/截图/可选一键构建），**不参与主生成链路** |

现已提供完整 manifest 驱动的工作流 bridge 与 Python CLI。命令行修改配置的唯一方式是编辑完整 JSON 并由 C# 整体读取；不提供单字段修改命令。模板和使用方式见 [BRIDGE.md](BRIDGE.md)。

## 依赖 · unity-python-bridge（bridge 仓库）

- **依赖关系**：本仓库**依赖** `unity-python-bridge`（bridge 仓库），两者需放在同一 Unity 项目的 `Assets/` 下。具体依赖点：
  - **C# 侧**：`Editor/Bridge/WorkflowBridgeCommands.cs`、`WorkflowObjectCommands.cs` 与 `Editor/PrefabProcessingUtility.cs` 编译期引用 bridge 程序集（`UnityPythonBridge` 命名空间：`BridgeCommand` / `BridgeContext` / `BridgeArgs`）；没有 bridge 仓库，这些命令代码无法编译。
  - **Python 侧**：`python/workflow_bridge.py` 直接 `from unity_bridge.client import UnityClient`，依赖 bridge 的 `unity_bridge` Python 包。
- **默认可用**：把两个仓库放入同一项目并启动 bridge server（Unity 菜单 `Tools > Unity Python Bridge > Start Server`）后，本仓库的 `workflow.*` 桥接命令与 `unity_bridge` Python 客户端**开箱即用**，无需额外安装或配置。
- **主链路无关**：上述依赖只让本仓库「拥有」bridge 能力；完整的地形生成算法（`TerrainBuilder` 等）是独立运行时代码，不在运行时调用 bridge。

## 分级精度要求（AI 搭建时参考）

当用户要求 AI 搭建地形 / 场景时，以 [GENERATION_LEVELS.md](GENERATION_LEVELS.md) 为分级质量标准：

- **L0 测试**：验证流程 / 单步功能，被要求步骤的数据完整生成；
- **L1 普通地形**：多种 TerrainLayer、备用 Prefab 覆盖率 ≥60%、肉眼合格可玩；
- **L2 高精度成品**：备用 Prefab 覆盖率 ≥90%、bridge 确认效果。

构建前按该文档 §6「交付自查流程」逐项核对，并在交付时声明所达级别与未达标项。

## 名词解释

| 名词 | 含义 |
|---|---|
| 工作流项目 | 工具根目录 `Assets/ai-unity-terrain-edit-workflow/TerrainGeneratorConfigs` 下的一个文件夹；一个文件夹 = 一套完整的地形生成元数据（含主配置 SO + 各层级 SO + 生成组 SO + MapData）。 |
| 主配置 | `TerrainPaintProjectSO`（ScriptableObject）：地形工作流的总配置，聚合素材池 / 规则 / 邻接组 / mapResolution / MapData 接口，是编辑器窗口与 `TerrainBuilder` 的单一数据入口。 |
| 层级配置 | `LayerConfigSO`（ScriptableObject）：单个语义层的颜色、名称、高度范围、道路参数与自然/道路 TerrainLayer 权重；数量 2~16，从属于主配置。 |
| 生成组 | 散布、摆件和定点阶段各自的 ScriptableObject 规则资产：`ScatterConfigSO` / `PropConfigSO` / `FixedPointConfigSO`。 |
| 备用预制体库 | `Generated/Prefabs/` 中所有可被散布、摆件和定点模块引用的 Prefab。根节点为标准 Transform 并挂载 `PrefabStructureInfo`；**标准位置为模型合并 Bounds 的「中心正下方」(center.x, min.y, center.z) 落在根节点原点 (0,0,0)**（见阶段 0）；其子物体可自由调整位置/方向/缩放，也可增删并拼合多个对象。 |

## 完整生成逻辑伪代码

> 本节是所有阶段的目标调用链。编辑器窗口和 Player 必须调用同一套运行时生成函数；两者只有 MapData 保存策略不同。伪代码中标注为「待实现」的部分是已确定调用边界、但当前 `TerrainBuilder` 尚未填充的实现。

### 1. 公共入口与 MapData 生命周期

```text
function CreateGenerationSession(project, terrain, environment):
    require project != null
    require terrain != null

    session.project = project
    session.terrain = terrain
    session.mapData = TerrainMapData.Load(project.mapDataFiles)
    session.persistMapData = (environment == UnityEditor && not Playing)
    return session

function StoreMapData(session, key, value):
    // 两种环境都先写本次生成会话，供后续阶段立即使用
    session.mapData.Set(key, value)

    if session.persistMapData:
        // 仅编辑器非游玩状态持久化
        project.WriteMap(key, value)
        project.RefreshMapDataRefs()
    else:
        // Player 或 Play Mode：不写磁盘，不修改 ScriptableObject
        do nothing

function EnsureMapData(session, key, calculate):
    if session.mapData.Contains(key):
        return session.mapData.Get(key)
    value = calculate()
    StoreMapData(session, key, value)
    return value

function Build(project, terrain, applyThrough):
    session = CreateGenerationSession(project, terrain, CurrentEnvironment)

    ApplyHeight(session)
    if applyThrough < TextureEdit:    return

    ApplyTexture(session)
    if applyThrough < ScatterEdit:    return

    ApplyScatter(session)
    if applyThrough < PropEdit:       return

    ApplyProps(session)
    if applyThrough < FixedPointEdit: return

    ApplyFixedPoints(session)
```

阶段必须从前向后执行。如果高度阶段未执行，贴图及后续阶段也不得执行。

### 2. 区域图与通用坐标

```text
function EnsureLayerMap(session):
    layerMap = session.mapData.Get("layerMap")
    if layerMap == null:
        error "缺少 layerMap"
        abort build
    validate layerMap is rectangular and matches project.mapResolution
    return layerMap

function PixelToTerrainLocal(px, py, mapWidth, mapHeight, terrainSize):
    localX = px / (mapWidth  - 1) * terrainSize.x
    localZ = py / (mapHeight - 1) * terrainSize.z
    return (localX, localZ)

function NormalizedToTerrainWorld(uv, terrain):
    localX = clamp01(uv.x) * terrain.size.x
    localZ = clamp01(uv.y) * terrain.size.z
    worldXZ = terrain.position.xz + (localX, localZ)
    worldY = terrain.position.y + terrain.SampleHeight(worldXZ)
    return (worldXZ.x, worldY, worldXZ.y)
```

首末像素的**中心**分别对齐 Terrain 局部坐标 `0` 与 `size`；因此像素实际覆盖在边界外额外延伸半个像素间距。

### 3. 高度生成与应用（`ApplyHeight`，已实现）

```text
function CalculateHeightMap(project, layerMap):
    for each pixel p:
        # 默认取中心像素所在层的 heightRange
        range = HeightRangeAt(project, layerMap, p)

        # 平滑：十字滤波采样周围一圈图层，按样本占比加权各层 heightRange
        # （等价于 layer 权重混合，如边界处 layer1:40% / layer2:60%）。
        # 区域内部样本全为同一层 → 权重 100% → 与原行为完全一致，
        # 仅图层交界处产生加权过渡，因此只平滑边界、不影响区域内部。
        if project.smoothIterations > 0:
            samples = [center p]
            for k in 1..smoothIterations:
                samples += p ± (k * smoothStep, 0)
                samples += p ± (0, k * smoothStep)
            discard out-of-map samples
            range = average of HeightRangeAt(samples)   # 分别平均 min / max

        worldXZ = p * PixelWorldSize(terrain, mapSize)
        noise = DeterministicNoise(worldXZ, project.heightSeed, project.heightScale)
        height[p] = lerp(range.min, range.max, noise)
    return height

function ApplyHeight(session):
    layerMap = EnsureLayerMap(session)
    height = EnsureMapData(session, "height",
        () => CalculateHeightMap(project, layerMap))

    normalized = ResizeAndSampleBilinear(
        height, terrain.terrainData.heightmapResolution,
        value => clamp01(value / terrain.terrainData.size.y))
    terrain.terrainData.SetHeights(normalized)
```

### 4. 距离场、形状骨架路网与地形贴图（`ApplyTexture`，已实现）

道路生成以 Layer 形状为主要输入，不要求人工锚点：先按邻接组拼合可生成道路的 Layer，分离连通区域，使用世界单位距离场判断区域是否具有走廊特征，再以拓扑保持细化提取中轴骨架。骨架端点的短支刺按世界长度与局部走廊宽度剪除；路面半径取 Layer `roadWidth` 与中轴到边界安全净空的较小值，并严格裁剪在所属邻接组内。`occupancy` MapData 兼容保留，当前语义改为剪枝后的单像素道路中心骨架；`road` 仍为硬路面遮罩。

全局骨架参数位于 `TerrainPaintConfig`：`minimumRoadRegionArea`、`minimumCorridorAspect`、`minimumSkeletonBranchLength`、`spurLengthToWidthRatio`、`roadBoundaryMargin`。

```text
function CalculateRoadMapData(project, layerMap, terrain):
    layerIds = FlattenAndRoundToInt(layerMap)
    w, h = mapSize
    pixelWorldSize = TerrainRoadGen.PixelWorldSize(terrain, w, h)
    validate no layer appears in multiple adjacencyGroups
    groups = TerrainRoadGen.GroupLayers(project)

    distance  = zeros(mapSize)
    occupancy = zeros(mapSize)
    road      = zeros(mapSize)

    for each group in groups:
        groupDistance = TerrainRoadGen.ComputeR(layerIds, w, h, group, pixelWorldSize, out _)
        TerrainRoadGen.GenerateRoads(
            layerIds, groupDistance, w, h, group,
            project.config, project.layers, pixelWorldSize,
            output groupOccupancy, output groupRoad)
        merge groupDistance into distance
        merge groupOccupancy into occupancy
        merge groupRoad into road

    offRoad = TerrainRoadGen.ComputeOffRoad(layerIds, road, w, h, pixelWorldSize)
    return distance, occupancy, road, offRoad

function EnsureRoadMapData(session):
    if any of distance/occupancy/road/offRoad is missing:
        all = CalculateRoadMapData(project, EnsureLayerMap(session))
        // 四项必须作为同一次计算的一致结果整体更新
        StoreMapData(session, "distance",  all.distance)
        StoreMapData(session, "occupancy", all.occupancy)
        StoreMapData(session, "road",      all.road)
        StoreMapData(session, "offRoad",   all.offRoad)

function ApplyTexture(session):
    EnsureRoadMapData(session)
    layerMap = session.mapData.Get("layerMap")
    road = session.mapData.Get("road")
    distance = session.mapData.Get("distance")

    terrainLayers = StableUnion(
        project.naturalTerrainLayers,
        project.roadTerrainLayers)
    terrain.terrainData.terrainLayers = terrainLayers

    for each alphamap sample p:
        layerIndex = SampleNearest(layerMap, p)
        natural = Normalize(naturalWeights * PerLayerNoise(naturalSeed))
        roadSurface = Normalize(roadWeights * PerLayerNoise(roadSeed))
        roadBlend = SampleNearest(road, p) * RoadBlendNoise(roadSeed)
        alphamap[p] = Normalize(lerp(natural, roadSurface, roadBlend))

    terrain.terrainData.SetAlphamaps(alphamap)
```

### 5. 散布生成与流式更新（`ApplyScatter`，已实现）

```text
function ApplyScatter(session):
    EnsureRoadMapData(session)
    for each scatterGroup with index groupIndex:
        runtime.seed = project.scatterSeed XOR Hash(groupIndex)
        runtime.chunks = ChunkUpdateManager(group.chunkSize, group.visibleDistance)
        runtime.prefabPools = CreatePools(group.prefabs where prefab != null and weight > 0)
        runtime.placementsByChunk = PrecomputeAllTerrainChunkPlacements(group)
        register runtime

function SetCameraPosition(worldXZ):
    localXZ = worldXZ - terrain.position.xz
    for each scatter runtime:
        activeChunks, inactiveChunks = runtime.chunks.MoveTo(localXZ)
        for each inactive chunk:
            release every instance to its prefab pool
        for each newly active chunk:
            InstantiatePrecomputedChunk(runtime, chunk)

function PrecomputeScatterChunk(runtime, chunk):
    candidatePixels = all pixels whose centers fall inside chunk bounds
    filter candidatePixels where:
        group.targetLayers contains layerMap[p]
        road[p] <= 0.5
        group.offRoadDistanceRange contains offRoad[p]

    rng = Random(runtime.seed XOR Hash(chunk.index))
    count = floor(validWorldArea * group.density)
    selected = Shuffle(candidatePixels, rng).Take(count)

    for each p in selected:
        prefab = WeightedPick(group.prefabs, rng)
        placements.Add(position, prefab, scale, yaw)

function InstantiatePrecomputedChunk(runtime, chunk):
    for each placement in runtime.placementsByChunk[chunk]:
        instance = prefabPool.Get(placement.prefab)
        apply placement pose and two-point height adaptation
```

### 6. 摆件生成（`ApplyProps`，已实现）

```text
function ApplyProps(session):
    EnsureRoadMapData(session)
    height = EnsureMapData(session, "height", CalculateHeightMap)

    for each propGroup with index groupIndex:
        rng = Random(project.propSeed XOR Hash(groupIndex))
        basisMap = choose by group.arrangementBasis:
            Distance -> distance
            OffRoad  -> offRoad
            Height   -> height

        candidates = pixels where:
            group.targetLayers contains layerMap[p]
            group.arrangementRange contains basisMap[p]

        targetCount = floor(WorldArea(candidates) * group.expectedDensity)
        placed = []
        failedAttempts = 0

        // 先满足每个 prefab 的 minimumCount，再按 weight 随机选取
        prefabSchedule = BuildWeightedScheduleWithMinimums(group.prefabs, targetCount, rng)

        while placed.count < targetCount and failedAttempts < group.maxFailedAttempts:
            batchCandidates = GenerateBatchCandidates(
                candidates, group.batchSize.y,
                group.distributionMode, rng)

            accepted = []
            for each candidate in batchCandidates:
                footprint = ReadPrefabFootprint(candidate.prefab)
                insideRatio = CalculateInsideTargetRatio(footprint, candidate.pose)
                if 1 - insideRatio > group.outOfBoundsTolerance: continue

                // Distance - R1 - R2 > spacing；spacing < 0 时允许互相重叠
                if not SatisfySpacing(candidate, placed, accepted,
                    group.distributionSpacing): continue
                accepted.Add(candidate)

            if accepted.count < group.batchSize.x:
                failedAttempts += 1
                continue

            for each candidate in accepted:
                candidate.rotation = choose by group.rotationMode:
                    TowardHighGradient -> direction of +Gradient(basisMap)
                    TowardLowGradient  -> direction of -Gradient(basisMap)
                    AlongContour      -> perpendicular to Gradient(basisMap)
                    Random            -> uniform 0..360 degrees
                Instantiate candidate.prefab at PixelToTerrainWorld(candidate.pixel)
                placed.Add(candidate)
            failedAttempts = 0
```

`distributionMode` 的候选点规则：`Scatter` 在有效区域独立取点；`Cluster` 以一个中心向周围聚簇；`Extend` 沿已接受物体的局部方向延伸。

实际实现采用视觉优先的蓝噪声近似：每次从 16 个候选中选择与现有摆件净空最大、并受低频噪声轻微调制的点；这保留 Poisson Disk 避免低频团块/空洞的视觉特性，同时能服从现有批次和分布模式。每个候选使用旋转后 Bounds 的 3×3 足迹采样检查目标区域，以水平包围半径执行世界空间间距验收。`minimumCount` 优先逐个尝试，之后才按权重批量生成。

### 7. 定点生成（`ApplyFixedPoints`，已实现）

```text
function ApplyFixedPoints(session):
    for each fixedPointGroup:
        if group.prefab == null: continue
        for each uv in group.positions:
            position = NormalizedToTerrainWorld(uv, terrain)
            instance = Instantiate(group.prefab)
            instance.position = position
            instance.rotation = Euler(0, group.rotationDegrees, 0)
            instance.scale = Vector3.one * group.scale
```

### 8. 编辑器窗口的职责

```text
function EditorWindow.CalculateOrApply():
    edit ScriptableObject configurations
    call the same runtime calculation/build functions shown above
    persist returned MapData through the editor storage adapter
    draw previews from MapData
    never implement an independent terrain generation algorithm

function WorkflowConfig.DrawMapDataPreview():
    for each persisted MapData entry sorted by key:
        data = project.ReadMap(key)
        min, max = FindRange(data)
        preview = NormalizeToGrayscaleTexture(data, min, max)
        draw key, resolution, min/max, preview
```

## 注意 · 距离语义

- **map 与 terrain 的尺寸无固定比例**：map（栅格，尺寸 = 主配置 `mapResolution`）可映射到任意实际 Terrain 尺寸，**可理解为 map 是 terrain 的俯视图**；映射比例在给定 Terrain 后按其实际 X/Z 尺寸计算。
- **像素中心对齐 Terrain 边界**：首个像素中心（数组索引 `(0,0)`）对应 Terrain 局部坐标 `(0,0)`，末个像素中心（数组索引 `(width-1,height-1)`）对应 Terrain 局部坐标 `(terrainSize.x,terrainSize.z)`。口头所称 128×128 工作流的 `(128,128)` 表示其右上边界；实际数组末像素索引为 `(127,127)`。
- **正向映射**：`localX = pixelX / (width - 1) * terrainSize.x`，`localZ = pixelZ / (height - 1) * terrainSize.z`；世界坐标还需加上 `terrain.transform.position`。反向映射为 `pixelX = localX / terrainSize.x * (width - 1)`、`pixelZ = localZ / terrainSize.z * (height - 1)`。
- **边缘外扩半像素**：由于首末像素的中心落在 Terrain 两侧边界，像素图的实际覆盖范围会在 Terrain 四周各超出半个像素间距。这样 Terrain 边缘仍由完整的最外圈像素覆盖，可避免边缘权重、掩码或插值表现异常。反向采样时，离散数据（如 `layerMap` / `road`）取最近像素，连续数据（如 `height` / `distance` / `offRoad`）使用双线性插值；超出范围的采样钳制到最外圈像素。
- **单位约定**：只有区域编辑的操作点和画笔半径使用像素。其余距离参数与距离结果统一使用世界单位。给定 Terrain 后，像素中心间距分别为 `pixelWorldSizeX = terrainSize.x / (width - 1)`、`pixelWorldSizeZ = terrainSize.z / (height - 1)`（`TerrainRoadGen.PixelWorldSize(terrain, w, h)`）；带两轴系数的欧氏距离变换直接输出世界距离。除 `layerMap` 外的所有 MapData（`height/distance/occupancy/road/offRoad`）烘焙都必须指定目标 Terrain 并按其实尺寸计算：CLI（`workflow.bake` / `workflow.run`）自动场景搜索（`terrain` 为空取第一个），编辑器窗口在「应用」页签手动选择。不存在独立的"预览尺度"。
- **示例**：128×128 的 map 映射到 1024×1024 的 Terrain 时，索引 `0` 的像素中心对应局部坐标 `0`，索引 `127` 的像素中心对应 `1024`；相邻中心间距为 `1024 / 127 ≈ 8.063m`，单轴实际覆盖约为 `[-4.0315, 1028.0315]`。
- 例：`distance`、`offRoad`、道路宽度/间距、散布离路范围和摆件间距全部是世界单位。

## 完整工作流

```
工作流配置 → 区域编辑 → 高度编辑 → 贴图编辑 → 散布编辑 → 摆件编辑 → 定点编辑 → 应用
```

| 阶段 | 输入 | 处理 | 产出（载体） | 状态 |
|---|---|---|---|---|
| 0 素材准备 | 美术资产（prefab/贴图/TerrainLayer/模型） | 将源 Prefab 生成为工具内部备用 Prefab（**创建时自动标准化：模型合并 Bounds 的「中心正下方」对齐根节点原点**），再按需调整子物体、方向与组合，并更新 Bounds/Billboard | TerrainLayer 素材池 + `Generated/Prefabs` 备用库 | **[已完成]** |
| 1 区域编辑 | 手绘语义层 | LayerMap 画布绘制，**每笔完写** | `layerMap`（MapData） | **[已完成]** |
| 2 高度编辑 | layerMap + 每层 heightRange | Perlin 插值 → 真实高度 | `height`（MapData） | **[已完成]** |
| 3 贴图编辑 | layerMap + 邻接组 + 权重规则 | 距离场 EDT + 形状感知道路提取 + 离路距离场 | `distance/occupancy/road/offRoad`（MapData） | **[已完成]** |
| 4 散布编辑 | layerMap + 多个散布生成组 | 按组配置目标层级、离路范围、密度、缩放、Prefab 池与流式区块参数 | `ScatterConfig/*.asset`；位置入 `PlacementCache/Scatter_xx.txt` | **[已完成]**（配置编辑 + 分组流式生成） |
| 5 摆件编辑 | 物体库 prefab + `PropConfigSO` | 多候选择优、值域/层级过滤、Bounds 足迹、分布与世界间距约束 | `PropConfig/*.asset`；位置入 `PlacementCache/Prop_xx.txt` | **[已完成]** |
| 6 定点编辑 | layerMap + 定点生成组 | 在只读 layer 图上预览归一化固定位置；每组使用单个 Prefab | `FixedPointConfig/*.asset`；位置入 `PlacementCache/Fixed_xx.txt` | **[已完成]**（配置、位置预览与实际应用） |
| 7 应用 | 主配置 + Terrain + 最终阶段 | `TerrainBuilder.Build(project, terrain, applyThrough)` 按前缀顺序执行 | TerrainData + GameObject | **[已完成]** |

## 阶段详述

### 阶段 0 · 素材准备 [已完成]

- 主配置持有 `naturalTerrainLayers` 与 `roadTerrainLayers`；散布和摆件 Prefab 分别由各自生成组管理。
- **备用预制体规范**（供散布、摆件、定点共同使用）：
  - 三个摆放模块不得直接引用源 Prefab 或工具目录外的任何 Prefab，必须先生成到 `Generated/Prefabs/`。
  - 备用 Prefab 根节点始终保持 position `(0,0,0)`、rotation identity、scale `(1,1,1)`，并挂载 `PrefabStructureInfo`。
  - **标准位置（中心正下方在原点）**：备用 Prefab 的内容应整体平移，使**全部 mesh 变换后的合并 Bounds 之「中心正下方」点 (center.x, min.y, center.z) 落在根节点原点 (0,0,0)**——即模型底部中心就是根节点位置：根节点落在哪里，模型底部就落在哪里。创建时由 `BuildCandidatePrefab` **自动执行**（`StandardizePivotToOrigin`，仅在创建时一次，创建后不再修改其中物体的位置）；对已存在的备用 Prefab 可用 `workflow.prefab.fix_pivot` 按同一规则手动修正（幂等，已标准化的调用不会产生位移）。
  - 根节点下的内容是可编辑的：允许调整子物体位置、方向、缩放，允许增加、删除子物体并拼合多个对象。
  - 再次通过处理工具导入同名备用 Prefab时，只更新结构信息和 Billboard，不覆盖人工修改的内容（**创建时自动标准化只在首次创建执行，已存在的备用 Prefab 不会被再次移动**）。
  - **添加备选 Prefab 后必须确认内容正确**：打开 `Assets/ai-unity-terrain-edit-workflow/Generated/Prefabs/` 下对应的备用 Prefab 检查模型。**pivot 已由创建时自动标准化处理**（见上），一般无需手工修正；若因特殊原因仍需调整（如源 Prefab 形态特殊、或人工拼合后需要重新对齐），可修正子物体的变换使模型按预期放置在原点（通常让模型底部中心落在根节点原点上），或直接调用 `workflow.prefab.fix_pivot` 自动对齐；确认无误后再更新 Bounds / 生成 Billboard。根节点本身不可移动（保持零变换），修正一律作用在子物体上。
  - **跨项目共享（处理一次、全局复用）**：备用 Prefab 是**工具级资产**（位于工具根 `Generated/Prefabs/`），**可被任意主配置对应的工具项目（`TerrainGeneratorConfigs/` 下任意项目）引用**；同一套美术资源（模型 / 贴图）只需处理一次，各项目通过各自生成组引用复用，避免同一模型内容在多个项目中被重复处理。阶段 0 是工具级的一次性处理，新建主配置项目不需要重新处理已处理过的素材。
  - **处理后登记视觉笔记（ModelFeature.md，本地文件）**：处理备用 Prefab 并**查看生成后的效果**（打开 Prefab / 场景视图检查模型落位，或查看模型截图）后，在工具根目录 `ModelFeature.md` 记录该备用 Prefab 的**大致视觉效果**（外形 / 轮廓 / 朝向倾向等**定性**描述），**不记录具体参数等定量内容**（如尺寸数值、数值范围）；**文件不存在则创建**。该文件是**本地笔记**，已被 `.gitignore` 忽略、不随仓库分发，供后续散布 / 摆件 / 定点摆放时参考。
- 摆件生成组使用独立的 `PropConfigSO`，不再复用简单对象列表容器。

### 阶段 1 · 区域编辑 [已完成]

- 画布尺寸 = 主配置 `mapResolution`（创建配置时单选 128/256/512/1024；工作流配置页可改，改动后需重新绘制/烘焙）。
- 区域编辑的持久事实来源是主配置中的 `paintOperations` 绘画操作列表：直线条带记录两点和半径（圆形单击记为两点相同的直线），矩形记录两点，三角形记录三点；每条操作同时记录目标 Layer，擦除记为 Layer0。
- 每画完一笔先把操作追加到列表，再调用 `LayerMap.ApplyPaintOperation` 增量应用；`LayerMap.RebuildFromPaintOperations` 会清空画布并按列表顺序完整重建。撤销通过删除列表最后一条操作并完整重建实现。
- **每画完一笔仍同步写 `MapData/layerMap.txt`**，作为后续高度、贴图和摆放阶段直接消费的栅格结果；操作列表保存在主配置资产中。
- 打开仅有旧 `layerMap`、尚无操作列表的配置时，会按每行连续同层区段一次性迁移为矩形操作，保留原有绘制结果。
- Layer0 恒为透明过渡层；**新建配置默认创建 3 层**（Layer0 透明 + 2 个语义层，`TerrainPaintProjectSO.DefaultLayerCount = 3`），可在工作流配置页增删层至上限 16。其余预设色（共 15 个）可改颜色/名称（颜色解析为层 ID 只此一步，后续流程不接触颜色）。
- **透明区域（未绘制位置 = Layer0 / 层ID = -1）[后续在全局配置补充设置，当前不处理]**：不属于任何语义层的空白位置。其下游行为（高度默认值 / 贴图权重 / 是否生成物体）**后续统一在全局配置中增加透明区域设置项**再落实；当前各阶段对 -1 暂按既有默认处理（高度平地、贴图全 0、不生成位置），不做专门设计、不单独落地规则。

### 阶段 2 · 高度编辑 [已完成]

- 逐点按所在层的 `heightRange`，用像素中心对应的世界 X/Z 坐标采样 Perlin 噪声（`heightSeed` + 世界空间 `heightScale` 频率），插值生成**真实高度**；Build 会按目标 Terrain 实际尺寸重算。
- 真实高度直接写入 `MapData/height.txt`（float[][]，**不归一化**）；**范围不持久化**，由显示 / 构建时遍历数据现算（`ToTexture` 统计后以 `out` 传出）。
- 预览图由窗口用 `MapDataTextureUtils.ToTexture` 生成，**不落盘**。
- 平滑参数（`smoothStep` 步长 / `smoothIterations` 迭代，十字线均值滤波）已实现于 `BakeHeightData`：不是对高度值求均值，而是用十字滤波采样周围一圈图层、按样本占比加权各层 `heightRange`（`smoothIterations=0` 时退化为单像素取层，行为与原实现一致）。区域内部样本全为同一层 → 权重 100% → 高度不变；仅图层交界处产生加权过渡，因此只平滑边界带、不影响区域内部的 Perlin 细节。

### 阶段 3 · 贴图编辑 [已完成]

- 链路：`ParseLayerIds`（色→层ID）→ `GroupLayers`（邻接组，冲突阻断）→ `ComputeR`（带 X/Z 世界间距的 Felzenszwalb 欧氏距离场）→ `GenerateRoads`（世界距离参数换算到栅格，G=占用/间隔缓冲，B=路面硬掩码）→ `ComputeOffRoad`（语义层拼合区域内到最近道路的世界距离）。
- 结果写入四个 MapData key：`distance`（R，世界距离）/ `occupancy`（G）/ `road`（B）/ `offRoad`（世界距离：语义层区域（不含 Layer0）内到最近道路的距离，道路处=0、区域外=0）。Build 时按目标 Terrain 的实际像素中心间距在内存中重算，避免预览比例污染最终结果。
- **offRoad 的无道路语义**：当配置中没有生成任何道路（road 全 0）时，`ComputeOffRoad` 把无穷远距离（≥1e9）视为 0——即"无道路 = 无离路约束"，散布/摆件的 `offRoadDistanceRange` 过滤不会因此误杀全部候选点（此前无路时 offRoad=1e10，任何有限范围都会过滤掉全部像素）。
- **alphamap 最终权重不落盘**：由 TerrainBuilder 在构建时用噪声生成（见阶段 7）。各层只保留权重规则（`naturalLayerWeights` / `roadLayerWeights`，索引 = 对应池 id）。原始自然/道路混合并归一化后，若 `textureSmoothingRadius > 0`，会对每个 alphamap 像素及其上下左右相距该半径的四个像素做五点平均，并再次归一化；边缘采样钳制到最近有效像素。

### 阶段 5 · 摆件编辑 [已完成]

- 位于散布编辑之后。生成时配置**多个生成组**（`PropConfigSO`，由主配置 `TerrainPaintProjectSO` 引用），每组独立描述一类物件的摆放规则；执行挂接 `TerrainBuilder.ApplyProps`。
- `PropConfigSO` 承载每个生成组，资产保存在 `TerrainGeneratorConfigs/<项目>/PropConfig/`；主配置提供单一全局 `propSeed`。`TerrainBuilder.ApplyProps` 已实现确定性候选生成、三种分布、梯度旋转、Bounds 验收、间距和高度适应。

**物体资源规范**（与阶段 0 一致）：
- 生成组只能引用 `Generated/Prefabs/` 中由处理工具创建的备用 Prefab，不能直接引用其它 Prefab。
- 备用 Prefab 根节点保持标准 Transform 并挂载 `PrefabStructureInfo`；**标准位置：mesh 合并 Bounds 的「中心正下方」(center.x, min.y, center.z) 落在原点 (0,0,0)**（创建时自动执行，见阶段 0）；子物体允许任意变换，也允许拼合多个对象。
- 备用 Prefab 内容由用户维护；重复处理不会覆盖内容，Bounds 和 Billboard 可在修改后批量刷新。

**摆件生成组（`PropConfigSO`）参数**：

| 参数 | 语义 | 层级 |
|---|---|---|
| 区块尺寸（Vector2） | 流式区块大小（米，x/z）；与散布/定点一致，为三类摆放生成组共有参数 | 组 |
| 可见距离（float，可 <0） | 区块激活半径（米）；**负数 = 无限模式（一次性全量显示，之后不再变动）** | 组 |
| 失败尝试次数上限 | 单次尝试（生成一批）**失败**（整批被销毁）的次数上限；生成循环在"已放置数达到目标"或"失败次数达上限"时停止 | 组 |
| 预期密度 | 该生成组期望的**单位面积物体数**（密度，如 个/100m² 或 个/m²）；实际目标数量 = round(预期密度 × 该组作用域面积) | 组 |
| 生成规模（Vector2Int） | 控制"单次尝试"的**批量生成**：单次尝试会尝试生成 **`Vector2Int.y` 个物体**；若这批物体中**合法数量 ≥ `Vector2Int.x`**，则保留该批（放置）；否则**完全销毁该次生成的内容**，本次尝试计入一次失败（见失败尝试次数上限） | 组 |
| 目标 layer（Flags） | 该组允许生成的**语义层掩码**（可多选）；候选点 layerMap id 不在掩码内视为越界 | 组 |
| 越界宽容（float，0~1） | 要求**多少比例**的已放置物体必须落在「排列区域 ∩ 排列位置值域 ∩ 目标 layer」内；1 = 严格全部在内，0 = 不约束（软约束容错） | 组 |
| 排列依据（enum） | 选择哪个距离场作为排列依据：`distance` / `offRoad` / `height` 等（复用现有 MapData key） | 组 |
| 排列位置（Vector2） | 生成位置在所选距离场中的**值域范围 [min, max]**（如 offRoad ∈ [2, 5] 米 = 距路边 2~5m 的带） | 组 |
| 旋转（enum） | 摆放旋转策略：**朝向高梯度** / **朝向低梯度** / **朝向等值线** / **任意**；基于排列依据距离场的梯度 / 等值线方向计算（比"朝向道路"更通用，适用于任意场） | 组 |
| 分布形式（enum） | 物体分布形态：**散列**（离散散布）/ **团簇**（成团聚集）/ **延伸**（沿场方向铺展成线/带） | 组 |
| 分布间距（float，可 <0） | 两物体中心间距约束：`Distance − R1 − R2 > 分布间距`（R = 物体半径）；**< 0 时，同一生成组内同一次尝试生成的一批物体允许互相重叠** | 组 |
| 权重 | 该 prefab 在组内被选中生成的相对概率权重 | 组内 prefab |
| 数量下限 | 该 prefab 在本组生成中**至少**要放置的数量（保底） | 组内 prefab |

> 注：实际目标数量由「预期密度 × 作用域面积」决定（作用域面积取该生成组选区面积，待确认是全图 / 语义层 / 手绘范围）；`生成规模` 为**批量生成控制（Vector2Int）**，不再作规模系数乘子；`越界宽容` 为软约束——硬检查（区域 + 位置 + 目标 layer + 分布间距）通过后，按该比例容忍部分物体越界放置。

> ⚠ **摆件设计决策（2026-08-22 补充）**：
> - **不采用坡度 / 高度变化限制**：本项目的地形是为**角色战斗**设计的场地、整体偏平坦，不需要（也不应）按坡度筛除候选点——这与纯自然场景散点（如 StraySpark 按坡度/高度过滤植被）的设计目标不同。
> - **防重叠（最小间距）不放入生成组规则**：将在**阶段 7 构建过程**中，按**实际 Terrain 尺寸**动态计算最小间距并去重（而非基于固定 MapData），以适配各种地形尺寸配置；届时与对象池 / 区块管理器协同避免穿插。
> - **表面对齐 + 高度偏移（贴合地面）将提供用户选项**：后续会在生成组中开放"按什么规则贴合地面"的可配置项（如对齐世界上 / 对齐表面法线 / 随机朝向 + 高度上抬 / 下压偏移），而非写死单一规则；具体选项集落地时再定。

**生成流程（草案）**：选区域（语义层 / 全图 / 手绘范围，待定）+ 载入 `目标 layer` 掩码 → 目标数量 = round(预期密度 × 作用域面积)，已放置 = 0、失败 = 0 → while 已放置 < 目标数量 且 失败 < 失败尝试次数上限：① **单次尝试生成一批**：按 `分布形式` 采样 **`Vector2Int.y`** 个候选点（散列 = 全域随机；团簇 = 团内随机成丛；延伸 = 沿场方向步进成线）；② 逐点合法检查：位置须落「排列依据 距离场 ∈ 排列位置值域 ∩ 目标 layer」，否则记越界（受 `越界宽容` 比例约束）；与已有**非同批**物体满足 `Distance − R1 − R2 > 分布间距`，否则该点不合法；③ **批次判定**：本批合法数量 ≥ **`Vector2Int.x`** → 保留该批合法物体（按 `权重` 选 prefab 并保障各 prefab `数量下限`，按 `旋转` 定朝向 + `表面对齐/高度偏移` 用户选项放置，已放置 += 合法数）；否则**完全销毁本批**、本次尝试计入一次失败 → 收尾校验"区域内"比例 ≥ `越界宽容`，不足且达失败上限则按当前结果收敛（记录警告）。

**与现有一致性**：seed 全局可复现；高度 = `Terrain.SampleHeight`；位置过滤复用 `layerMap` / `offRoad` / `distance` / `height` MapData；摆件在构建期一次性实例化到独立根节点，避免墙段等连续构图被区块切断。

### 阶段 4 · 散布编辑 [已完成]

- 主配置只保留一个全局 `scatterSeed`，不再区分树木与细节。
- 每个 `ScatterConfigSO` 表示一个散布生成组，配置：区块尺寸、可见距离、Prefab 池、密度、随机缩放范围、离路距离范围（Vector2 min/max）和目标层级（`TerrainWorkflowLayerMask` Flags）。
- **区块尺寸与可见距离是所有摆放类生成组（散布/摆件/定点）共有的参数**：`chunkSize`（Vector2，米）与 `visibleDistance`（float，米）。**可见距离为负数 = 无限模式**：第一次驱动时一次性激活全部已注册区块（相当于全图实例化），之后更新不再有任何变动（active/inactive 恒空），适合全量静态场景；非负数时保持按观察点距离的流式激活/回收。
- 配置资产统一存放在 `TerrainGeneratorConfigs/<项目>/ScatterConfig/`；编辑器中的“添加散布生成组”会创建独立 `.asset`，删除组时同步删除对应资产。
- 散布位置不落 MapData。`TerrainBuilder.Build` 会遍历地形覆盖的全部区块，预计算每个区块的位置、Prefab、缩放和偏航列表；`SetCameraPosition(Vector2)` 只负责按可见距离从对象池实例化或回收。目标层级与 `offRoad` 范围过滤通过后，按密度选择像素中心、按 Prefab 权重随机选择，并使用全局 seed ⊕ 组 index ⊕ 区块 index 保证结果可复现。
- 启用 `twoPointHeightAdaptation` 的 Prefab 会按缩放和 Y 旋转计算 Bounds X 两端的世界位置，分别采样 Terrain 高度并取平均值作为根节点 Y；散布和定点使用同一规则。
- 最外圈像素与 Terrain 的映射遵守“像素中心对齐 Terrain 边界”规则；世界观察点在进入区块系统前转换为 Terrain 局部 X/Z 坐标。

### 阶段 6 · 定点编辑 [已完成]

- 界面左侧只读显示当前 layer 图，右侧编辑多个定点生成组。
- 每组配置标识颜色、单个 Prefab、归一化位置列表（X/Y 均为 0~1）、Y 轴旋转角度（0~360°）和统一缩放，以及三类摆放生成组共有的 `chunkSize` / `visibleDistance`（负数 = 无限模式全量显示）。
- layer 图按标识颜色绘制每个位置；标记为带黑色外框的圆点，Y 坐标向上对应 Terrain 的 Z 方向。
- 每组资产保存在 `TerrainGeneratorConfigs/<项目>/FixedPointConfig/`。`TerrainBuilder.ApplyFixedPoints` 会按归一化位置映射 Terrain X/Z，并应用配置的 Y 旋转、缩放和高度适应后实例化。

### 阶段 7 · 应用 [已完成]

- 编辑器的「应用」子界面选择目标 Terrain 和最终阶段，已接线 `TerrainBuilder.Build(project, terrain, applyThrough)`。
- 执行顺序固定为 `ApplyHeight → ApplyTexture → ApplyScatter → ApplyProps → ApplyFixedPoints`；未勾选前置阶段时，后续阶段不执行。
- 当前 `ApplyHeight / ApplyTexture / ApplyScatter / ApplyProps / ApplyFixedPoints` 均已实现。
- 散布不一次性创建全图实例：`Build` 初始化各生成组的 `ChunkUpdateManager` 与对象池，之后由 `SetCameraPosition(Vector2)` 驱动生成和回收。

- **ApplyAlphamap 草案**：逐像素 `L = layerMap[p]`，`base = road[p]>0.5 ? roadLayerWeights[L] : naturalLayerWeights[L]`；对权重>0 的层叠加独立 Perlin 噪声打破条带（`w[i] = base[i] × (1 - blendSoft + blendSoft × n)`），可选按 `distance`（构建时现算归一化）做层边界渐变，归一化 Σw=1 → SetAlphamaps。参数（noiseScale / blendSoft / 是否距离场过渡）**[待设计]**；**seed 均为全局 seed**。
- **L = -1（透明区域）的权重方案 [待设计]**：默认纹理权重或全 0，见阶段 1 的透明区域特殊处理。

### 阶段 8 · 运行时 [进行中]

- 运行时只读 float[][]（主配置 `mapDataFiles` 持 TextAsset 引用，随构建打包）；图片永不参与运行时。
- `TerrainRoadGen`、`DistanceFieldGenerator`、`UniformPointGenerator`、`ChunkUpdateManager` 等核心算法均不依赖 `UnityEditor`，Player 中可直接调用。
- `TerrainBuilder.Build(projectConfig, terrain, applyThrough)` 是编辑器与运行时共用的唯一实地形应用入口；窗口的「应用」按钮只是对该入口的编辑器包装。
- 散布阶段由 `SetCameraPosition(Vector2)` 按生成组流式生成 / 回收（区块管理器驱动，见阶段 5）。
- 后续构建增强也必须实现在运行时的 `TerrainBuilder` 或其运行时服务中，不得实现在 `LayerEditorWindow`。

## MapData 存储层 [已完成]

- 生命周期：编辑器中计算的 MapData 通过 `WriteMap` 长期保存；Player 中新计算的 MapData 只写入当次 `TerrainBuilder.MapData`（`TerrainMapData`），不写磁盘、不修改配置资产。
- 接口（主配置 `TerrainPaintProjectSO` 上）：`ReadMap(key)→float[][]` / `WriteMap(key, float[][])` / `DeleteMap(key)` / `HasMap(key)`。
- 文件：`Assets/ai-unity-terrain-edit-workflow/TerrainGeneratorConfigs/<配置>/MapData/{key}.txt`。
- 格式：CSV，手写解析（无第三方库）。首行元数据头 `#key=...;w=...;h=...`（解析器跳过 `#` 行）；数值 **F3 三位小数**、InvariantCulture（跨平台一致）。
- 引用：主配置持 `mapDataFiles`（`key + TextAsset`），随 SO 打进构建；**编辑器读直接读磁盘文件（保最新）**，**运行时走 TextAsset**。
- 辅助：`MapDataTextureUtils`（float[][]↔Texture2D，仅编辑期显示/采集）。
- 预览：「工作流配置 → MapData 预览」枚举当前配置已持久化的全部 MapData，每项独立归一化为灰度图并显示 key、分辨率与 min/max。
- key 约定（共 6 个）：`layerMap / height / distance / occupancy / road / offRoad` **[已完成]**。散布位置不入 MapData（构建时按生成组与区块动态生成，见阶段 5）；放置位置缓存独立存于 `PlacementCache/`（见下节）。

## 放置缓存存储层（PlacementCache）[已完成]

- **位置缓存按「类 × 生成组」独立成文件**，目录 `TerrainGeneratorConfigs/<项目>/PlacementCache/`（与 `MapData/` 同级，不混放）；每类每生成组一个 txt：`Scatter_xx.txt` / `Prop_xx.txt` / `Fixed_xx.txt`（xx = 组索引）。
- 文件头统一包含：指纹（`#fingerprint=`，覆盖该组全部生成输入，任一参数/内容变化 → 指纹不同 → 缓存失效）、**区块尺寸（`#chunkSize=`）与可见距离（`#visibleDistance=`）**，便于按文件管理各组的流式参数；随后是数据行：
  - `Scatter`：`instanceId, pixelX, pixelZ, scale, yaw`（像素坐标，运行时按 Terrain 尺寸换算世界）
  - `Prop`：`instanceId, worldX, worldZ, yaw`
  - `Fixed`：`instanceId, worldX, worldZ, yaw, scale`
- 生命周期：**编辑器（非运行时）Build** 逐组计算并写各自文件，`RefreshPlacementCacheRefs` 更新主配置 `placementCacheFiles`（`key=文件名 + TextAsset`）引用并 `SaveAssets`；**运行时 Build** 逐组读取对应文件、校验该组指纹，一致则直接复用位置（散布流式区块照常驱动，摆件/定点直接实例化），不一致或无缓存则**只重算不写盘**。
- 指纹输入：Terrain 尺寸/位置、全局种子、每层道路参数、邻接组、layerMap 内容、该组全部参数与每个 prefab 的 instanceId+权重。layerMap/road/offRoad 等派生数据不入指纹（由输入推导）。
- 删除生成组时窗口同步删除对应缓存文件与主配置引用；定点位置本为手动配置，同样入缓存以统一"编辑器计算 → 运行时复用"链路。
- 引用解析用 **instanceId**：同一 Unity 会话内稳定（编辑器 Build → 编辑器 Play 命中）；跨会话/Player 失效时自动回退重算（不写盘）。

## 目录结构

```
Utils/
├── DistanceFieldGenerator.cs   [已完成] bool[][] → int[][] 四邻域 BFS；false=0，可选边界源，无源时报错并返回全 0
├── UniformPointGenerator.cs    [已完成] 均匀分布随机点（网格抖动 Jittered Grid，确定性种子可复现）
└── ChunkUpdateManager.cs       [已完成] 区块更新管理器（激活/失活区块集合，MoveTo 驱动；供 TerrainBuilder 分组流式生成）

LayerEditor/
├── CsvArrayCodec.cs            [已完成] MapData CSV 手写编解码（元数据头 / F3 / ToJagged / ToFlat）
├── MapDataStore.cs             [已完成] MapData/{key}.txt 文件 IO（写/读/删/存在性）
├── LayerMap.cs                 [已完成] 层图绘制核心（画布 ↔ 层ID float[][]，撤销/填充/线条）
├── LayerPalette.cs             [已完成] 15 预设色（Layer0 恒透明）
├── LayerConfigSO.cs            [已完成] 每层配置（颜色/名称/高度范围/道路参数/TerrainLayer 权重）
├── TerrainPaintProjectSO.cs    [已完成] 主配置（素材池/规则/邻接组/mapResolution/mapDataFiles + MapData 接口）
├── TerrainRoadGen.cs           [已完成] 核心算法（EDT 距离场 / 随机游走 / RGB 合成 / 高度烘焙 float[][]）
├── ScatterConfigSO.cs          [已完成] 单个散布生成组配置
├── PropConfigSO.cs             [已完成] 单个摆件生成组配置与实际放置
├── FixedPointConfigSO.cs       [已完成] 单个定点生成组配置与实际应用
├── PrefabStructureInfo.cs      [已完成] 候选 Prefab 结构信息（Bounds、BillboardMode、运行时面片朝向 + 静态更新入口）
├── TerrainBuilder.cs           [已完成] 高度/贴图/散布/摆件/定点构建组件
└── Editor/
    ├── LayerEditorWindow.cs    [已完成] 八阶段工作流窗口（散布生成组 + 最终应用页 + MapData 接线）
    └── MapDataTextureUtils.cs  [已完成] float[][]↔Texture2D（仅显示/采集）

Editor/
├── TerrainEditWorkflowMenu.cs  [已完成] 菜单入口（Tools / Terrain Edit Workflow）
├── PrefabProcessingUtility.cs  [已完成] 构建候选包装 Prefab；批量更新 Billboard 与完整变换 Bounds
└── Bridge/
    ├── WorkflowBridgeCommands.cs   [已完成] 工作流专属桥接命令（workflow.configure/export/prefab.*/bake/validate/build/run，挂在 bridge 的 [BridgeCommand] 框架上）
    └── WorkflowObjectCommands.cs   [已完成] workflow.prefab.fix_pivot（备用 Prefab pivot 标准化，幂等）

python/
├── workflow_bridge.py          [已完成] 工作流 CLI（configure / export / run 三个子命令，依赖 unity_bridge 包）
├── manifest.example.json       [已完成] 完整 manifest 模板（非片段）
├── height_test_manifest.json   [已完成] 高度生成测试 manifest
├── road_generation_test_manifest.json [已完成] 路网生成测试 manifest
└── road_generation_test_check.py [已完成] 路网生成结果校验脚本

```

工具根目录下、与脚本目录并列的地形生成元数据目录：

```text
Assets/ai-unity-terrain-edit-workflow/TerrainGeneratorConfigs/
└── <ProjectName>/
    ├── <ProjectName>.asset       主配置 TerrainPaintProjectSO
    ├── Layer_00.asset ...       语义层配置
    ├── ScatterConfig/           散布生成组
    ├── PropConfig/              摆件生成组
    ├── FixedPointConfig/        定点生成组
    ├── MapData/                编辑器持久化的栅格数据
    └── PlacementCache/          放置位置缓存（每类×每生成组一个 txt，与 MapData 同级）
```

## 菜单与窗口 [已完成]

- `Tools / Terrain Edit Workflow / Log Version`：打印版本号。
- `Tools / Terrain Edit Workflow / Open Terrain Paint Workflow`：打开工作流窗口。
- 窗口八个子界面按流程排列：工作流配置 / 区域编辑 / 高度编辑 / 贴图编辑 / 散布编辑 / 摆件编辑 / 定点编辑 / 应用。散布编辑按生成组配置；应用页选择目标 Terrain 与连续应用阶段。

## 备用预制体与文件隔离规范

- 散布、摆件、定点三个摆放模块只能引用经 `PrefabProcessingUtility.BuildCandidatePrefab` 处理的备用 Prefab，不能直接引用原始素材 Prefab。
- 备用 Prefab 必须位于 `Assets/ai-unity-terrain-edit-workflow/Generated/Prefabs/`，根节点必须挂有 `PrefabStructureInfo`，且根 Transform 必须为 position `(0,0,0)`、rotation identity、scale `(1,1,1)`。
- 首次生成使用空的标准根节点包装源 Prefab；生成后可自由调整所有子物体的变换，并可增加、删除或拼合多个对象。再次处理同名备用 Prefab 不会重建或覆盖这些人工修改，只更新结构信息与 Billboard。
- **添加备选 Prefab 后必须到 `Generated/Prefabs/` 确认内容正确**：部分源 Prefab 的模型**中心正下方不在原点**（模型 pivot 不在底部中心，或底部与原点存在偏移），需要调整备用 Prefab 的子物体变换，使模型按预期落位（通常令模型底部中心对准根节点原点）；确认无误后再更新 Bounds 或生成 Billboard。修正只作用于子物体，根节点保持零变换。
- 工具产生的主配置、生成组和 MapData 保存在工具内的 `TerrainGeneratorConfigs/`；所有备用 Prefab、Billboard 图片和派生材质集中保存在 `Generated/`，不会直接散落在工具根目录。删除工具目录会同时移除全部工具产物，不会在原项目其他目录留下生成文件，也不会修改原始素材资产。
- **跨项目共享 + 视觉笔记**：备用 Prefab 为工具级资产，可被多个主配置对应的工具项目共享引用（同一套素材只处理一次，不随项目重复处理）；确认效果后须在工具根目录 `ModelFeature.md` 登记其**大致视觉效果**（定性描述，不记定量参数），该文件为本地笔记（`.gitignore` 忽略、不进版本库），文件不存在则创建。详见阶段 0。
- `PrefabStructureInfo.billboardMode` 可选：不使用 LOD、使用十字面片、一字面片朝向相机、一字面片仅偏航转向。朝向相机模式每帧令 Billboard 子节点 rotation 完全等于 MainCamera rotation；仅偏航模式只跟随相机 Y 角。**适用性：Billboard 仅建议用于树木、草丛等远观只求轮廓/体量的对象；房屋、建筑等不规则或轮廓关键的对象应保持 `billboardMode = None`，见 [GENERATION_LEVELS.md](GENERATION_LEVELS.md) §0 G8。**
- 批量添加在 BillboardMode 非 None 时会立即完成截图、材质、面片和 LOD 装配；批量更新 Billboard 可在之后统一重建。截图固定来自 `(0,0,1)`，并使用强度 2 的相机同向平行光；每个备用 Prefab 使用 `src/billboard.mat` 为模板创建独立的透明、双面、无阴影材质，并自动装入 `src/cross.prefab` 或 `src/linear.prefab`。两个面片 Prefab 均使用标准根 Transform，模型为第一个子物体；缩放基准为宽 2m、高 1m。面片 X/Z 中心对齐 Bounds 中心，底部枢轴对齐 Bounds 的 Y 最低点。
- 生成目录固定为：`Generated/Prefabs/`（备用 Prefab）、`Generated/Billboards/`（PNG）、`Generated/Materials/`（派生材质）。`Generated/` 已从工具源码版本控制中排除。
- Billboard 生成后自动配置根节点 `LODGroup`：原模型 Renderers 为 LOD0，面片 Renderers 为 LOD1；**LOD0→LOD1 的切换阈值 = 处理预制体界面的「LOD Billboard 切换阈值」滑块（0~1，默认 0.1）**，LOD1 在 1% 时剔除。该阈值只是处理时的输入参数，不写入任何工作流配置，最终落在每个 Prefab 的 LODGroup 组件上。
- **已知问题（待后续解决）**：透明交叉面片存在渲染遮挡/排序异常，特定视角下可能表现为其中一个面片始终位于另一个面片前方。当前 Shader 的透明深度方案不能覆盖所有交叉透明面的排序情形，后续需要专门调整渲染方案。
- 应用 Terrain 前会扫描散布、摆件、定点的全部 Prefab 引用；空引用、不是 `Generated/Prefabs/` 中的备用 Prefab、缺少 `PrefabStructureInfo`、根 Transform 未归一化，以及已启用 Billboard 但缺少有效 `LODGroup`/面片都会阻止应用，并显示具体生成组和资源路径。
- 工作流配置页的备用预制体区域提供：批量创建备用 Prefab、批量生成 Billboard、按需更新 Bounds、强制更新 Bounds；应用页仅保留目标 Terrain、应用阶段和执行入口。

## 与 unity-python-bridge 的关系 [已完成]

- bridge 仍不参与 Unity 编辑器主链路；本项目通过项目内 Editor 扩展注册命令，依赖 bridge 而不修改它。
- `workflow.configure` 是唯一配置写入口，`workflow.run` 读取同一份完整 JSON 并继续完成 Prefab 处理、区域重建、派生图烘焙、应用前校验和 Terrain 构建。
- manifest 的 `terrain` 可留空；`workflow.run` 会自动使用场景中找到的第一个 Terrain。填写名称时则精确查找，场景中没有 Terrain 会直接报错。
- Python CLI、命令参数及 manifest 说明见 [BRIDGE.md](BRIDGE.md)。关掉 bridge 时 Unity 编辑器工作流照常使用。

## 实施里程碑

- **M1 [已完成]** MapData 存储层（CsvArrayCodec / MapDataStore / SO 接口 / TextureUtils / 窗口接线）。
- **M2 [已完成]** 散布生成组配置编辑（区块、可见距离、Prefab 权重池、密度、缩放、离路范围、目标层级）及分组流式生成。
- **M3 [已完成]** TerrainBuilder 高度、构建时 alphamap、全区块散布位置预计算及对象池流式生成已完成。
- **M4 [已完成]** 摆件和定点生成组配置、预览与实际应用已完成。
- **M5 [已完成]** bridge 可选集成：完整 JSON 配置模板、整体导入和 manifest 驱动的一键构建。

## 待拍板事项（已收敛）

1. ~~散布编辑产出形态~~ → **已定**：规则按 `ScatterConfigSO` 生成组存储；位置不持久化，构建时按区块、目标层级与 offRoad 范围动态生成。
2. ~~摆件编辑~~ → **已定（2026-08-22）**：使用 `PropConfigSO` 生成组，包含失败上限、密度、批量规模、层级掩码、值域、旋转、分布、间距以及带权重/数量下限的 Prefab 池。
3. alphamap 构建时噪声参数（noiseScale / blendSoft / 是否用距离场过渡）——构建时再定。
4. ~~导出 JSON~~ → **已定**：不导出。
5. ~~TerrainBuilder 双模式~~ → **已定**：只暴露 `Build()`，构建时机由实际项目按需调用。
6. seed 策略 → **已定**：散布与摆件分别使用主配置的 `scatterSeed` / `propSeed`，再与生成组和区块索引混合以保证可复现。
