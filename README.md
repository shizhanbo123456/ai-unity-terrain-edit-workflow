# AiTerrainWorkflow

AI 地形编辑工作流 —— 独立工具仓库，存放地形编辑相关的算法与工具类。

## 目录结构

```
Utils/
├── UniformPointGenerator.cs    # 均匀分布随机点生成（网格抖动 Jittered Grid）
└── ObjectGroup.cs              # ObjectGroup ScriptableObject（组名 + GameObject 列表）

LayerEditor/
├── LayerMap.cs                 # 层图数据：CPU 读写图片 + 圆形/矩形/三角绘制算法（命令行可复用）
├── LayerPalette.cs             # 图层预设：layer0 透明 + 16 个预设色 + 可编辑名称
└── Editor/
    └── LayerEditorWindow.cs    # IMGUI 绘画窗口（Tools / Terrain Edit Workflow / Open Layer Editor）

Editor/
└── TerrainEditWorkflowMenu.cs  # 菜单栏工具（Tools / Terrain Edit Workflow）

ModelFeatures.md                # 模型特征记录（常用预制体的类型/尺寸/外形/方向，供地形搭建参考）
```

C# 代码统一使用命名空间 `AiTerrainWorkflow`。

## Utils / UniformPointGenerator

在矩形区域 `[min, max]` 内生成 `count` 个均匀分布的随机点（`Vector2`）：

```csharp
List<Vector2> UniformPointGenerator.Generate(int count, Vector2 min, Vector2 max, int seed = 20260818)
```

- 网格抖动（分层采样）：区域按宽高比自适应切分 `cols×rows`，每格一点、格内随机偏移 → 全局均匀不聚簇
- `System.Random(seed)` 确定性伪随机 → 相同 seed 输出逐点一致（缺省 `DefaultSeed = 20260818`）
- 退化边界：区域宽或高为 0 时退化为线/点均匀排布；区域无效（max < min）抛异常
- 纯静态工具类，不依赖 Unity 命令系统，供本仓库后续工具调用

## ObjectGroup（ScriptableObject）

一组 GameObject 的命名集合，作为资产保存在 Assets 下：右键 **Create → AiTerrainWorkflow → ObjectGroup**。

```csharp
public class ObjectGroup : ScriptableObject
{
    public string groupName;                 // 组名（如 "Forest Trees"）
    public List<GameObject> gameObjects;     // 组内 GameObject 列表
}
```

## 菜单栏工具（Tools / Terrain Edit Workflow）

| 菜单项 | 功能 |
|---|---|
| `Tools / Terrain Edit Workflow / Log Version` | Console 打印当前工具版本号 |
| `Tools / Terrain Edit Workflow / Open Layer Editor` | 打开 LayerEditor 绘画窗口 |

- 版本号写在 `Editor/TerrainEditWorkflowMenu.cs` 的 `Version` 常量中（当前 **v1.2**）；后续功能有变更时手动同步更新

## LayerEditor（多色块区域绘画工具）

编辑器窗口（`Tools / Terrain Edit Workflow / Open Layer Editor`），绘制一张 **CPU 可读写的 RGBA32 图片**，用多个色块表示不同地形区域（如路=黄、水=蓝、森林=绿），为后续 AI 地形编辑提供区域层图。

**工具**（工具栏切换）：

| 工具 | 操作 |
|---|---|
| 圆形画笔 | 单击画实心圆；**拖拽画"起点→终点"直线条带**（无自由笔画）；半径可调 |
| 矩形填充 | 拖拽定义对角区域，抬起时整块填充 |
| 三角填充 | 依次点击 3 个顶点，第 3 次点击时填充三角形 |

**图层列表**（窗口右侧）：
- `Layer0` 恒为**透明**——代表过渡区域；选中它绘画即"擦除"为过渡
- 其余 16 个内置预设色（差别较大），**名称可手动编辑**（如 "Layer1 红色" → "地面"）；`Layer{n}` 前缀固定不可改
- 绘画时需先选中一个颜色；绘制**完全覆盖**目标像素（alpha=1，无边缘模糊，可覆盖任意旧色）

**源图片**（工具栏第二行）：
- 留空 = 新建画布；设置一张已有 PNG 后，画布从该图加载（尺寸跟随），导出时**直接覆盖原图**
- 点"重置画布"会清空源图片引用，回到新建模式

**其它**：
- 画布尺寸可设置（8~1024，默认 256×256），"重置画布"重新按新尺寸清空
- 撤销：Ctrl+Z 或工具栏按钮（最多保留 32 步快照）
- 导出：设置源图片时覆盖原图；新建模式下保存到 `LayerEditor/Output/`，文件名自动递增（`LayerMap_1.png`、`LayerMap_2.png`…），不覆盖已有文件。Output 目录为本地导出产物，已在 `.gitignore` 中忽略
- 核心类 `LayerMap` 不依赖 UnityEditor——后续 bridge 命令行可直接调用同样的绘制算法（圆形/矩形/三角形）
