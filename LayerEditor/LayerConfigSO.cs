#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 单个层级配置（每个层级一个 SO 资产，由总 SO TerrainPaintProjectSO 持有）。
    ///
    /// 层级数量固定 16 个。**名称与颜色只能在 Inspector 中修改**——
    /// 编辑器窗口（配置修改 / 绘画子界面）只读取显示，不允许在窗口中改动。
    /// 其余参数（贴图、道路生成）可在窗口的「配置修改」子界面编辑。
    /// </summary>
    public class LayerConfigSO : ScriptableObject
    {
        [Header("标识（仅可在 Inspector 修改）")]
        [Tooltip("层级颜色（层次图中该层像素的颜色）")]
        public Color32 color;
        [Tooltip("层级名称（语义文本，如 \"草地\"）")]
        public string layerName;

        [Header("自然地面贴图")]
        [Tooltip("非路面区域的贴图列表，多选按 value-noise 加权混合")]
        public List<Texture2D> naturalTextures = new List<Texture2D>();
        public int naturalSeed;

        [Header("道路贴图")]
        [Tooltip("路面区域的贴图列表，多选按 value-noise 加权混合")]
        public List<Texture2D> roadTextures = new List<Texture2D>();
        public int roadSeed;

        [Header("道路生成")]
        [Tooltip("是否生成路面；false → 该层 R 全 0，路不可进入")]
        public bool generateRoad = true;
        [Tooltip("B 胶囊半径（世界距离）")]
        public float roadWidth = 2f;
        [Tooltip("G 胶囊半径 = 占用/间隔缓冲（世界距离）")]
        public float roadSpacingMin = 4f;
        [Tooltip("烘焙时对 R 的重映射曲线（不作用于 B）")]
        public AnimationCurve roadFinalRemap = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [Tooltip("可邻接层级索引（0 基，用于组合层级分组）")]
        public List<int> adjLayers = new List<int>();
    }
}
#endif
