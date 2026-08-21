using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 全局配置（TerrainPaintProjectSO 的一部分，非独立资产）。
    /// 参数语义见设计文档《混合距离场与路面生成工具_设计文档(2).md》。
    /// </summary>
    [Serializable]
    public class TerrainPaintConfig
    {
        [Header("随机游走")]
        [Tooltip("相邻游走点的最小距离（世界距离）；也是候选采样邻域半径")]
        public float roadStep = 2f;
        [Tooltip("完全随机找起点的尝试次数上限")]
        public int walkStartTries = 10;
        [Tooltip("游走时每个当前点周围取多少个候选点")]
        public int walkCandidateCount = 8;
        [Tooltip("选起点时周围取多少个候选点用于「覆盖停止」判断")]
        public int startCoverStopSamples = 8;
        [Tooltip("随机游走统一种子（与贴图种子完全独立）")]
        public int walkSeed = 0;
        [Tooltip("单条路径步数硬上限（安全护栏）")]
        public int maxStepsPerPath = 256;
        [Tooltip("防卷曲距离 = G 应用间距阈值（世界距离），默认 3m")]
        public float gApplySpacing = 3f;

        [Header("贴图混合")]
        [Tooltip("value-noise 加权混合的空间频率（世界距离）")]
        public float noiseScale = 1f;

        [Header("坐标换算")]
        [Tooltip("像素 ↔ 世界换算（= Terrain 世界尺寸 / 图片分辨率）；世界距离参数（roadStep/roadWidth 等）除以它转成像素")]
        public float worldPerPixel = 0.4f;
    }

    /// <summary>
    /// 地形贴图工作流的总配置 SO。一个配置 = TerrainGeneratorConfigs 下的一个子文件夹，
    /// 内含：本总 SO + 若干层级 SO（layers，数量 2~16）+ 层次图（layerMap）。
    ///
    /// 编辑器窗口的所有信息都从本 SO 加载（窗口本身不存储持久数据）。
    /// TerrainPaintConfig（全局参数）是本 SO 的一部分；层级配置在各自的 LayerConfigSO 中。
    ///
    /// 字段按四个子界面用 Header 划分专属配置：
    ///   - 区域编辑：层次图（layerMap，绘画画布）
    ///   - 高度编辑：heightSeed / heightScale / smoothStep / smoothIterations
    ///   - 贴图编辑：TerrainPaintConfig（随机游走/贴图混合/坐标换算）+ 全局贴图种子 + 两个 TerrainLayer 池 + 邻接组
    ///   - 树木编辑 / 细节编辑：Prefab 池（treePrefabs / detailPrefabs）+ 全局种子（treeSeed / detailSeed）+ 区块参数（treeChunkSize / treeVisibleDistance / detailChunkSize / detailVisibleDistance）
    /// </summary>
    [CreateAssetMenu(fileName = "TerrainPaintProject", menuName = "AiTerrainWorkflow/Layer Editor/Terrain Paint Project")]
    public class TerrainPaintProjectSO : ScriptableObject
    {
        /// <summary>Layer 数量下限（含 Layer0 透明层）。</summary>
        public const int MinLayerCount = 2;
        /// <summary>Layer 数量上限（含 Layer0 透明层）。</summary>
        public const int MaxLayerCount = 16;

        [Tooltip("全部层级配置（Layer0 为完全透明过渡层，其余为可编辑颜色/名称的层级；数量 2~16）")]
        public List<LayerConfigSO> layers = new List<LayerConfigSO>();

        // ---------- 区域编辑 ----------

        [Header("区域编辑")]
        [Tooltip("层次图（配置文件夹内资产；区域编辑子界面的画布）")]
        public Texture2D layerMap;

        // ---------- 贴图编辑 ----------

        [Header("贴图编辑")]
        [Tooltip("全局参数（随机游走 / 贴图混合 / 坐标换算）")]
        public TerrainPaintConfig config = new TerrainPaintConfig();

        [Tooltip("自然贴图 value-noise 种子（全局，不细化到层级）")]
        public int naturalSeed = 0;
        [Tooltip("道路贴图 value-noise 种子（全局，不细化到层级）")]
        public int roadSeed = 0;

        [Tooltip("用于自然地面的 TerrainLayer 列表。各层级 LayerConfigSO.naturalLayerWeights 的索引对应本池。")]
        public List<TerrainLayer> naturalTerrainLayers = new List<TerrainLayer>();

        [Tooltip("用于道路的 TerrainLayer 列表。各层级 LayerConfigSO.roadLayerWeights 的索引对应本池。")]
        public List<TerrainLayer> roadTerrainLayers = new List<TerrainLayer>();

        [Tooltip("邻接组（组合层级分组）：每个组是一个层级索引列表，如 {{1,2,3},{4,5}}。同一层级不可出现在多个组中（会校验报错）。")]
        public List<List<int>> adjacencyGroups = new List<List<int>>();

        // ---------- 高度编辑 ----------

        [Header("高度编辑")]
        [Tooltip("烘焙高度图用的噪声种子")]
        public int heightSeed = 0;
        [Tooltip("烘焙高度图用的噪声空间频率（越大噪声变化越快）")]
        public float heightScale = 1f;

        /// <summary>
        /// 高度图平滑步长（像素）：十字线均值滤波的采样间距。
        /// 生成高度图后，对其中每一个点 (x,y)：
        /// 取 (x,y)、(x+k*step,y)、(x-k*step,y)、(x,y+k*step)、(x,y-k*step)，
        /// 其中 k 为 &gt;0 且 &lt;= 平滑迭代的取值，共 1+4*平滑迭代 个点；
        /// 去除超出边界的点后，采样剩余点的高度并取平均值，作为 (x,y) 的最终高度。
        /// 暂未参与代码运算，仅记录参数语义。
        /// </summary>
        [Tooltip("高度图平滑步长（像素）：十字线均值滤波的采样间距（暂未参与运算）")]
        public int smoothStep = 1;

        /// <summary>
        /// 高度图平滑迭代次数：决定十字线均值滤波的采样半径（k 取 1..平滑迭代）。
        /// 采样点数为 1+4*平滑迭代；为 0 时仅取中心点自身，即不平滑。
        /// 暂未参与代码运算，仅记录参数语义。
        /// </summary>
        [Tooltip("高度图平滑迭代：十字线均值滤波采样半径，采样点数=1+4*迭代（暂未参与运算）")]
        public int smoothIterations = 0;

        // ---------- 树木编辑 ----------

        [Header("树木编辑")]
        [Tooltip("树木位置生成用的全局种子（TreeSeed，不细化到层级）")]
        public int treeSeed = 0;
        [Tooltip("树木区块尺寸（米，x/z）：构建时按区块划分生成树木的粒度")]
        public Vector2 treeChunkSize = new Vector2(16f, 16f);
        [Tooltip("树木可见距离（米）：区块中心距观察点（TerrainBuilder.SetCameraPosition）超过该值则回收隐藏")]
        public float treeVisibleDistance = 60f;
        [Tooltip("树木/植被 Prefab 池（树木编辑 · 全局配置中的物理列表）")]
        public List<GameObject> treePrefabs = new List<GameObject>();

        // ---------- 细节编辑 ----------

        [Header("细节编辑")]
        [Tooltip("细节位置生成用的全局种子（DetailSeed，不细化到层级）")]
        public int detailSeed = 0;
        [Tooltip("细节区块尺寸（米，x/z）：构建时按区块划分生成细节的粒度")]
        public Vector2 detailChunkSize = new Vector2(8f, 8f);
        [Tooltip("细节可见距离（米）：区块中心距观察点（TerrainBuilder.SetCameraPosition）超过该值则回收隐藏")]
        public float detailVisibleDistance = 40f;
        [Tooltip("细节网格/草 Prefab 池（细节编辑 · 全局配置中的物理列表）")]
        public List<GameObject> detailPrefabs = new List<GameObject>();

        // ---------- MapData 栅格数据（存储层） ----------

        /// <summary>MapData 子目录名（位于配置文件夹下）。</summary>
        public const string MapDataFolderName = "MapData";

        /// <summary>允许的栅格分辨率（创建配置时单选）。</summary>
        public static readonly int[] AllowedResolutions = { 128, 256, 512, 1024 };

        [Header("MapData 栅格数据")]
        [Tooltip("所有栅格化数据的尺寸（创建配置时单选 128/256/512/1024；layerMap/height/distance/occupancy/road 等均为此尺寸）")]
        public int mapResolution = 512;

        [Tooltip("本配置 MapData 目录下的 txt 文件引用（key + TextAsset）。编辑器写入后维护；运行时经它读取 float[][]。")]
        public List<MapDataRef> mapDataFiles = new List<MapDataRef>();

        /// <summary>MapData 文件引用项。</summary>
        [Serializable]
        public class MapDataRef
        {
            public string key;
            public TextAsset file;
        }

        // ---------- 辅助方法 ----------

        /// <summary>
        /// 同步所有层级 SO 的自然/道路/树木/细节权重列表长度，使其与四个池对齐。
        /// 添加 / 删除 / 重排池元素后调用（截断或补零）。
        /// </summary>
        public void SyncAllLayerWeights()
        {
            int natCount = naturalTerrainLayers.Count;
            int roadCount = roadTerrainLayers.Count;
            int treeCount = treePrefabs.Count;
            int detailCount = detailPrefabs.Count;
            foreach (var layer in layers)
            {
                if (layer == null) continue;
                layer.SyncWeightLists(natCount, roadCount, treeCount, detailCount);
            }
        }

        /// <summary>获取指定工作流层启用的自然 TerrainLayer 池索引（权重 &gt; 0）。</summary>
        public List<int> GetNaturalIndicesForLayer(int layerIndex)
        {
            var result = new List<int>();
            if (layerIndex < 0 || layerIndex >= layers.Count || layers[layerIndex] == null)
                return result;
            var weights = layers[layerIndex].naturalLayerWeights;
            for (int i = 0; i < weights.Count; i++)
                if (weights[i] > 0) result.Add(i);
            return result;
        }

        /// <summary>获取指定工作流层启用的道路 TerrainLayer 池索引（权重 &gt; 0）。</summary>
        public List<int> GetRoadIndicesForLayer(int layerIndex)
        {
            var result = new List<int>();
            if (layerIndex < 0 || layerIndex >= layers.Count || layers[layerIndex] == null)
                return result;
            var weights = layers[layerIndex].roadLayerWeights;
            for (int i = 0; i < weights.Count; i++)
                if (weights[i] > 0) result.Add(i);
            return result;
        }

        /// <summary>
        /// 检测被加入多个邻接组的层级索引（出现于 ≥2 个组）。返回值升序；空列表 = 无冲突。
        /// </summary>
        public List<int> FindDuplicateLayerIndices()
        {
            var count = new Dictionary<int, int>();
            foreach (var group in adjacencyGroups)
            {
                if (group == null) continue;
                foreach (var idx in group)
                {
                    if (idx < 0) continue;
                    count.TryGetValue(idx, out int c);
                    count[idx] = c + 1;
                }
            }
            var dup = new List<int>();
            foreach (var kv in count)
                if (kv.Value > 1) dup.Add(kv.Key);
            dup.Sort();
            return dup;
        }

        /// <summary>是否存在层级被加入多个邻接组。</summary>
        public bool HasAdjacencyConflict => FindDuplicateLayerIndices().Count > 0;

        // ---------- MapData 接口（ReadMap/WriteMap/DeleteMap/HasMap） ----------

        /// <summary>MapData 目录（Assets 相对路径，如 Assets/.../TerrainGeneratorConfigs/&lt;配置&gt;/MapData）；非编辑器环境返回 null。</summary>
        public string MapDataDirRelative()
        {
#if UNITY_EDITOR
            string soPath = UnityEditor.AssetDatabase.GetAssetPath(this);
            if (string.IsNullOrEmpty(soPath)) return null;
            string dir = Path.GetDirectoryName(soPath)?.Replace('\\', '/');
            return string.IsNullOrEmpty(dir) ? null : dir + "/" + MapDataFolderName;
#else
            return null;
#endif
        }

        /// <summary>MapData 目录（绝对路径）；非编辑器环境返回 null。</summary>
        public string MapDataDirAbsolute()
        {
            string rel = MapDataDirRelative();
            return string.IsNullOrEmpty(rel) ? null : Path.Combine(Application.dataPath, "..", rel);
        }

        /// <summary>指定 key 的 txt 文件路径（Assets 相对路径）；非编辑器环境返回 null。</summary>
        public string GetMapFilePath(string key)
        {
            string rel = MapDataDirRelative();
            return string.IsNullOrEmpty(rel) ? null : rel + "/" + MapDataStore.SanitizeKey(key) + ".txt";
        }

        private MapDataRef GetMapDataRef(string key)
        {
            foreach (var e in mapDataFiles)
                if (e != null && e.key == key) return e;
            return null;
        }

        /// <summary>
        /// 读取栅格数据（float[][]）。
        /// 运行时：从 SO 持有的 TextAsset（随构建打包）解码；
        /// 编辑器：直接读磁盘文件（保证每笔写盘后都是最新内容）。
        /// 不存在返回 null。
        /// </summary>
        public float[][] ReadMap(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            // 运行时优先 TextAsset（打包可读）
            var entry = GetMapDataRef(key);
            if (entry != null && entry.file != null)
                return CsvArrayCodec.Decode(entry.file.text);

#if UNITY_EDITOR
            string dir = MapDataDirAbsolute();
            if (!string.IsNullOrEmpty(dir))
            {
                var store = new MapDataStore(dir);
                if (store.Exists(key))
                    return store.Read(key);
            }
#endif
            return null;
        }

        public bool HasMap(string key)
        {
            if (GetMapDataRef(key) != null) return true;
#if UNITY_EDITOR
            string dir = MapDataDirAbsolute();
            if (!string.IsNullOrEmpty(dir))
                return new MapDataStore(dir).Exists(key);
#endif
            return false;
        }

        /// <summary>写入栅格数据（float[][] → CSV txt）。仅编辑器使用；不触发资产刷新（由 RefreshMapDataRefs 统一做）。</summary>
        public void WriteMap(string key, float[][] values)
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(key) || values == null) return;
            string dir = MapDataDirAbsolute();
            if (string.IsNullOrEmpty(dir)) return;
            new MapDataStore(dir).Write(key, values);

            // 引用维护：确保列表中存在该 key 项（file 暂为空，刷新后重链）
            var entry = GetMapDataRef(key);
            if (entry == null)
                mapDataFiles.Add(new MapDataRef { key = key });
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>删除栅格数据（文件 + 引用）。仅编辑器使用。</summary>
        public void DeleteMap(string key)
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(key)) return;
            string dir = MapDataDirAbsolute();
            if (!string.IsNullOrEmpty(dir))
                new MapDataStore(dir).Delete(key);
            mapDataFiles.RemoveAll(e => e != null && e.key == key);
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>
        /// 同步 MapData txt 引用：刷新资产后按 key 重链 TextAsset，移除已不存在的文件引用。
        /// 建议在「保存/关窗/切配置」等提交点调用；每笔写盘不必调用（避免频繁资产刷新卡编辑器）。
        /// </summary>
        public void RefreshMapDataRefs(bool refreshAssets = true)
        {
#if UNITY_EDITOR
            string dirRel = MapDataDirRelative();
            if (string.IsNullOrEmpty(dirRel)) return;

            if (refreshAssets)
                UnityEditor.AssetDatabase.Refresh();

            var keys = new List<string>();
            foreach (var e in mapDataFiles)
                if (e != null && !string.IsNullOrEmpty(e.key)) keys.Add(e.key);

            foreach (var key in keys)
            {
                string p = dirRel + "/" + MapDataStore.SanitizeKey(key) + ".txt";
                var ta = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(p);
                var entry = GetMapDataRef(key);
                if (entry == null)
                {
                    entry = new MapDataRef { key = key };
                    mapDataFiles.Add(entry);
                }
                entry.file = ta;
            }

            // 移除已无文件的引用
            string dirFull = MapDataDirAbsolute();
            if (!string.IsNullOrEmpty(dirFull))
            {
                mapDataFiles.RemoveAll(e =>
                {
                    if (e == null || string.IsNullOrEmpty(e.key)) return true;
                    return !File.Exists(Path.Combine(dirFull, MapDataStore.SanitizeKey(e.key) + ".txt"));
                });
            }
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
    }
}
