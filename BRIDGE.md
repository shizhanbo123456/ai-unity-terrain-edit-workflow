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
python python\workflow_bridge.py run python\manifest.example.json
```

`run` 按以下顺序执行：创建或加载项目、构建备用 Prefab、写入工作流配置与生成组、重建区域操作、烘焙派生 MapData、校验 Prefab/LOD/根变换，并构建真实 Terrain。`terrain` 填写名称时精确查找；留空字符串时自动使用场景中找到的第一个 Terrain；场景中没有 Terrain 则报错。校验失败时 CLI 返回退出码 2，并输出逐项错误。

只导入配置但不烘焙和构建时使用：

```powershell
python python\workflow_bridge.py configure config.json --project Assets/ai-unity-terrain-edit-workflow/TerrainGeneratorConfigs/Demo/Demo.asset
```

Python CLI 刻意只提供 `configure` 和 `run` 两个入口。命令行侧不提供单字段 set/add/remove 命令；要修改任何工作流配置，必须编辑完整 JSON 后重新导入。这样 JSON 是可审查、可复现的唯一外部配置来源，不会出现一长串参数命令或部分更新语义。

Billboard 模式：`None`、`CrossPlanes`、`FaceCamera`、`YawOnly`。构建备用 Prefab 会强制根节点 position/rotation/scale 为 `(0,0,0)`、`(0,0,0)`、`(1,1,1)`。

## 原生 bridge 命令

| 命令 | 主要参数 | 用途 |
|---|---|---|
| `workflow.configure` | `path`, `message` | 用完整 manifest 创建或整体覆盖配置（唯一配置写命令） |
| `workflow.prefab.build` | `path`, `type`, `placed` | 构建单个备用 Prefab |
| `workflow.prefab.update_bounds` | `active` | 批量更新 Bounds；`active=true` 强制 |
| `workflow.prefab.update_billboards` | 无 | 批量刷新启用的 Billboard |
| `workflow.bake` | `path`, `active` | 烘焙区域或全部派生图 |
| `workflow.validate` | `path` | 执行应用前校验 |
| `workflow.build` | `path`, `terrain`, `type` | 构建到场景 Terrain |
| `workflow.run` | `path`, `message` | 完整 manifest 流程 |

其余原生命令只处理生成资产、派生数据、校验或执行构建，不用于修改工作流配置。Python CLI 只暴露 `workflow.configure` 与 `workflow.run`；完整结构统一放进 bridge 原生 `message` JSON，因此不要求修改 bridge 的 `BridgeArgs`。

## Manifest

`python/manifest.example.json` 是完整模板，不是只展示常用字段的片段。C# 会拒绝缺少必需顶层集合或不是完整 16 层的 manifest，防止遗漏字段时把默认值误当成用户配置。模板覆盖：

- `projectName`、`resolution`、`projectPath`：项目创建或定位。
- 高度噪声、平滑参数，以及 `paintConfig` 中的道路随机游走、混合噪声和世界/像素预览比例。
- `naturalTerrainLayers`、`roadTerrainLayers`、完整 16 个 `layers`（含道路重映射曲线）、`adjacencyGroups`。
- `scatterGroups`、`propGroups`、`fixedGroups`：三个摆放模块的全部当前字段。
- `prefabs`：需要先处理的源 Prefab 及 Billboard/两点适高选项。
- `areaOperations`：`Line`（两点+半径）、`Rectangle`（两点）、`Triangle`（三点）的有序操作列表；坐标为 LayerMap 像素。
- `bake`：是否重建全部派生图；否则只重建 LayerMap。
- `terrain`、`applyThrough`：目标 Terrain 名称与最终应用阶段；`terrain` 可留空以自动寻找。

距离约定：只有区域绘画坐标和半径使用像素；高度、道路宽度、散布间距、摆件间距等配置均按世界米解释。构建时由 Terrain 尺寸与 MapData 分辨率换算世界米/像素。

## 自动化建议

- 使用 `run` 完成 Prefab 处理、烘焙、校验与可选 Terrain 构建；它已内置正确顺序。
- 生成组引用应指向处理后的 `Generated/Prefabs` 资产。若同一个 manifest 同时声明 `prefabs` 和生成组引用，`run` 会先创建备用 Prefab 再解析引用。
- `prefabs[].path` 是一次性的源素材输入；`scatterGroups`、`propGroups`、`fixedGroups` 中的实际引用只能指向 `Generated/Prefabs/`，不能直接引用源 Prefab 或其它目录的 Prefab。
- 备用 Prefab 是允许人工维护的工作流资产：可以移动、旋转、缩放其子物体，也可以增删和拼合多个对象；只需保持根节点标准 Transform。再次运行同名处理不会覆盖这些内容，修改后可重新生成 Bounds/Billboard。
- 命令会保存工作流自身的资产；不会改写源 Prefab。`run` 总会构建场景 Terrain，`terrain` 为空时自动寻找；只想导入配置而不构建时使用 `configure`。
