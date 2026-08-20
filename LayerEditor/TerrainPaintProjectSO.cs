#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
    /// 内含：本总 SO + 16 个层级 SO（layers）+ 层次图（layerMap）。
    ///
    /// 编辑器窗口的所有信息都从本 SO 加载（窗口本身不存储持久数据）。
    /// TerrainPaintConfig（全局参数）是本 SO 的一部分；层级配置在各自的 LayerConfigSO 中。
    ///
    /// TerrainLayer 池与贴图种子均在本 SO 中统一管理：
    ///   - naturalTerrainLayers / roadTerrainLayers：两组独立的 TerrainLayer 池
    ///   - naturalSeed / roadSeed：全局 value-noise 种子（不细化到层级）
    /// 每个工作流层（LayerConfigSO）持有两个 int 列表（自然/道路），
    /// 列表索引 = 对应池的 TerrainLayer id，值 = 该 TL 在贴图混合中的权重（0 = 不纳入）。
    /// </summary>
    [CreateAssetMenu(fileName = "TerrainPaintProject", menuName = "AiTerrainWorkflow/Layer Editor/Terrain Paint Project")]
    public class TerrainPaintProjectSO : ScriptableObject
    {
        [Tooltip("全局配置（随机游走 / 贴图混合 / 坐标换算）")]
        public TerrainPaintConfig config = new TerrainPaintConfig();

        [Tooltip("全部层级配置（固定 16 个）")]
        public List<LayerConfigSO> layers = new List<LayerConfigSO>();

        [Tooltip("层次图（配置文件夹内资产；绘画子界面的画布）")]
        public Texture2D layerMap;

        // ---------- 自然贴图 TerrainLayer 池 ----------

        [Header("自然贴图 TerrainLayer 池")]
        [Tooltip("用于自然地面的 TerrainLayer 列表。各层级 LayerConfigSO.naturalLayerWeights 的索引对应本池。")]
        public List<TerrainLayer> naturalTerrainLayers = new List<TerrainLayer>();

        // ---------- 道路贴图 TerrainLayer 池 ----------

        [Header("道路贴图 TerrainLayer 池")]
        [Tooltip("用于道路的 TerrainLayer 列表。各层级 LayerConfigSO.roadLayerWeights 的索引对应本池。")]
        public List<TerrainLayer> roadTerrainLayers = new List<TerrainLayer>();

        // ---------- 全局贴图种子 ----------

        [Header("全局贴图种子")]
        [Tooltip("自然贴图 value-noise 种子（全局，不细化到层级）")]
        public int naturalSeed = 0;
        [Tooltip("道路贴图 value-noise 种子（全局，不细化到层级）")]
        public int roadSeed = 0;

        // ---------- 计算结果 ----------

        [HideInInspector, Tooltip("各组合层级的距离场全局最大值（自动计算）")]
        public float[] groupMaxD;

        [Tooltip("计算结果图（RGB：R=距离场，G=占用/间隔，B=路面掩码）")]
        public Texture2D resultTexture;

        // ---------- 辅助方法 ----------

        /// <summary>
        /// 同步所有层级 SO 的自然/道路权重列表长度，使其与两个池对齐。
        /// 添加 / 删除 / 重排 TerrainLayer 池后调用（截断或补零）。
        /// </summary>
        public void SyncAllLayerWeights()
        {
            int natCount = naturalTerrainLayers.Count;
            int roadCount = roadTerrainLayers.Count;
            foreach (var layer in layers)
            {
                if (layer == null) continue;
                layer.SyncWeightLists(natCount, roadCount);
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
    }
}
#endif
