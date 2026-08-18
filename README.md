# AiTerrainWorkflow

AI 地形编辑工作流 —— 独立工具仓库，存放地形编辑相关的算法与工具类。

## 目录结构

```
Utils/
└── UniformPointGenerator.cs    # 均匀分布随机点生成（网格抖动 Jittered Grid）
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
