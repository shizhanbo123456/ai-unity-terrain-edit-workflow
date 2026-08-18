using AiTerrainWorkflow.Gui;
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
        private const string Version = "v1.1";

        /// <summary>菜单前缀（Tools 下拉下）。</summary>
        private const string MenuRoot = "Tools/Terrain Edit Workflow/";

        [MenuItem(MenuRoot + "Log Version")]
        public static void LogVersion()
        {
            Debug.Log($"[Terrain Edit Workflow] {Version}");
        }

        /// <summary>
        /// 创建 UI Toolkit 运行时测试物体（Play 模式可行性测试）：
        /// 一个挂 RuntimeUiTest（纯 C# 控件测试），一个挂 UxmlUiTest（UXML/USS 资产测试）。
        /// 进入 Play 模式即可看到两个测试面板；不需要的物体可先 SetActive(false)。
        /// </summary>
        [MenuItem(MenuRoot + "Create UI Test Setup")]
        public static void CreateUiTestSetup()
        {
            var runtimeGo = new GameObject("UI Toolkit Test (Runtime C#)");
            runtimeGo.AddComponent<RuntimeUiTest>();

            var uxmlGo = new GameObject("UI Toolkit Test (UXML Asset)");
            uxmlGo.AddComponent<UxmlUiTest>();

            Selection.activeGameObject = runtimeGo;
            Debug.Log("[Terrain Edit Workflow] 已创建 2 个 UI 测试物体，进入 Play 模式查看。");
        }
    }
}
