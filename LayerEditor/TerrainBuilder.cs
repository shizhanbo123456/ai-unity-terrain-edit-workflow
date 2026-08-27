using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Pool;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 构建端组件：接收主配置 SO，将一个真实 Terrain 构建为工作流编辑器中的样子
    /// （高度 / 贴图 / 散布 / 摆件 / 定点，详见 README「完整生成逻辑伪代码」）。
    ///
    /// 已实现高度、地形贴图、散布和定点构建。散布在 Build 时预计算全地形各区块的位置列表，
    /// 再由 <see cref="SetCameraPosition"/> 驱动对象池的动态生成与回收。摆件使用多候选择优、
    /// Bounds 足迹与间距约束生成兼顾自然感和可控性的静态布局。
    ///
    /// 对象池约定：
    ///   - 每个散布 Prefab 分配唯一 key（= 组内 Prefab 池索引），物体 name = key.ToString()，
    ///     回收时按物体名称解析出池 key；
    ///   - 全部实例挂在隐藏容器（HideFlags.HideInHierarchy）下，**Hierarchy 面板不显示**；
    ///   - 取出 / 新建时 SetActive(true)，放回池时 SetActive(false)。
    /// 高度以 Terrain 组件（SampleHeight）为准，不依赖高度 MapData 是否已生成 / 已应用。
    /// </summary>
    [ExecuteAlways]
    public class TerrainBuilder : MonoBehaviour
    {
        private TerrainPaintProjectSO _config;
        private Terrain _terrain;

        /// <summary>当前这次 Build 的 MapData 内存集合；运行时新计算结果只存放在此处。</summary>
        public TerrainMapData MapData { get; private set; }

        [Header("编辑器驱动")]
        [Tooltip("仅编辑器模式（非播放）有效：勾选后允许在编辑器中用 MainCamera 位置驱动散布区块的流式生成/回收。运行时始终按 MainCamera 驱动，不受本项影响。")]
        public bool updateInEditMode = true;

        // 放置缓存（编辑器计算 → 运行时复用）：按「类 × 生成组」拆分，每组一个文件
        private readonly List<PlacementCache.CacheData> _scatterCacheGroups = new List<PlacementCache.CacheData>();
        private readonly List<PlacementCache.CacheData> _propCacheGroups = new List<PlacementCache.CacheData>();
        private readonly List<PlacementCache.CacheData> _fixedCacheGroups = new List<PlacementCache.CacheData>();

        private sealed class ScatterRuntime
        {
            public sealed class Placement
            {
                public int prefabKey;
                public Vector2Int pixel;
                public float scale;
                public float yaw;
            }

            public ScatterConfigSO config;
            public int groupIndex;
            public ChunkUpdateManager chunks;
            public int seed;
            public readonly Dictionary<int, ObjectPool<GameObject>> pools = new Dictionary<int, ObjectPool<GameObject>>();
            public readonly List<int> prefabKeys = new List<int>();
            public readonly List<int> prefabWeights = new List<int>();
            public readonly Dictionary<Vector2Int, List<GameObject>> objectsByChunk =
                new Dictionary<Vector2Int, List<GameObject>>();
            public readonly Dictionary<Vector2Int, List<Placement>> placementsByChunk =
                new Dictionary<Vector2Int, List<Placement>>();
        }

        private sealed class PropPlacement
        {
            public GameObject prefab;
            public Vector2Int pixel;
            public Vector2 worldXZ;
            public float yaw;
            public float radius;
        }

        private readonly List<ScatterRuntime> _scatterRuntimes = new List<ScatterRuntime>();
        private Transform _poolRoot;
        private Transform _propRoot;
        private Transform _fixedRoot;

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
        /// 重复 Build 执行到散布/定点阶段时会重建对应根节点，避免重复保留上一次生成物。
        ///
        /// 放置缓存（散布+摆件）：
        ///   - 编辑器（非运行时）调用：重算散布/摆件位置，构建结束后写入主配置引用的 PlacementCache txt；
        ///   - 运行时调用：若主配置已引用缓存 txt 且配置指纹一致，直接读取位置复用（不再重算）；
        ///     指纹不一致或没有缓存则重新计算，且只保留在内存（不写盘、不修改配置资产）。
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

            // 放置缓存：编辑器模式收集（每组一个 CacheData），运行时逐组读取校验指纹
            _scatterCacheGroups.Clear();
            _propCacheGroups.Clear();
            _fixedCacheGroups.Clear();

            ApplyHeight();
            if ((int)applyThrough < (int)TerrainWorkflowStage.TextureEdit) return;
            ApplyTexture();
            if ((int)applyThrough < (int)TerrainWorkflowStage.ScatterEdit) return;
            ApplyScatter();
            if ((int)applyThrough < (int)TerrainWorkflowStage.PropEdit)
            {
                // 散布已执行：保存含散布位置的缓存（摆件/定点段为空，运行时对应组会重新计算）
                if (!Application.isPlaying) SavePlacementCaches(projectConfig);
                return;
            }
            ApplyProps();
            if ((int)applyThrough < (int)TerrainWorkflowStage.FixedPointEdit)
            {
                // 散布+摆件已执行：保存对应缓存（定点段为空）
                if (!Application.isPlaying) SavePlacementCaches(projectConfig);
                return;
            }
            ApplyFixedPoints();

            // 编辑器（非运行时）：把本次重算/收集的散布+摆件+定点位置写入各生成组缓存 txt 并更新主配置引用
            if (!Application.isPlaying)
                SavePlacementCaches(projectConfig);
        }

        /// <summary>
        /// 编辑器模式（非播放）驱动入口：勾选 <see cref="updateInEditMode"/> 时，
        /// 用场景 MainCamera 的世界 X/Z 位置驱动所有散布生成组的区块流式生成/回收。
        /// 运行时（Play）始终驱动，不受勾选框影响。Build 之前 _scatterRuntimes 为空，直接返回。
        /// </summary>
        private void Update()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && !updateInEditMode) return;
