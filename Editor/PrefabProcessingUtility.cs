#if UNITY_EDITOR
using System;
using System.IO;
using AiTerrainWorkflow.LayerEditor;
using UnityEditor;
using UnityEngine;

namespace AiTerrainWorkflow.Editor
{
    /// <summary>候选 Prefab 的编辑器构建入口。</summary>
    public static class PrefabProcessingUtility
    {
        public const string WorkflowRoot = "Assets/ai-unity-terrain-edit-workflow";

        /// <summary>
        /// 在工作流根目录创建同名包装 Prefab：空根节点 + 原 Prefab 的嵌套实例，
        /// 然后写入/更新根节点上的 PrefabStructureInfo。
        /// </summary>
        /// <param name="targetPrefab">Project 中作为内容来源的 .prefab 资产。</param>
        /// <param name="generateBillboard">是否生成 Billboard（当前由信息组件保存该配置）。</param>
        /// <param name="twoPointHeightAdaptation">是否启用两点高度适应。</param>
        /// <returns>创建完成的包装 Prefab 资产。</returns>
        public static GameObject BuildCandidatePrefab(
            GameObject targetPrefab,
            bool generateBillboard,
            bool twoPointHeightAdaptation)
        {
            if (targetPrefab == null)
                throw new ArgumentNullException(nameof(targetPrefab));

            string sourcePath = AssetDatabase.GetAssetPath(targetPrefab);
            if (string.IsNullOrEmpty(sourcePath) ||
                !sourcePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("targetPrefab 必须是 Project 中的 .prefab 资产。", nameof(targetPrefab));
            }

            string prefabName = Path.GetFileNameWithoutExtension(sourcePath);
            string outputPath = WorkflowRoot + "/" + prefabName + ".prefab";
            if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("源 Prefab 已位于候选 Prefab 输出位置，不能包装自身: " + outputPath);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(outputPath) != null)
                throw new InvalidOperationException("同名候选 Prefab 已存在，不会自动覆盖: " + outputPath);

            GameObject wrapperRoot = null;
            try
            {
                // 根节点保持为空；源对象以嵌套 Prefab 实例的形式成为唯一直接子节点。
                wrapperRoot = new GameObject(prefabName);
                var nested = PrefabUtility.InstantiatePrefab(targetPrefab) as GameObject;
                if (nested == null)
                    throw new InvalidOperationException("无法实例化源 Prefab: " + sourcePath);

                nested.transform.SetParent(wrapperRoot.transform, false);
                nested.name = targetPrefab.name;

                var saved = PrefabUtility.SaveAsPrefabAsset(wrapperRoot, outputPath);
                if (saved == null)
                    throw new InvalidOperationException("候选 Prefab 保存失败: " + outputPath);
            }
            finally
            {
                if (wrapperRoot != null)
                    UnityEngine.Object.DestroyImmediate(wrapperRoot);
            }

            AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceSynchronousImport);
            var candidatePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(outputPath);
            if (candidatePrefab == null)
                throw new InvalidOperationException("无法重新加载候选 Prefab: " + outputPath);

            PrefabStructureInfo.UpdatePrefabStructure(
                candidatePrefab,
                generateBillboard,
                twoPointHeightAdaptation);

            return AssetDatabase.LoadAssetAtPath<GameObject>(outputPath);
        }
    }
}
#endif
