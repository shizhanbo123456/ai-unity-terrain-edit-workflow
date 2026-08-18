# AiTerrainWorkflow

AI 地形编辑工作流 —— 独立工具仓库，存放地形编辑相关的算法与工具类。

## 目录结构

```
Utils/
└── UniformPointGenerator.cs    # 均匀分布随机点生成（网格抖动 Jittered Grid）

objectpref.py                   # ObjectPref 命令行工具（key-value 信息录入/读取）
objectpref.json                 # ObjectPref 数据文件（自动创建，JSON 对象 {"key": "value"}）
```

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
