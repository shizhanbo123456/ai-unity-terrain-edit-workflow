#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// LayerEditor 绘画窗口（IMGUI）。
    ///
    /// 工具（工具栏切换）：
    ///   圆形画笔  单击画实心圆；拖拽从按下点到抬起点画一条等宽直线条带（无自由笔画）
    ///   矩形填充  拖拽定义对角区域，抬起时整块填充
    ///   三角填充  依次点击三个顶点，第三次点击时填充三角形
    /// 所有绘制完全覆盖目标像素（alpha=1，不模糊）；选中 layer0（透明）即擦除为过渡区域。
    /// 支持撤销（Ctrl+Z / 工具栏按钮）；导出固定路径 PNG。
    /// </summary>
    public class LayerEditorWindow : EditorWindow
    {
        private enum Tool
        {
            CircleBrush,
            RectFill,
            TriangleFill,
        }

        private const string MenuPath = "Tools/Terrain Edit Workflow/Open Layer Editor";

        private LayerMap _map;
        private List<LayerInfo> _layers;
        private int _selectedLayer;

        private Tool _tool = Tool.CircleBrush;
        private int _brushRadius = 6;

        // 窗口顶部输入的新尺寸
        private int _newWidth = 256;
        private int _newHeight = 256;

        // 交互状态
        private bool _dragging;
        private Vector2Int _dragStartPx;
        private Vector2Int _dragCurrentPx;
        private readonly List<Vector2Int> _triPoints = new List<Vector2Int>();

        // 画布显示
        private Rect _canvasRect;
        private float _canvasScale = 1f;

        private Color CurrentLayerColor
        {
            get
            {
                var c = _layers[_selectedLayer].color;
                return new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
            }
        }

        [MenuItem(MenuPath)]
        public static void Open()
        {
            GetWindow<LayerEditorWindow>("Layer Editor");
        }

        private void OnEnable()
        {
            _map = new LayerMap(_newWidth, _newHeight);
            _layers = LayerPalette.CreateDefaultLayers();
            _selectedLayer = 0;
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawCanvasArea();
            HandleCanvasEvents();
        }

        // ---------- 工具栏 ----------

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var toolNames = new[] { "圆形画笔", "矩形填充", "三角填充" };
            var newTool = GUILayout.Toolbar((int)_tool, toolNames, EditorStyles.toolbarButton);
            if (newTool != (int)_tool)
            {
                _tool = (Tool)newTool;
                _triPoints.Clear();
            }
            if (_tool == Tool.CircleBrush)
            {
                GUILayout.Space(6);
                GUILayout.Label("半径", EditorStyles.miniLabel);
                _brushRadius = EditorGUILayout.IntSlider(_brushRadius, 1, 64, GUILayout.Width(120));
            }

            GUILayout.FlexibleSpace();

            GUILayout.Label("尺寸", EditorStyles.miniLabel);
            _newWidth = Mathf.Clamp(EditorGUILayout.IntField(_newWidth, GUILayout.Width(48)), 8, 1024);
            GUILayout.Label("x", EditorStyles.miniLabel);
            _newHeight = Mathf.Clamp(EditorGUILayout.IntField(_newHeight, GUILayout.Width(48)), 8, 1024);
            if (GUILayout.Button("重置画布", EditorStyles.toolbarButton))
            {
                _map.Resize(_newWidth, _newHeight);
                _triPoints.Clear();
                Repaint();
            }

            GUILayout.Space(8);
            if (GUILayout.Button("撤销", EditorStyles.toolbarButton))
            {
                _map.Undo();
            }
            if (GUILayout.Button("保存 PNG", EditorStyles.toolbarButton))
            {
                SavePng();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void SavePng()
        {
            string full = System.IO.Path.Combine(Application.dataPath, "..", LayerMap.DefaultSaveRelativePath);
            _map.SavePng(full);
            AssetDatabase.Refresh();
            Debug.Log($"[LayerEditor] 已保存: {LayerMap.DefaultSaveRelativePath}");
        }

        // ---------- 画布 + 图层列表面板 ----------

        private void DrawCanvasArea()
        {
            EditorGUILayout.BeginHorizontal();

            _canvasRect = GUILayoutUtility.GetRect(100f, 100f,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            _canvasRect = new Rect(4f, _canvasRect.y, Mathf.Max(100f, _canvasRect.width - 4f), _canvasRect.height);

            EditorGUILayout.BeginVertical(GUILayout.Width(230));
            DrawLayerList();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            DrawCanvas();
        }

        private void DrawCanvas()
        {
            if (_map == null || Event.current.type != EventType.Repaint)
                return;

            int w = _map.Width, h = _map.Height;
            float scale = Mathf.Min(_canvasRect.width / w, _canvasRect.height / h);
            _canvasScale = scale;
            float dw = w * scale, dh = h * scale;
            var drawRect = new Rect(
                _canvasRect.x + (_canvasRect.width - dw) * 0.5f,
                _canvasRect.y + (_canvasRect.height - dh) * 0.5f,
                dw, dh);

            // 背景（透明区域显示为深色底，便于看出 layer0）
            DrawTinted(_canvasRect, new Color(0.25f, 0.25f, 0.28f, 1f));

            // 画布本体
            GUI.DrawTexture(drawRect, _map.Texture);

            // 画布边框
            DrawRectOutline(drawRect, new Color(0.8f, 0.8f, 0.8f, 1f), 1f);

            // 交互预览
            DrawInteractionPreview(drawRect);

            // 状态栏提示
            string hint = _tool == Tool.CircleBrush
                ? "左键单击画圆，拖拽画直线条带"
                : _tool == Tool.RectFill
                    ? "左键拖拽定义矩形区域"
                    : "依次点击 3 个顶点（已点 " + _triPoints.Count + " 个）";
            GUI.Label(new Rect(_canvasRect.x, _canvasRect.yMax - 20, _canvasRect.width, 20), hint);
        }

        private void DrawTinted(Rect r, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(r, EditorGUIUtility.whiteTexture);
            GUI.color = Color.white;
        }

        private void DrawRectOutline(Rect r, Color color, float thickness)
        {
            DrawTinted(new Rect(r.x, r.y, r.width, thickness), color);
            DrawTinted(new Rect(r.x, r.yMax - thickness, r.width, thickness), color);
            DrawTinted(new Rect(r.x, r.y, thickness, r.height), color);
            DrawTinted(new Rect(r.xMax - thickness, r.y, thickness, r.height), color);
        }

        private void DrawInteractionPreview(Rect drawRect)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            if (_dragging)
            {
                var color = CurrentLayerColor;
                color.a = 0.4f;
                Vector2 start = PixToScreen(_dragStartPx, drawRect);
                Vector2 cur = PixToScreen(_dragCurrentPx, drawRect);

                if (_tool == Tool.RectFill)
                {
                    var r = new Rect(
                        Mathf.Min(start.x, cur.x), Mathf.Min(start.y, cur.y),
                        Mathf.Abs(cur.x - start.x), Mathf.Abs(cur.y - start.y));
                    DrawTinted(r, color);
                }
                else if (_tool == Tool.CircleBrush)
                {
                    // 预览线宽 = 直径（2×radius），与实际 DrawLine 的条带宽一致
                    DrawThickLine(start, cur, _brushRadius * 2f * _canvasScale, color);
                }
            }

            if (_tool == Tool.TriangleFill && _triPoints.Count > 0)
            {
                var color = CurrentLayerColor;
                color.a = 0.5f;
                // 只显示已点顶点的十字标记，不画顶点间连线
                for (int i = 0; i < _triPoints.Count; i++)
                {
                    Vector2 p = PixToScreen(_triPoints[i], drawRect);
                    DrawCross(p, 5f, color);
                }
            }
        }

        private Vector2 PixToScreen(Vector2Int p, Rect drawRect)
        {
            // 与 ScreenToPix 对称的 y 翻转：像素 y=0（底行）显示在屏幕下方
            return new Vector2(drawRect.x + (p.x + 0.5f) * _canvasScale,
                drawRect.yMax - (p.y + 0.5f) * _canvasScale);
        }

        private void DrawCross(Vector2 p, float half, Color color)
        {
            DrawTinted(new Rect(p.x - half, p.y - 1, half * 2, 2), color);
            DrawTinted(new Rect(p.x - 1, p.y - half, 2, half * 2), color);
        }

        private void DrawThickLine(Vector2 a, Vector2 b, float thickness, Color color)
        {
            Vector2 dir = b - a;
            float len = dir.magnitude;
            if (len < 0.001f)
            {
                DrawTinted(new Rect(a.x - thickness * 0.5f, a.y - thickness * 0.5f, thickness, thickness), color);
                return;
            }
            int segs = Mathf.Max(1, Mathf.CeilToInt(len / (thickness * 0.5f)));
            for (int i = 0; i <= segs; i++)
            {
                float t = (float)i / segs;
                float px = Mathf.Lerp(a.x, b.x, t);
                float py = Mathf.Lerp(a.y, b.y, t);
                DrawTinted(new Rect(px - thickness * 0.5f, py - thickness * 0.5f, thickness, thickness), color);
            }
        }

        // ---------- 图层列表 ----------

        private void DrawLayerList()
        {
            EditorGUILayout.LabelField("图层（点选颜色绘制）", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            for (int i = 0; i < _layers.Count; i++)
            {
                var layer = _layers[i];
                bool isSelected = i == _selectedLayer;

                EditorGUILayout.BeginHorizontal();

                bool nowSelected = GUILayout.Toggle(isSelected, GUIContent.none, GUILayout.Width(18));
                if (nowSelected != isSelected)
                {
                    _selectedLayer = i;
                    _triPoints.Clear();
                }

                var swatchRect = GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20), GUILayout.Height(20));
                Color swatch = new Color(layer.color.r / 255f, layer.color.g / 255f, layer.color.b / 255f, 1f);
                if (i == 0)
                {
                    DrawTinted(swatchRect, new Color(0.7f, 0.7f, 0.7f, 1f));
                    DrawTinted(swatchRect, new Color(0.4f, 0.4f, 0.4f, 0.5f));
                }
                else
                {
                    DrawTinted(swatchRect, swatch);
                }
                DrawRectOutline(swatchRect, isSelected ? new Color(1f, 0.8f, 0.2f, 1f) : new Color(0f, 0f, 0f, 0.4f), 1f);

                string newName = EditorGUILayout.TextField(layer.name);
                if (newName != layer.name)
                    layer.name = newName;

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"当前层: {_layers[_selectedLayer].name}", EditorStyles.miniLabel);
        }

        // ---------- 鼠标事件 ----------

        private void HandleCanvasEvents()
        {
            if (_map == null)
                return;

            var e = Event.current;
            bool inCanvas = _canvasRect.Contains(e.mousePosition);

            if (e.type == EventType.KeyDown && e.control && e.keyCode == KeyCode.Z)
            {
                if (_map.Undo())
                    Repaint();
                e.Use();
                return;
            }

            if (!inCanvas || e.button != 0)
                return;

            if (e.type == EventType.MouseDown)
            {
                if (_tool == Tool.TriangleFill)
                {
                    Vector2Int px = ScreenToPix(e.mousePosition);
                    _triPoints.Add(px);
                    if (_triPoints.Count == 3)
                    {
                        var a = _triPoints[0];
                        var b = _triPoints[1];
                        var c = _triPoints[2];
                        _map.FillTriangle(a.x, a.y, b.x, b.y, c.x, c.y, _layers[_selectedLayer].color);
                        _triPoints.Clear();
                    }
                    Repaint();
                    e.Use();
                }
                else
                {
                    _dragging = true;
                    _dragStartPx = ScreenToPix(e.mousePosition);
                    _dragCurrentPx = _dragStartPx;
                    Repaint();
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && _dragging)
            {
                _dragCurrentPx = ScreenToPix(e.mousePosition);
                Repaint();
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _dragging)
            {
                _dragCurrentPx = ScreenToPix(e.mousePosition);
                var color = _layers[_selectedLayer].color;

                if (_tool == Tool.CircleBrush)
                {
                    if (_dragStartPx == _dragCurrentPx)
                        _map.FillCircle(_dragStartPx.x, _dragStartPx.y, _brushRadius, color);
                    else
                        _map.DrawLine(_dragStartPx.x, _dragStartPx.y, _dragCurrentPx.x, _dragCurrentPx.y,
                            _brushRadius, color);
                }
                else if (_tool == Tool.RectFill)
                {
                    _map.FillRect(_dragStartPx.x, _dragStartPx.y, _dragCurrentPx.x, _dragCurrentPx.y, color);
                }

                _dragging = false;
                Repaint();
                e.Use();
            }
        }

        private Vector2Int ScreenToPix(Vector2 screen)
        {
            int w = _map.Width, h = _map.Height;
            float dw = w * _canvasScale, dh = h * _canvasScale;
            float ox = _canvasRect.x + (_canvasRect.width - dw) * 0.5f;
            float oy = _canvasRect.y + (_canvasRect.height - dh) * 0.5f;
            int px = Mathf.FloorToInt((screen.x - ox) / _canvasScale);
            // y 翻转：像素数组 y=0 是图片底行（Texture2D 原点在左下），而屏幕 y 向下。
            // 屏幕顶部(y 小)应对应像素 y 大(图片顶行)，否则显示会垂直镜像。
            int py = h - 1 - Mathf.FloorToInt((screen.y - oy) / _canvasScale);
            px = Mathf.Clamp(px, 0, w - 1);
            py = Mathf.Clamp(py, 0, h - 1);
            return new Vector2Int(px, py);
        }
    }
}
#endif
