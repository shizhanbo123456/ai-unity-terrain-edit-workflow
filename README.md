# AiTerrainWorkflow

AI 地形编辑工作流 —— 独立工具仓库，存放地形编辑相关的算法与工具类。

## 目录结构

```
Utils/
├── UniformPointGenerator.cs    # 均匀分布随机点生成（网格抖动 Jittered Grid）
└── ObjectGroup.cs              # ObjectGroup ScriptableObject（组名 + GameObject 列表）

Editor/
└── TerrainEditWorkflowMenu.cs  # 菜单栏工具（Tools / Terrain Edit Workflow）

Gui/
├── RuntimeUiTest.cs            # UI Toolkit 运行时测试（纯 C# 构建，Play 模式）
├── UxmlUiTest.cs               # UI Toolkit 运行时测试（UXML/USS 资产加载，Play 模式）
├── TestLayout.uxml             # 测试布局资产
└── TestStyle.uss               # 测试样式资产

objectpref.py                   # ObjectPref 命令行工具（key-value 信息录入/读取）
objectpref.json                 # ObjectPref 数据文件（自动创建，JSON 对象 {"key": "value"}）
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

## ObjectPref（key-value 信息存储）

命令行工具，把任意 `(key, value)` 字符串信息以 **JSON 对象**形式存入本目录下的 `objectpref.json`（UTF-8）。纯标准库，零依赖。

```bash
# 录入 / 更新（key 已存在时必须显式加 --overwrite，否则报错退出，不会静默覆盖）
python objectpref.py set <key> <value> [--overwrite]

# 读取（key 不存在时报错退出）
python objectpref.py get <key>

# 列出全部 key-value
python objectpref.py list

# 可选 --file <路径>：自定义数据文件（写在子命令前后均可）
python objectpref.py set foo bar --file ./my.json
python objectpref.py --file ./my.json get foo
```

- **JSON 格式保证**：标准库 `json` 序列化（不手写拼接）；**原子写入**（临时文件 + `os.replace`），写入中断不会损坏原文件；文件缺失视为空，内容非法时明确报错且不静默覆盖
- 退出码：`0` 成功 / `1` 错误（重复 key 未覆盖、key 不存在、JSON 损坏等）
- 数据文件随 git 版本控制，可跨机器同步

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
| `Tools / Terrain Edit Workflow / Create UI Test Setup` | 一键创建 2 个 UI Toolkit 运行时测试物体 |

- 版本号写在 `Editor/TerrainEditWorkflowMenu.cs` 的 `Version` 常量中（当前 **v1.1**）；后续功能有变更时手动同步更新

## Gui / UI Toolkit 运行时测试（Play 模式）

UI Toolkit 在 Unity 2022.3 **内置、零安装**。本目录提供两套 Play 模式可行性测试：

| 脚本 | 测试内容 | 用法 |
|---|---|---|
| `RuntimeUiTest` | 纯 C# 构建：Label/Button/TextField/Slider/Toggle/DropdownField/ProgressBar/ListView + Flex 布局 + 事件回调 + 实时事件日志 | 挂到场景物体（Add Component 搜 "Runtime UI Test"）→ Play |
| `UxmlUiTest` | UXML/USS **资产**加载：TestLayout.uxml + TestStyle.uss（含 hover/active 状态样式） | 挂到场景物体（Add Component 搜 "Uxml UI Test"）→ Play |

- 两者都自动创建 PanelSettings 并挂载 UIDocument，**无需手工准备资产**
- 最快路径：菜单 **Tools → Terrain Edit Workflow → Create UI Test Setup**，再进入 Play 模式
- UXML/USS 来源：Inspector 字段引用（正式用法，打包 Player 必须走此路）；留空时编辑器 Play 模式下自动从本目录加载（AssetDatabase 仅编辑器进程可用）
