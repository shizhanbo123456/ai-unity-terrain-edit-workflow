using System;
using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    public enum PropArrangementBasis
    {
        Distance,
        OffRoad,
        Height,
    }

    public enum PropRotationMode
    {
        TowardHighGradient,
        TowardLowGradient,
        AlongContour,
        Random,
    }

    public enum PropDistributionMode
    {
        Scatter,
        Cluster,
        Extend,
    }

    [Serializable]
    public class PropPrefabEntry
    {
        public GameObject prefab;
        [Min(0)] public int weight = 1;
        [Min(0)] public int minimumCount;
    }

    /// <summary>一组规则化摆件生成配置；实际放置由 TerrainBuilder 后续实现。</summary>
    public class PropConfigSO : ScriptableObject
    {
        public string groupName = "摆件生成组";

        [Min(0)] public int maxFailedAttempts = 20;
        [Min(0f)] public float expectedDensity = 0.01f;

        [Tooltip("单次尝试至少保留数量 x / 尝试生成数量 y")]
        public Vector2Int batchSize = new Vector2Int(1, 1);

        public TerrainWorkflowLayerMask targetLayers =
            TerrainWorkflowLayerMask.All & ~TerrainWorkflowLayerMask.Layer0;

        [Range(0f, 1f)] public float outOfBoundsTolerance = 1f;
        public PropArrangementBasis arrangementBasis = PropArrangementBasis.OffRoad;
        public Vector2 arrangementRange = new Vector2(0f, 10000f);
        public PropRotationMode rotationMode = PropRotationMode.Random;
        public PropDistributionMode distributionMode = PropDistributionMode.Scatter;

        [Tooltip("Distance - R1 - R2 必须大于该值；负数允许同批物体重叠")]
        public float distributionSpacing;

        public List<PropPrefabEntry> prefabs = new List<PropPrefabEntry>();
    }
}
