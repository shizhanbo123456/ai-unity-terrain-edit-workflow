# 分级精度规则（GENERATION LEVELS）

> AI 地形生成工作流（AiTerrainWorkflow v1.4）的质量分级要求。
> 用途：当以不同精度要求生成地形时，本文件给出每一级必须满足的具体要求与验收标准。
> 版本：v1.1（2026-08-24 修订：明确 Billboard 适用性——仅树木等「看个大概」对象可用，房屋等不规则物体禁用；README 增加引用入口）

## 0. 通用规则（所有级别强制，不分级）

| # | 规则 | 说明 |
|---|---|---|
| G1 | 可复现性 | 全部 seed（heightSeed / naturalSeed / roadSeed / scatterSeed / propSeed）必须显式设置；同一完整 manifest 重复执行结果一致 |
| G2 | 引用规范 | 散布 / 摆件 / 定点三个摆放模块只能引用 `Generated/Prefabs/` 下的备用 Prefab；根节点保持零变换（position 0,0,0 / rotation identity / scale 1,1,1）并挂载 `PrefabStructureInfo`；不得直接引用源 Prefab |
| G3 | 配置完整性 | 使用完整 manifest 导入（或窗口完整配置）；不允许靠缺字段默认值顶替；C# 会拒绝缺少必需顶层集合的 manifest |
| G4 | 距离语义 | 仅区域绘画操作的坐标与画笔半径使用像素；高度、道路宽度/间距、散布间距、离路范围、可见距离等一律按世界米解释 |
| G5 | 数据落盘 | 编辑器阶段计算的 MapData / PlacementCache 必须实际生成；测试级尤其要逐项核对文件存在且非空 |
| G6 | 构建前校验 | `workflow.validate` 必须通过（空引用 / 非备用 Prefab / 根变换异常 / Billboard 缺 LODGroup 都会阻断应用）；校验失败 = 该级别不达标 |
| G7 | 交付记录 | 交付时声明所用级别；任何未达标项必须明确列出（不允许静默降级） |
| G8 | Billboard 适用性 | **只对「远观只求轮廓/体量」的对象启用 Billboard**：树木、灌木、草丛、远处简单岩石等高密度/低细节对象。<br>**房屋、建筑等不规则或轮廓关键的对象必须 `billboardMode = None`**（保持全模型渲染），否则视角变化时面片形状失真、穿帮。判断标准：远景下该对象的轮廓/结构是否仍需可辨——需可辨 → 不用；只剩色块与体量 → 可用 |

## 1. 级别总览

| 级别 | 名称 | 适用场景 | 一句话验收标准 |
|---|---|---|---|
| L0 | 测试验证 | 验证流程 / 单步功能 | 被要求步骤的数据**完整生成**，Validate 通过 |
| L1 | 普通地形 | 正式游戏场地 | 多种 TerrainLayer + 中等素材覆盖，肉眼合格、可玩 |
| L2 | 高精度成品 | 最终 / 展示场景 | 备用 Prefab 使用率 **≥90%**，全部素材参与，bridge 确认效果 |

三级之间为递进关系：L1 必须满足 L0 的全部通用约束，L2 必须满足 L1 的约束。

## 2. L0 · 测试验证

**核心目标**：完整执行工作流每一步（或被特别指定测试的某一步）并产出对应数据。

| 维度 | 要求 |
|---|---|
| 数据完整性（核心） | **全流程测试**：8 个阶段全部执行 → 6 个 MapData key（`layerMap / height / distance / occupancy / road / offRoad`）+ 每类 PlacementCache（`Scatter_xx.txt / Prop_xx.txt / Fixed_xx.txt`）+ 全部配置资产，逐项核对存在且非空。<br>**单步测试**：只执行被指定步骤及其前置步骤；被测试步骤的产出必须完整；未测步骤在交付记录中声明「未测试」 |
| 素材准备 | 被引用模块至少 1 个备用 Prefab（经 `PrefabProcessingUtility` 处理，位于 `Generated/Prefabs/`） |
| 区域编辑 | ≥1 条绘画操作落盘，`layerMap.txt` 生成；覆盖 ≥1 个语义层（建议 2 个） |
| 高度编辑 | 测到该步则必须产出 `height.txt`（真实高度，不归一化） |
| 贴图编辑 | 测到该步则必须产出 `distance / occupancy / road / offRoad` 四件套；TerrainLayer 可最小化（≥2 个） |
| 散布编辑 | ≥1 个生成组 + `PlacementCache/Scatter_00.txt`；每组 1 个备用 Prefab 即可 |
| 摆件编辑 | ≥1 个生成组 + `PlacementCache/Prop_00.txt` |
| 定点编辑 | ≥1 个生成组 + `PlacementCache/Fixed_00.txt`（可仅 1~2 个位置） |
| 应用（若要求） | `workflow.validate` 通过 + 构建成功，Terrain 实际生效 |
| **通过标准** | 所有被要求步骤的数据文件存在且非空；无阻断性错误；Validate 通过 |
| 备注 | 允许 128/256 小分辨率；允许最小配置（速度优先） |

## 3. L1 · 普通地形（标准）

**核心目标**：正式游戏场地，质量与性能平衡。

