#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 地形贴图工作流窗口（改造自 LayerEditor 绘画窗口）。
    ///
    /// 四个子界面：
    ///   ① 配置修改  全局参数（TerrainPaintConfig）+ 逐层参数（LayerConfigSO 的贴图/道路参数；
    ///                层名与颜色只读，需在 Inspector 中修改对应 SO）
    ///   ② 绘画      层次图绘制（原 LayerEditor 全部功能；图层颜色/名称从层级 SO 读取）
    ///   ③ 贴图编辑  TerrainLayer 列表 + layer×TerrainLayer 矩阵（自然/道路双复选框）；距离场/游走计算下阶段实现
    ///   ④ 应用      占位（下阶段实现：传入 Terrain 并烘焙 splatmap）
    ///
    /// 窗口本身不存储持久数据：所有信息从总 SO（TerrainPaintProjectSO）加载，
    /// 修改直接写入 SO。创建新地形配置时自动创建 TerrainGeneratorConfigs/&lt;名称&gt;/ 子文件夹
    /// 及其中的总 SO + 16 个层级 SO。
    /// </summary>
    public class LayerEditorWindow : EditorWindow
    {
        private enum Tool
        {
            CircleBrush,
            RectFill,
            TriangleFill,
        }

        private enum WorkflowStep
        {
            Config,
            Paint,
            Texture,
            Apply,
        }

        /// <summary>配置根目录（Assets 相对路径）；每个配置一个子文件夹。</summary>
        public const string ConfigRootDirRelative =
            "Assets/unity-terrain-edit-workflow/LayerEditor/TerrainGeneratorConfigs";

        private const string PrefsLastProject = "AiTerrainWorkflow.LastPaintProject";
        private const int LayerCount = 16;

        private TerrainPaintProjectSO _project;
        private WorkflowStep _step = WorkflowStep.Config;

        // 创建配置 UI
        private bool _creating;
        private string _newConfigName = "";

        // 配置修改子界面 UI 状态
        private Vector2 _configScroll;
        private readonly List<bool> _layerFoldouts = new List<bool>();

        // 绘画子界面状态
        private Tool _tool = Tool.CircleBrush;
        private bool _erase;
        private int _brushRadius = 6;
        private LayerMap _map;
        private int _selectedLayer;
        private int _newWidth = 256;
        private int _newHeight = 256;
        private bool _dragging;
        private int _canvasHotControl;
        private Vector2Int _dragStartPx;
        private Vector2Int _dragCurrentPx;
        private readonly List<Vector2Int> _triPoints = new List<Vector2Int>();
        private Rect _canvasRect;
        private float _canvasScale = 1f;

        // 贴图编辑子界面 UI 状态
        private Vector2 _texScroll;
        private Texture2D _resultPreview;
        private int[] _layerIdsCache;

        private bool HasProject => _project != null;

        private Color32 CurrentLayerColor32
        {
            get
            {
                if (_erase || _project == null || _project.layers.Count == 0)
                    return LayerPalette.Transparent;
                return _project.layers[_selectedLayer].color;
            }
        }

        private Color CurrentLayerColor
        {
            get
            {
                var c = CurrentLayerColor32;
                return new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
            }
        }

        private string CurrentLayerName
        {
            get
            {
                if (_project == null || _project.layers.Count == 0)
                    return "";
                var l = _project.layers[_selectedLayer];
                return l != null ? l.layerName : "";
            }
        }

        [MenuItem("Tools/Terrain Edit Workflow/Open Terrain Paint Workflow")]
        public static void Open()
        {
            GetWindow<LayerEditorWindow>("Terrain Paint Workflow");
        }

        private void OnEnable()
        {
            string path = EditorPrefs.GetString(PrefsLastProject, "");
            if (!string.IsNullOrEmpty(path))
                _project = AssetDatabase.LoadAssetAtPath<TerrainPaintProjectSO>(path);
            if (_project != null)
                _resultPreview = _project.resultTexture;
        }

        // ---------- 主布局 ----------

        private void OnGUI()
        {
            DrawProjectBar();
            if (!HasProject)
            {
                DrawNoProject();
                return;
            }

            switch (_step)
            {
                case WorkflowStep.Config: DrawConfigView(); break;
                case WorkflowStep.Paint: DrawPaintView(); break;
                case WorkflowStep.Texture: DrawTextureView(); break;
                case WorkflowStep.Apply: DrawApplyView(); break;
            }
        }

        private void DrawProjectBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("配置", EditorStyles.miniLabel);
            var newProject = (TerrainPaintProjectSO)EditorGUILayout.ObjectField(
                _project, typeof(TerrainPaintProjectSO), false, GUILayout.Width(280));
            if (newProject != _project)
            {
                SavePaintMapIfAny();
                _project = newProject;
                _resultPreview = _project != null ? _project.resultTexture : null;
                _layerIdsCache = null;
                RememberProject();
                Repaint();
            }
            if (GUILayout.Button("创建新地形配置", EditorStyles.toolbarButton))
                _creating = !_creating;
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (_creating)
                DrawCreateConfig();

            if (HasProject)
            {
                var names = new[] { "配置修改", "绘画", "贴图编辑", "应用" };
                int newStep = GUILayout.Toolbar((int)_step, names);
                if (newStep != (int)_step)
                {
                    SavePaintMapIfAny();
                    _step = (WorkflowStep)newStep;
                }
                EditorGUILayout.Space(4);
            }
        }

        private void DrawCreateConfig()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("名称", EditorStyles.miniLabel);
            _newConfigName = EditorGUILayout.TextField(_newConfigName);
            if (GUILayout.Button("创建"))
            {
                if (TryCreateProject(_newConfigName))
                    _creating = false;
            }
            if (GUILayout.Button("取消"))
                _creating = false;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawNoProject()
        {
            EditorGUILayout.HelpBox(
                "未选择地形配置。\n\n请在上方 ObjectField 中选择一个已创建的配置，\n" +
                "或点击「创建新地形配置」新建一个（会自动创建子文件夹、总 SO 与 16 个层级 SO）。",
                MessageType.Info);
        }

        // ---------- 创建配置 ----------

        private bool TryCreateProject(string name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                EditorUtility.DisplayDialog("创建配置", "请输入配置名称", "确定");
                return false;
            }

            string dirRel = ConfigRootDirRelative + "/" + name;
            string dirFull = Path.Combine(Application.dataPath, "..", dirRel);
            if (Directory.Exists(dirFull))
            {
                EditorUtility.DisplayDialog("创建配置", $"已存在同名配置: {name}", "确定");
                return false;
            }
            Directory.CreateDirectory(dirFull);

            var project = ScriptableObject.CreateInstance<TerrainPaintProjectSO>();
            project.name = name;
            for (int i = 0; i < LayerCount; i++)
            {
                var layer = ScriptableObject.CreateInstance<LayerConfigSO>();
                layer.color = LayerPalette.PresetColors[i];
                layer.layerName = LayerPalette.PresetDefaultNames[i];
                string layerPath = $"{dirRel}/Layer_{i + 1:00}.asset";
                AssetDatabase.CreateAsset(layer, layerPath);
                project.layers.Add(layer);
                project.usageMatrix.Add(new LayerTerrainUsage());
            }
            AssetDatabase.CreateAsset(project, $"{dirRel}/{name}.asset");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            _project = project;
            RememberProject();
            _step = WorkflowStep.Config;
            _resultPreview = null;
            _layerIdsCache = null;
            Debug.Log($"[Terrain Paint Workflow] 已创建配置: {dirRel}");
            return true;
        }

        private void RememberProject()
        {
            if (_project != null)
                EditorPrefs.SetString(PrefsLastProject, AssetDatabase.GetAssetPath(_project));
            else
                EditorPrefs.DeleteKey(PrefsLastProject);
        }

        // ---------- ① 配置修改 ----------

        private void EnsureLayerFoldouts()
        {
            int n = _project != null ? _project.layers.Count : 0;
            while (_layerFoldouts.Count < n) _layerFoldouts.Add(false);
            if (_layerFoldouts.Count > n) _layerFoldouts.RemoveRange(n, _layerFoldouts.Count - n);
        }

        private void DrawConfigView()
        {
            EnsureLayerFoldouts();
            _configScroll = EditorGUILayout.BeginScrollView(_configScroll);

            EditorGUILayout.LabelField("全局配置", EditorStyles.boldLabel);
            DrawGlobalConfig();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("层级配置（名称/颜色只读，请在 Inspector 修改对应 SO）", EditorStyles.boldLabel);
            for (int i = 0; i < _project.layers.Count; i++)
            {
                var layer = _project.layers[i];
                if (layer == null) continue;
                DrawLayerConfig(i, layer);
            }

            EditorGUILayout.EndScrollView();
            EditorUtility.SetDirty(_project);
        }

        private void DrawGlobalConfig()
        {
            var cfg = _project.config;
            cfg.roadStep = Mathf.Max(0.01f, EditorGUILayout.FloatField("Road Step (m)", cfg.roadStep));
            cfg.walkStartTries = Mathf.Max(1, EditorGUILayout.IntField("Walk Start Tries", cfg.walkStartTries));
            cfg.walkCandidateCount = Mathf.Max(1, EditorGUILayout.IntField("Walk Candidate Count", cfg.walkCandidateCount));
            cfg.startCoverStopSamples = Mathf.Max(1, EditorGUILayout.IntField("Start Cover Stop Samples", cfg.startCoverStopSamples));
            cfg.walkSeed = EditorGUILayout.IntField("Walk Seed", cfg.walkSeed);
            cfg.maxStepsPerPath = Mathf.Max(1, EditorGUILayout.IntField("Max Steps Per Path", cfg.maxStepsPerPath));
            cfg.gApplySpacing = Mathf.Max(0.01f, EditorGUILayout.FloatField("G Apply Spacing / 防卷曲 (m)", cfg.gApplySpacing));
            cfg.noiseScale = Mathf.Max(0.01f, EditorGUILayout.FloatField("Noise Scale (m)", cfg.noiseScale));
            cfg.worldPerPixel = Mathf.Max(0.001f, EditorGUILayout.FloatField("World Per Pixel (m/px)", cfg.worldPerPixel));
        }

        private void DrawLayerConfig(int index, LayerConfigSO layer)
        {
            EditorGUILayout.BeginHorizontal();
            bool open = _layerFoldouts[index];

            var swatchRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
            var c = new Color(layer.color.r / 255f, layer.color.g / 255f, layer.color.b / 255f, 1f);
            DrawTinted(swatchRect, c);

            open = EditorGUILayout.Foldout(open, $"Layer{index + 1}  {layer.layerName}", true);
            _layerFoldouts[index] = open;
            EditorGUILayout.EndHorizontal();

            if (!open)
                return;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("颜色 / 名称请在 Inspector 中修改", EditorStyles.miniLabel);

            EditorGUILayout.LabelField("自然地面贴图", EditorStyles.boldLabel);
            DrawTextureList(layer.naturalTextures, "自然");
            layer.naturalSeed = EditorGUILayout.IntField("自然贴图种子", layer.naturalSeed);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("道路贴图", EditorStyles.boldLabel);
            DrawTextureList(layer.roadTextures, "道路");
            layer.roadSeed = EditorGUILayout.IntField("道路贴图种子", layer.roadSeed);

            EditorGUILayout.Space(4);
            layer.generateRoad = EditorGUILayout.Toggle("生成道路", layer.generateRoad);
            layer.roadWidth = Mathf.Max(0.01f, EditorGUILayout.FloatField("Road Width (m)", layer.roadWidth));
            layer.roadSpacingMin = Mathf.Max(0.01f, EditorGUILayout.FloatField("Road Spacing Min (m)", layer.roadSpacingMin));
            layer.roadFinalRemap = EditorGUILayout.CurveField("Road Final Remap", layer.roadFinalRemap);

            EditorGUILayout.LabelField("可邻接层级（组合分组）", EditorStyles.boldLabel);
            DrawIntList(layer.adjLayers);

            EditorGUILayout.EndVertical();
            EditorUtility.SetDirty(layer);
        }

        private void DrawTextureList(List<Texture2D> list, string label)
        {
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                list[i] = (Texture2D)EditorGUILayout.ObjectField(
                    $"{label}[{i}]", list[i], typeof(Texture2D), false);
                if (GUILayout.Button("-", GUILayout.Width(20)))
                    list.RemoveAt(i--);
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button($"+ 添加{label}贴图"))
                list.Add(null);
        }

        private void DrawIntList(List<int> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                list[i] = Mathf.Clamp(EditorGUILayout.IntField($"层级[{i}]", list[i]), 0, LayerCount - 1);
                if (GUILayout.Button("-", GUILayout.Width(20)))
                    list.RemoveAt(i--);
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ 添加邻接层"))
                list.Add(0);
        }

        // ---------- ② 绘画 ----------

        private void DrawPaintView()
        {
            EnsurePaintMap();
            DrawPaintToolbar();
            DrawCanvasArea();
            HandleCanvasEvents();
        }

        private void EnsurePaintMap()
        {
            if (_map != null)
                return;

            if (_project.layerMap != null)
            {
                string path = AssetDatabase.GetAssetPath(_project.layerMap);
                string full = Path.Combine(Application.dataPath, "..", path);
                _map = new LayerMap(2, 2);
                if (_map.LoadPng(full))
                {
                    _newWidth = _map.Width;
                    _newHeight = _map.Height;
                }
                else
                {
                    Debug.LogWarning($"[Terrain Paint Workflow] 无法加载层次图 {path}，新建空白画布");
                    _map = new LayerMap(_newWidth, _newHeight);
                }
            }
            else
            {
                _map = new LayerMap(_newWidth, _newHeight);
            }
        }

        private void SavePaintMapIfAny()
        {
            if (_project == null || _map == null)
                return;
            string dirRel = Path.GetDirectoryName(AssetDatabase.GetAssetPath(_project))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dirRel))
                return;
            string fileRel = dirRel + "/layerMap.png";
            string full = Path.Combine(Application.dataPath, "..", fileRel);
            _map.SavePng(full);
            AssetDatabase.Refresh();
            _project.layerMap = AssetDatabase.LoadAssetAtPath<Texture2D>(fileRel);
            EditorUtility.SetDirty(_project);
            _layerIdsCache = null;
        }

        private void DrawPaintToolbar()
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

            _erase = GUILayout.Toggle(_erase, "擦除", EditorStyles.toolbarButton);
            GUILayout.Space(8);

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
                _map.Undo();
            if (GUILayout.Button("保存层次图", EditorStyles.toolbarButton))
            {
                SavePaintMapIfAny();
                Repaint();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void DrawCanvasArea()
        {
            EditorGUILayout.BeginHorizontal();

            const float canvasPadding = 16f;
            var raw = GUILayoutUtility.GetRect(100f, 100f,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            _canvasRect = new Rect(
                raw.x + canvasPadding,
                raw.y + canvasPadding,
                Mathf.Max(100f, raw.width - canvasPadding * 2f),
                Mathf.Max(100f, raw.height - canvasPadding * 2f));

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

            DrawTinted(_canvasRect, new Color(0.25f, 0.25f, 0.28f, 1f));
            GUI.DrawTexture(drawRect, _map.Texture);
            DrawRectOutline(drawRect, new Color(0.8f, 0.8f, 0.8f, 1f), 1f);
            DrawInteractionPreview(drawRect);

            string hint = _tool == Tool.CircleBrush
                ? (_erase ? "擦除：单击画圆，拖拽画直线条带" : "左键单击画圆，拖拽画直线条带")
                : _tool == Tool.RectFill
                    ? "左键拖拽定义矩形区域"
                    : "依次点击 3 个顶点（已点 " + _triPoints.Count + " 个）";
            GUI.Label(new Rect(_canvasRect.x, _canvasRect.yMax - 20, _canvasRect.width, 20), hint);
        }

        private void DrawLayerList()
        {
            EditorGUILayout.LabelField("层级（点选绘制）", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            for (int i = 0; i < _project.layers.Count; i++)
            {
                var layer = _project.layers[i];
                if (layer == null) continue;
                bool isSelected = i == _selectedLayer;

                EditorGUILayout.BeginHorizontal();

                bool nowSelected = GUILayout.Toggle(isSelected, GUIContent.none, GUILayout.Width(18));
                if (nowSelected != isSelected)
                {
                    _selectedLayer = i;
                    _triPoints.Clear();
                }

                var swatchRect = GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20), GUILayout.Height(20));
                var swatch = new Color(layer.color.r / 255f, layer.color.g / 255f, layer.color.b / 255f, 1f);
                DrawTinted(swatchRect, swatch);
                DrawRectOutline(swatchRect,
                    isSelected ? new Color(1f, 0.8f, 0.2f, 1f) : new Color(0f, 0f, 0f, 0.4f), 1f);

                EditorGUILayout.LabelField($"Layer{i + 1}", EditorStyles.miniLabel, GUILayout.Width(52));
                EditorGUILayout.LabelField(layer.layerName, EditorStyles.miniLabel, GUILayout.Width(130));

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                _erase ? "当前: 擦除（透明）" : $"当前: Layer{_selectedLayer + 1} {CurrentLayerName}",
                EditorStyles.miniLabel);
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
                    DrawThickLine(start, cur, _brushRadius * 2f * _canvasScale, color);
                }
            }

            if (_tool == Tool.TriangleFill && _triPoints.Count > 0)
            {
                var color = CurrentLayerColor;
                color.a = 0.5f;
                for (int i = 0; i < _triPoints.Count; i++)
                {
                    Vector2 p = PixToScreen(_triPoints[i], drawRect);
                    DrawCross(p, 5f, color);
                }
            }
        }

        // ---------- ③ 贴图编辑 ----------

        private void DrawTextureView()
        {
            _texScroll = EditorGUILayout.BeginScrollView(_texScroll);

            EditorGUILayout.LabelField("TerrainLayer 列表（贴图矩阵的列）", EditorStyles.boldLabel);
            for (int i = 0; i < _project.terrainLayers.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _project.terrainLayers[i] = (TerrainLayer)EditorGUILayout.ObjectField(
                    $"TerrainLayer[{i}]", _project.terrainLayers[i], typeof(TerrainLayer), false);
                if (GUILayout.Button("-", GUILayout.Width(20)))
                {
                    _project.terrainLayers.RemoveAt(i);
                    SyncMatrix();
                }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ 添加 TerrainLayer"))
            {
                _project.terrainLayers.Add(null);
                SyncMatrix();
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("同步矩阵尺寸（对齐层数与 TerrainLayer 数）"))
                SyncMatrix();

            EditorGUILayout.Space(8);
            DrawUsageMatrix();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("距离场 + 路网计算（RGB 三通道）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "点击计算：按组合层级分组 → 距离场 R（maxD 自动归一化）→ 随机游走生成 G/B。\n" +
                "结果合成一张 RGB 图：R=距离场（红），G=占用/间隔（绿），B=路面掩码（蓝）。",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("计算距离场 + 路网", GUILayout.Width(180)))
                ComputeRgb();
            if (GUILayout.Button("重新解析层次图", GUILayout.Width(140)))
                _layerIdsCache = null;
            EditorGUILayout.EndHorizontal();

            if (_resultPreview != null)
            {
                EditorGUILayout.Space(6);
                float previewW = Mathf.Min(320f, position.width - 40f);
                float previewH = previewW * (float)_resultPreview.height / Mathf.Max(1, _resultPreview.width);
                GUILayout.Label(_resultPreview, GUILayout.Width(previewW), GUILayout.Height(previewH));
            }

            EditorGUILayout.EndScrollView();
            EditorUtility.SetDirty(_project);
        }

        private void ComputeRgb()
        {
            if (_project.layerMap == null)
            {
                EditorUtility.DisplayDialog("计算失败", "尚无层次图。请先在「绘画」子界面绘制并保存层次图。", "确定");
                return;
            }
            if (_project.layers == null || _project.layers.Count == 0)
            {
                EditorUtility.DisplayDialog("计算失败", "配置中没有层级（layers 为空）。", "确定");
                return;
            }

            int[] ids = _layerIdsCache;
            if (ids == null || ids.Length != _project.layerMap.width * _project.layerMap.height)
            {
                ids = TerrainRoadGen.ParseLayerIds(_project.layerMap, _project.layers);
                _layerIdsCache = ids;
            }

            var tex = TerrainRoadGen.ComputeAll(_project, ids, out _, out _, out _);

            // 保存结果图到配置文件夹
            string dirRel = Path.GetDirectoryName(AssetDatabase.GetAssetPath(_project))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dirRel))
                return;
            string fileRel = dirRel + "/result_RGB.png";
            string full = Path.Combine(Application.dataPath, "..", fileRel);
            File.WriteAllBytes(full, tex.EncodeToPNG());
            AssetDatabase.Refresh();
            _project.resultTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(fileRel);
            EditorUtility.SetDirty(_project);
            _resultPreview = _project.resultTexture;

            Debug.Log($"[Terrain Paint Workflow] RGB 计算完成，已保存 {fileRel}" +
                      $"（{_project.layers.Count} 层 / {_project.groupMaxD?.Length ?? 0} 个组合层）");
            Repaint();
        }

        private void SyncMatrix()
        {
            if (_project == null)
                return;
            int rows = _project.layers.Count;
            int cols = _project.terrainLayers.Count;
            while (_project.usageMatrix.Count < rows)
                _project.usageMatrix.Add(new LayerTerrainUsage());
            if (_project.usageMatrix.Count > rows)
                _project.usageMatrix.RemoveRange(rows, _project.usageMatrix.Count - rows);
            for (int r = 0; r < rows; r++)
            {
                var row = _project.usageMatrix[r];
                if (row == null)
                {
                    row = new LayerTerrainUsage();
                    _project.usageMatrix[r] = row;
                }
                ResizeBools(row.natural, cols);
                ResizeBools(row.road, cols);
            }
            EditorUtility.SetDirty(_project);
        }

        private static void ResizeBools(List<bool> list, int count)
        {
            while (list.Count < count) list.Add(false);
            if (list.Count > count) list.RemoveRange(count, list.Count - count);
        }

        private void DrawUsageMatrix()
        {
            int rows = _project.layers.Count;
            int cols = _project.terrainLayers.Count;
            EditorGUILayout.LabelField($"layer × TerrainLayer 矩阵（{rows} 层 × {cols} 列）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("每格两个复选框：左=自然地面启用，右=道路启用", MessageType.None);

            const float labelW = 110f;
            const float cellW = 64f;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(labelW);
            for (int c = 0; c < cols; c++)
            {
                var tl = _project.terrainLayers[c];
                string tn = tl != null ? tl.name : $"TL{c}";
                EditorGUILayout.LabelField(tn, EditorStyles.miniLabel, GUILayout.Width(cellW));
            }
            EditorGUILayout.EndHorizontal();

            for (int r = 0; r < rows; r++)
            {
                if (r >= _project.usageMatrix.Count)
                    break;
                var row = _project.usageMatrix[r];
                var layer = r < _project.layers.Count ? _project.layers[r] : null;
                string rowName = layer != null ? $"{r + 1}.{layer.layerName}" : $"Layer{r + 1}";

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(rowName, EditorStyles.miniLabel, GUILayout.Width(labelW));
                for (int c = 0; c < cols; c++)
                {
                    EditorGUILayout.BeginHorizontal(GUILayout.Width(cellW));
                    bool n = c < row.natural.Count && row.natural[c];
                    bool rd = c < row.road.Count && row.road[c];
                    n = EditorGUILayout.Toggle(n, GUILayout.Width(28));
                    rd = EditorGUILayout.Toggle(rd, GUILayout.Width(28));
                    if (c < row.natural.Count) row.natural[c] = n;
                    if (c < row.road.Count) row.road[c] = rd;
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        // ---------- ④ 应用（占位） ----------

        private void DrawApplyView()
        {
            EditorGUILayout.HelpBox(
                "「应用」子界面将在下一阶段实现：\n" +
                "· 传入一个 Terrain\n" +
                "· 将矩阵中启用的 TerrainLayer 写入 Terrain\n" +
                "· 按 R/G/B 结果烘焙 splatmap",
                MessageType.Info);
        }

        // ---------- 绘画工具函数（沿用原实现） ----------

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

        private Vector2 PixToScreen(Vector2Int p, Rect drawRect)
        {
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

            if (e.button != 0)
                return;

            if (e.type == EventType.MouseDown)
            {
                if (!inCanvas)
                    return;

                if (_tool == Tool.TriangleFill)
                {
                    Vector2Int px = ScreenToPix(e.mousePosition);
                    _triPoints.Add(px);
                    if (_triPoints.Count == 3)
                    {
                        var a = _triPoints[0];
                        var b = _triPoints[1];
                        var c = _triPoints[2];
                        _map.FillTriangle(a.x, a.y, b.x, b.y, c.x, c.y, CurrentLayerColor32);
                        _triPoints.Clear();
                    }
                    Repaint();
                    e.Use();
                }
                else
                {
                    _canvasHotControl = GUIUtility.GetControlID(FocusType.Passive);
                    GUIUtility.hotControl = _canvasHotControl;
                    _dragging = true;
                    _dragStartPx = ScreenToPix(e.mousePosition);
                    _dragCurrentPx = _dragStartPx;
                    Repaint();
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && _dragging
                     && GUIUtility.hotControl == _canvasHotControl)
            {
                _dragCurrentPx = ScreenToPix(e.mousePosition);
                Repaint();
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _dragging
                     && GUIUtility.hotControl == _canvasHotControl)
            {
                _dragCurrentPx = ScreenToPix(e.mousePosition);
                var color = CurrentLayerColor32;

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
                GUIUtility.hotControl = 0;
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
            int py = h - 1 - Mathf.FloorToInt((screen.y - oy) / _canvasScale);
            px = Mathf.Clamp(px, 0, w - 1);
            py = Mathf.Clamp(py, 0, h - 1);
            return new Vector2Int(px, py);
        }
    }
}
#endif
