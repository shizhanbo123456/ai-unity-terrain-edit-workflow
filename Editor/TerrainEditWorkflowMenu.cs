using System;
using AiTerrainWorkflow.Gui;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

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

        /// <summary>测试用 PanelSettings 资产路径（带主题，脚本与菜单共用）。</summary>
        public const string PanelSettingsAssetPath = "Assets/unity-terrain-edit-workflow/Gui/TestPanelSettings.asset";

        /// <summary>备用最小运行时主题（当内置默认主题加载不到时使用）。</summary>
        private const string FallbackThemePath = "Assets/unity-terrain-edit-workflow/Gui/DefaultRuntimeTheme.tss";

        [MenuItem(MenuRoot + "Log Version")]
        public static void LogVersion()
        {
            Debug.Log($"[Terrain Edit Workflow] {Version}");
        }

        /// <summary>
        /// 创建 UI Toolkit 运行时测试物体（Play 模式可行性测试）：
        /// 先确保 Gui/TestPanelSettings.asset（带主题）存在，再创建
        /// 一个挂 RuntimeUiTest（纯 C# 控件测试）、一个挂 UxmlUiTest（UXML/USS 资产测试）。
        /// 进入 Play 模式即可看到两个测试面板；不需要的物体可先 SetActive(false)。
        /// </summary>
        [MenuItem(MenuRoot + "Create UI Test Setup")]
        public static void CreateUiTestSetup()
        {
            var panelSettings = EnsurePanelSettingsAsset();

            var runtimeGo = new GameObject("UI Toolkit Test (Runtime C#)");
            runtimeGo.AddComponent<RuntimeUiTest>();

            var uxmlGo = new GameObject("UI Toolkit Test (UXML Asset)");
            uxmlGo.AddComponent<UxmlUiTest>();

            if (panelSettings != null)
            {
                runtimeGo.GetComponent<UIDocument>().panelSettings = panelSettings;
                uxmlGo.GetComponent<UIDocument>().panelSettings = panelSettings;
            }

            Selection.activeGameObject = runtimeGo;
            var themeState = panelSettings != null && panelSettings.themeStyleSheet != null
                ? "主题正常"
                : "未设置主题（UI 可能渲染异常）";
            Debug.Log($"[Terrain Edit Workflow] 已创建 2 个 UI 测试物体（{themeState}），进入 Play 模式查看。");
        }

        /// <summary>
        /// 获取（不存在则创建）测试用 PanelSettings 资产，并确保挂上运行时主题。
        /// 主题查找顺序：内置包默认主题 → 全库搜索 ThemeStyleSheet → 备用最小主题 .tss。
        /// </summary>
        public static PanelSettings EnsurePanelSettingsAsset()
        {
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsAssetPath);
            if (ps == null)
            {
                ps = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(ps, PanelSettingsAssetPath);
            }

            if (ps.themeStyleSheet == null)
            {
                var theme = FindRuntimeTheme();
                if (theme != null)
                {
                    ps.themeStyleSheet = theme;
                    EditorUtility.SetDirty(ps);
                    AssetDatabase.SaveAssets();
                }
                else
                {
                    Debug.LogWarning(
                        "[Terrain Edit Workflow] 找不到任何 ThemeStyleSheet，PanelSettings 将无主题，" +
                        "UI 可能无法正常渲染。请手动给 " + PanelSettingsAssetPath + " 指定 themeStyleSheet。");
                }
            }
            return ps;
        }

        /// <summary>查找可用的运行时主题样式表（ThemeStyleSheet）。</summary>
        private static ThemeStyleSheet FindRuntimeTheme()
        {
            // 1) 内置包 com.unity.ui 的默认运行时主题（Unity 2021.2+ 标准路径）
            const string builtin = "Packages/com.unity.ui/Runtime/Resources/UnityDefaultRuntimeTheme.tss";
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(builtin);
            if (theme != null)
            {
                return theme;
            }

            // 2) 全库搜索（含已加载包内的资产），优先名字含 DefaultRuntime 的
            var guids = AssetDatabase.FindAssets("t:ThemeStyleSheet");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var candidate = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(path);
                if (candidate != null &&
                    candidate.name.IndexOf("DefaultRuntime", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate;
                }
            }

            // 3) 任意 ThemeStyleSheet
            foreach (var guid in guids)
            {
                var candidate = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
                if (candidate != null)
                {
                    return candidate;
                }
            }

            // 4) 本仓库预置的备用最小主题
            return AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(FallbackThemePath);
        }
    }
}
