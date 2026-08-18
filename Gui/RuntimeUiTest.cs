using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AiTerrainWorkflow.Gui
{
    /// <summary>
    /// UI Toolkit 运行时（Play 模式）可行性测试 —— 纯 C# 构建版。
    ///
    /// 用法：挂到场景任意物体（Add Component 搜索 "Runtime UI Test"），直接进入 Play 模式即可看到
    /// 一组 UI Toolkit 控件：Label / Button / TextField / Slider / Toggle / DropdownField /
    /// ProgressBar / ListView / 事件日志，并演示 Flex 布局与内联样式。
    ///
    /// 无需手工准备资产：脚本会自动创建 PanelSettings 并赋值给 UIDocument（RequireComponent 自动挂载）。
    /// 若只需测 UXML/USS 资产工作流，请用同目录的 UxmlUiTest。
    /// </summary>
    [AddComponentMenu("AiTerrainWorkflow/Gui/Runtime UI Test")]
    [RequireComponent(typeof(UIDocument))]
    public class RuntimeUiTest : MonoBehaviour
    {
        /// <summary>事件日志：最多保留的行数。</summary>
        private const int MaxLogLines = 12;

        private UIDocument _uiDocument;
        private readonly List<string> _logLines = new List<string>();
        private Label _logLabel;

        private void Awake()
        {
            _uiDocument = GetComponent<UIDocument>();
            if (_uiDocument.panelSettings == null)
            {
                // 运行时动态创建 PanelSettings（不落盘为资产，测试用足够）
                _uiDocument.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            }
            BuildUi(_uiDocument.rootVisualElement);
        }

        private void BuildUi(VisualElement root)
        {
            root.Clear();
            root.style.flexGrow = 1f;
            root.style.paddingTop = root.style.paddingBottom = root.style.paddingLeft = root.style.paddingRight = 12;
            root.style.backgroundColor = new Color(0.09f, 0.09f, 0.13f, 0.96f);

            // 标题
            var title = new Label("UI Toolkit Runtime Test (AiTerrainWorkflow)");
            title.style.fontSize = 20;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.marginBottom = 8;
            root.Add(title);

            // 主区域：左右两列（演示 Flex 横向布局）
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexGrow = 1f;
            root.Add(row);

            // 左列：控件区
            var left = new VisualElement();
            left.style.flexGrow = 1f;
            left.style.paddingRight = 16;
            row.Add(left);

            // 按钮 + 事件回调
            var btn = new Button(() => Log("Button.clicked"))
            {
                text = "Button (clicked event)",
            };
            btn.style.width = 200;
            left.Add(btn);

            // 带 USS class 的按钮（演示 class 机制与 hover 状态见 TestStyle.uss）
            var styledBtn = new Button(() => Log("StyledButton.clicked"))
            {
                text = "Styled Button (.test-btn)",
            };
            styledBtn.AddToClassList("test-btn");
            styledBtn.style.width = 200;
            left.Add(styledBtn);

            // TextField + ChangeEvent
            var field = new TextField("TextField:");
            field.RegisterValueChangedCallback(e => Log($"TextField.ValueChanged = \"{e.newValue}\""));
            left.Add(field);

            // Slider
            var slider = new Slider("Slider:", 0f, 100f) { value = 40f };
            slider.RegisterValueChangedCallback(e => Log($"Slider.ValueChanged = {e.newValue:F1}"));
            left.Add(slider);

            // Toggle
            var toggle = new Toggle("Toggle:");
            toggle.RegisterValueChangedCallback(e => Log($"Toggle.ValueChanged = {e.newValue}"));
            left.Add(toggle);

            // DropdownField
            var dropdown = new DropdownField("DropdownField:", new List<string> { "A", "B", "C" }, 0);
            dropdown.RegisterValueChangedCallback(e => Log($"DropdownField.ValueChanged = \"{e.newValue}\""));
            left.Add(dropdown);

            // ProgressBar
            var progress = new ProgressBar { lowValue = 0f, highValue = 100f, value = 66f };
            progress.title = "ProgressBar (66%)";
            left.Add(progress);

            // 右列：ListView + 事件日志
            var right = new VisualElement();
            right.style.flexGrow = 1f;
            row.Add(right);

            var listTitle = new Label("ListView:");
            listTitle.style.color = Color.white;
            right.Add(listTitle);

            var items = new List<string> { "Tree A", "Tree B", "Rock C", "House D", "Bush E" };
            var listView = new ListView(
                items,
                itemHeight: 24,
                makeItem: () =>
                {
                    var l = new Label();
                    l.style.color = Color.white;
                    return l;
                },
                bindItem: (element, index) =>
                {
                    ((Label)element).text = items[index];
                    element.style.backgroundColor = (index % 2 == 0)
                        ? new Color(0.2f, 0.2f, 0.3f, 1f)
                        : new Color(0.15f, 0.15f, 0.22f, 1f);
                });
            listView.style.height = 150;
            right.Add(listView);

            // 事件日志区
            var logTitle = new Label("Event log (updates live):");
            logTitle.style.color = Color.white;
            logTitle.style.marginTop = 10;
            right.Add(logTitle);

            _logLabel = new Label("(尚无事件，点击上方控件试试)");
            _logLabel.style.color = new Color(0.7f, 0.95f, 0.8f, 1f);
            _logLabel.style.whiteSpace = WhiteSpace.Normal;
            right.Add(_logLabel);
        }

        private void Log(string message)
        {
            _logLines.Add(message);
            while (_logLines.Count > MaxLogLines)
            {
                _logLines.RemoveAt(0);
            }
            if (_logLabel != null)
            {
                _logLabel.text = string.Join("\n", _logLines);
            }
        }
    }
}
