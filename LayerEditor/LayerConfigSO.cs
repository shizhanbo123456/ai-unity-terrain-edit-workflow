using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 单个层级配置（每个层级一个 SO 资产，由总 SO TerrainPaintProjectSO 持有）。
    ///
    /// 层级数量 2~16（Layer0 恒为透明过渡层）。
    ///   - 区域编辑：颜色（层次图该层像素颜色）与名称（语义文本）——只读，需在 Inspector 修改
    ///   - 贴图编辑：自然/道路 TerrainLayer 权重列表 + 道路生成参数（用于信息生成计算）
    ///   - 高度编辑：高度范围（heightRange，烘焙高度图时用）
    /// 注：邻接层级（组合分组）已移至全局配置 TerrainPaintProjectSO.adjacencyGroups。
    /// </summary>
    public class LayerConfigSO : ScriptableObject
    {
        // ---------- 区域编辑 ----------

        [Header("区域编辑")]
        [Tooltip("层级颜色（层次图中该层像素的颜色）")]
        public Color32 color;
        [Tooltip("层级名称（语义文本，如 \"草地\"）")]
        public string layerName;

        // ---------- 贴图编辑 ----------

        [Header("贴图编辑")]
        [Tooltip("自然 TerrainLayer 权重：索引 = TerrainPaintProjectSO.naturalTerrainLayers 池 id，值 = 权重（0 = 不纳入混合）。")]
        public List<int> naturalLayerWeights = new List<int>();
        [Tooltip("道路 TerrainLayer 权重：索引 = TerrainPaintProjectSO.roadTerrainLayers 池 id，值 = 权重（0 = 不纳入混合）。")]
        public List<int> roadLayerWeights = new List<int>();

        [Tooltip("是否生成路面；false → 该层 R 全 0，路不可进入")]
        public bool generateRoad = true;
        [Tooltip("B 胶囊半径（世界距离）")]
        public float roadWidth = 2f;
        [Range(0f, 1f)]
        [Tooltip("抗卷曲系数（0~1）：实际 G 禁区滞后距离 = antiCurl × stepWorld × 2。默认 0.5 = 1 倍步距；<0.25 抗卷曲效果较差，>0.75 路径生成较难")]
        public float antiCurl = 0.5f;
        [Tooltip("烘焙时对 R 的重映射曲线（不作用于 B）")]
        public AnimationCurve roadFinalRemap = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        // ---------- 高度编辑 ----------

        [Header("高度编辑")]
        [Tooltip("该层级的预期高度范围 (min, max)。烘焙高度图时，噪声在该范围内插值生成高度值。")]
        public Vector2 heightRange = new Vector2(0f, 1f);

        /// <summary>同步自然/道路权重列表长度与各 TerrainLayer 池对齐（截断或补零）。</summary>
        public void SyncWeightLists(int naturalPoolCount, int roadPoolCount)
        {
            SyncListLength(naturalLayerWeights, naturalPoolCount);
            SyncListLength(roadLayerWeights, roadPoolCount);
        }

        private static void SyncListLength(List<int> list, int count)
        {
            while (list.Count < count) list.Add(0);
            if (list.Count > count) list.RemoveRange(count, list.Count - count);
        }
    }
}
