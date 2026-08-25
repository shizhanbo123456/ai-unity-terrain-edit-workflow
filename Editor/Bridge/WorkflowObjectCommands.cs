#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityPythonBridge;

namespace AiTerrainWorkflow.Editor.Bridge
{
    /// <summary>workflow.prefab.fix_pivot 的返回：修正前后合并 Bounds（center/size）与平移量 offset。</summary>
    [Serializable]
    public sealed class FixPivotResult
    {
        public string operation;
        public string path;
        public string message;
        public bool valid;
        public string[] errors;
        public int movedChildCount;
        public float[] beforeCenter;
        public float[] beforeSize;
        public float[] afterCenter;
        public float[] afterSize;
        public float[] offset;
    }

    public static class WorkflowObjectCommands
    {
        // 说明：场景物体实例化/销毁、Prefab 资产内部编辑等 5 条通用命令已迁移至 unity-python-bridge，
        // 成为其原生命令 gameobject.instantiate / gameobject.destroy / prefab.edit / prefab.remove /
        // prefab.instantiate（见 bridge 仓库 Runtime/Commands/GameObjectCommands.cs 与 PrefabEditCommands.cs）。
        // 本文件仅保留仍强依赖本工作流 Prefab 管线（PrefabProcessingUtility.StandardizePivotToOrigin）的 fix_pivot。

        // ---------- workflow.prefab.fix_pivot ----------

        /// <summary>
        /// 计算 Prefab 内所有 Renderer 变换后的合并 Bounds，并将包围盒「中心正下方」
        /// （center.x, min.y, center.z）平移到原点 (0,0,0)：整体平移所有直接子物体，
        /// 使模型底部中心落在根节点原点上。根节点保持零变换（符合阶段 0 规范，mesh 修正一律作用在子物体上）。
        /// 参数: path = Prefab 资产路径（必填）。
        /// 返回修正前后的合并 Bounds 与平移量 offset。
        /// </summary>
        [BridgeCommand("workflow.prefab.fix_pivot",
            "计算 Prefab 所有 mesh 变换后的合并 Bounds，并将中心正下方 (center.x, min.y, center.z) 平移到原点 (0,0,0)。参数: path=Prefab资产路径(必填)")]
        public static object FixPivot(BridgeContext ctx, BridgeArgs args)
        {
            string prefabPath = NormalizeAssetPath(args.path);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) throw new ArgumentException("找不到 Prefab: " + prefabPath);

            GameObject contentsRoot = null;
            try
            {
                contentsRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                var status = PrefabProcessingUtility.StandardizePivotToOrigin(
                    contentsRoot.transform, out Vector3 offset, out Bounds before, out Bounds after);
                if (status != PrefabProcessingUtility.PivotStandardizeStatus.Ok)
                {
                    throw new InvalidOperationException(status switch
                    {
                        PrefabProcessingUtility.PivotStandardizeStatus.NoRenderers =>
                            $"Prefab 内没有任何带有效网格的 Renderer，无法计算 Bounds: {prefabPath}",
                        PrefabProcessingUtility.PivotStandardizeStatus.NoChildren =>
                            $"Prefab 根节点下没有直接子物体（mesh 直接挂在根节点上时无法在不移动根节点的情况下平移），请手动处理: {prefabPath}",
                        PrefabProcessingUtility.PivotStandardizeStatus.RootHasRenderer =>
                            $"Prefab 根节点自身挂有 Renderer，平移子物体无法修正根上的 mesh，请手动处理: {prefabPath}",
                        _ => "未知原因",
                    });
                }
                PrefabUtility.SaveAsPrefabAsset(contentsRoot, prefabPath);
                return new FixPivotResult
                {
                    operation = "prefab.fix_pivot",
                    path = prefabPath,
                    message = $"offset=({offset.x:F4}, {offset.y:F4}, {offset.z:F4})",
                    valid = true,
                    errors = Array.Empty<string>(),
                    movedChildCount = contentsRoot.transform.childCount,
                    beforeCenter = new[] { before.center.x, before.center.y, before.center.z },
                    beforeSize = new[] { before.size.x, before.size.y, before.size.z },
                    afterCenter = new[] { after.center.x, after.center.y, after.center.z },
                    afterSize = new[] { after.size.x, after.size.y, after.size.z },
                    offset = new[] { offset.x, offset.y, offset.z },
                };
            }
            finally
            {
                if (contentsRoot != null)
                    PrefabUtility.UnloadPrefabContents(contentsRoot);
            }
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? "").Replace('\\', '/');
        }
    }
}
#endif
