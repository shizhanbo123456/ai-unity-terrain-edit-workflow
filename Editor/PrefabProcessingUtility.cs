#if UNITY_EDITOR
using System;
using System.IO;
using AiTerrainWorkflow.LayerEditor;
using UnityEditor;
using UnityEngine;
using UnityPythonBridge;
using UnityPythonBridge.Commands;

namespace AiTerrainWorkflow.Editor
{
    /// <summary>候选 Prefab 的编辑器构建入口。</summary>
    public static class PrefabProcessingUtility
    {
        public const string WorkflowRoot = "Assets/ai-unity-terrain-edit-workflow";
        public const string BillboardOutputDirectory =
            "Assets/ai-unity-terrain-edit-workflow/Billboards";

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

        /// <summary>
        /// 遍历工作流目录下所有挂有 PrefabStructureInfo 的候选 Prefab，
        /// 为 generateBillboard=true 的对象从 (0,0,1) 方向生成正交 Billboard。
        /// </summary>
        /// <returns>成功更新的 Billboard 数量。</returns>
        public static int UpdateAllBillboards()
        {
            int updated = 0;
            foreach (string prefabPath in FindCandidatePrefabPaths())
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var info = prefab != null ? prefab.GetComponent<PrefabStructureInfo>() : null;
                if (info == null || !info.generateBillboard)
                    continue;

                try
                {
                    PrefabBillboardCommand.Billboard(
                        new BridgeContext(),
                        new BridgeArgs
                        {
                            path = prefabPath,
                            output = BillboardOutputDirectory,
                            cameraPosition = new[] { 0f, 0f, 1f },
                            pixelsPerMeter = 100f,
                        });
                    updated++;
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[PrefabProcessingUtility] Billboard 更新失败: {prefabPath}\n{exception}");
                }
            }

            if (updated > 0)
                AssetDatabase.Refresh();
            return updated;
        }

        /// <summary>
        /// 批量计算候选 Prefab 的完整变换 AABB，并写入 PrefabStructureInfo 的 XYZ 范围。
        /// 非强制刷新时，只处理三个范围均为 (0,0) 的 Prefab。
        /// </summary>
        /// <param name="forceRefresh">true=全部重算；false=已有任一非零 Bounds 的对象跳过。</param>
        /// <returns>成功更新的 Prefab 数量。</returns>
        public static int UpdateAllBounds(bool forceRefresh)
        {
            int updated = 0;
            foreach (string prefabPath in FindCandidatePrefabPaths())
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var info = prefab != null ? prefab.GetComponent<PrefabStructureInfo>() : null;
                if (info == null)
                    continue;
                if (!forceRefresh && !BoundsAreAllZero(info))
                    continue;

                try
                {
                    var result = PrefabBillboardCommand.Bounds(
                        new BridgeContext(),
                        new BridgeArgs { path = prefabPath }) as PrefabBoundsResult;
                    if (result == null)
                        throw new InvalidOperationException("prefab.bounds 未返回 PrefabBoundsResult");

                    WriteBounds(prefabPath, result);
                    updated++;
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[PrefabProcessingUtility] Bounds 更新失败: {prefabPath}\n{exception}");
                }
            }

            if (updated > 0)
                AssetDatabase.SaveAssets();
            return updated;
        }

        private static string[] FindCandidatePrefabPaths()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { WorkflowRoot });
            var paths = new System.Collections.Generic.List<string>(guids.Length);
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent<PrefabStructureInfo>() != null)
                    paths.Add(path);
            }
            return paths.ToArray();
        }

        private static bool BoundsAreAllZero(PrefabStructureInfo info)
        {
            return info.boundsX == Vector2.zero &&
                   info.boundsY == Vector2.zero &&
                   info.boundsZ == Vector2.zero;
        }

        private static void WriteBounds(string prefabPath, PrefabBoundsResult bounds)
        {
            GameObject contentsRoot = null;
            try
            {
                contentsRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                var info = contentsRoot.GetComponent<PrefabStructureInfo>();
                if (info == null)
                    throw new InvalidOperationException("候选 Prefab 根节点缺少 PrefabStructureInfo");

                info.boundsX = new Vector2(bounds.min.x, bounds.max.x);
                info.boundsY = new Vector2(bounds.min.y, bounds.max.y);
                info.boundsZ = new Vector2(bounds.min.z, bounds.max.z);
                PrefabUtility.SaveAsPrefabAsset(contentsRoot, prefabPath);
            }
            finally
            {
                if (contentsRoot != null)
                    PrefabUtility.UnloadPrefabContents(contentsRoot);
            }
        }
    }
}
#endif
