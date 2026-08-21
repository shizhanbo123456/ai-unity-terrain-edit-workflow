using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 构建端组件：接收主配置 SO，将一个真实 Terrain 构建为工作流编辑器中的样子
    /// （高度 / 纹理 / 植被 / 树 + 实例化摆件，详见 README「阶段 7 · TerrainBuilder 构建」）。
    ///
    /// 当前已实现：树木 / 细节的**按区块动态生成与回收**——Build 时初始化两个区块管理器
    /// （<see cref="ChunkUpdateManager"/>，参数从主配置读取）与对象池（key 自增）；
    /// 由 <see cref="SetCameraPosition"/> 驱动：区块中心进入可见距离则按生成参数生成并填充，
    /// 超出则回收物体到对象池。其余构建步骤（高度 / 纹理 / 摆件）后续实现。
    ///
    /// 对象池约定：
    ///   - 每个树 / 细节原型分配唯一自增 key（= prefab 池索引），物体 name = key.ToString()，
    ///     回收时按物体名称解析出池 key；
    ///   - 全部实例挂在隐藏容器（HideFlags.HideInHierarchy）下，**Hierarchy 面板不显示**；
    ///   - 取出 / 新建时 SetActive(true)，放回池时 SetActive(false)。
    /// 高度以 Terrain 组件（SampleHeight）为准，不依赖高度 MapData 是否已生成 / 已应用。
    /// </summary>
    public class TerrainBuilder : MonoBehaviour
    {
        private TerrainPaintProjectSO _config;
        private Terrain _terrain;

        // 区块管理器（树木 / 细节）
        private ChunkUpdateManager _treeChunks;
        private ChunkUpdateManager _detailChunks;

        // 对象池（key 自增；物体 name = key）
        private readonly Dictionary<int, ObjectPool<GameObject>> _treePools = new Dictionary<int, ObjectPool<GameObject>>();
        private readonly Dictionary<int, ObjectPool<GameObject>> _detailPools = new Dictionary<int, ObjectPool<GameObject>>();
        private Transform _poolRoot;

        // 区块 → 活跃物体列表（回收时按区块整体 Release）
        private readonly Dictionary<Vector2Int, List<GameObject>> _treeObjectsByChunk = new Dictionary<Vector2Int, List<GameObject>>();
        private readonly Dictionary<Vector2Int, List<GameObject>> _detailObjectsByChunk = new Dictionary<Vector2Int, List<GameObject>>();

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
        /// 构建：缓存 MapData、按主配置初始化树木 / 细节区块管理器与对象池。
        /// 其余构建步骤（PrepareTerrain / ApplyHeight / ApplyAlphamap / PlaceProps / PostProcess）后续实现。
        /// 注意：当前实现假定单次 Build；重复 Build 需先自行回收全部已生成物体。
        /// </summary>
        public void Build(TerrainPaintProjectSO projectConfig, Terrain terrain)
        {
            if (projectConfig == null || terrain == null)
            {
                Debug.LogError("[TerrainBuilder] Build 失败：projectConfig / terrain 为空。");
                return;
            }

            _config = projectConfig;
            _terrain = terrain;

            // 缓存 MapData（层 ID / 道路 / 离路距离）
            _layerMapData = projectConfig.ReadMap("layerMap");
            _roadData = projectConfig.ReadMap("road");
            _offRoadData = projectConfig.ReadMap("offRoad");
            _dataReady = _layerMapData != null && _roadData != null && _offRoadData != null;
            _dataWarned = false;

            int res = Mathf.Max(1, projectConfig.mapResolution);
            _wppX = terrain.terrainData.size.x / res;
            _wppZ = terrain.terrainData.size.z / res;

            // 区块管理器：参数从主配置读取
            _treeChunks = new ChunkUpdateManager(projectConfig.treeChunkSize, projectConfig.treeVisibleDistance);
            _detailChunks = new ChunkUpdateManager(projectConfig.detailChunkSize, projectConfig.detailVisibleDistance);

            // 隐藏容器：Hierarchy 面板不显示（子树全部不显示）
            if (_poolRoot == null)
            {
                var go = new GameObject("_TerrainBuilderPools");
                go.hideFlags = HideFlags.HideInHierarchy;
                _poolRoot = go.transform;
            }

            // 对象池：遍历 prefab 池分配自增 key（key = prefab 池索引）
            _treePools.Clear();
            for (int i = 0; i < projectConfig.treePrefabs.Count; i++)
                if (projectConfig.treePrefabs[i] != null)
                    _treePools[i] = CreatePool(projectConfig.treePrefabs[i], i);

            _detailPools.Clear();
            for (int i = 0; i < projectConfig.detailPrefabs.Count; i++)
                if (projectConfig.detailPrefabs[i] != null)
                    _detailPools[i] = CreatePool(projectConfig.detailPrefabs[i], i);
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
        /// 观察点移动到 pos（Vector2(x, z)）：按树木 / 细节各自的可见距离，
        /// 激活新进入范围的区块（生成物体）、回收离开范围的区块（物体放回对象池）。
        /// </summary>
        public void SetCameraPosition(Vector2 pos)
        {
            if (_treeChunks == null || _detailChunks == null || _config == null || _terrain == null)
                return;

            UpdateChunks(_treeChunks, _treePools, _treeObjectsByChunk, _config.treeSeed, true, pos);
            UpdateChunks(_detailChunks, _detailPools, _detailObjectsByChunk, _config.detailSeed, false, pos);
        }

        private void UpdateChunks(ChunkUpdateManager mgr, Dictionary<int, ObjectPool<GameObject>> pools,
            Dictionary<Vector2Int, List<GameObject>> byChunk, int baseSeed, bool isTree, Vector2 pos)
        {
            mgr.MoveTo(pos, out var active, out var inactive);

            // 1) 回收离开范围的区块：按物体名称解析池 key → Release
            foreach (var idx in inactive)
            {
                if (!byChunk.TryGetValue(idx, out var list)) continue;
                foreach (var go in list)
                {
                    if (go == null) continue;
                    if (int.TryParse(go.name, out int key) && pools.TryGetValue(key, out var pool))
                        pool.Release(go);
                }
                list.Clear();
                byChunk.Remove(idx);
            }

            // 2) 生成新进入范围的区块（已激活的跳过，不重复生成）
            foreach (var idx in active)
            {
                if (!byChunk.ContainsKey(idx))
                    GenerateChunk(mgr, idx, pools, byChunk, baseSeed, isTree);
            }
        }

        /// <summary>
        /// 生成单个区块内的树木 / 细节：
        /// 世界范围 → 像素范围 → 收集合法像素（语义层 lid≥1、非道路、offRoad ≥ 对应离路限制）
        /// → 区块 seed = 全局 seed ⊕ 区块 index → 按层密度洗牌取位 → 按权重选原型 → 从对象池取出并摆放
        /// （高度 = Terrain.SampleHeight，位置 = 像素中心 × 实际 worldPerPixel + Terrain 原点）。
        /// </summary>
        private void GenerateChunk(ChunkUpdateManager mgr, Vector2Int idx,
            Dictionary<int, ObjectPool<GameObject>> pools,
            Dictionary<Vector2Int, List<GameObject>> byChunk, int baseSeed, bool isTree)
        {
            if (!_dataReady)
            {
                if (!_dataWarned)
                {
                    _dataWarned = true;
                    Debug.LogWarning("[TerrainBuilder] MapData（layerMap/road/offRoad）缺失，跳过树木/细节生成。请先在工作流窗口烘焙贴图。");
                }
                return;
            }

            mgr.GetChunkBounds(idx, out float xMin, out float yMin, out float xMax, out float yMax);
            Vector3 tPos = _terrain.transform.position;
            int mapH = _layerMapData.Length;
            int mapW = mapH > 0 ? _layerMapData[0].Length : 0;
            if (mapW <= 0) return;

            // 世界范围 → 像素范围（clamp 到 map 内）
            int pxMin = Mathf.Clamp(Mathf.FloorToInt((xMin - tPos.x) / _wppX), 0, mapW - 1);
            int pxMax = Mathf.Clamp(Mathf.CeilToInt((xMax - tPos.x) / _wppX) - 1, 0, mapW - 1);
            int pzMin = Mathf.Clamp(Mathf.FloorToInt((yMin - tPos.z) / _wppZ), 0, mapH - 1);
            int pzMax = Mathf.Clamp(Mathf.CeilToInt((yMax - tPos.z) / _wppZ) - 1, 0, mapH - 1);

            // 收集合法像素：语义层（lid≥1）+ 非道路 + 离路 ≥ 对应 limit
            var layerPixels = new Dictionary<int, List<Vector2Int>>();
            for (int pz = pzMin; pz <= pzMax; pz++)
            {
                var row = _layerMapData[pz];
                for (int px = pxMin; px <= pxMax; px++)
                {
                    int lid = Mathf.RoundToInt(row[px]);
                    if (lid < 1 || lid >= _config.layers.Count || _config.layers[lid] == null)
                        continue;
                    if (_roadData[pz][px] > 0.5f)
                        continue;
                    var layer = _config.layers[lid];
                    float limit = isTree ? layer.treeRoadDistanceLimit : layer.detailRoadDistanceLimit;
                    if (limit > 0f && _offRoadData[pz][px] < limit)
                        continue;
                    if (!layerPixels.TryGetValue(lid, out var list))
                    {
                        list = new List<Vector2Int>();
                        layerPixels[lid] = list;
                    }
                    list.Add(new Vector2Int(px, pz));
                }
            }

            // 区块 seed：全局 seed ⊕ 区块 index（不同区块不重复、可复现）
            int chunkSeed = baseSeed ^ (idx.x * 73856093) ^ (idx.y * 19349663);
            var rng = new System.Random(chunkSeed);
            float pixelArea = _wppX * _wppZ;

            var placed = new List<GameObject>();
            foreach (var kv in layerPixels)
            {
                var layer = _config.layers[kv.Key];
                var pixels = kv.Value;
                float density = isTree ? layer.treeDensity : layer.detailDensity;
                if (density <= 0f) continue;
                int count = Mathf.FloorToInt(pixels.Count * pixelArea * density);
                if (count <= 0) continue;

                Shuffle(pixels, rng); // 洗牌取前 count 个：均匀、无重复
                int take = Mathf.Min(count, pixels.Count);
                var weights = isTree ? layer.treeWeights : layer.detailWeights;
                var scaleRange = isTree ? layer.treeScale : layer.detailScale;

                for (int i = 0; i < take; i++)
                {
                    var pxp = pixels[i];
                    int key = PickWeighted(rng, weights);
                    if (key < 0 || !pools.TryGetValue(key, out var pool))
                        continue;
                    var go = pool.Get();
                    float s = Mathf.Lerp(scaleRange.x, scaleRange.y, (float)rng.NextDouble());
                    go.transform.position = PixelToWorld(pxp.x, pxp.y);
                    go.transform.localScale = Vector3.one * s;
                    placed.Add(go);
                }
            }

            if (placed.Count > 0)
                byChunk[idx] = placed;
        }

        /// <summary>像素中心 → 世界坐标；Y = Terrain.SampleHeight（以 Terrain 组件高度为准）。</summary>
        private Vector3 PixelToWorld(int px, int pz)
        {
            Vector3 tPos = _terrain.transform.position;
            float wx = tPos.x + (px + 0.5f) * _wppX;
            float wz = tPos.z + (pz + 0.5f) * _wppZ;
            float wy = _terrain.SampleHeight(new Vector3(wx, 0f, wz));
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

        /// <summary>按权重随机选原型池 key（= prefab 池索引）；权重全 0 返回 -1。</summary>
        private static int PickWeighted(System.Random rng, List<int> weights)
        {
            if (weights == null) return -1;
            int total = 0;
            for (int i = 0; i < weights.Count; i++)
                if (weights[i] > 0) total += weights[i];
            if (total <= 0) return -1;
            int r = rng.Next(total);
            for (int i = 0; i < weights.Count; i++)
            {
                if (weights[i] <= 0) continue;
                if (r < weights[i]) return i;
                r -= weights[i];
            }
            return -1;
        }
    }
}
