using UnityEditor;
using UnityEngine;

namespace AiTerrainWorkflow
{
    /// <summary>
    /// AiTerrainWorkflow 菜单栏工具（Tools / Terrain Edit Workflow）。
    ///
    /// 版本号约定：写死在下文 Log 内容中（当前 v1.1）。后续功能有变更时，
    /// 手动同步更新这里的版本号即可。
    /// </summary>
    public static class TerrainEditWorkflowMenu
    {
    /// <summary>当前工具版本号（有变更时手动更新）。</summary>
    private const string Version = "v1.2";

        /// <summary>菜单前缀（Tools 下拉下）。</summary>
        private const string MenuRoot = "Tools/Terrain Edit Workflow/";

        [MenuItem(MenuRoot + "Log Version")]
        public static void LogVersion()
        {
            Debug.Log($"[Terrain Edit Workflow] {Version}");
        }

        [MenuItem(MenuRoot + "Open Layer Editor")]
        public static void OpenLayerEditor()
        {
            AiTerrainWorkflow.LayerEditor.LayerEditorWindow.Open();
        }
    }
}
