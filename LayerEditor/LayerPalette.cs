#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 单个绘画图层：颜色 + 可编辑名称。
    /// </summary>
    [System.Serializable]
    public class LayerInfo
    {
        public Color32 color;
        public string name;

        public LayerInfo(Color32 color, string name)
        {
            this.color = color;
            this.name = name;
        }
    }

    /// <summary>
    /// 图层预设表：layer0 恒为透明（过渡区域），其余为 16 个差别较大的内置预设色。
    /// 名称允许手动编辑；颜色固定（预设），后续如需自定义色可再扩展。
    /// </summary>
    public static class LayerPalette
    {
        /// <summary>预设颜色数量（不含 layer0 透明）。</summary>
        public const int PresetColorCount = 16;

        /// <summary>layer0 透明色（过渡区域/擦除）。</summary>
        public static readonly Color32 Transparent = new Color32(0, 0, 0, 0);

        /// <summary>16 个差别较大的预设色（RGB 全饱和、肉眼可区分）。</summary>
        public static readonly Color32[] PresetColors =
        {
            new Color32(230, 0, 18, 255),    // 红
            new Color32(0, 115, 230, 255),   // 蓝
            new Color32(0, 180, 60, 255),    // 绿
            new Color32(255, 200, 0, 255),   // 黄
            new Color32(140, 60, 220, 255),  // 紫
            new Color32(255, 90, 0, 255),    // 橙
            new Color32(0, 200, 200, 255),   // 青
            new Color32(255, 60, 180, 255),  // 粉
            new Color32(120, 80, 40, 255),   // 棕
            new Color32(150, 150, 150, 255), // 灰
            new Color32(80, 200, 120, 255),  // 浅绿
            new Color32(200, 150, 80, 255),  // 沙色
            new Color32(90, 90, 220, 255),   // 靛蓝
            new Color32(220, 220, 220, 255), // 银白
            new Color32(40, 40, 40, 255),    // 深灰
            new Color32(180, 230, 240, 255), // 浅青
        };

        /// <summary>与 PresetColors 一一对应的默认名称（可编辑）。</summary>
        public static readonly string[] PresetDefaultNames =
        {
            "红色", "蓝色", "绿色", "黄色", "紫色", "橙色", "青色", "粉色",
            "棕色", "灰色", "浅绿", "沙色", "靛蓝", "银白", "深灰", "浅青",
        };

        /// <summary>
        /// 生成默认图层列表：第 0 项恒为透明（过渡），随后是 16 个预设色。
        /// </summary>
        public static List<LayerInfo> CreateDefaultLayers()
        {
            var list = new List<LayerInfo>(PresetColorCount + 1)
            {
                new LayerInfo(Transparent, "Layer0 过渡(透明)"),
            };
            for (int i = 0; i < PresetColorCount; i++)
            {
                list.Add(new LayerInfo(PresetColors[i], $"Layer{i + 1} {PresetDefaultNames[i]}"));
            }
            return list;
        }
    }
}
#endif
