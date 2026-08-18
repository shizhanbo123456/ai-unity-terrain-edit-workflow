using UnityEngine;
using UnityEngine.UIElements;

namespace AiTerrainWorkflow.Gui
{
    /// <summary>
    /// UI Toolkit 运行时（Play 模式）可行性测试 —— UXML / USS 资产加载版。
    ///
    /// 用法：挂到场景任意物体（Add Component 搜索 "Uxml UI Test"），进入 Play 模式即可看到
    /// TestLayout.uxml（布局）+ TestStyle.uss（样式）加载后的界面。
    ///
    /// UXML/USS 来源二选一：
    ///   1) Inspector 中手动把 visualTree / styleSheet 拖到字段（任何模式均可用，推荐正式用法）；
    ///   2) 留空时，在编辑器 Play 模式下自动从本目录加载（AssetDatabase 仅在编辑器进程可用，
    ///      打包后的 Player 中必须走字段引用或 Resources/Addressables）。
    /// </summary>
    [AddComponentMenu("AiTerrainWorkflow/Gui/Uxml UI Test")]
    [RequireComponent(typeof(UIDocument))]
    public class UxmlUiTest : MonoBehaviour
    {
        [Tooltip("UXML 布局资产；留空时编辑器模式下自动从 Gui/TestLayout.uxml 加载")]
        [SerializeField] private VisualTreeAsset visualTree;

        [Tooltip("USS 样式资产；留空时编辑器模式下自动从 Gui/TestStyle.uss 加载")]
        [SerializeField] private StyleSheet styleSheet;

        private void Awake()
        {
            var uiDocument = GetComponent<UIDocument>();
            if (uiDocument.panelSettings == null)
            {
                // 运行时动态创建 PanelSettings（不落盘为资产，测试用足够）
                uiDocument.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            }

#if UNITY_EDITOR
            if (visualTree == null || styleSheet == null)
            {
                const string dir = "Assets/unity-terrain-edit-workflow/Gui/";
                visualTree = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(dir + "TestLayout.uxml");
                styleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>(dir + "TestStyle.uss");
            }
#endif

            var root = uiDocument.rootVisualElement;
            root.Clear();
            if (styleSheet != null)
            {
                root.styleSheets.Add(styleSheet);
            }

            if (visualTree != null)
            {
                visualTree.CloneTree(root);
            }
            else
            {
                root.Add(new Label(
                    "未加载到 UXML。请在 Inspector 中把 TestLayout.uxml 拖到 visualTree 字段，" +
                    "或确认在 Unity 编辑器中运行（Play 模式）。"));
            }
        }
    }
}
