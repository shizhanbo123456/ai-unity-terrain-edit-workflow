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
    /// layer × TerrainLayer 矩阵的一行（对应一个层级）。
    /// natural / road 长度须与 TerrainPaintProjectSO.terrainLayers 一致。
    /// </summary>
    [Serializable]
    public class LayerTerrainUsage
    {
        [Tooltip("该 TerrainLayer 是否用于自然地面")]
        public List<bool> natural = new List<bool>();
        [Tooltip("该 TerrainLayer 是否用于道路")]
        public List<bool> road = new List<bool>();
    }

    /// <summary>
    /// 地形贴图工作流的总配置 SO。一个配置 = TerrainGeneratorConfigs 下的一个子文件夹，
    /// 内含：本总 SO + 16 个层级 SO（layers）+ 层次图（layerMap）。
    ///
    /// 编辑器窗口的所有信息都从本 SO 加载（窗口本身不存储持久数据）。
    /// TerrainPaintConfig（全局配置）是本 SO 的一部分；层级配置在各自的 LayerConfigSO 中。
    /// </summary>
    [CreateAssetMenu(fileName = "TerrainPaintProject", menuName = "AiTerrainWorkflow/Layer Editor/Terrain Paint Project")]
    public class TerrainPaintProjectSO : ScriptableObject
    {
        [Tooltip("全局配置（随机游走 / 贴图混合）")]
        public TerrainPaintConfig config = new TerrainPaintConfig();

        [Tooltip("全部层级配置（固定 16 个）")]
        public List<LayerConfigSO> layers = new List<LayerConfigSO>();

        [Tooltip("层次图（配置文件夹内资产；绘画子界面的画布）")]
        public Texture2D layerMap;

        [Tooltip("本配置用到的 TerrainLayer 列表（贴图矩阵的列）")]
        public List<TerrainLayer> terrainLayers = new List<TerrainLayer>();

        [Tooltip("layer × TerrainLayer 矩阵：行 = layers，列 = terrainLayers；每格两个复选框（自然/道路）")]
        public List<LayerTerrainUsage> usageMatrix = new List<LayerTerrainUsage>();

        [HideInInspector, Tooltip("各组合层级的距离场全局最大值（自动计算）")]
        public float[] groupMaxD;

        [Tooltip("计算结果图（RGB：R=距离场，G=占用/间隔，B=路面掩码）")]
        public Texture2D resultTexture;
    }
}
#endif
