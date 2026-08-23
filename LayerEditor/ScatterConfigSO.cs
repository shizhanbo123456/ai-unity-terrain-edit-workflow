using System;
using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    [Serializable]
    public class ScatterPrefabEntry
    {
        public GameObject prefab;
        [Min(0)] public int weight = 1;
    }

    /// <summary>一组在目标语义层与离路距离范围内均匀生成的散布物体配置。</summary>
    public class ScatterConfigSO : ScriptableObject
    {
        [Tooltip("生成组显示名称")]
        public string groupName = "散布生成组";

        [Tooltip("流式生成区块尺寸（米，x/z）")]
        public Vector2 chunkSize = new Vector2(16f, 16f);

        [Tooltip("区块中心距观察点不超过该距离时保持激活")]
        public float visibleDistance = 60f;

        [Tooltip("本组 Prefab 池；按每项权重随机选取")]
        public List<ScatterPrefabEntry> prefabs = new List<ScatterPrefabEntry>();

        [Tooltip("目标区域内的生成密度（个/平方米）")]
        public float density = 0.05f;

        [Tooltip("实例均匀随机缩放范围（min/max）")]
        public Vector2 randomScale = new Vector2(0.8f, 1.2f);

        [Tooltip("允许生成的离路距离范围（米，min/max）；按 offRoad MapData 过滤")]
        public Vector2 offRoadDistanceRange = new Vector2(0f, 10000f);

        [Tooltip("允许生成的语义层；可多选 Layer0~Layer15")]
        public TerrainWorkflowLayerMask targetLayers = TerrainWorkflowLayerMask.All & ~TerrainWorkflowLayerMask.Layer0;
    }
}
