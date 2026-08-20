# AiTerrainWorkflow

AI 地形编辑工作流 —— 独立工具仓库，存放地形编辑相关的算法与工具类。

C# 代码统一使用命名空间 `AiTerrainWorkflow`。

## 目录结构

```
Utils/
├── UniformPointGenerator.cs    # 均匀分布随机点生成（网格抖动 Jittered Grid）
└── ObjectGroup.cs              # ObjectGroup ScriptableObject（组名 + GameObject 列表）

LayerEditor/                    # 地形贴图工作流（层次图 → 距离场 → 路网 → 贴图）
├── LayerMap.cs                 # 层图数据：CPU 读写图片 + 圆形/矩形/三角绘制算法
├── LayerPalette.cs             # 层级预设色：16 个内置色 + 默认名称（创建配置时初始化）
├── LayerConfigSO.cs            # 每层一个的层级配置 SO（颜色/名称 + 贴图 + 道路参数）
├── TerrainPaintProjectSO.cs    # 总配置 SO（全局配置 + 16 层级 SO + 层次图 + 矩阵 + 结果）
├── TerrainRoadGen.cs           # 核心算法：层ID解析/组合分组/距离场R/随机游走G+B/RGB合成
└── Editor/
    └── LayerEditorWindow.cs    # 四子界面工作流窗口（Tools / Terrain Edit Workflow / Open Terrain Paint Workflow）

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

## 地形贴图工作流窗口（四子界面）

改造自原 LayerEditor 绘画窗口。**窗口本身不存储持久数据**——所有信息从总 SO（`TerrainPaintProjectSO`）加载，修改直接写入 SO；编辑器顶部 ObjectField 选择/创建配置（EditorPrefs 记住上次使用的配置）。

| 子界面 | 功能 |
|---|---|
| ① 配置修改 | 全局参数（随机游走/贴图混合/坐标换算/种子/TerrainLayer 池/邻接组）+ 逐层参数（权重列表、generateRoad、roadWidth、roadSpacingMin、roadFinalRemap）。**层名与颜色只读**，需在 Project 面板中修改对应 `LayerConfigSO` 资产 |
| ② 绘画 | 层次图绘制（圆形画笔/矩形/三角填充 + 擦除；撤销；保存为配置文件夹内 `layerMap.png`）。图层颜色/名称从 16 个层级 SO 读取 |
| ③ 贴图编辑 | TerrainLayer 列表 + layer×TerrainLayer 矩阵（每格「自然/道路」双复选框）；「计算距离场 + 路网」一键跑完整链路，结果保存为 `result_RGB.png` 并预览 |
| ④ 应用 | 占位（下一阶段：传入 Terrain，写入 TerrainLayer 并烘焙 splatmap） |

**创建新地形配置**：输入名称后自动创建 `TerrainGeneratorConfigs/<名称>/` 子文件夹，内含总 SO + 16 个层级 SO（颜色/名称取 LayerPalette 预设）+ 层次图。

## 配置架构（ScriptableObject）

- **总 SO（`TerrainPaintProjectSO`）**：全局配置 `TerrainPaintConfig`（roadStep / walkStartTries / walkCandidateCount / startCoverStopSamples / walkSeed / maxStepsPerPath / gApplySpacing / noiseScale / worldPerPixel）+ 16 个层级 SO 列表 + 层次图 + TerrainLayer 列表 + 矩阵 + 计算结果（groupMaxD、resultTexture）。
- **层级 SO（`LayerConfigSO`）**：每个层级一个。颜色与名称**只能在 Inspector 修改**；其余参数（自然/道路贴图、生成道路开关、胶囊半径、重映射曲线、邻接层）可在窗口配置修改界面编辑。
- **存储**：全部配置资产在 `LayerEditor/TerrainGeneratorConfigs/` 下按配置分子文件夹，**本地项目数据，不进版本库**（`.gitignore` 忽略）。

## 核心算法（TerrainRoadGen）

链路（详见桌面设计文档《混合距离场与路面生成工具_设计文档(2).md》）：

1. **层ID解析** `ParseLayerIds`：层次图颜色 → 层ID 整数数组（颜色解析只此一步，后续流程不接触颜色）。
2. **组合层级分组** `GroupLayers`：按全局 `adjacencyGroups`（`List<List<int>>` 邻接组）分组（仅 `generateRoad=true` 层）；未出现在任何组的有效层自动单独成组；同一层级跨组重复会报 Error 并阻断计算。
3. **距离场 R** `ComputeR`：对组合层区域做 Felzenszwalb 欧氏距离变换，`maxD` 自动归一化 → R∈[0,1]（边界 0 / 最深内陆 1），区域外 0。
4. **随机游走** `GenerateRoads`：每个组合层独立生成路网——候选点必须在起点同组合层（跨组跳过）；按 R 加权选点；**防卷曲**（锚点与新点距离 > `gApplySpacing` 才批量回填 G 胶囊，半径 = 沿途所在层 `roadSpacingMin`）；闭环合并（末点附近历史点接入网络）；结束对路径所有边统一填 B 胶囊（半径 = 所在层 `roadWidth`）。G = 占用/间隔缓冲（防绕圈 + 密度控制），B = 路面硬掩码。
5. **合成** `ComposeRgb`：一张 RGB 图（R=距离场红，G=占用绿，B=路面蓝），结果存 `result_RGB.png`。

参数语义与默认值见设计文档；EDT 算法已独立验证。

## LayerMap（绘制核心类）

CPU 可读写 RGBA32 图片：`FillCircle`（圆形画笔）、`DrawLine`（拖拽直线条带）、`FillRect`（矩形填充）、`FillTriangle`（三角填充）；完全覆盖 alpha=1 不模糊；撤销快照 32 步；`SavePng`/`LoadPng`/`Resize`。不依赖 UnityEditor，供窗口与后续复用。

## ModelFeatures.md

模型特征记录：常用预制体的**类型 / 尺寸 / 外形 / 放置规则**，供 AI 地形搭建摆放时参考。尺寸统一用 bridge `mesh-bounds --placed` 视觉尺寸（x宽 × y高 × z深）。已覆盖 Bonfires / Crystals / Props / Timber / Tower / Tree / Vines / Grass / Rock / Stone&Cliff / Wall。
