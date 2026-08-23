using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 构建端组件：接收主配置 SO，将一个真实 Terrain 构建为工作流编辑器中的样子
    /// （高度 / 贴图 / 散布 / 摆件 / 定点，详见 README「完整生成逻辑伪代码」）。
    ///
    /// 当前已实现：散布生成组的**按区块动态生成与回收**——Build 时按组初始化区块管理器
    /// （<see cref="ChunkUpdateManager"/>）与对象池；
    /// 由 <see cref="SetCameraPosition"/> 驱动：区块中心进入可见距离则按生成参数生成并填充，
    /// 超出则回收物体到对象池。其余构建步骤（高度 / 贴图 / 摆件 / 定点）待实现。
    ///
    /// 对象池约定：
    ///   - 每个散布 Prefab 分配唯一 key（= 组内 Prefab 池索引），物体 name = key.ToString()，
    ///     回收时按物体名称解析出池 key；
    ///   - 全部实例挂在隐藏容器（HideFlags.HideInHierarchy）下，**Hierarchy 面板不显示**；
    ///   - 取出 / 新建时 SetActive(true)，放回池时 SetActive(false)。
    /// 高度以 Terrain 组件（SampleHeight）为准，不依赖高度 MapData 是否已生成 / 已应用。
    /// </summary>
    public class TerrainBuilder : MonoBehaviour
    {
        private TerrainPaintProjectSO _config;
        private Terrain _terrain;

        /// <summary>当前这次 Build 的 MapData 内存集合；运行时新计算结果只存放在此处。</summary>
        public TerrainMapData MapData { get; private set; }

        private sealed class ScatterRuntime
        {
            public ScatterConfigSO config;
            public ChunkUpdateManager chunks;
            public int seed;
            public readonly Dictionary<int, ObjectPool<GameObject>> pools = new Dictionary<int, ObjectPool<GameObject>>();
            public readonly List<int> prefabKeys = new List<int>();
            public readonly List<int> prefabWeights = new List<int>();
            public readonly Dictionary<Vector2Int, List<GameObject>> objectsByChunk =
                new Dictionary<Vector2Int, List<GameObject>>();
        }

        private readonly List<ScatterRuntime> _scatterRuntimes = new List<ScatterRuntime>();
        private Transform _poolRoot;

        // MapData 缓存（Build 时读取一次；高度以 Terrain 为准，不读 height）
        private float[][] _layerMapData;
        private float[][] _roadData;
        private float[][] _offRoadData;
        private bool _dataReady;
        private bool _dataWarned;

        // 像素 → 世界换算（按 Terrain 实际尺寸 / mapResolution）
        private float _wppX = 1f;
        private float _wppZ = 1f;

        /// <summary>
        /// 构建：创建本次 MapData 内存会话，并从高度到 applyThrough 按顺序应用各阶段。
        /// 注意：当前实现假定单次 Build；重复 Build 需先自行回收全部已生成物体。
        /// </summary>
        public void Build(TerrainPaintProjectSO projectConfig, Terrain terrain, TerrainWorkflowStage applyThrough)
        {
            if (projectConfig == null || terrain == null)
            {
                Debug.LogError("[TerrainBuilder] Build 失败：projectConfig / terrain 为空。");
                return;
            }

            _config = projectConfig;
            _terrain = terrain;
            MapData = TerrainMapData.Load(projectConfig);

            ApplyHeight();
            if ((int)applyThrough < (int)TerrainWorkflowStage.TextureEdit) return;
            ApplyTexture();
            if ((int)applyThrough < (int)TerrainWorkflowStage.ScatterEdit) return;
            ApplyScatter();
            if ((int)applyThrough < (int)TerrainWorkflowStage.PropEdit) return;
            ApplyProps();
            if ((int)applyThrough < (int)TerrainWorkflowStage.FixedPointEdit) return;
            ApplyFixedPoints();
        }

        /// <summary>应用高度数据；具体实现后续补充。</summary>
        private void ApplyHeight()
        {
        }

        /// <summary>应用地形贴图；具体实现后续补充。</summary>
        private void ApplyTexture()
        {
        }

        /// <summary>初始化各散布生成组的流式区块与对象池。</summary>
        private void ApplyScatter()
        {
            var projectConfig = _config;
            var terrain = _terrain;

            // 缓存 MapData（层 ID / 道路 / 离路距离）
            _layerMapData = MapData.Get("layerMap");
            _roadData = MapData.Get("road");
            _offRoadData = MapData.Get("offRoad");
            _dataReady = _layerMapData != null && _roadData != null && _offRoadData != null;
            _dataWarned = false;

            int mapH = _layerMapData != null ? _layerMapData.Length : projectConfig.mapResolution;
            int mapW = mapH > 0 && _layerMapData != null ? _layerMapData[0].Length : projectConfig.mapResolution;
            _wppX = terrain.terrainData.size.x / Mathf.Max(1, mapW - 1);
            _wppZ = terrain.terrainData.size.z / Mathf.Max(1, mapH - 1);

            // 隐藏容器：Hierarchy 面板不显示（所有后代节点也不显示）
            if (_poolRoot == null)
            {
                var go = new GameObject("_TerrainBuilderPools");
                go.hideFlags = HideFlags.HideInHierarchy;
                _poolRoot = go.transform;
            }

            _scatterRuntimes.Clear();
            for (int groupIndex = 0; groupIndex < projectConfig.scatterGroups.Count; groupIndex++)
            {
                var group = projectConfig.scatterGroups[groupIndex];
                if (group == null) continue;

                var runtime = new ScatterRuntime
                {
                    config = group,
                    chunks = new ChunkUpdateManager(group.chunkSize, group.visibleDistance),
                    seed = projectConfig.scatterSeed ^ (groupIndex * 83492791),
                };
                for (int prefabIndex = 0; prefabIndex < group.prefabs.Count; prefabIndex++)
                {
                    var entry = group.prefabs[prefabIndex];
                    if (entry == null || entry.prefab == null || entry.weight <= 0) continue;
                    runtime.pools[prefabIndex] = CreatePool(entry.prefab, prefabIndex);
                    runtime.prefabKeys.Add(prefabIndex);
                    runtime.prefabWeights.Add(entry.weight);
                }
                _scatterRuntimes.Add(runtime);
            }
        }

        /// <summary>应用摆件；具体实现后续补充。</summary>
        private void ApplyProps()
        {
        }

        /// <summary>应用定点物体；具体实现后续补充。</summary>
        private void ApplyFixedPoints()
        {
        }

        private ObjectPool<GameObject> CreatePool(GameObject prefab, int key)
        {
            return new ObjectPool<GameObject>(
                createFunc: () =>
                {
                    var go = Instantiate(prefab, _poolRoot);
                    go.name = key.ToString(); // 物体名称 = 池 key（回收时解析）
                    go.hideFlags = HideFlags.HideInHierarchy;
                    go.SetActive(false);
                    return go;
                },
                actionOnGet: go => go.SetActive(true),
                actionOnRelease: go => go.SetActive(false),
                collectionCheck: false);
        }

        /// <summary>
        /// 观察点移动到 pos（Vector2(x, z)）：按各散布生成组的可见距离，
        /// 激活新进入范围的区块（生成物体）、回收离开范围的区块（物体放回对象池）。
        /// </summary>
        public void SetCameraPosition(Vector2 pos)
        {
            if (_config == null || _terrain == null)
                return;

            Vector3 terrainPosition = _terrain.transform.position;
            var localPos = new Vector2(pos.x - terrainPosition.x, pos.y - terrainPosition.z);
            foreach (var runtime in _scatterRuntimes)
                UpdateChunks(runtime, localPos);
        }

        private void UpdateChunks(ScatterRuntime runtime, Vector2 pos)
        {
            runtime.chunks.MoveTo(pos, out var active, out var inactive);

            // 1) 回收离开范围的区块：按物体名称解析池 key → Release
            foreach (var idx in inactive)
            {
                if (!runtime.objectsByChunk.TryGetValue(idx, out var list)) continue;
                foreach (var go in list)
                {
                    if (go == null) continue;
                    if (int.TryParse(go.name, out int key) && runtime.pools.TryGetValue(key, out var pool))
                        pool.Release(go);
                }
                list.Clear();
                runtime.objectsByChunk.Remove(idx);
            }

            // 2) 生成新进入范围的区块（已激活的跳过，不重复生成）
            foreach (var idx in active)
            {
                if (!runtime.objectsByChunk.ContainsKey(idx))
                    GenerateChunk(runtime, idx);
            }
        }

        /// <summary>
        /// 生成单个散布区块：
        /// 世界范围 → 像素范围 → 收集合法像素（语义层 lid≥1、非道路、offRoad ≥ 对应离路限制）
        /// → 区块 seed = 全局 seed ⊕ 区块 index → 按层密度洗牌取位 → 按权重选原型 → 从对象池取出并摆放
        /// （高度 = Terrain.SampleHeight，位置 = 像素中心 × 实际 worldPerPixel + Terrain 原点）。
        /// </summary>
        private void GenerateChunk(ScatterRuntime runtime, Vector2Int idx)
        {
            if (!_dataReady)
            {
                if (!_dataWarned)
                {
                    _dataWarned = true;
                    Debug.LogWarning("[TerrainBuilder] MapData（layerMap/road/offRoad）缺失，跳过散布生成。");
                }
                return;
            }

            runtime.chunks.GetChunkBounds(idx, out float xMin, out float yMin, out float xMax, out float yMax);
            int mapH = _layerMapData.Length;
            int mapW = mapH > 0 ? _layerMapData[0].Length : 0;
            if (mapW <= 0) return;

            // 世界范围 → 像素范围（clamp 到 map 内）
            int pxMin = Mathf.Clamp(Mathf.FloorToInt(xMin / _wppX), 0, mapW - 1);
            int pxMax = Mathf.Clamp(Mathf.CeilToInt(xMax / _wppX) - 1, 0, mapW - 1);
            int pzMin = Mathf.Clamp(Mathf.FloorToInt(yMin / _wppZ), 0, mapH - 1);
            int pzMax = Mathf.Clamp(Mathf.CeilToInt(yMax / _wppZ) - 1, 0, mapH - 1);

            // 收集合法像素：语义层（lid≥1）+ 非道路 + 离路 ≥ 对应 limit
            var layerPixels = new Dictionary<int, List<Vector2Int>>();
            for (int pz = pzMin; pz <= pzMax; pz++)
            {
                var row = _layerMapData[pz];
                for (int px = pxMin; px <= pxMax; px++)
                {
                    int lid = Mathf.RoundToInt(row[px]);
                    int layerIndex = lid < 0 ? 0 : lid;
                    if (layerIndex >= TerrainPaintProjectSO.MaxLayerCount)
                        continue;
                    var layerFlag = (TerrainWorkflowLayerMask)(1 << layerIndex);
                    if ((runtime.config.targetLayers & layerFlag) == 0) continue;
                    if (_roadData[pz][px] > 0.5f)
                        continue;
                    float offRoad = _offRoadData[pz][px];
                    if (offRoad < runtime.config.offRoadDistanceRange.x ||
                        offRoad > runtime.config.offRoadDistanceRange.y)
                        continue;
                    if (!layerPixels.TryGetValue(layerIndex, out var list))
                    {
                        list = new List<Vector2Int>();
                        layerPixels[layerIndex] = list;
                    }
                    list.Add(new Vector2Int(px, pz));
                }
            }

            // 区块 seed：全局 seed ⊕ 区块 index（不同区块不重复、可复现）
            int chunkSeed = runtime.seed ^ (idx.x * 73856093) ^ (idx.y * 19349663);
            var rng = new System.Random(chunkSeed);
            float pixelArea = _wppX * _wppZ;

            var placed = new List<GameObject>();
            foreach (var kv in layerPixels)
            {
                var pixels = kv.Value;
                float density = runtime.config.density;
                if (density <= 0f) continue;
                int count = Mathf.FloorToInt(pixels.Count * pixelArea * density);
                if (count <= 0) continue;

                Shuffle(pixels, rng); // 洗牌取前 count 个：均匀、无重复
                int take = Mathf.Min(count, pixels.Count);
                var scaleRange = runtime.config.randomScale;

                for (int i = 0; i < take; i++)
                {
                    var pxp = pixels[i];
                    if (runtime.prefabKeys.Count == 0) continue;
                    int key = PickWeightedPrefab(runtime, rng);
                    if (!runtime.pools.TryGetValue(key, out var pool))
                        continue;
                    var go = pool.Get();
                    float s = Mathf.Lerp(scaleRange.x, scaleRange.y, (float)rng.NextDouble());
                    go.transform.position = PixelToWorld(pxp.x, pxp.y);
                    go.transform.localScale = Vector3.one * s;
                    placed.Add(go);
                }
            }

            runtime.objectsByChunk[idx] = placed;
        }

        /// <summary>像素中心 → 世界坐标；Y = Terrain.SampleHeight（以 Terrain 组件高度为准）。</summary>
        private Vector3 PixelToWorld(int px, int pz)
        {
            Vector3 tPos = _terrain.transform.position;
            float wx = tPos.x + px * _wppX;
            float wz = tPos.z + pz * _wppZ;
            float wy = tPos.y + _terrain.SampleHeight(new Vector3(wx, 0f, wz));
            return new Vector3(wx, wy, wz);
        }

        private static void Shuffle<T>(List<T> list, System.Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static int PickWeightedPrefab(ScatterRuntime runtime, System.Random rng)
        {
            int total = 0;
            for (int i = 0; i < runtime.prefabWeights.Count; i++) total += runtime.prefabWeights[i];
            if (total <= 0) return -1;

            int value = rng.Next(total);
            for (int i = 0; i < runtime.prefabWeights.Count; i++)
            {
                if (value < runtime.prefabWeights[i]) return runtime.prefabKeys[i];
                value -= runtime.prefabWeights[i];
            }
            return -1;
        }

    }
}
