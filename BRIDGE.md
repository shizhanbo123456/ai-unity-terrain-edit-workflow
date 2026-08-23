# AI Terrain Workflow Bridge

本目录提供工作流专属的 bridge 扩展。C# 命令位于 `Editor/Bridge/WorkflowBridgeCommands.cs`，Python CLI 位于 `python/workflow_bridge.py`。扩展依赖同一 Unity 项目中的 `unity-python-bridge`，但不修改 bridge 仓库；删除本工作流目录即可完整移除这些命令。

## 准备

1. 在 Unity 中导入并编译 `unity-python-bridge` 与本项目，启动 bridge server。
2. 让 Python 能找到 bridge 客户端（PowerShell 示例）：

```powershell
$env:PYTHONPATH = "D:\Files\unityprojects\RevelryOfSepulcher\Assets\unity-python-bridge\python"
python python\workflow_bridge.py --help
```

所有 Unity 资产路径都使用 `Assets/...`。相对输出、生成的备用 Prefab、Billboard 贴图和材质均由现有处理工具限制在 `Assets/ai-unity-terrain-edit-workflow/Generated` 中。

## 完整命令行流程

复制并修改 `python/manifest.example.json`，随后运行：

```powershell
python python\workflow_bridge.py run --manifest python\manifest.example.json
```

`run` 按以下顺序执行：创建或加载项目、构建备用 Prefab、写入工作流配置与生成组、重建区域操作、烘焙派生 MapData、校验 Prefab/LOD/根变换，并在 manifest 指定 `terrain` 时构建真实 Terrain。校验失败时 CLI 返回退出码 2，并输出逐项错误。

也可以逐步运行：

```powershell
python python\workflow_bridge.py project-create Demo --resolution 512
python python\workflow_bridge.py configure config.json --project Assets/ai-unity-terrain-edit-workflow/TerrainGeneratorConfigs/Demo/Demo.asset
python python\workflow_bridge.py prefab-build Assets/Tree.prefab --billboard-mode CrossPlanes --two-point-height
python python\workflow_bridge.py prefab-update-bounds --force
python python\workflow_bridge.py prefab-update-billboards
python python\workflow_bridge.py area-rebuild Assets/.../Demo.asset operations.json
python python\workflow_bridge.py bake Assets/.../Demo.asset
python python\workflow_bridge.py validate Assets/.../Demo.asset
python python\workflow_bridge.py build Assets/.../Demo.asset --terrain Terrain --through FixedPointEdit
```

Billboard 模式：`None`、`CrossPlanes`、`FaceCamera`、`YawOnly`。构建备用 Prefab 会强制根节点 position/rotation/scale 为 `(0,0,0)`、`(0,0,0)`、`(1,1,1)`。

## 原生 bridge 命令

| 命令 | 主要参数 | 用途 |
|---|---|---|
| `workflow.project.create` | `name`, `width` | 创建项目及 16 个图层资产 |
| `workflow.configure` | `path`, `message` | 以 manifest 配置已有项目 |
| `workflow.prefab.build` | `path`, `type`, `placed` | 构建单个备用 Prefab |
| `workflow.prefab.update_bounds` | `active` | 批量更新 Bounds；`active=true` 强制 |
| `workflow.prefab.update_billboards` | 无 | 批量刷新启用的 Billboard |
| `workflow.area.rebuild` | `path`, `message` | 替换绘画操作并完整重建 LayerMap |
| `workflow.bake` | `path`, `active` | 烘焙区域或全部派生图 |
| `workflow.validate` | `path` | 执行应用前校验 |
| `workflow.build` | `path`, `terrain`, `type` | 构建到场景 Terrain |
| `workflow.run` | `path`, `message` | 完整 manifest 流程 |

Python CLI 只是这些命令的稳定参数适配层；复杂结构统一放进 bridge 原生 `message` JSON，因此不要求修改 bridge 的 `BridgeArgs`。

## Manifest

示例文件覆盖以下配置：

- `projectName`、`resolution`、`projectPath`：项目创建或定位。
- `terrainLayers`、`layers`、`adjacencyGroups`：材质池、16 层规则和相邻层组合。
- `scatterGroups`、`propGroups`、`fixedPointGroups`：三个摆放模块。
- `prefabs`：需要先处理的源 Prefab 及 Billboard/两点适高选项。
- `areaOperations`：`Line`（两点+半径）、`Rectangle`（两点）、`Triangle`（三点）的有序操作列表；坐标为 LayerMap 像素。
- `bake`：是否重建全部派生图；否则只重建 LayerMap。
- `terrain`、`applyThrough`：可选场景 Terrain 与最终应用阶段。

最小区域操作文件也可以单独传给 `area-rebuild`：

```json
{
  "operations": [
    { "type": "Line", "layerIndex": 1, "a": [20,20], "b": [200,180], "radius": 8 },
    { "type": "Rectangle", "layerIndex": 2, "a": [40,40], "b": [100,90] }
  ]
}
```

距离约定：只有区域绘画坐标和半径使用像素；高度、道路宽度、散布间距、摆件间距等配置均按世界米解释。构建时由 Terrain 尺寸与 MapData 分辨率换算世界米/像素。

## 自动化建议

- 先执行 `validate` 再执行 `build`；`run` 已内置该顺序。
- 生成组引用应指向处理后的 `Generated/Prefabs` 资产。若同一个 manifest 同时声明 `prefabs` 和生成组引用，`run` 会先创建备用 Prefab 再解析引用。
- 命令会保存工作流自身的资产；不会改写源 Prefab。场景 Terrain 构建属于显式操作，只在 `build` 或带 `terrain` 的 `run` 中发生。