| 维度 | 要求 |
|---|---|
| 配置 | 分辨率 512（推荐）；全部 seed 显式设置；完整 manifest 导入 |
| TerrainLayer | **多种**：自然层 ≥2 + 道路层 ≥1（合计 ≥3）；每个语义层权重明确，不允许全 0 层 |
| 区域编辑 | layerMap 覆盖目标区域 ≥70%；有效语义层 ≥3 个区域；绘画操作列表完整（可重建） |
| 高度编辑 | 每层 heightRange 合理；`smoothIterations ≥ 1`，图层边界过渡自然 |
| 贴图编辑 | 道路网连通、宽度合理；alphamap 噪声开启，无肉眼条带；`textureSmoothingRadius ≥ 1` |
| 散布编辑 | ≥2 个生成组；每组 ≥2 个候选 Prefab；密度按区域语义设置（林地密、岩石疏）；离路范围过滤生效 |
| 摆件编辑 | ≥1 个生成组；旋转策略（梯度 / 等值线）+ 间距约束；无明显重叠穿模 |
| 定点编辑 | ≥1 组、≥5 个位置；摆放自然、不对称 |
| 素材覆盖率 | 备用 Prefab 整体覆盖率 **≥60%**（每个可用备用 Prefab 至少被一个生成组以 weight>0 引用） |
| Billboard | 仅对适用对象启用（见 G8）：树木 / 草丛等「看个大概」的高密度对象优先；房屋等不规则物体**不启用**（billboardMode = None） |
| 应用 | Validate 通过 + 完整构建（ApplyThrough 至定点阶段） |
| **通过标准** | 贴图无条带、无穿模、分布合理、可进入游戏测试 |

## 4. L2 · 高精度成品

**核心目标**：最终 / 展示场景，最大化素材利用与视觉效果。

| 维度 | 要求 |
|---|---|
| 配置 | 分辨率 512 或 1024；seed 显式设置 |
| TerrainLayer | **全部可用素材参与**：natural + road 池的 TerrainLayer 全部引用；权重按区域语义精调 |
| 备用 Prefab 覆盖率 | **≥90%**：每个可用备用 Prefab 至少被一个生成组以 weight>0 引用；未用到的可用素材 ≤10%；生成组引用全部指向 `Generated/Prefabs/` |
| 生成组 | 按区域类型拆分，每类 ≥3 个生成组；分布形式（散列 / 团簇 / 延伸）混合使用 |
| 摆件 | 多组、多旋转策略混合、间距精细；结合高度做表面对齐与偏移 |
| 定点 | 重要地标 / 建筑群**手工语义摆放**（非随机），位置有设计意图 |
| Billboard / LOD | 按 G8 区分对象类型：树木 / 草丛等适用对象配置 Billboard（cross / linear 面片 + LOD 切换阈值）；房屋等不规则 / 细节对象 `billboardMode = None`，保持全模型渲染，**不强制面片替换** |
| bridge 确认（bridge 可用时必须） | ① `workflow.validate` 应用前校验全过；② `view.camera` 截图 / `prefab.screenshot` 视觉确认；③ `mesh.bounds` / `prefab.bounds` 量尺寸核对比例；④ `terrain.get_heights` / `terrain.get_alphamaps` / `terrain.list_trees` 抽查真实 Terrain 数据；⑤ `scene.tree` 检查场景结构；⑥ `workflow.object.*` / `prefab.edit` 微调。<br>**bridge 不可用时**：以编辑器窗口 + Scene 视图检查替代，并在交付记录中注明「未用 bridge 确认」 |
| **通过标准** | 截图验收（构图自然、无穿模、无大片空洞）；validate 全过；构建成功；prefab 覆盖率 ≥90% |

## 5. 桥接工具速查（供 L2 引用）

工作流侧（`python/workflow_bridge.py` 或原生命令）：

| 命令 | 用途 |
|---|---|
| `workflow.validate` | 应用前校验（L0/L1/L2 构建前必须通过） |
| `workflow.build` / `workflow.run` | 构建真实 Terrain / 完整 manifest 流程 |
| `workflow.object.instantiate` / `destroy` | 场景物体实例化 / 销毁（微调） |
| `workflow.prefab.edit` / `remove` / `instantiate` | 备用 Prefab 资产内部编辑 |

unity-python-bridge 侧（依赖同一 Unity 项目，不参与主链路）：

| 命令 | 用途 |
|---|---|
| `view.camera` | 相机视角截图（视觉验收） |
| `prefab.screenshot` / `prefab.bounds` / `prefab.billboard` | 素材截图 / Bounds 量测 / Billboard 处理 |
| `mesh.bounds` | 场景物体 Bounds 量测（核对比例） |
| `scene.tree` / `prefab.tree` | 场景 / Prefab 层级检查 |
| `terrain.list` / `terrain.get_heights` / `terrain.get_alphamaps` / `terrain.get_layers` / `terrain.list_trees` / `terrain.list_details` | 抽查真实 TerrainData |
| `terrain.set_heights` / `terrain.set_alphamaps` / `terrain.set_details` / `terrain.add_trees` / `terrain.stash` / `terrain.apply_stash` | 直接读写 TerrainData（外围工具，按需取用） |
| `gameobject.get` / `gameobject.set` | 场景物体 Transform 读取 / 修改 |
| `bridge.ping` / `bridge.version` | 连通性 / 版本确认 |

## 6. 交付自查流程

1. 确认目标级别（L0 / L1 / L2），如被指定单步测试则记录具体步骤。
2. 按对应级别表格逐项核对；L0 重点核对数据文件清单，L2 重点核对覆盖率与 bridge 确认记录；所有级别核对 Billboard 适用性（G8）。
3. 运行 `workflow.validate`，记录通过情况。
4. 若要求构建，运行 `workflow.build`（或 `run`），确认 Terrain 实际生效。
5. 输出交付记录：目标级别、实际执行步骤、数据文件清单、validate 结果、覆盖率、bridge 使用情况、未达标项列表。
