using System;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 挂在候选 Prefab 根节点上的结构信息。
    /// 供散布、摆件、定点等生成流程共享 Prefab 级别的放置元数据。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PrefabStructureInfo : MonoBehaviour
    {
        [Tooltip("是否为该 Prefab 生成 Billboard；具体生成规则后续接入")]
        public bool generateBillboard;

        [Tooltip("是否使用两点高度适应；具体语义和算法后续接入")]
        public bool twoPointHeightAdaptation;

        [Tooltip("Prefab 世界 AABB 在 X 轴上的范围（min/max）")]
        public Vector2 boundsX;

        [Tooltip("Prefab 世界 AABB 在 Y 轴上的范围（min/max）")]
        public Vector2 boundsY;

        [Tooltip("Prefab 世界 AABB 在 Z 轴上的范围（min/max）")]
        public Vector2 boundsZ;

#if UNITY_EDITOR
        /// <summary>
        /// 为目标 Prefab 新增或更新结构信息，并强制归一化其根 Transform。
        /// 每次调用均设置 localPosition=zero、localRotation=identity、localScale=one。
        /// </summary>
        /// <param name="targetPrefab">Project 中的目标 .prefab 资产。</param>
        /// <param name="generateBillboard">是否生成 Billboard（当前仅保存配置）。</param>
        /// <param name="twoPointHeightAdaptation">是否启用两点高度适应。</param>
        /// <returns>保存后的 PrefabStructureInfo 组件。</returns>
        public static PrefabStructureInfo UpdatePrefabStructure(
            GameObject targetPrefab,
            bool generateBillboard,
            bool twoPointHeightAdaptation)
        {
            if (targetPrefab == null)
                throw new ArgumentNullException(nameof(targetPrefab));

            string assetPath = AssetDatabase.GetAssetPath(targetPrefab);
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("targetPrefab 必须是 Project 中的 .prefab 资产。", nameof(targetPrefab));
            }

            GameObject contentsRoot = null;
            try
            {
                contentsRoot = PrefabUtility.LoadPrefabContents(assetPath);

                // 这是该接口的强制不变量：无论组件是新增还是更新，每次调用都归一化根节点。
                Transform rootTransform = contentsRoot.transform;
                rootTransform.localPosition = Vector3.zero;
                rootTransform.localRotation = Quaternion.identity;
                rootTransform.localScale = Vector3.one;

                var info = contentsRoot.GetComponent<PrefabStructureInfo>();
                if (info == null)
                    info = contentsRoot.AddComponent<PrefabStructureInfo>();

                info.generateBillboard = generateBillboard;
                info.twoPointHeightAdaptation = twoPointHeightAdaptation;

                PrefabUtility.SaveAsPrefabAsset(contentsRoot, assetPath);
            }
            finally
            {
                if (contentsRoot != null)
                    PrefabUtility.UnloadPrefabContents(contentsRoot);
            }

            var savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            var savedInfo = savedPrefab != null ? savedPrefab.GetComponent<PrefabStructureInfo>() : null;
            if (savedInfo == null)
                throw new InvalidOperationException("PrefabStructureInfo 保存失败: " + assetPath);

            return savedInfo;
        }
#endif
    }
}