#endif
            if (_config == null || _terrain == null) return;
            if (_scatterRuntimes.Count == 0) return;
            Camera camera = Camera.main;
            if (camera == null) return;
            Vector3 position = camera.transform.position;
            SetCameraPosition(new Vector2(position.x, position.z));
        }

        /// <summary>把真实高度 MapData 双线性采样到 Terrain heightmap，并按 Terrain 高度归一化。</summary>
        private void ApplyHeight()
        {
            float[][] layerMap = MapData.Get("layerMap");
            if (!TryGetMapSize(layerMap, out int mapW, out int mapH))
            {
                Debug.LogWarning("[TerrainBuilder] layerMap 缺失，跳过高度应用。");
                return;
            }

            TerrainData terrainData = _terrain.terrainData;
            var pixelWorldSize = new Vector2(
                terrainData.size.x / Mathf.Max(1, mapW - 1),
                terrainData.size.z / Mathf.Max(1, mapH - 1));
            int[] layerIds = FlattenLayerIds(layerMap, mapW, mapH);
            float[][] height = TerrainRoadGen.BakeHeightData(
                _config, layerIds, mapW, mapH, pixelWorldSize);
            if (height == null) return;
            MapData.Set("height", height);

            int resolution = terrainData.heightmapResolution;
            var normalized = new float[resolution, resolution];
            float terrainHeight = Mathf.Max(0.0001f, terrainData.size.y);
            for (int z = 0; z < resolution; z++)
            {
                float v = resolution > 1 ? (float)z / (resolution - 1) : 0f;
                for (int x = 0; x < resolution; x++)
                {
                    float u = resolution > 1 ? (float)x / (resolution - 1) : 0f;
                    normalized[z, x] = Mathf.Clamp01(SampleBilinear(height, u, v) / terrainHeight);
                }
            }
            terrainData.SetHeights(0, 0, normalized);
        }

        /// <summary>噪声混合自然/道路各自的权重结果，再按道路噪声参数混合两类地表。</summary>
        private void ApplyTexture()
        {
            float[][] layerMap = MapData.Get("layerMap");
            if (!TryGetMapSize(layerMap, out int mapW, out int mapH))
            {
                Debug.LogWarning("[TerrainBuilder] layerMap 缺失，跳过贴图应用。");
                return;
            }
            EnsureRoadMapData(layerMap, mapW, mapH);
            float[][] road = MapData.Get("road");
            if (!HasMapSize(road, mapW, mapH))
            {
                Debug.LogWarning("[TerrainBuilder] road MapData 缺失，跳过贴图应用。");
                return;
            }

            List<TerrainLayer> terrainLayers = BuildTerrainLayerUnion();
            if (terrainLayers.Count == 0)
            {
                Debug.LogWarning("[TerrainBuilder] 未配置 TerrainLayer，跳过贴图应用。");
                return;
            }

            TerrainData terrainData = _terrain.terrainData;
            terrainData.terrainLayers = terrainLayers.ToArray();
            int alphaW = terrainData.alphamapWidth;
            int alphaH = terrainData.alphamapHeight;
            var alphamaps = new float[alphaH, alphaW, terrainLayers.Count];
            float noiseScale = Mathf.Max(0.01f, _config.config.noiseScale);

            for (int z = 0; z < alphaH; z++)
            {
                float v = alphaH > 1 ? (float)z / (alphaH - 1) : 0f;
                for (int x = 0; x < alphaW; x++)
                {
                    float u = alphaW > 1 ? (float)x / (alphaW - 1) : 0f;
                    float sampleWorldX = u * terrainData.size.x;
                    float sampleWorldZ = v * terrainData.size.z;
                    int layerIndex = Mathf.RoundToInt(SampleNearest(layerMap, u, v));
                    LayerConfigSO layer = layerIndex >= 0 && layerIndex < _config.layers.Count
                        ? _config.layers[layerIndex]
                        : null;
                    float[] natural = BuildNoisyWeights(
                        layer != null ? layer.naturalLayerWeights : null,
                        _config.naturalTerrainLayers,
                        terrainLayers,
                        sampleWorldX, sampleWorldZ, _config.naturalSeed, noiseScale);
                    float[] roadWeights = BuildNoisyWeights(
                        layer != null ? layer.roadLayerWeights : null,
                        _config.roadTerrainLayers,
                        terrainLayers,
                        sampleWorldX, sampleWorldZ, _config.roadSeed, noiseScale);

                    float roadMask = Mathf.Clamp01(SampleNearest(road, u, v));
                    float roadNoise = Mathf.PerlinNoise(
                        sampleWorldX / noiseScale + _config.roadSeed * 0.173f,
                        sampleWorldZ / noiseScale + _config.roadSeed * 0.317f);
                    float roadBlend = roadMask * roadNoise;
                    if (!HasPositiveWeight(roadWeights)) roadBlend = 0f;
                    if (!HasPositiveWeight(natural)) roadBlend = 1f;

                    float total = 0f;
                    for (int i = 0; i < terrainLayers.Count; i++)
                    {
                        float value = Mathf.Lerp(natural[i], roadWeights[i], roadBlend);
                        alphamaps[z, x, i] = value;
                        total += value;
                    }
                    if (total <= 0.000001f)
                        alphamaps[z, x, 0] = 1f;
                    else
                        for (int i = 0; i < terrainLayers.Count; i++)
                            alphamaps[z, x, i] /= total;
                }
            }
            ApplyTextureSmoothing(alphamaps, Mathf.Max(0, _config.config.textureSmoothingRadius));
            terrainData.SetAlphamaps(0, 0, alphamaps);
        }

        /// <summary>
        /// 对已混合并归一化的 alphamap 做五点均值平滑：中心、±X、±Z。
        /// 半径以 alphamap 像素计，边缘采样钳制到最近有效像素；每个像素的权重向量随后重新归一化。
        /// </summary>
        private static void ApplyTextureSmoothing(float[,,] alphamaps, int radius)
        {
            if (radius <= 0) return;

            int height = alphamaps.GetLength(0);
            int width = alphamaps.GetLength(1);
            int layerCount = alphamaps.GetLength(2);
            if (height == 0 || width == 0 || layerCount == 0) return;

            var source = (float[,,])alphamaps.Clone();
            for (int z = 0; z < height; z++)
            {
                int zMinus = Mathf.Clamp(z - radius, 0, height - 1);
                int zPlus = Mathf.Clamp(z + radius, 0, height - 1);
                for (int x = 0; x < width; x++)
                {
                    int xMinus = Mathf.Clamp(x - radius, 0, width - 1);
                    int xPlus = Mathf.Clamp(x + radius, 0, width - 1);
                    float total = 0f;
                    for (int layer = 0; layer < layerCount; layer++)
                    {
                        float value = (source[z, x, layer] + source[z, xMinus, layer] +
                                       source[z, xPlus, layer] + source[zMinus, x, layer] +
                                       source[zPlus, x, layer]) / 5f;
                        alphamaps[z, x, layer] = value;
                        total += value;
                    }

                    if (total <= 0.000001f)
                        alphamaps[z, x, 0] = 1f;
                    else
                        for (int layer = 0; layer < layerCount; layer++)
                            alphamaps[z, x, layer] /= total;
                }
            }
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

            ClearGeneratedRoot(ref _poolRoot);
            var poolObject = new GameObject("_TerrainBuilderPools");
            poolObject.hideFlags = HideFlags.HideInHierarchy;
            if (!Application.isPlaying) poolObject.hideFlags |= HideFlags.DontSave;
            poolObject.transform.SetParent(transform, false);
            _poolRoot = poolObject.transform;

            _scatterRuntimes.Clear();
            for (int groupIndex = 0; groupIndex < projectConfig.scatterGroups.Count; groupIndex++)
            {
                var group = projectConfig.scatterGroups[groupIndex];
                if (group == null) continue;

                var runtime = new ScatterRuntime
                {
                    config = group,
                    groupIndex = groupIndex,
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

                // 运行时：逐组读取缓存文件（Scatter_{index}.txt）并校验指纹 → 直接重建放置列表
                bool usedCache = false;
                if (Application.isPlaying)
                {
                    PlacementCache.CacheData groupCache = TryLoadScatterGroupCache(groupIndex);
                    if (groupCache != null && groupCache.scatter.Count > 0)
                    {
                        usedCache = RebuildScatterFromCache(runtime, groupCache.scatter);
                        if (!usedCache)
                            Debug.LogWarning($"[TerrainBuilder] 散布组 {group.groupName} 缓存无法解析（prefab instanceId 变化），已回退重新计算。");
                        else
                            Debug.Log($"[TerrainBuilder] 散布组 {group.groupName} 指纹一致，已从 PlacementCache 复用位置。");
                    }
                    else if (groupCache == null && HasPlacementCacheKey(PlacementCache.ScatterFileName(groupIndex)))
                    {
                        Debug.LogWarning($"[TerrainBuilder] 散布组 {group.groupName} 缓存指纹不一致或格式无效，已重新计算（不写盘）。");
                    }
                }

                if (!usedCache)
                {
                    PrecomputeScatterPlacements(runtime);
                    // 编辑器模式：把本次重算的位置收集进该组缓存（Build 末尾落盘）
                    if (!Application.isPlaying)
                    {
                        var cache = new PlacementCache.CacheData
                        {
                            chunkSize = group.chunkSize,
                            visibleDistance = group.visibleDistance,
                            fingerprint = ComputeScatterFingerprint(groupIndex),
                        };
                        CollectScatterCache(cache, runtime, groupIndex);
                        while (_scatterCacheGroups.Count <= groupIndex)
                            _scatterCacheGroups.Add(null);
                        _scatterCacheGroups[groupIndex] = cache;
                    }
                }

                _scatterRuntimes.Add(runtime);
            }
        }

        /// <summary>把某散布组预计算好的放置列表转存为缓存条目（prefab instanceId + 像素 + 缩放 + 偏航）。</summary>
        private static void CollectScatterCache(
            PlacementCache.CacheData cache, ScatterRuntime runtime, int groupIndex)
        {
            cache.scatter.Clear();
            foreach (var pair in runtime.placementsByChunk)
            {
                foreach (ScatterRuntime.Placement placement in pair.Value)
                {
                    GameObject prefab = placement.prefabKey >= 0 &&
                                        placement.prefabKey < runtime.config.prefabs.Count
                        ? runtime.config.prefabs[placement.prefabKey].prefab
                        : null;
                    if (prefab == null) continue;
                    cache.scatter.Add(new PlacementCache.ScatterPlacement
                    {
                        prefabInstanceId = prefab.GetInstanceID(),
                        pixelX = placement.pixel.x,
                        pixelZ = placement.pixel.y,
                        scale = placement.scale,
                        yaw = placement.yaw,
                    });
                }
            }
        }

        /// <summary>
        /// 用缓存条目重建某散布组的 placementsByChunk（像素 → 区块索引映射与预计算一致）。
        /// 任一 prefab instanceId 在当前组内无法解析时返回 false（调用方回退重新计算）。
        /// </summary>
        private bool RebuildScatterFromCache(ScatterRuntime runtime, List<PlacementCache.ScatterPlacement> cached)
        {
            var keyByInstanceId = new Dictionary<int, int>();
            for (int i = 0; i < runtime.config.prefabs.Count; i++)
            {
                var entry = runtime.config.prefabs[i];
                if (entry == null || entry.prefab == null || entry.weight <= 0) continue;
                int instanceId = entry.prefab.GetInstanceID();
                if (!keyByInstanceId.ContainsKey(instanceId)) keyByInstanceId[instanceId] = i;
            }

            float chunkWidth = Mathf.Max(0.0001f, runtime.config.chunkSize.x);
            float chunkDepth = Mathf.Max(0.0001f, runtime.config.chunkSize.y);
            int countX = Mathf.CeilToInt(_terrain.terrainData.size.x / chunkWidth);
            int countZ = Mathf.CeilToInt(_terrain.terrainData.size.z / chunkDepth);

            runtime.placementsByChunk.Clear();
            for (int i = 0; i < cached.Count; i++)
            {
                PlacementCache.ScatterPlacement entry = cached[i];
                if (!keyByInstanceId.TryGetValue(entry.prefabInstanceId, out int key)) return false;
                int idxX = Mathf.Clamp(
                    Mathf.FloorToInt(entry.pixelX * _wppX / chunkWidth), 0, countX - 1);
                int idxZ = Mathf.Clamp(
                    Mathf.FloorToInt(entry.pixelZ * _wppZ / chunkDepth), 0, countZ - 1);
                var chunkIndex = new Vector2Int(idxX, idxZ);
                runtime.chunks.RegisterChunk(chunkIndex);
                if (!runtime.placementsByChunk.TryGetValue(chunkIndex, out var list))
                {
                    list = new List<ScatterRuntime.Placement>();
                    runtime.placementsByChunk[chunkIndex] = list;
                }
                list.Add(new ScatterRuntime.Placement
                {
                    prefabKey = key,
                    pixel = new Vector2Int(entry.pixelX, entry.pixelZ),
                    scale = entry.scale,
                    yaw = entry.yaw,
                });
            }
            return true;
        }

        // ---------- 放置缓存读写（按生成组，每组一个文件） ----------

        /// <summary>主配置是否已引用该缓存文件名（运行时判断是否有缓存可读）。</summary>
        private bool HasPlacementCacheKey(string fileName)
        {
            return LoadPlacementCacheAsset(fileName) != null;
        }

        /// <summary>读取主配置引用的指定缓存文件 TextAsset（无则 null）。</summary>
        private TextAsset LoadPlacementCacheAsset(string fileName)
        {
            if (_config == null || _config.placementCacheFiles == null) return null;
            foreach (var e in _config.placementCacheFiles)
                if (e != null && e.key == fileName) return e.file;
            return null;
        }

        /// <summary>运行时：读取并校验散布组缓存（Scatter_{index}.txt）；未命中返回 null。</summary>
        private PlacementCache.CacheData TryLoadScatterGroupCache(int groupIndex)
        {
            TextAsset asset = LoadPlacementCacheAsset(PlacementCache.ScatterFileName(groupIndex));
            if (asset == null) return null;
            PlacementCache.CacheData data = PlacementCache.DecodeScatter(asset.text);
            if (data == null) return null;
            if (data.fingerprint != ComputeScatterFingerprint(groupIndex)) return null;
            return data;
        }

        /// <summary>运行时：读取并校验摆件组缓存（Prop_{index}.txt）；未命中返回 null。</summary>
        private PlacementCache.CacheData TryLoadPropGroupCache(int groupIndex)
        {
            TextAsset asset = LoadPlacementCacheAsset(PlacementCache.PropFileName(groupIndex));
            if (asset == null) return null;
            PlacementCache.CacheData data = PlacementCache.DecodeProps(asset.text);
            if (data == null) return null;
            if (data.fingerprint != ComputePropFingerprint(groupIndex)) return null;
            return data;
        }

        /// <summary>运行时：读取并校验定点组缓存（Fixed_{index}.txt）；未命中返回 null。</summary>
        private PlacementCache.CacheData TryLoadFixedGroupCache(int groupIndex)
        {
            TextAsset asset = LoadPlacementCacheAsset(PlacementCache.FixedFileName(groupIndex));
            if (asset == null) return null;
            PlacementCache.CacheData data = PlacementCache.DecodeFixed(asset.text);
            if (data == null) return null;
            if (data.fingerprint != ComputeFixedFingerprint(groupIndex)) return null;
            return data;
        }

        /// <summary>编辑器（非运行时）Build 结束时：按生成组逐个写入缓存 txt 并更新主配置引用。</summary>
        private void SavePlacementCaches(TerrainPaintProjectSO project)
        {
            for (int i = 0; i < _scatterCacheGroups.Count; i++)
            {
                var cache = _scatterCacheGroups[i];
                if (cache == null) continue;
                cache.fingerprint = ComputeScatterFingerprint(i);
                project.WritePlacementCacheFile(
                    PlacementCache.ScatterFileName(i), PlacementCache.EncodeScatter(cache));
            }
            for (int i = 0; i < _propCacheGroups.Count; i++)
            {
                var cache = _propCacheGroups[i];
                if (cache == null) continue;
                cache.fingerprint = ComputePropFingerprint(i);
                project.WritePlacementCacheFile(
                    PlacementCache.PropFileName(i), PlacementCache.EncodeProps(cache));
            }
            for (int i = 0; i < _fixedCacheGroups.Count; i++)
            {
                var cache = _fixedCacheGroups[i];
                if (cache == null) continue;
                cache.fingerprint = ComputeFixedFingerprint(i);
                project.WritePlacementCacheFile(
                    PlacementCache.FixedFileName(i), PlacementCache.EncodeFixed(cache));
            }
            project.RefreshPlacementCacheRefs(true);
        }

        /// <summary>指纹公共前缀：Terrain 尺寸/位置、高度/散布/摆件种子、每层道路参数、邻接组、layerMap 内容。</summary>
        private void AppendFingerprintBase(StringBuilder sb)
        {
            sb.Append("t=").Append(_terrain.terrainData.size.x.ToString("R", CultureInfo.InvariantCulture))
              .Append(',').Append(_terrain.terrainData.size.z.ToString("R", CultureInfo.InvariantCulture))
              .Append(";tp=").Append(_terrain.transform.position.x.ToString("R", CultureInfo.InvariantCulture))
              .Append(',').Append(_terrain.transform.position.z.ToString("R", CultureInfo.InvariantCulture))
              .Append(";hs=").Append(_config.heightSeed)
              .Append(";hsc=").Append(_config.heightScale.ToString("R", CultureInfo.InvariantCulture))
              .Append(";ss=").Append(_config.scatterSeed)
              .Append(";ps=").Append(_config.propSeed)
              .Append(';');

            for (int i = 0; i < _config.layers.Count; i++)
            {
                LayerConfigSO layer = _config.layers[i];
                if (layer == null) continue;
                sb.Append("l").Append(i).Append(':')
                  .Append(layer.generateRoad ? 1 : 0).Append(',')
                  .Append(layer.roadWidth.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            }
            for (int g = 0; g < _config.adjacencyGroups.Count; g++)
            {
                List<int> group = _config.adjacencyGroups[g];
                if (group == null) continue;
                sb.Append("a").Append(g).Append(':');
                foreach (int idx in group) sb.Append(idx).Append(',');
                sb.Append(';');
            }
            sb.Append("lm=").Append(HashMapData(_layerMapData)).Append(';');
        }

        /// <summary>散布组指纹：公共前缀 + 该组全部参数 + 组内 prefab instanceId/权重。</summary>
        private string ComputeScatterFingerprint(int groupIndex)
        {
            var sb = new StringBuilder();
            AppendFingerprintBase(sb);
            ScatterConfigSO group = groupIndex < _config.scatterGroups.Count ? _config.scatterGroups[groupIndex] : null;
            if (group == null) { sb.Append("sg=").Append(groupIndex).Append(":null"); return sb.ToString(); }
            sb.Append("sg=").Append(groupIndex).Append(':')
              .Append(group.chunkSize.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.chunkSize.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.visibleDistance.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.density.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.randomScale.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.randomScale.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.offRoadDistanceRange.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.offRoadDistanceRange.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append((int)group.targetLayers).Append(';');
            foreach (ScatterPrefabEntry entry in group.prefabs)
            {
                if (entry == null || entry.prefab == null) continue;
                sb.Append(entry.prefab.GetInstanceID()).Append(':').Append(entry.weight).Append(',');
            }
            return sb.ToString();
        }

        /// <summary>摆件组指纹：公共前缀 + 该组全部参数 + 组内 prefab instanceId/权重/数量下限。</summary>
        private string ComputePropFingerprint(int groupIndex)
        {
            var sb = new StringBuilder();
            AppendFingerprintBase(sb);
            PropConfigSO group = groupIndex < _config.propGroups.Count ? _config.propGroups[groupIndex] : null;
            if (group == null) { sb.Append("pg=").Append(groupIndex).Append(":null"); return sb.ToString(); }
            sb.Append("pg=").Append(groupIndex).Append(':')
              .Append(group.chunkSize.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.chunkSize.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.visibleDistance.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.maxFailedAttempts).Append(',')
              .Append(group.expectedDensity.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.batchSize.x).Append(',').Append(group.batchSize.y).Append(',')
              .Append((int)group.targetLayers).Append(',')
              .Append(group.outOfBoundsTolerance.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append((int)group.arrangementBasis).Append(',')
              .Append(group.arrangementRange.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.arrangementRange.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append((int)group.rotationMode).Append(',').Append((int)group.distributionMode).Append(',')
              .Append(group.distributionSpacing.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            foreach (PropPrefabEntry entry in group.prefabs)
            {
                if (entry == null || entry.prefab == null) continue;
                sb.Append(entry.prefab.GetInstanceID()).Append(':').Append(entry.weight).Append(':')
                  .Append(entry.minimumCount).Append(',');
            }
            return sb.ToString();
        }

        /// <summary>定点组指纹：公共前缀 + 该组全部参数（prefab instanceId / 旋转 / 缩放 / 归一化位置列表）。</summary>
        private string ComputeFixedFingerprint(int groupIndex)
        {
            var sb = new StringBuilder();
            AppendFingerprintBase(sb);
            FixedPointConfigSO group = groupIndex < _config.fixedPointGroups.Count ? _config.fixedPointGroups[groupIndex] : null;
            if (group == null) { sb.Append("fg=").Append(groupIndex).Append(":null"); return sb.ToString(); }
            sb.Append("fg=").Append(groupIndex).Append(':')
              .Append(group.chunkSize.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.chunkSize.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.visibleDistance.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            if (group.prefab != null)
                sb.Append(group.prefab.GetInstanceID()).Append(',');
            sb.Append(group.rotationDegrees.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(group.scale.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            if (group.positions != null)
                foreach (Vector2 pos in group.positions)
                    sb.Append(pos.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                      .Append(pos.y.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            return sb.ToString();
        }

        /// <summary>对 float[][] 做简单确定性哈希（FNV-1a），用于 MapData 内容指纹。</summary>
        private static string HashMapData(float[][] data)
        {
            if (data == null) return "null";
            unchecked
            {
                uint hash = 2166136261;
                foreach (var row in data)
                {
                    if (row == null) continue;
                    foreach (float value in row)
                    {
                        int rounded = Mathf.RoundToInt(value * 1000f);
                        hash ^= (uint)rounded;
                        hash *= 16777619;
                    }
                }
                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>按配置的值域、分布、Bounds 间距和梯度朝向生成静态摆件。</summary>
        private void ApplyProps()
        {
            ClearGeneratedRoot(ref _propRoot);
            var rootObject = new GameObject("_TerrainBuilderProps");
            rootObject.transform.SetParent(transform, false);
            _propRoot = rootObject.transform;

            float[][] layerMap = MapData.Get("layerMap");
            if (!TryGetMapSize(layerMap, out int mapW, out int mapH))
            {
                Debug.LogWarning("[TerrainBuilder] layerMap 缺失，跳过摆件生成。");
                return;
            }
            float[][] height = MapData.Get("height");
            float[][] distance = MapData.Get("distance");
            float[][] offRoad = MapData.Get("offRoad");
            Vector3 terrainSize = _terrain.terrainData.size;
            var pixelWorldSize = new Vector2(
                terrainSize.x / Mathf.Max(1, mapW - 1),
                terrainSize.z / Mathf.Max(1, mapH - 1));

            for (int groupIndex = 0; groupIndex < _config.propGroups.Count; groupIndex++)
            {
                PropConfigSO group = _config.propGroups[groupIndex];
                if (group == null || group.prefabs == null || group.prefabs.Count == 0)
                    continue;

                // 运行时：逐组读取缓存文件（Prop_{index}.txt）并校验指纹 → 直接实例化缓存摆件
                bool usedCache = false;
                if (Application.isPlaying)
                {
                    PlacementCache.CacheData groupCache = TryLoadPropGroupCache(groupIndex);
                    if (groupCache != null && groupCache.props.Count > 0)
                    {
                        usedCache = InstantiatePropsFromCache(group, groupCache.props);
                        if (!usedCache)
                            Debug.LogWarning($"[TerrainBuilder] 摆件组 {group.groupName} 缓存无法解析（prefab instanceId 变化），已回退重新计算。");
                        else
                            Debug.Log($"[TerrainBuilder] 摆件组 {group.groupName} 指纹一致，已从 PlacementCache 复用位置。");
                    }
                    else if (groupCache == null && HasPlacementCacheKey(PlacementCache.PropFileName(groupIndex)))
                    {
                        Debug.LogWarning($"[TerrainBuilder] 摆件组 {group.groupName} 缓存指纹不一致或格式无效，已重新计算（不写盘）。");
                    }
                }
                if (usedCache) continue;

                float[][] basis = group.arrangementBasis == PropArrangementBasis.Distance
                    ? distance
                    : group.arrangementBasis == PropArrangementBasis.Height ? height : offRoad;
                if (!HasMapSize(basis, mapW, mapH))
                {
                    Debug.LogWarning($"[TerrainBuilder] 摆件组 {group.groupName} 缺少 {group.arrangementBasis} 数据，已跳过。");
                    continue;
                }
                List<PropPlacement> placed = BuildPropGroup(
                    group, groupIndex, layerMap, basis, mapW, mapH, pixelWorldSize);

                // 编辑器模式：把本次重算的摆件位置收集进该组缓存（Build 末尾落盘）
                if (!Application.isPlaying)
                {
                    var cache = new PlacementCache.CacheData
                    {
                        chunkSize = group.chunkSize,
                        visibleDistance = group.visibleDistance,
                        fingerprint = ComputePropFingerprint(groupIndex),
                    };
                    CollectPropsCache(cache, placed);
                    while (_propCacheGroups.Count <= groupIndex)
                        _propCacheGroups.Add(null);
                    _propCacheGroups[groupIndex] = cache;
                }
            }
        }

        /// <summary>把某摆件组实际放置的结果转存为缓存条目（prefab instanceId + 世界 XZ + 偏航）。</summary>
        private static void CollectPropsCache(PlacementCache.CacheData cache, List<PropPlacement> placed)
        {
            cache.props.Clear();
            if (placed == null) return;
            foreach (PropPlacement placement in placed)
            {
                if (placement == null || placement.prefab == null) continue;
                cache.props.Add(new PlacementCache.PropPlacement
                {
                    prefabInstanceId = placement.prefab.GetInstanceID(),
                    worldX = placement.worldXZ.x,
                    worldZ = placement.worldXZ.y,
                    yaw = placement.yaw,
                });
            }
        }

        /// <summary>用缓存条目直接实例化摆件（组内按 instanceId 解析 prefab；任一解析失败返回 false 回退重算）。</summary>
        private bool InstantiatePropsFromCache(
            PropConfigSO group, List<PlacementCache.PropPlacement> cached)
        {
            var prefabByInstanceId = new Dictionary<int, GameObject>();
            for (int i = 0; i < group.prefabs.Count; i++)
            {
                PropPrefabEntry entry = group.prefabs[i];
                if (entry == null || entry.prefab == null) continue;
                int instanceId = entry.prefab.GetInstanceID();
                if (!prefabByInstanceId.ContainsKey(instanceId)) prefabByInstanceId[instanceId] = entry.prefab;
            }
            for (int i = 0; i < cached.Count; i++)
                if (!prefabByInstanceId.ContainsKey(cached[i].prefabInstanceId)) return false;

            for (int i = 0; i < cached.Count; i++)
            {
                PlacementCache.PropPlacement entry = cached[i];
                GameObject prefab = prefabByInstanceId[entry.prefabInstanceId];
                var placement = new PropPlacement
                {
                    prefab = prefab,
                    pixel = Vector2Int.zero,
                    worldXZ = new Vector2(entry.worldX, entry.worldZ),
                    yaw = entry.yaw,
                    radius = GetPrefabHorizontalRadius(prefab),
                };
                InstantiateProp(placement);
            }
            return true;
        }

        private List<PropPlacement> BuildPropGroup(
            PropConfigSO group,
            int groupIndex,
            float[][] layerMap,
            float[][] basis,
            int mapW,
            int mapH,
            Vector2 pixelWorldSize)
        {
            var validPixels = new List<Vector2Int>();
            for (int z = 0; z < mapH; z++)
            for (int x = 0; x < mapW; x++)
            {
                int layerIndex = Mathf.Max(0, Mathf.RoundToInt(layerMap[z][x]));
                if (layerIndex >= TerrainPaintProjectSO.MaxLayerCount) continue;
                var flag = (TerrainWorkflowLayerMask)(1 << layerIndex);
                if ((group.targetLayers & flag) == 0) continue;
                float value = basis[z][x];
                if (value < Mathf.Min(group.arrangementRange.x, group.arrangementRange.y) ||
                    value > Mathf.Max(group.arrangementRange.x, group.arrangementRange.y)) continue;
                validPixels.Add(new Vector2Int(x, z));
            }
            if (validPixels.Count == 0) return null;
            var validPixelSet = new HashSet<Vector2Int>(validPixels);

            float validArea = validPixels.Count * pixelWorldSize.x * pixelWorldSize.y;
            int requestedCount = Mathf.Max(0, Mathf.RoundToInt(validArea * group.expectedDensity));
            List<int> prefabSchedule = BuildPropPrefabSchedule(group, requestedCount,
                new System.Random(_config.propSeed ^ (groupIndex * 83492791)), out int minimumTotal);
            if (prefabSchedule.Count == 0) return null;

            var rng = new System.Random(_config.propSeed ^ (groupIndex * 83492791));
            var placed = new List<PropPlacement>();
            int scheduleIndex = 0;
            int failedBatches = 0;
            int proposalCount = Mathf.Max(1, group.batchSize.y);
            int requiredCount = Mathf.Clamp(group.batchSize.x, 1, proposalCount);

            while (scheduleIndex < prefabSchedule.Count && failedBatches < Mathf.Max(1, group.maxFailedAttempts))
            {
                var acceptedBatch = new List<PropPlacement>();
                int remaining = prefabSchedule.Count - scheduleIndex;
                bool placingMinimum = scheduleIndex < minimumTotal;
                int proposals = placingMinimum ? 1 : Mathf.Min(proposalCount, remaining);
                for (int i = 0; i < proposals; i++)
                {
                    int prefabIndex = prefabSchedule[scheduleIndex + i];
                    GameObject prefab = group.prefabs[prefabIndex].prefab;
                    if (prefab == null) continue;
                    PropPlacement proposal = FindVisualPropCandidate(
                        group, prefab, validPixels, placed, acceptedBatch,
                        validPixelSet, layerMap, basis, mapW, mapH, pixelWorldSize, rng);
                    if (proposal != null) acceptedBatch.Add(proposal);
                }

                int batchRequired = placingMinimum ? 1 : Mathf.Min(requiredCount, proposals);
                if (acceptedBatch.Count < batchRequired)
                {
                    failedBatches++;
                    continue;
                }

                foreach (PropPlacement placement in acceptedBatch)
                {
                    InstantiateProp(placement);
                    placed.Add(placement);
                }
                scheduleIndex += proposals;
                failedBatches = 0;
            }
            return placed;
        }

        private List<int> BuildPropPrefabSchedule(
            PropConfigSO group, int requestedCount, System.Random rng, out int minimumTotal)
        {
            var schedule = new List<int>();
            minimumTotal = 0;
            for (int i = 0; i < group.prefabs.Count; i++)
            {
                PropPrefabEntry entry = group.prefabs[i];
                if (entry == null || entry.prefab == null) continue;
                int minimum = Mathf.Max(0, entry.minimumCount);
                minimumTotal += minimum;
                for (int n = 0; n < minimum; n++) schedule.Add(i);
            }
            int target = Mathf.Max(requestedCount, minimumTotal);
            while (schedule.Count < target)
            {
                int picked = PickWeightedPropPrefab(group, rng);
                if (picked < 0) break;
                schedule.Add(picked);
            }
            return schedule;
        }

        private static int PickWeightedPropPrefab(PropConfigSO group, System.Random rng)
        {
            int total = 0;
            foreach (PropPrefabEntry entry in group.prefabs)
                if (entry != null && entry.prefab != null && entry.weight > 0) total += entry.weight;
            if (total <= 0) return -1;
            int value = rng.Next(total);
            for (int i = 0; i < group.prefabs.Count; i++)
            {
                PropPrefabEntry entry = group.prefabs[i];
                if (entry == null || entry.prefab == null || entry.weight <= 0) continue;
                if (value < entry.weight) return i;
                value -= entry.weight;
            }
            return -1;
        }

        private PropPlacement FindVisualPropCandidate(
            PropConfigSO group,
            GameObject prefab,
            List<Vector2Int> validPixels,
            List<PropPlacement> placed,
            List<PropPlacement> acceptedBatch,
            HashSet<Vector2Int> validPixelSet,
            float[][] layerMap,
            float[][] basis,
            int mapW,
            int mapH,
            Vector2 pixelWorldSize,
            System.Random rng)
        {
            const int CandidateTrials = 16;
            PropPlacement best = null;
            float bestScore = float.NegativeInfinity;
            for (int trial = 0; trial < CandidateTrials; trial++)
            {
                Vector2Int pixel = SelectPropPixel(
                    group.distributionMode, validPixels, validPixelSet, placed, pixelWorldSize, rng);
                float yaw = CalculatePropYaw(group.rotationMode, pixel, basis, mapW, mapH, pixelWorldSize, rng);
                float radius = GetPrefabHorizontalRadius(prefab);
                Vector2 worldXZ = PixelToWorldXZ(pixel.x, pixel.y);
                var candidate = new PropPlacement
                {
                    prefab = prefab,
                    pixel = pixel,
                    worldXZ = worldXZ,
                    yaw = yaw,
                    radius = radius,
                };
                if (!PassesPropFootprint(
                        candidate, group, layerMap, basis, mapW, mapH, pixelWorldSize)) continue;
                float clearance = MinimumPropClearance(candidate, placed, acceptedBatch, group.distributionSpacing);
                if (clearance < 0f) continue;

                // 多候选择优产生蓝噪声式空隙；低频噪声轻微调制，避免过于规则。
                float visualNoise = Mathf.PerlinNoise(
                    worldXZ.x * 0.037f + _config.propSeed * 0.11f,
                    worldXZ.y * 0.037f + _config.propSeed * 0.19f);
                float score = Mathf.Min(clearance, radius * 6f) + visualNoise * radius * 0.75f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        private Vector2Int SelectPropPixel(
            PropDistributionMode mode,
            List<Vector2Int> validPixels,
            HashSet<Vector2Int> validPixelSet,
            List<PropPlacement> placed,
            Vector2 pixelWorldSize,
            System.Random rng)
        {
            if (placed.Count == 0 || mode == PropDistributionMode.Scatter)
                return validPixels[rng.Next(validPixels.Count)];

            PropPlacement anchor = placed[rng.Next(placed.Count)];
            Vector2 target;
            if (mode == PropDistributionMode.Cluster)
            {
                double angle = rng.NextDouble() * System.Math.PI * 2.0;
                float distance = anchor.radius * Mathf.Lerp(1.5f, 5f, (float)rng.NextDouble());
                target = anchor.worldXZ + new Vector2(
                    (float)System.Math.Cos(angle) * distance,
                    (float)System.Math.Sin(angle) * distance);
            }
            else
            {
                float distance = anchor.radius * Mathf.Lerp(1.8f, 3.5f, (float)rng.NextDouble());
                float radians = anchor.yaw * Mathf.Deg2Rad;
                target = anchor.worldXZ + new Vector2(Mathf.Sin(radians), Mathf.Cos(radians)) * distance;
            }

            Vector3 terrainPosition = _terrain.transform.position;
            var targetPixel = new Vector2Int(
                Mathf.RoundToInt((target.x - terrainPosition.x) / pixelWorldSize.x),
                Mathf.RoundToInt((target.y - terrainPosition.z) / pixelWorldSize.y));
            if (validPixelSet.Contains(targetPixel)) return targetPixel;

            Vector2Int best = validPixels[rng.Next(validPixels.Count)];
            float bestDistance = float.PositiveInfinity;
            int searchRadius = Mathf.Clamp(
                Mathf.CeilToInt(anchor.radius * 6f /
                    Mathf.Max(0.0001f, Mathf.Min(pixelWorldSize.x, pixelWorldSize.y))), 4, 64);
            for (int dz = -searchRadius; dz <= searchRadius; dz++)
            for (int dx = -searchRadius; dx <= searchRadius; dx++)
            {
                var candidate = new Vector2Int(targetPixel.x + dx, targetPixel.y + dz);
                if (!validPixelSet.Contains(candidate)) continue;
                Vector2 world = PixelToWorldXZ(candidate.x, candidate.y);
                float distance = (world - target).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        private static float CalculatePropYaw(
            PropRotationMode mode,
            Vector2Int pixel,
            float[][] basis,
            int mapW,
            int mapH,
            Vector2 pixelWorldSize,
            System.Random rng)
        {
            if (mode == PropRotationMode.Random)
                return (float)rng.NextDouble() * 360f;
            int x0 = Mathf.Max(0, pixel.x - 1), x1 = Mathf.Min(mapW - 1, pixel.x + 1);
            int z0 = Mathf.Max(0, pixel.y - 1), z1 = Mathf.Min(mapH - 1, pixel.y + 1);
            float gradientX = (basis[pixel.y][x1] - basis[pixel.y][x0]) /
                              Mathf.Max(0.0001f, (x1 - x0) * pixelWorldSize.x);
            float gradientZ = (basis[z1][pixel.x] - basis[z0][pixel.x]) /
                              Mathf.Max(0.0001f, (z1 - z0) * pixelWorldSize.y);
            Vector2 direction = new Vector2(gradientX, gradientZ);
            if (direction.sqrMagnitude < 0.000001f)
                return (float)rng.NextDouble() * 360f;
            if (mode == PropRotationMode.TowardLowGradient) direction = -direction;
            if (mode == PropRotationMode.AlongContour)
                direction = new Vector2(-direction.y, direction.x);
            return Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;
        }

        private static float GetPrefabHorizontalRadius(GameObject prefab)
        {
            PrefabStructureInfo info = prefab != null ? prefab.GetComponent<PrefabStructureInfo>() : null;
            if (info == null) return 0.5f;
            float x = Mathf.Max(Mathf.Abs(info.boundsX.x), Mathf.Abs(info.boundsX.y));
            float z = Mathf.Max(Mathf.Abs(info.boundsZ.x), Mathf.Abs(info.boundsZ.y));
            return Mathf.Max(0.05f, Mathf.Sqrt(x * x + z * z));
        }

        private bool PassesPropFootprint(
            PropPlacement candidate,
            PropConfigSO group,
            float[][] layerMap,
            float[][] basis,
            int mapW,
            int mapH,
            Vector2 pixelWorldSize)
        {
            PrefabStructureInfo info = candidate.prefab.GetComponent<PrefabStructureInfo>();
            float minX = info != null ? info.boundsX.x : -candidate.radius;
            float maxX = info != null ? info.boundsX.y : candidate.radius;
            float minZ = info != null ? info.boundsZ.x : -candidate.radius;
            float maxZ = info != null ? info.boundsZ.y : candidate.radius;
            Quaternion rotation = Quaternion.Euler(0f, candidate.yaw, 0f);
            int valid = 0;
            const int samplesPerAxis = 3;
            for (int iz = 0; iz < samplesPerAxis; iz++)
            for (int ix = 0; ix < samplesPerAxis; ix++)
            {
                float tx = ix / (samplesPerAxis - 1f);
                float tz = iz / (samplesPerAxis - 1f);
                Vector3 offset = rotation * new Vector3(
                    Mathf.Lerp(minX, maxX, tx), 0f, Mathf.Lerp(minZ, maxZ, tz));
                Vector2 sampleWorld = candidate.worldXZ + new Vector2(offset.x, offset.z);
                int px = Mathf.RoundToInt(
                    (sampleWorld.x - _terrain.transform.position.x) / pixelWorldSize.x);
                int pz = Mathf.RoundToInt(
                    (sampleWorld.y - _terrain.transform.position.z) / pixelWorldSize.y);
                if (px < 0 || px >= mapW || pz < 0 || pz >= mapH) continue;
                int layerIndex = Mathf.Max(0, Mathf.RoundToInt(layerMap[pz][px]));
                if (layerIndex >= TerrainPaintProjectSO.MaxLayerCount) continue;
                var flag = (TerrainWorkflowLayerMask)(1 << layerIndex);
                float value = basis[pz][px];
                if ((group.targetLayers & flag) != 0 &&
                    value >= Mathf.Min(group.arrangementRange.x, group.arrangementRange.y) &&
                    value <= Mathf.Max(group.arrangementRange.x, group.arrangementRange.y))
                    valid++;
            }
            float outsideRatio = 1f - valid / (float)(samplesPerAxis * samplesPerAxis);
            return outsideRatio <= Mathf.Clamp01(group.outOfBoundsTolerance);
        }

        private static float MinimumPropClearance(
            PropPlacement candidate,
            List<PropPlacement> placed,
            List<PropPlacement> acceptedBatch,
            float spacing)
        {
            float minimum = float.PositiveInfinity;
            for (int i = 0; i < placed.Count; i++)
                minimum = Mathf.Min(minimum, PropClearance(candidate, placed[i], spacing));
            for (int i = 0; i < acceptedBatch.Count; i++)
                minimum = Mathf.Min(minimum, PropClearance(candidate, acceptedBatch[i], spacing));
            return float.IsPositiveInfinity(minimum) ? candidate.radius * 8f : minimum;
        }

        private static float PropClearance(PropPlacement a, PropPlacement b, float spacing)
        {
            return Vector2.Distance(a.worldXZ, b.worldXZ) - a.radius - b.radius - spacing;
        }

        private void InstantiateProp(PropPlacement placement)
        {
            Quaternion rotation = Quaternion.Euler(0f, placement.yaw, 0f);
            GameObject instance = Instantiate(placement.prefab, _propRoot);
            instance.transform.rotation = rotation;
            instance.transform.localScale = Vector3.one;
            float worldY = SamplePlacementHeight(
                placement.prefab, placement.worldXZ.x, placement.worldXZ.y, rotation, 1f);
            instance.transform.position = new Vector3(
                placement.worldXZ.x, worldY, placement.worldXZ.y);
        }

        private Vector2 PixelToWorldXZ(int px, int pz)
        {
            Vector3 terrainPosition = _terrain.transform.position;
            return new Vector2(
                terrainPosition.x + px * _wppX,
                terrainPosition.z + pz * _wppZ);
        }

        /// <summary>按归一化 X/Z、统一旋转和缩放实例化定点物体（支持缓存：运行时逐组读取 Fixed_{index}.txt）。</summary>
        private void ApplyFixedPoints()
        {
            ClearGeneratedRoot(ref _fixedRoot);
            var rootObject = new GameObject("_TerrainBuilderFixedPoints");
            rootObject.hideFlags = HideFlags.HideInHierarchy;
            if (!Application.isPlaying) rootObject.hideFlags |= HideFlags.DontSave;
            rootObject.transform.SetParent(transform, false);
            _fixedRoot = rootObject.transform;

            for (int groupIndex = 0; groupIndex < _config.fixedPointGroups.Count; groupIndex++)
            {
                FixedPointConfigSO group = _config.fixedPointGroups[groupIndex];
                if (group == null || group.prefab == null || group.positions == null)
                    continue;

                // 运行时：逐组读取缓存文件（Fixed_{index}.txt）并校验指纹 → 直接实例化缓存定点
                bool usedCache = false;
                if (Application.isPlaying)
                {
                    PlacementCache.CacheData groupCache = TryLoadFixedGroupCache(groupIndex);
                    if (groupCache != null && groupCache.fixedPoints.Count > 0)
                    {
                        usedCache = InstantiateFixedFromCache(group, groupCache.fixedPoints);
                        if (!usedCache)
                            Debug.LogWarning($"[TerrainBuilder] 定点组 {groupIndex} 缓存无法解析（prefab instanceId 变化），已回退重新计算。");
                        else
                            Debug.Log($"[TerrainBuilder] 定点组 {groupIndex} 指纹一致，已从 PlacementCache 复用位置。");
                    }
                    else if (groupCache == null && HasPlacementCacheKey(PlacementCache.FixedFileName(groupIndex)))
                    {
                        Debug.LogWarning($"[TerrainBuilder] 定点组 {groupIndex} 缓存指纹不一致或格式无效，已重新计算（不写盘）。");
                    }
                }
                if (usedCache) continue;

                // 正常按配置实例化；编辑器模式同时收集缓存
                var cache = new PlacementCache.CacheData
                {
                    chunkSize = group.chunkSize,
                    visibleDistance = group.visibleDistance,
                    fingerprint = ComputeFixedFingerprint(groupIndex),
                };
                Quaternion rotation = Quaternion.Euler(0f, group.rotationDegrees, 0f);
                float scale = Mathf.Max(0f, group.scale);
                foreach (Vector2 uv in group.positions)
                {
                    Vector3 terrainPosition = _terrain.transform.position;
                    Vector3 terrainSize = _terrain.terrainData.size;
                    float worldX = terrainPosition.x + Mathf.Clamp01(uv.x) * terrainSize.x;
                    float worldZ = terrainPosition.z + Mathf.Clamp01(uv.y) * terrainSize.z;
                    var instance = Instantiate(group.prefab, _fixedRoot);
                    instance.transform.rotation = rotation;
                    instance.transform.localScale = Vector3.one * scale;
                    float worldY = SamplePlacementHeight(group.prefab, worldX, worldZ, rotation, scale);
                    instance.transform.position = new Vector3(worldX, worldY, worldZ);

                    if (!Application.isPlaying)
                        cache.fixedPoints.Add(new PlacementCache.FixedPlacement
                        {
                            prefabInstanceId = group.prefab.GetInstanceID(),
                            worldX = worldX,
                            worldZ = worldZ,
                            yaw = group.rotationDegrees,
                            scale = scale,
                        });
                }
                if (!Application.isPlaying)
                {
                    while (_fixedCacheGroups.Count <= groupIndex)
                        _fixedCacheGroups.Add(null);
                    _fixedCacheGroups[groupIndex] = cache;
                }
            }
        }

        /// <summary>用缓存条目直接实例化定点物体（按 instanceId 解析 prefab；任一解析失败返回 false 回退重算）。</summary>
        private bool InstantiateFixedFromCache(FixedPointConfigSO group, List<PlacementCache.FixedPlacement> cached)
        {
            for (int i = 0; i < cached.Count; i++)
            {
                PlacementCache.FixedPlacement entry = cached[i];
                if (entry.prefabInstanceId != group.prefab.GetInstanceID()) return false;
            }
            foreach (PlacementCache.FixedPlacement entry in cached)
            {
                Quaternion rotation = Quaternion.Euler(0f, entry.yaw, 0f);
                var instance = Instantiate(group.prefab, _fixedRoot);
                instance.transform.rotation = rotation;
                instance.transform.localScale = Vector3.one * Mathf.Max(0f, entry.scale);
                float worldY = SamplePlacementHeight(
                    group.prefab, entry.worldX, entry.worldZ, rotation, Mathf.Max(0f, entry.scale));
                instance.transform.position = new Vector3(entry.worldX, worldY, entry.worldZ);
            }
            return true;
        }

        private ObjectPool<GameObject> CreatePool(GameObject prefab, int key)
        {
            return new ObjectPool<GameObject>(
                createFunc: () =>
                {
                    var go = Instantiate(prefab, _poolRoot);
                    go.name = key.ToString(); // 物体名称 = 池 key（回收时解析）
                    go.hideFlags = HideFlags.HideInHierarchy;
                    if (!Application.isPlaying) go.hideFlags |= HideFlags.DontSave;
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

        /// <summary>实例化 Build 时已经为该区块确定的散布位置列表。</summary>
        private void GenerateChunk(ScatterRuntime runtime, Vector2Int idx)
        {
            // 层数据缺失时：仅当该区块没有已重建的缓存位置（缓存命中场景不依赖三图）才跳过
            if (!_dataReady && !runtime.placementsByChunk.ContainsKey(idx))
            {
                if (!_dataWarned)
                {
                    _dataWarned = true;
                    Debug.LogWarning("[TerrainBuilder] MapData（layerMap/road/offRoad）缺失，跳过散布生成。");
                }
                return;
            }

            var placed = new List<GameObject>();
            if (!runtime.placementsByChunk.TryGetValue(idx, out var placements))
            {
                runtime.objectsByChunk[idx] = placed;
                return;
            }
            foreach (ScatterRuntime.Placement placement in placements)
            {
                if (!runtime.pools.TryGetValue(placement.prefabKey, out var pool)) continue;
                GameObject prefab = runtime.config.prefabs[placement.prefabKey].prefab;
                var go = pool.Get();
                Quaternion rotation = Quaternion.Euler(0f, placement.yaw, 0f);
                go.transform.rotation = rotation;
                go.transform.localScale = Vector3.one * placement.scale;
                Vector3 position = PixelToWorld(placement.pixel.x, placement.pixel.y);
                position.y = SamplePlacementHeight(
                    prefab, position.x, position.z, rotation, placement.scale);
                go.transform.position = position;
                placed.Add(go);
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

        private void PrecomputeScatterPlacements(ScatterRuntime runtime)
        {
            runtime.placementsByChunk.Clear();
            if (!_dataReady || runtime.prefabKeys.Count == 0 || runtime.config.density <= 0f)
                return;
            Vector3 terrainSize = _terrain.terrainData.size;
            float chunkWidth = Mathf.Max(0.0001f, runtime.config.chunkSize.x);
            float chunkDepth = Mathf.Max(0.0001f, runtime.config.chunkSize.y);
            int countX = Mathf.CeilToInt(terrainSize.x / chunkWidth);
            int countZ = Mathf.CeilToInt(terrainSize.z / chunkDepth);
            for (int x = 0; x < countX; x++)
            for (int z = 0; z < countZ; z++)
            {
                var chunkIndex = new Vector2Int(x, z);
                // 无限可见距离（负数）时注册全部区块，首次 MoveTo 一次性激活
                runtime.chunks.RegisterChunk(chunkIndex);
                PrecomputeScatterChunk(runtime, chunkIndex);
            }
        }

        private void PrecomputeScatterChunk(ScatterRuntime runtime, Vector2Int idx)
        {
            runtime.chunks.GetChunkBounds(idx, out float xMin, out float zMin, out float xMax, out float zMax);
            Vector3 terrainSize = _terrain.terrainData.size;
            xMax = Mathf.Min(xMax, terrainSize.x);
            zMax = Mathf.Min(zMax, terrainSize.z);
            int mapH = _layerMapData.Length;
            int mapW = _layerMapData[0].Length;
            int pxMin = Mathf.Clamp(Mathf.CeilToInt(xMin / _wppX), 0, mapW - 1);
            int pxMax = xMax >= terrainSize.x
                ? mapW - 1
                : Mathf.Clamp(Mathf.CeilToInt(xMax / _wppX) - 1, 0, mapW - 1);
            int pzMin = Mathf.Clamp(Mathf.CeilToInt(zMin / _wppZ), 0, mapH - 1);
            int pzMax = zMax >= terrainSize.z
                ? mapH - 1
                : Mathf.Clamp(Mathf.CeilToInt(zMax / _wppZ) - 1, 0, mapH - 1);

            var layerPixels = new Dictionary<int, List<Vector2Int>>();
            for (int pz = pzMin; pz <= pzMax; pz++)
            for (int px = pxMin; px <= pxMax; px++)
            {
                int layerIndex = Mathf.Max(0, Mathf.RoundToInt(_layerMapData[pz][px]));
                if (layerIndex >= TerrainPaintProjectSO.MaxLayerCount) continue;
                var layerFlag = (TerrainWorkflowLayerMask)(1 << layerIndex);
                if ((runtime.config.targetLayers & layerFlag) == 0) continue;
                if (_roadData[pz][px] > 0.5f) continue;
                float offRoad = _offRoadData[pz][px];
                if (offRoad < runtime.config.offRoadDistanceRange.x ||
                    offRoad > runtime.config.offRoadDistanceRange.y) continue;
                if (!layerPixels.TryGetValue(layerIndex, out var pixels))
                {
                    pixels = new List<Vector2Int>();
                    layerPixels[layerIndex] = pixels;
                }
                pixels.Add(new Vector2Int(px, pz));
            }

            int chunkSeed = runtime.seed ^ (idx.x * 73856093) ^ (idx.y * 19349663);
            var rng = new System.Random(chunkSeed);
            float pixelArea = _wppX * _wppZ;
            var placements = new List<ScatterRuntime.Placement>();
            foreach (List<Vector2Int> pixels in layerPixels.Values)
            {
                int count = Mathf.Min(
                    pixels.Count,
                    Mathf.FloorToInt(pixels.Count * pixelArea * runtime.config.density));
                if (count <= 0) continue;
                Shuffle(pixels, rng);
                for (int i = 0; i < count; i++)
                {
                    int key = PickWeightedPrefab(runtime, rng);
                    if (key < 0) continue;
                    placements.Add(new ScatterRuntime.Placement
                    {
                        prefabKey = key,
                        pixel = pixels[i],
                        scale = Mathf.Lerp(
                            runtime.config.randomScale.x,
                            runtime.config.randomScale.y,
                            (float)rng.NextDouble()),
                        yaw = (float)rng.NextDouble() * 360f,
                    });
                }
            }
            runtime.placementsByChunk[idx] = placements;
        }

        private float SamplePlacementHeight(
            GameObject prefab,
            float worldX,
            float worldZ,
            Quaternion rotation,
            float scale)
        {
            PrefabStructureInfo info = prefab != null ? prefab.GetComponent<PrefabStructureInfo>() : null;
            if (info == null || !info.twoPointHeightAdaptation)
                return SampleTerrainWorldHeight(worldX, worldZ);

            Vector3 leftOffset = rotation * new Vector3(info.boundsX.x * scale, 0f, 0f);
            Vector3 rightOffset = rotation * new Vector3(info.boundsX.y * scale, 0f, 0f);
            float leftHeight = SampleTerrainWorldHeight(worldX + leftOffset.x, worldZ + leftOffset.z);
            float rightHeight = SampleTerrainWorldHeight(worldX + rightOffset.x, worldZ + rightOffset.z);
            return (leftHeight + rightHeight) * 0.5f;
        }

        private float SampleTerrainWorldHeight(float worldX, float worldZ)
        {
            Vector3 terrainPosition = _terrain.transform.position;
            Vector3 terrainSize = _terrain.terrainData.size;
            float x = Mathf.Clamp(worldX, terrainPosition.x, terrainPosition.x + terrainSize.x);
            float z = Mathf.Clamp(worldZ, terrainPosition.z, terrainPosition.z + terrainSize.z);
            return terrainPosition.y + _terrain.SampleHeight(new Vector3(x, terrainPosition.y, z));
        }

        private void EnsureRoadMapData(float[][] layerMap, int mapW, int mapH)
        {
            if (_config.FindDuplicateLayerIndices().Count > 0)
            {
                Debug.LogError("[TerrainBuilder] 邻接组存在重复 Layer，无法生成道路数据。");
                return;
            }
            int[] ids = FlattenLayerIds(layerMap, mapW, mapH);
            Vector3 terrainSize = _terrain.terrainData.size;
            var pixelWorldSize = new Vector2(
                terrainSize.x / Mathf.Max(1, mapW - 1),
                terrainSize.z / Mathf.Max(1, mapH - 1));
            Texture2D preview = TerrainRoadGen.ComputeAll(
                _config, ids, mapW, mapH, pixelWorldSize,
                out var distance, out var occupancy, out var road);
            if (preview == null) return;
            if (Application.isPlaying) Destroy(preview);
            else DestroyImmediate(preview);
            MapData.Set("distance", CsvArrayCodec.ToJagged(distance, mapW, mapH));
            MapData.Set("occupancy", CsvArrayCodec.ToJagged(occupancy, mapW, mapH));
            MapData.Set("road", CsvArrayCodec.ToJagged(road, mapW, mapH));
            MapData.Set("offRoad", CsvArrayCodec.ToJagged(
                TerrainRoadGen.ComputeOffRoad(ids, road, mapW, mapH, pixelWorldSize), mapW, mapH));
        }

        private List<TerrainLayer> BuildTerrainLayerUnion()
        {
            var result = new List<TerrainLayer>();
            foreach (TerrainLayer layer in _config.naturalTerrainLayers)
                if (layer != null && !result.Contains(layer)) result.Add(layer);
            foreach (TerrainLayer layer in _config.roadTerrainLayers)
                if (layer != null && !result.Contains(layer)) result.Add(layer);
            return result;
        }

        private static float[] BuildNoisyWeights(
            List<int> configuredWeights,
            List<TerrainLayer> sourceLayers,
            List<TerrainLayer> union,
            float worldX,
            float worldZ,
            int seed,
            float noiseScale)
        {
            var result = new float[union.Count];
            if (configuredWeights == null || sourceLayers == null) return result;
            int count = Mathf.Min(configuredWeights.Count, sourceLayers.Count);
            for (int i = 0; i < count; i++)
            {
                int baseWeight = configuredWeights[i];
                TerrainLayer terrainLayer = sourceLayers[i];
                if (baseWeight <= 0 || terrainLayer == null) continue;
                int target = union.IndexOf(terrainLayer);
                if (target < 0) continue;
                float noise = Mathf.PerlinNoise(
                    worldX / noiseScale + seed * 0.131f + i * 17.17f,
                    worldZ / noiseScale + seed * 0.293f + i * 31.31f);
                result[target] += baseWeight * Mathf.Lerp(0.5f, 1.5f, noise);
            }
            Normalize(result);
            return result;
        }

        private static bool HasPositiveWeight(float[] weights)
        {
            for (int i = 0; i < weights.Length; i++)
                if (weights[i] > 0f) return true;
            return false;
        }

        private static void Normalize(float[] values)
        {
            float sum = 0f;
            for (int i = 0; i < values.Length; i++) sum += values[i];
            if (sum <= 0.000001f) return;
            for (int i = 0; i < values.Length; i++) values[i] /= sum;
        }

        private static bool TryGetMapSize(float[][] map, out int width, out int height)
        {
            height = map != null ? map.Length : 0;
            width = height > 0 && map[0] != null ? map[0].Length : 0;
            return HasMapSize(map, width, height) && width > 0 && height > 0;
        }

        private static bool HasMapSize(float[][] map, int width, int height)
        {
            if (map == null || map.Length != height || width <= 0 || height <= 0) return false;
            for (int y = 0; y < height; y++)
                if (map[y] == null || map[y].Length != width) return false;
            return true;
        }

        private static int[] FlattenLayerIds(float[][] layerMap, int width, int height)
        {
            var result = new int[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                result[y * width + x] = Mathf.RoundToInt(layerMap[y][x]);
            return result;
        }

        private static float SampleNearest(float[][] map, float u, float v)
        {
            int height = map.Length;
            int width = map[0].Length;
            int x = Mathf.Clamp(Mathf.RoundToInt(u * (width - 1)), 0, width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(v * (height - 1)), 0, height - 1);
            return map[y][x];
        }

        private static float SampleBilinear(float[][] map, float u, float v)
        {
            int height = map.Length;
            int width = map[0].Length;
            float fx = Mathf.Clamp01(u) * (width - 1);
            float fy = Mathf.Clamp01(v) * (height - 1);
            int x0 = Mathf.FloorToInt(fx), x1 = Mathf.Min(x0 + 1, width - 1);
            int y0 = Mathf.FloorToInt(fy), y1 = Mathf.Min(y0 + 1, height - 1);
            float tx = fx - x0, ty = fy - y0;
            return Mathf.Lerp(
                Mathf.Lerp(map[y0][x0], map[y0][x1], tx),
                Mathf.Lerp(map[y1][x0], map[y1][x1], tx),
                ty);
        }

        private static void ClearGeneratedRoot(ref Transform root)
        {
            if (root == null) return;
            if (Application.isPlaying) Destroy(root.gameObject);
            else DestroyImmediate(root.gameObject);
            root = null;
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
