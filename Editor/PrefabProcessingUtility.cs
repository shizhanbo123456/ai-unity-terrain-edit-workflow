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
        public const string GeneratedRoot = WorkflowRoot + "/Generated";
        public const string CandidatePrefabDirectory = GeneratedRoot + "/Prefabs";
        public const string BillboardOutputDirectory =
            GeneratedRoot + "/Billboards";
        public const string BillboardMaterialDirectory =
            GeneratedRoot + "/Materials";
        private const string BillboardTemplateMaterialPath =
            "Assets/ai-unity-terrain-edit-workflow/src/billboard.mat";
        private const string CrossPlanePrefabPath =
            "Assets/ai-unity-terrain-edit-workflow/src/cross.prefab";
        private const string LinearPlanePrefabPath =
            "Assets/ai-unity-terrain-edit-workflow/src/linear.prefab";
        private const float PlaneSourceWidth = 2f;
        private const float PlaneSourceHeight = 1f;

        /// <summary>判断 Prefab 是否为可供工作流三个摆放模块引用的已处理备用 Prefab。</summary>
        public static bool IsProcessedCandidatePrefab(GameObject prefab, out string reason)
        {
            if (prefab == null)
            {
                reason = "Prefab 为空";
                return false;
            }

            string path = AssetDatabase.GetAssetPath(prefab).Replace('\\', '/');
            if (string.IsNullOrEmpty(path) ||
                !path.StartsWith(CandidatePrefabDirectory + "/", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Prefab 不在工具目录内，请先通过“批量添加备用预制体”处理";
                return false;
            }

            if (prefab.GetComponent<PrefabStructureInfo>() == null)
            {
                reason = "根节点缺少 PrefabStructureInfo";
                return false;
            }

            Transform root = prefab.transform;
            if (root.localPosition.sqrMagnitude > 0.00000001f ||
                Quaternion.Angle(root.localRotation, Quaternion.identity) > 0.0001f ||
                (root.localScale - Vector3.one).sqrMagnitude > 0.00000001f)
            {
                reason = $"根 Transform 未归一化：position={root.localPosition}, " +
                         $"rotation={root.localEulerAngles}, scale={root.localScale}";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// 在工作流根目录创建同名包装 Prefab：空根节点 + 原 Prefab 的嵌套实例，
        /// 然后写入/更新根节点上的 PrefabStructureInfo。
        /// </summary>
        /// <param name="targetPrefab">Project 中作为内容来源的 .prefab 资产。</param>
        /// <param name="billboardMode">Billboard/LOD 使用方式。</param>
        /// <param name="twoPointHeightAdaptation">是否启用两点高度适应。</param>
        /// <returns>创建完成的包装 Prefab 资产。</returns>
        public static GameObject BuildCandidatePrefab(
            GameObject targetPrefab,
            BillboardMode billboardMode,
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
            EnsureAssetFolder(CandidatePrefabDirectory);
            string outputPath = CandidatePrefabDirectory + "/" + prefabName + ".prefab";
            if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("源 Prefab 已位于候选 Prefab 输出位置，不能包装自身: " + outputPath);
            var existingCandidate = AssetDatabase.LoadAssetAtPath<GameObject>(outputPath);
            if (existingCandidate != null)
            {
                if (!WrapsSourcePrefab(existingCandidate, sourcePath))
                    throw new InvalidOperationException(
                        "同名备用 Prefab 已存在，但它包装的不是当前源 Prefab: " + outputPath);
                PrefabStructureInfo.UpdatePrefabStructure(
                    existingCandidate,
                    billboardMode,
                    twoPointHeightAdaptation);
                if (billboardMode != BillboardMode.None)
                    UpdateBillboard(outputPath, billboardMode);
                return AssetDatabase.LoadAssetAtPath<GameObject>(outputPath);
            }

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
                billboardMode,
                twoPointHeightAdaptation);

            if (billboardMode != BillboardMode.None)
                UpdateBillboard(outputPath, billboardMode);

            return AssetDatabase.LoadAssetAtPath<GameObject>(outputPath);
        }

        private static bool WrapsSourcePrefab(GameObject candidate, string expectedSourcePath)
        {
            if (candidate == null || candidate.transform.childCount == 0)
                return false;
            var child = candidate.transform.GetChild(0).gameObject;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(child);
            string actualPath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            return string.Equals(actualPath, expectedSourcePath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 遍历工作流目录下所有挂有 PrefabStructureInfo 的候选 Prefab，
        /// 为 billboardMode!=None 的对象从 (0,0,1) 方向生成正交 Billboard，
        /// 随后创建对应面片、独立材质并配置 LODGroup。
        /// </summary>
        /// <returns>成功更新的 Billboard 数量。</returns>
        public static int UpdateAllBillboards()
        {
            int updated = 0;
            foreach (string prefabPath in FindCandidatePrefabPaths())
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var info = prefab != null ? prefab.GetComponent<PrefabStructureInfo>() : null;
                if (info == null || info.billboardMode == BillboardMode.None)
                    continue;

                try
                {
                    UpdateBillboard(prefabPath, info.billboardMode);
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
            if (!AssetDatabase.IsValidFolder(CandidatePrefabDirectory))
                return Array.Empty<string>();
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { CandidatePrefabDirectory });
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

        private static void UpdateBillboard(string prefabPath, BillboardMode mode)
        {
            EnsureAssetFolder(BillboardOutputDirectory);
            var result = PrefabBillboardCommand.Billboard(
                new BridgeContext(),
                new BridgeArgs
                {
                    path = prefabPath,
                    output = BillboardOutputDirectory,
                    cameraPosition = new[] { 0f, 0f, 1f },
                    pixelsPerMeter = 100f,
                    light = 2f,
                }) as PrefabBillboardResult;
            if (result == null)
                throw new InvalidOperationException("prefab.billboard 未返回 PrefabBillboardResult");

            string texturePath = BillboardOutputDirectory + "/" +
                                 Path.GetFileNameWithoutExtension(prefabPath) + ".png";
            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
            AttachBillboard(prefabPath, mode, texturePath, result);
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

        private static void AttachBillboard(
            string prefabPath,
            BillboardMode mode,
            string texturePath,
            PrefabBillboardResult capture)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
                throw new InvalidOperationException("无法加载 Billboard 图片: " + texturePath);

            EnsureAssetFolder(BillboardMaterialDirectory);
            string materialPath = BillboardMaterialDirectory + "/" +
                                  Path.GetFileNameWithoutExtension(prefabPath) + "_Billboard.mat";
            var billboardShader = Shader.Find("Custom/BothFaceRender");
            if (billboardShader == null)
                throw new InvalidOperationException("找不到 Shader: Custom/BothFaceRender");
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                var template = AssetDatabase.LoadAssetAtPath<Material>(BillboardTemplateMaterialPath);
                if (template != null)
                    material = new Material(template);
                else
                    material = new Material(billboardShader);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            material.shader = billboardShader;
            material.mainTexture = texture;
            EditorUtility.SetDirty(material);

            string planePrefabPath = mode == BillboardMode.CrossPlanes
                ? CrossPlanePrefabPath
                : LinearPlanePrefabPath;
            var planePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(planePrefabPath);
            if (planePrefab == null)
                throw new InvalidOperationException("找不到 Billboard 面片 Prefab: " + planePrefabPath);
            if (planePrefab.transform.childCount == 0)
                throw new InvalidOperationException("Billboard 面片 Prefab 根节点缺少模型子物体: " + planePrefabPath);

            GameObject contentsRoot = null;
            try
            {
                contentsRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                var info = contentsRoot.GetComponent<PrefabStructureInfo>();
                if (info == null)
                    throw new InvalidOperationException("备用 Prefab 根节点缺少 PrefabStructureInfo");

                Transform oldBillboard = contentsRoot.transform.Find("_Billboard");
                if (oldBillboard != null)
                    UnityEngine.Object.DestroyImmediate(oldBillboard.gameObject);

                Renderer[] originalRenderers = contentsRoot.GetComponentsInChildren<Renderer>(true);
                var billboard = PrefabUtility.InstantiatePrefab(planePrefab, contentsRoot.transform) as GameObject;
                if (billboard == null)
                    throw new InvalidOperationException("Billboard 面片实例化失败: " + planePrefabPath);
                billboard.name = "_Billboard";
                billboard.transform.localPosition = Vector3.zero;
                billboard.transform.localRotation = Quaternion.identity;
                billboard.transform.localScale = Vector3.one;

                Renderer[] billboardRenderers = billboard.GetComponentsInChildren<Renderer>(true);
                if (billboardRenderers.Length == 0)
                    throw new InvalidOperationException("Billboard 面片不包含 Renderer: " + planePrefabPath);
                foreach (Renderer renderer in billboardRenderers)
                    renderer.sharedMaterial = material;

                float horizontalScale = capture.projectedWidth / PlaneSourceWidth;
                float verticalScale = capture.projectedHeight / PlaneSourceHeight;
                billboard.transform.localScale = new Vector3(
                    horizontalScale, verticalScale, horizontalScale);
                // 面片 Prefab 的 Y 枢轴位于底边，X/Z 枢轴位于水平中心。
                // 因此水平轴继续对齐 AABB 中心，垂直轴则对齐截图投影的最低点。
                billboard.transform.localPosition = new Vector3(
                    capture.boundsCenter.x,
                    capture.boundsCenter.y - capture.projectedHeight * 0.5f,
                    capture.boundsCenter.z);

                info.billboardMode = mode;
                info.billboardTransform = billboard.transform;

                var lodGroup = contentsRoot.GetComponent<LODGroup>();
                if (lodGroup == null)
                    lodGroup = contentsRoot.AddComponent<LODGroup>();
                lodGroup.SetLODs(new[]
                {
                    new LOD(0.5f, originalRenderers),
                    new LOD(0.01f, billboardRenderers),
                });
                lodGroup.RecalculateBounds();

                PrefabUtility.SaveAsPrefabAsset(contentsRoot, prefabPath);
            }
            finally
            {
                if (contentsRoot != null)
                    PrefabUtility.UnloadPrefabContents(contentsRoot);
            }

            AssetDatabase.SaveAssets();
        }

        private static void EnsureAssetFolder(string assetPath)
        {
            string fullPath = Path.Combine(
                Application.dataPath,
                assetPath.Substring("Assets/".Length));
            Directory.CreateDirectory(fullPath);
            AssetDatabase.Refresh();
        }
    }
}
#endif
