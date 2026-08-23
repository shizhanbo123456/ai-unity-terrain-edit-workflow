using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>在归一化地形坐标中放置同一 Prefab 的一组定点配置。</summary>
    public class FixedPointConfigSO : ScriptableObject
    {
        [Tooltip("编辑器 layer 图上用于标识本组位置的颜色")]
        public Color markerColor = Color.cyan;

        [Tooltip("本组每个定点生成的单个 Prefab")]
        public GameObject prefab;

        [Tooltip("地形归一化位置列表；x/y 均为 0~1，分别映射 Terrain 局部 X/Z")]
        public List<Vector2> positions = new List<Vector2>();

        [Tooltip("绕世界 Y 轴旋转角度，单位为度")]
        [Range(0f, 360f)] public float rotationDegrees;

        [Tooltip("统一缩放")]
        [Min(0f)] public float scale = 1f;
    }
}
