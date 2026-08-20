#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 单个层级配置（每个层级一个 SO 资产，由总 SO TerrainPaintProjectSO 持有）。
    ///
    /// 层级数量 2~16（Layer0 恒为透明过渡层）。字段按四个子界面用 Header 划分专属配置：
    ///   - 区域编辑：颜色（层次图该层像素颜色）与名称（语义文本）——只读，需在 Inspector 修改
    ///   - 贴图编辑：自然/道路 TerrainLayer 权重列表 + 道路生成参数（用于信息生成计算）
    ///   - 树木编辑 / 细节编辑：暂无（后续阶段扩展）
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
        [Tooltip("G 胶囊半径 = 占用/间隔缓冲（世界距离）")]
        public float roadSpacingMin = 4f;
        [Tooltip("烘焙时对 R 的重映射曲线（不作用于 B）")]
        public AnimationCurve roadFinalRemap = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        // ---------- 树木编辑（暂无） ----------

        // ---------- 细节编辑（暂无） ----------

        /// <summary>同步自然/道路权重列表长度与两个池对齐（截断或补零）。</summary>
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
#endif
