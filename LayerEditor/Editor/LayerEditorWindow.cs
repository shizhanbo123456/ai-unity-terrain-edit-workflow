#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using AiTerrainWorkflow.Editor;
using UnityEditor;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 地形贴图工作流窗口（改造自 LayerEditor 绘画窗口）。
    ///
    /// 顶层九个子界面（顶部工具栏靠右）：工作流配置 / 区域编辑 / 高度编辑 / 道路编辑 / 贴图编辑 / 散布编辑 / 摆件编辑 / 定点编辑 / 应用。
    /// 「工作流配置」整页显示：全部已持久化 MapData 预览、Layer 数量（2~16）、各层颜色/名称（Layer0 透明锁定）。
    /// 「散布编辑」按多个 ScatterConfigSO 生成组配置均匀散布规则。
    /// 其余编辑子界面为左右分栏布局：
    ///   左栏（窄）：全局配置（上，该子界面专属的全局字段）+ 层级配置（下，逐层折叠），整体共同滚动
    ///   右栏（宽）：信息生成（区域编辑=画布绘制；高度编辑=烘焙高度图；道路编辑=距离场/路网计算）
    ///
    /// 窗口本身不存储持久数据：所有信息从总 SO（TerrainPaintProjectSO）加载，
    /// 修改直接写入 SO。创建新地形配置时自动创建 TerrainGeneratorConfigs/&lt;名称&gt;/ 子文件夹
    /// 及其中的总 SO + 层级 SO。
    /// </summary>
    public class LayerEditorWindow : EditorWindow
    {
        private enum Tool
        {
            CircleBrush,
            RectFill,
            TriangleFill,
            PolygonFill,
            EllipseFill,
            SectorFill,
        }

        /// <summary>顶层八个子界面，按工作流从配置、编辑到应用依次排列。</summary>
        private enum MainTab
        {
            WorkflowConfig,
            AreaEdit,
            HeightEdit,
            Road,
            Texture,
            ScatterEdit,
            PropEdit,
            FixedPointEdit,
            Apply,
        }

        /// <summary>配置根目录（Assets 相对路径）；每个配置一个子文件夹。</summary>
        public const string ConfigRootDirRelative =
            "Assets/ai-unity-terrain-edit-workflow/TerrainGeneratorConfigs";
        private const string LegacyScriptConfigRootDirRelative =
            "Assets/ai-unity-terrain-edit-workflow/LayerEditor/TerrainGeneratorConfigs";
        private const string LegacyProjectConfigRootDirRelative = "Assets/TerrainGeneratorConfigs";

        private const string PrefsLastProject = "AiTerrainWorkflow.LastPaintProject";

        private TerrainPaintProjectSO _project;
        private MainTab _mainTab = MainTab.WorkflowConfig;

        /// <summary>工作流配置中填入的 Terrain（仅窗口内临时，不保存到配置 SO）。</summary>
        private Terrain _terrainField;

        // 应用阶段（仅窗口会话状态）：必须为从高度开始的连续前缀。
        private readonly bool[] _applyStages = { true, true, true, true, true };

        // 备用 Prefab 批量创建（仅窗口会话状态）。
        private readonly List<GameObject> _candidatePrefabSources = new List<GameObject>();
        private BillboardMode _candidateBillboardMode;
        private bool _candidateTwoPointHeightAdaptation;
        [Tooltip("LOD0→Billboard 的屏幕相对高度切换阈值（0~1）；仅处理时生效，不写入工作流配置")]
        private float _candidateLodTransition = 0.1f;

        // 创建配置 UI
        private bool _creating;
        private string _newConfigName = "";
        private int _createResolution = 512;

        // 左栏配置滚动状态（全局配置 + 层级配置 共同滚动）
        private Vector2 _configScroll;
        private readonly List<bool> _layerFoldouts = new List<bool>();

        // 散布生成组界面状态
        private Vector2 _scatterScroll;
        private readonly List<bool> _scatterFoldouts = new List<bool>();
        private Vector2 _propScroll;
        private readonly List<bool> _propFoldouts = new List<bool>();
        private Vector2 _fixedPointScroll;
        private readonly List<bool> _fixedPointFoldouts = new List<bool>();

        // 区域编辑子界面状态
        private Tool _tool = Tool.CircleBrush;
        private bool _erase;
        private int _brushRadius = 6;
        private LayerMap _map;
        private int _selectedLayer;
        private bool _dragging;
        private int _canvasHotControl;
        private Vector2Int _dragStartPx;
        private Vector2Int _dragCurrentPx;
        private readonly List<Vector2Int> _triPoints = new List<Vector2Int>();
        private Rect _canvasRect;
        private float _canvasScale = 1f;

        // 贴图/高度编辑子界面 UI 状态（预览仅内存，不落盘）
        private Vector2 _texScroll;
        private Texture2D _resultPreview;
        private Texture2D _heightPreview;
        private int[] _layerIdsCache;
        private float _lastHeightMin;
        private float _lastHeightMax;
        private float _lastRMax;
        private readonly Dictionary<string, Texture2D> _mapDataPreviews =
            new Dictionary<string, Texture2D>();
        private readonly Dictionary<string, Vector2> _mapDataPreviewRanges =
            new Dictionary<string, Vector2>();
        private TerrainPaintProjectSO _mapDataPreviewProject;

        private bool HasProject => _project != null;

        private Color32 CurrentLayerColor32
        {
            get
            {
                if (_erase || _project == null || _project.layers.Count == 0
                    || _selectedLayer < 0 || _selectedLayer >= _project.layers.Count)
                    return LayerPalette.Transparent;
                return _project.layers[_selectedLayer].color;
            }
        }

        private int CurrentPaintLayerIndex => _erase ? 0 : _selectedLayer;

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
                if (_project == null || _project.layers.Count == 0
                    || _selectedLayer < 0 || _selectedLayer >= _project.layers.Count)
                    return "";
                var l = _project.layers[_selectedLayer];
                return l != null ? l.layerName : "";
            }
        }

        // 菜单入口统一在 Editor/TerrainEditWorkflowMenu.cs（此处仅保留 Open 供其调用，避免同名菜单项重复注册）
        public static void Open()
        {
            GetWindow<LayerEditorWindow>("Terrain Paint Workflow");
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui -= DrawRoadAnchorSceneHandles;
            SceneView.duringSceneGui += DrawRoadAnchorSceneHandles;
            string path = EditorPrefs.GetString(PrefsLastProject, "");
            if (path.StartsWith(LegacyScriptConfigRootDirRelative + "/"))
            {
                path = ConfigRootDirRelative + path.Substring(LegacyScriptConfigRootDirRelative.Length);
                EditorPrefs.SetString(PrefsLastProject, path);
            }
            else if (path.StartsWith(LegacyProjectConfigRootDirRelative + "/"))
            {
                path = ConfigRootDirRelative + path.Substring(LegacyProjectConfigRootDirRelative.Length);
                EditorPrefs.SetString(PrefsLastProject, path);
            }
            if (!string.IsNullOrEmpty(path))
                _project = AssetDatabase.LoadAssetAtPath<TerrainPaintProjectSO>(path);
            if (_project != null)
            {
                EnsurePaintMap();
                LoadResultPreview();
                RebuildMapDataPreviews();
            }
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DrawRoadAnchorSceneHandles;
            SavePaintMapIfAny();
            ClearMapDataPreviews();
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

            // 工作流配置：无分栏，直接显示
            if (_mainTab == MainTab.WorkflowConfig)
            {
                DrawWorkflowConfigView();
                return;
            }

            // 应用是工作流最后一步，单独整页显示。
            if (_mainTab == MainTab.Apply)
            {
                DrawApplyView();
                return;
            }

            // 散布编辑：按生成组配置。
            if (_mainTab == MainTab.ScatterEdit)
            {
                DrawScatterEditView();
                return;
            }

            if (_mainTab == MainTab.PropEdit)
            {
                DrawPropEditView();
                return;
            }

            if (_mainTab == MainTab.FixedPointEdit)
            {
                DrawFixedPointEditView();
                return;
            }

            // 编辑子界面：左右分栏（左=全局/层级配置，右=信息生成）
            DrawEditSplitView();
        }

        /// <summary>
        /// 编辑子界面的左右分栏布局：左侧窄栏为「全局配置 + 层级配置」拼成的整块（共同滚动），
        /// 右侧宽栏显示信息生成（原各子界面核心功能）。
        /// </summary>
        private void DrawEditSplitView()
        {
            const float leftWidth = 360f;

            EditorGUILayout.BeginHorizontal();

            // 左栏（窄）：全局配置 + 层级配置 拼成一个大滚动区
            EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth));
            _configScroll = EditorGUILayout.BeginScrollView(_configScroll);
            DrawGlobalConfigView();
            EditorGUILayout.Space(8);
            DrawLayerConfigView();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // 分隔线
            EditorGUILayout.BeginVertical(GUILayout.Width(6));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();

            // 右栏（宽）：信息生成
            EditorGUILayout.BeginVertical();
            DrawInfoGenView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
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
                ClearMapDataPreviews();
                _project = newProject;
                _resultPreview = null;
                _heightPreview = null;
                _map = null;
                _layerIdsCache = null;
                if (_project != null)
                {
                    EnsurePaintMap();
                    LoadResultPreview();
                    RebuildMapDataPreviews();
                }
                RememberProject();
                Repaint();
            }
            if (GUILayout.Button("创建新地形配置", EditorStyles.toolbarButton))
                _creating = !_creating;

            GUILayout.FlexibleSpace();

            // 九个子界面按工作流顺序排列，应用始终位于最后。
            var mainNames = new[] { "工作流配置", "区域编辑", "高度编辑", "道路编辑", "贴图编辑", "散布编辑", "摆件编辑", "定点编辑", "应用" };
            int newMain = GUILayout.Toolbar((int)_mainTab, mainNames, EditorStyles.toolbarButton);
            if (newMain != (int)_mainTab)
            {
                SavePaintMapIfAny();
                _mainTab = (MainTab)newMain;
                if (_mainTab == MainTab.WorkflowConfig)
                    RebuildMapDataPreviews();
            }
            EditorGUILayout.EndHorizontal();

            if (_creating)
                DrawCreateConfig();
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

            // 栅格分辨率单选：写入主配置，之后所有 MapData 栅格数据均此尺寸
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("栅格尺寸", EditorStyles.miniLabel);
            int sel = System.Array.IndexOf(TerrainPaintProjectSO.AllowedResolutions, _createResolution);
            if (sel < 0) sel = 2;
            sel = GUILayout.Toolbar(sel, new[] { "128", "256", "512", "1024" });
            _createResolution = TerrainPaintProjectSO.AllowedResolutions[sel];
            EditorGUILayout.EndHorizontal();
        }

        private void DrawNoProject()
        {
            EditorGUILayout.HelpBox(
                "未选择地形配置。\n\n请在上方 ObjectField 中选择一个已创建的配置，\n" +
                "或点击「创建新地形配置」新建一个（会自动创建子文件夹、总 SO 与 3 个层级 SO）。",
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
            Directory.CreateDirectory(Path.Combine(dirFull, "ScatterConfig"));
            Directory.CreateDirectory(Path.Combine(dirFull, "PropConfig"));
            Directory.CreateDirectory(Path.Combine(dirFull, "FixedPointConfig"));

            var project = ScriptableObject.CreateInstance<TerrainPaintProjectSO>();
            project.name = name;
            project.mapResolution = _createResolution;
            // 默认创建 3 层（Layer0 透明 + 2 个颜色层），可在工作流配置中调整
            for (int i = 0; i < TerrainPaintProjectSO.DefaultLayerCount; i++)
            {
                project.layers.Add(CreateLayerSO(i, dirRel));
            }
            project.SyncAllLayerWeights();
            AssetDatabase.CreateAsset(project, $"{dirRel}/{name}.asset");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            _project = project;
            RememberProject();
            _mainTab = MainTab.WorkflowConfig;
            _resultPreview = null;
            _layerIdsCache = null;
            Debug.Log($"[Terrain Paint Workflow] 已创建配置: {dirRel}");
            return true;
        }

        /// <summary>
        /// 创建单个层级 SO：index 0 为完全透明过渡层（颜色锁定），其余按 LayerPalette 预设色初始化。
        /// </summary>
        private static LayerConfigSO CreateLayerSO(int index, string dirRel)
        {
            var layer = ScriptableObject.CreateInstance<LayerConfigSO>();
            if (index == 0)
            {
                layer.color = LayerPalette.Transparent;
                layer.layerName = "过渡(透明)";
            }
            else
            {
                int preset = Mathf.Min(index - 1, LayerPalette.PresetColors.Length - 1);
                layer.color = LayerPalette.PresetColors[preset];
                layer.layerName = LayerPalette.PresetDefaultNames[preset];
            }
            string layerPath = $"{dirRel}/Layer_{index:00}.asset";
            AssetDatabase.CreateAsset(layer, layerPath);
            return layer;
        }

        private void RememberProject()
        {
            if (_project != null)
                EditorPrefs.SetString(PrefsLastProject, AssetDatabase.GetAssetPath(_project));
            else
                EditorPrefs.DeleteKey(PrefsLastProject);
        }

        // ---------- 工作流配置（无子页签） ----------

        private void DrawWorkflowConfigView()
        {
            _configScroll = EditorGUILayout.BeginScrollView(_configScroll);

            EditorGUILayout.LabelField("工作流配置", EditorStyles.boldLabel);

            // 栅格分辨率（创建时选定；修改后需重新烘焙）
            EditorGUILayout.LabelField("栅格分辨率", EditorStyles.boldLabel);
            int resSel = System.Array.IndexOf(TerrainPaintProjectSO.AllowedResolutions, _project.mapResolution);
            if (resSel < 0) resSel = 2;
            int newResSel = EditorGUILayout.Popup("栅格分辨率", resSel, new[] { "128", "256", "512", "1024" });
            if (newResSel != resSel)
                _project.mapResolution = TerrainPaintProjectSO.AllowedResolutions[newResSel];
            EditorGUILayout.HelpBox(
                "所有栅格化数据（layerMap/height/distance/occupancy/road）均为该尺寸；修改后请重新绘制/烘焙。",
                MessageType.None);

            EditorGUILayout.Space(6);

            DrawMapDataPreviews();

            EditorGUILayout.Space(8);

            // Layer 数量（2~16）
            int newCount = Mathf.Clamp(
                EditorGUILayout.IntField("Layer 数量", _project.layers.Count),
                TerrainPaintProjectSO.MinLayerCount, TerrainPaintProjectSO.MaxLayerCount);
            if (newCount != _project.layers.Count)
            {
                ResizeLayers(newCount);
                Repaint();
            }
            EditorGUILayout.HelpBox(
                $"Layer0 为完全透明过渡层（颜色不可编辑）；其余层级可编辑颜色与名称。数量范围 {TerrainPaintProjectSO.MinLayerCount}~{TerrainPaintProjectSO.MaxLayerCount}。",
                MessageType.None);

            EditorGUILayout.Space(6);

            // 各层颜色/名称编辑
            for (int i = 0; i < _project.layers.Count; i++)
            {
                var layer = _project.layers[i];
                if (layer == null) continue;

                EditorGUILayout.BeginHorizontal();
                var swatchRect = GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20), GUILayout.Height(20));
                var c = new Color(layer.color.r / 255f, layer.color.g / 255f, layer.color.b / 255f,
                    layer.color.a / 255f);
                DrawTinted(swatchRect, c);
                DrawRectOutline(swatchRect, new Color(0f, 0f, 0f, 0.4f), 1f);

                EditorGUILayout.LabelField($"Layer{i}", EditorStyles.miniLabel, GUILayout.Width(52));

                if (i == 0)
                {
                    // Layer0：颜色锁定为完全透明，仅名称可编辑
                    EditorGUILayout.LabelField("透明（锁定）", EditorStyles.miniLabel, GUILayout.Width(90));
                    layer.layerName = EditorGUILayout.TextField(layer.layerName);
                }
                else
                {
                    var newColor = EditorGUILayout.ColorField(GUIContent.none, layer.color, false, true, false, GUILayout.Width(60));
                    if (newColor != (Color)layer.color)
                        layer.color = (Color32)newColor;
                    layer.layerName = EditorGUILayout.TextField(layer.layerName);
                }
                EditorGUILayout.EndHorizontal();
                EditorUtility.SetDirty(layer);
            }

            EditorGUILayout.Space(16);
            DrawCandidatePrefabProcessing();

            EditorGUILayout.EndScrollView();
            EditorUtility.SetDirty(_project);
        }

        private void DrawMapDataPreviews()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("MapData 预览", EditorStyles.boldLabel);
            if (GUILayout.Button("刷新", GUILayout.Width(64f)))
                RebuildMapDataPreviews();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "显示当前配置已持久化的全部 MapData。每项按自身最小值和最大值独立归一化为灰度图，仅用于查看。",
                MessageType.None);

            if (_mapDataPreviewProject != _project)
                RebuildMapDataPreviews();
            if (_mapDataPreviews.Count == 0)
            {
                EditorGUILayout.LabelField("尚未生成 MapData。", EditorStyles.miniLabel);
                return;
            }

            var keys = new List<string>(_mapDataPreviews.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            foreach (string key in keys)
            {
                Texture2D texture = _mapDataPreviews[key];
                if (texture == null) continue;
                Vector2 range = _mapDataPreviewRanges[key];
                EditorGUILayout.LabelField(
                    $"{key}  {texture.width}×{texture.height}  [{range.x:F3}, {range.y:F3}]",
                    EditorStyles.miniBoldLabel);
                float width = Mathf.Min(220f, position.width - 60f);
                float height = width * texture.height / Mathf.Max(1f, texture.width);
                GUILayout.Label(texture, GUILayout.Width(width), GUILayout.Height(height));
                EditorGUILayout.Space(4f);
            }
        }

        private void RebuildMapDataPreviews()
        {
            ClearMapDataPreviews();
            _mapDataPreviewProject = _project;
            if (_project == null || _project.mapDataFiles == null) return;

            var seen = new HashSet<string>();
            foreach (var entry in _project.mapDataFiles)
            {
                if (entry == null || string.IsNullOrEmpty(entry.key) || !seen.Add(entry.key)) continue;
                float[][] data = _project.ReadMap(entry.key);
                Texture2D texture = MapDataTextureUtils.ToTexture(data, out float min, out float max);
                if (texture == null) continue;
                _mapDataPreviews.Add(entry.key, texture);
                _mapDataPreviewRanges.Add(entry.key, new Vector2(min, max));
            }
        }

        private void ClearMapDataPreviews()
        {
            foreach (Texture2D texture in _mapDataPreviews.Values)
                if (texture != null) DestroyImmediate(texture);
            _mapDataPreviews.Clear();
            _mapDataPreviewRanges.Clear();
            _mapDataPreviewProject = null;
        }

        private void DrawApplyView()
        {
            _configScroll = EditorGUILayout.BeginScrollView(_configScroll);

            EditorGUILayout.LabelField("应用", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "这是工作流的最后一步。选择目标 Terrain 和需要应用到的最终阶段，然后按顺序执行高度至该阶段。",
                MessageType.Info);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("目标 Terrain（仅本次窗口会话，不保存到配置；烘焙/计算/应用均需要）", EditorStyles.boldLabel);
            _terrainField = (Terrain)EditorGUILayout.ObjectField(
                "Terrain", _terrainField, typeof(Terrain), true);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("应用范围", EditorStyles.boldLabel);
            string[] stageNames = { "高度编辑", "贴图编辑", "散布编辑", "摆件编辑", "定点编辑" };
            bool previousEnabled = true;
            for (int i = 0; i < _applyStages.Length; i++)
            {
                using (new EditorGUI.DisabledScope(!previousEnabled))
                    _applyStages[i] = previousEnabled && EditorGUILayout.ToggleLeft(stageNames[i], _applyStages[i]);
                previousEnabled &= _applyStages[i];
            }
            EditorGUILayout.HelpBox("阶段按界面顺序应用；当前置阶段未勾选时，其后的阶段不会应用。", MessageType.None);

            using (new EditorGUI.DisabledScope(_terrainField == null || !_applyStages[0]))
            {
                if (GUILayout.Button("应用", GUILayout.Height(28f)))
                    ApplyWorkflowToTerrain();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawCandidatePrefabProcessing()
        {
            EditorGUILayout.LabelField("备用预制体处理", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "批量创建和处理工作流目录下的备用预制体。创建时会为每个源 Prefab 生成标准 Transform 的同名包装 Prefab，" +
                "并写入 PrefabStructureInfo。生成后可以编辑其子物体变换、增删子物体或拼合多个对象；再次处理不会覆盖这些内容。" +
                "散布、摆件和定点只能引用 Generated/Prefabs 中的备用预制体，不能直接引用其它 Prefab。",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "添加备选 Prefab 后请到 Assets/ai-unity-terrain-edit-workflow/Generated/Prefabs 找到对应备用 Prefab 确认内容正确：" +
                "部分源 Prefab 的模型中心正下方不在原点（模型 pivot 不在底部中心或有偏移），" +
                "需要调整子物体变换让模型按预期落位（通常底部中心对准根节点原点），确认无误后再更新 Bounds / 生成 Billboard。" +
                "修正只作用于子物体，根节点保持零变换。",
                MessageType.Warning);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("批量添加备用预制体", EditorStyles.boldLabel);
            for (int i = 0; i < _candidatePrefabSources.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _candidatePrefabSources[i] = (GameObject)EditorGUILayout.ObjectField(
                        $"Prefab {i + 1}", _candidatePrefabSources[i], typeof(GameObject), false);
                    if (GUILayout.Button("移除", GUILayout.Width(48f)))
                    {
                        _candidatePrefabSources.RemoveAt(i);
                        i--;
                    }
                }
            }

            if (GUILayout.Button("添加 Prefab 到列表", GUILayout.Height(22f)))
                _candidatePrefabSources.Add(null);

            _candidateBillboardMode = (BillboardMode)EditorGUILayout.EnumPopup(
                "Billboard 模式", _candidateBillboardMode);
            _candidateTwoPointHeightAdaptation = EditorGUILayout.Toggle(
                "两点高度适应", _candidateTwoPointHeightAdaptation);
            _candidateLodTransition = EditorGUILayout.Slider(
                "LOD Billboard 切换阈值", _candidateLodTransition, 0f, 1f);

            bool hasCandidateSource = _candidatePrefabSources.Exists(prefab => prefab != null);
            using (new EditorGUI.DisabledScope(!hasCandidateSource))
            {
                if (GUILayout.Button("批量添加备用预制体", GUILayout.Height(26f)))
                    RunCandidatePrefabBatch("备用预制体添加", BuildCandidatePrefabsFromList);
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("批量更新备用预制体", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Billboard 仅处理模式不是“不使用 LOD”的对象；普通包围盒更新会跳过已有数据的对象。",
                MessageType.None);

            if (GUILayout.Button("批量更新 Billboard", GUILayout.Height(24f)))
                RunCandidatePrefabBatch("Billboard", () => PrefabProcessingUtility.UpdateAllBillboards(_candidateLodTransition));

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("更新包围盒", GUILayout.Height(24f)))
                    RunCandidatePrefabBatch("包围盒", () => PrefabProcessingUtility.UpdateAllBounds(false));

                if (GUILayout.Button("强制更新包围盒", GUILayout.Height(24f)))
                    RunCandidatePrefabBatch("包围盒（强制）", () => PrefabProcessingUtility.UpdateAllBounds(true));
            }
        }

        private int BuildCandidatePrefabsFromList()
        {
            int created = 0;
            foreach (GameObject sourcePrefab in _candidatePrefabSources)
            {
                if (sourcePrefab == null)
                    continue;
                try
                {
                    PrefabProcessingUtility.BuildCandidatePrefab(
                        sourcePrefab,
                        _candidateBillboardMode,
                        _candidateTwoPointHeightAdaptation,
                        _candidateLodTransition);
                    created++;
                }
                catch (System.Exception exception)
                {
                    string path = AssetDatabase.GetAssetPath(sourcePrefab);
                    Debug.LogError($"[Terrain Paint Workflow] 备用预制体创建失败: {path}\n{exception}");
                }
            }
            _candidatePrefabSources.Clear();
            return created;
        }

        private void RunCandidatePrefabBatch(string operation, System.Func<int> action)
        {
            try
            {
                int count = action();
                string message = $"{operation}更新完成：{count} 个备用预制体";
                Debug.Log("[Terrain Paint Workflow] " + message);
                ShowNotification(new GUIContent(message));
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"[Terrain Paint Workflow] {operation}批量更新失败\n{exception}");
                EditorUtility.DisplayDialog("批量更新失败", exception.Message, "确定");
            }
        }

        private void ApplyWorkflowToTerrain()
        {
            if (_project == null || _terrainField == null)
                return;

            var prefabErrors = ValidatePlacementPrefabs();
            if (prefabErrors.Count > 0)
            {
                string details = string.Join("\n", prefabErrors);
                Debug.LogError("[Terrain Paint Workflow] 应用已阻止，备用预制体检查失败：\n" + details);
                EditorUtility.DisplayDialog(
                    "无法应用：备用预制体不符合要求",
                    details,
                    "确定");
                return;
            }

            int lastStage = 0;
            while (lastStage + 1 < _applyStages.Length && _applyStages[lastStage + 1])
                lastStage++;

            var builder = _terrainField.GetComponent<TerrainBuilder>();
            if (builder == null)
                builder = Undo.AddComponent<TerrainBuilder>(_terrainField.gameObject);

            builder.Build(_project, _terrainField, (TerrainWorkflowStage)lastStage);
        }

        private List<string> ValidatePlacementPrefabs()
        {
            var errors = new List<string>();

            for (int groupIndex = 0; groupIndex < _project.scatterGroups.Count; groupIndex++)
            {
                var group = _project.scatterGroups[groupIndex];
                if (group == null) continue;
                for (int prefabIndex = 0; prefabIndex < group.prefabs.Count; prefabIndex++)
                {
                    var entry = group.prefabs[prefabIndex];
                    ValidatePlacementPrefab(
                        entry != null ? entry.prefab : null,
                        $"散布组[{groupIndex}] {group.groupName} / Prefab[{prefabIndex}]",
                        errors);
                }
            }

            for (int groupIndex = 0; groupIndex < _project.propGroups.Count; groupIndex++)
            {
                var group = _project.propGroups[groupIndex];
                if (group == null) continue;
                for (int prefabIndex = 0; prefabIndex < group.prefabs.Count; prefabIndex++)
                {
                    var entry = group.prefabs[prefabIndex];
                    ValidatePlacementPrefab(
                        entry != null ? entry.prefab : null,
                        $"摆件组[{groupIndex}] {group.groupName} / Prefab[{prefabIndex}]",
                        errors);
                }
            }

            for (int groupIndex = 0; groupIndex < _project.fixedPointGroups.Count; groupIndex++)
            {
                var group = _project.fixedPointGroups[groupIndex];
                if (group == null) continue;
                ValidatePlacementPrefab(group.prefab, $"定点组[{groupIndex}]", errors);
            }

            return errors;
        }

        private static void ValidatePlacementPrefab(
            GameObject prefab,
            string location,
            List<string> errors)
        {
            if (!PrefabProcessingUtility.IsProcessedCandidatePrefab(prefab, out string reason))
            {
                string path = prefab != null ? AssetDatabase.GetAssetPath(prefab) : "<空>";
                errors.Add($"• {location}: {path} — {reason}");
                return;
            }

            ValidateLodForFutureBillboard(prefab, location, errors);
        }

        private static void ValidateLodForFutureBillboard(
            GameObject prefab,
            string location,
            List<string> errors)
        {
            var info = prefab.GetComponent<PrefabStructureInfo>();
            if (info == null || info.billboardMode == BillboardMode.None)
                return;

            var lodGroup = prefab.GetComponent<LODGroup>();
            if (lodGroup == null)
            {
                errors.Add($"• {location}: {AssetDatabase.GetAssetPath(prefab)} — " +
                           "已启用 Billboard，但根节点缺少 LODGroup；请先批量更新 Billboard");
                return;
            }
            if (info.billboardTransform == null ||
                info.billboardTransform.GetComponentsInChildren<Renderer>(true).Length == 0)
            {
                errors.Add($"• {location}: {AssetDatabase.GetAssetPath(prefab)} — " +
                           "Billboard 面片未正常挂载；请先批量更新 Billboard");
            }
        }

        /// <summary>调整 Layer 数量：增层创建新 SO（末尾追加），减层删除末尾 SO 资产。</summary>
        private void ResizeLayers(int newCount)
        {
            if (_project == null) return;

            string dirRel = Path.GetDirectoryName(AssetDatabase.GetAssetPath(_project))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dirRel)) return;

            while (_project.layers.Count < newCount)
            {
                int idx = _project.layers.Count;
                _project.layers.Add(CreateLayerSO(idx, dirRel));
            }
            while (_project.layers.Count > newCount)
            {
                int lastIdx = _project.layers.Count - 1;
                var last = _project.layers[lastIdx];
                if (last != null)
                {
                    string assetPath = AssetDatabase.GetAssetPath(last);
                    if (!string.IsNullOrEmpty(assetPath))
                        AssetDatabase.DeleteAsset(assetPath);
                }
                _project.layers.RemoveAt(lastIdx);
            }

            _project.SyncAllLayerWeights();
            _selectedLayer = Mathf.Clamp(_selectedLayer, 0, Mathf.Max(0, _project.layers.Count - 1));
            EnsureLayerFoldouts();
            EditorUtility.SetDirty(_project);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[Terrain Paint Workflow] Layer 数量调整为 {newCount}");
        }

        // ---------- 配置页签（全局/层级） ----------

        private void EnsureLayerFoldouts()
        {
            int n = _project != null ? _project.layers.Count : 0;
            while (_layerFoldouts.Count < n) _layerFoldouts.Add(false);
            if (_layerFoldouts.Count > n) _layerFoldouts.RemoveRange(n, _layerFoldouts.Count - n);
        }

        private void DrawGlobalConfigView()
        {
            switch (_mainTab)
            {
                case MainTab.AreaEdit: DrawAreaGlobalConfig(); break;
                case MainTab.HeightEdit: DrawHeightGlobalConfig(); break;
                case MainTab.Road: DrawRoadGlobalConfig(); break;
                case MainTab.Texture: DrawTextureGlobalConfig(); break;
            }
            EditorUtility.SetDirty(_project);
        }

        /// <summary>区域编辑 · 全局配置：暂无（层次图已移至工作流配置页面）。</summary>
        private void DrawAreaGlobalConfig()
        {
            EditorGUILayout.LabelField("区域编辑 · 全局配置", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "区域编辑无专属全局配置；层次图（绘画画布）已在「工作流配置」页面中管理。",
                MessageType.Info);
        }

        /// <summary>高度编辑 · 全局配置：噪声 seed / scale + 平滑参数 + 烘焙结果（min/max/高度图）。</summary>
        private void DrawHeightGlobalConfig()
        {
            EditorGUILayout.LabelField("高度编辑 · 全局配置", EditorStyles.boldLabel);
            _project.heightSeed = EditorGUILayout.IntField("高度 Seed", _project.heightSeed);
            _project.heightScale = Mathf.Max(0.001f, EditorGUILayout.FloatField("高度 Scale（噪声频率）", _project.heightScale));

            _project.smoothStep = Mathf.Max(1, EditorGUILayout.IntField("平滑步长（像素）", _project.smoothStep));
            _project.smoothIterations = Mathf.Max(0, EditorGUILayout.IntField("平滑迭代", _project.smoothIterations));
            EditorGUILayout.HelpBox("平滑参数（十字线均值滤波）暂未参与烘焙运算，仅记录配置，后续接入。", MessageType.Info);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("烘焙结果（只读）", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"高度 Min: {_lastHeightMin:F2}");
            EditorGUILayout.LabelField($"高度 Max: {_lastHeightMax:F2}");
            EditorGUILayout.HelpBox(
                _project.HasMap("height")
                    ? "高度数据已烘焙（MapData/height.txt），预览在右侧信息生成栏。"
                    : "尚未烘焙高度数据（MapData/height.txt 不存在）。",
                MessageType.None);
        }

        /// <summary>道路编辑 · 全局配置：锚点延伸、邻接组和道路数据结果。</summary>
        private void DrawRoadGlobalConfig()
        {
            EditorGUILayout.LabelField("道路编辑 · 全局配置", EditorStyles.boldLabel);
            DrawRoadGenerationConfig();
            EditorGUILayout.Space(10);
            DrawAdjacencyGroups();
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("生成结果（只读）", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"距离场 Max: {_lastRMax:F2} m");
            EditorGUILayout.HelpBox(
                _project.HasMap("road")
                    ? "道路数据已生成（MapData/road.txt），预览位于右侧。"
                    : "尚未生成道路数据。",
                MessageType.None);
        }

        /// <summary>贴图编辑 · 全局配置：TerrainLayer 池、贴图种子、噪声和平滑。</summary>
        private void DrawTextureGlobalConfig()
        {
            EditorGUILayout.LabelField("贴图编辑 · 全局配置", EditorStyles.boldLabel);
            DrawTextureBlendConfig();
            EditorGUILayout.Space(10);
            DrawGlobalTerrainLayers();
        }

        /// <summary>邻接组（组合层级分组）编辑器：List&lt;List&lt;int&gt;&gt;，同一层级不可跨组重复。</summary>
        private void DrawAdjacencyGroups()
        {
            EditorGUILayout.LabelField("邻接组（组合层级分组）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "每个组是一个层级索引列表（如 {1,2,3}）。同一层级不可出现在多个组中，否则下方会报 Error 且计算被阻断。",
                MessageType.None);

            var groups = _project.adjacencyGroups;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"组 {gi}", EditorStyles.boldLabel);
                if (GUILayout.Button("删除组", GUILayout.Width(60)))
                {
                    groups.RemoveAt(gi--);
                    continue;
                }
                EditorGUILayout.EndHorizontal();

                var group = groups[gi];
                for (int i = 0; i < group.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    group[i] = Mathf.Clamp(
                        EditorGUILayout.IntField($"层级[{i}]", group[i]),
                        0, Mathf.Max(0, _project.layers.Count - 1));
                    if (GUILayout.Button("-", GUILayout.Width(20)))
                        group.RemoveAt(i--);
                    EditorGUILayout.EndHorizontal();
                }
                if (GUILayout.Button($"+ 添加层级到组 {gi}"))
                    group.Add(0);
                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("+ 添加邻接组"))
                groups.Add(new List<int>());

            EditorGUILayout.Space(4);

            // 冲突检查：同一层级出现在多个组 → Error 提示
            var dups = _project.FindDuplicateLayerIndices();
            if (dups.Count > 0)
            {
                string names = string.Join(", ", dups.ConvertAll(i => $"Layer{i}"));
                EditorGUILayout.HelpBox(
                    $"以下层级被加入多个邻接组（将导致计算被阻断）: {names}",
                    MessageType.Error);
            }
        }

        private void DrawLayerConfigView()
        {
            EnsureLayerFoldouts();
            switch (_mainTab)
            {
                case MainTab.AreaEdit:
                    EditorGUILayout.LabelField("层级配置 · 区域编辑（颜色/名称）", EditorStyles.boldLabel);
                    for (int i = 0; i < _project.layers.Count; i++)
                    {
                        var layer = _project.layers[i];
                        if (layer == null) continue;
                        DrawAreaLayerConfig(i, layer);
                    }
                    break;

                case MainTab.HeightEdit:
                    EditorGUILayout.LabelField("层级配置 · 高度编辑（每层高度范围）", EditorStyles.boldLabel);
                    for (int i = 0; i < _project.layers.Count; i++)
                    {
                        var layer = _project.layers[i];
                        if (layer == null) continue;
                        DrawHeightLayerConfig(i, layer);
                    }
                    break;

                case MainTab.Texture:
                    EditorGUILayout.LabelField("层级配置 · 贴图编辑", EditorStyles.boldLabel);
                    for (int i = 0; i < _project.layers.Count; i++)
                    {
                        var layer = _project.layers[i];
                        if (layer == null) continue;
                        DrawTextureLayerConfig(i, layer);
                    }
                    break;

                case MainTab.Road:
                    EditorGUILayout.LabelField("层级配置 · 道路编辑", EditorStyles.boldLabel);
                    for (int i = 0; i < _project.layers.Count; i++)
                    {
                        var layer = _project.layers[i];
                        if (layer == null) continue;
                        DrawRoadLayerConfig(i, layer);
                    }
                    break;
            }
            EditorUtility.SetDirty(_project);
        }

        /// <summary>高度编辑 · 单个层级的配置：高度范围（min, max）。</summary>
        private void DrawHeightLayerConfig(int index, LayerConfigSO layer)
        {
            EditorGUILayout.BeginHorizontal();
            bool open = _layerFoldouts[index];

            var swatchRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
            var c = new Color(layer.color.r / 255f, layer.color.g / 255f, layer.color.b / 255f, 1f);
            DrawTinted(swatchRect, c);

            open = EditorGUILayout.Foldout(open, $"Layer{index}  {layer.layerName}", true);
            _layerFoldouts[index] = open;
            EditorGUILayout.EndHorizontal();

            if (!open)
                return;

            EditorGUILayout.BeginVertical("box");
            var range = EditorGUILayout.Vector2Field("高度范围 (min, max)", layer.heightRange);
            if (range.x > range.y) range.y = range.x; // 保证 min <= max
            layer.heightRange = range;
            EditorGUILayout.EndVertical();
            EditorUtility.SetDirty(layer);
        }

        /// <summary>区域编辑 · 单个层级的配置：颜色/名称（只读）。</summary>
        private void DrawAreaLayerConfig(int index, LayerConfigSO layer)
        {
            EditorGUILayout.BeginHorizontal();
            bool open = _layerFoldouts[index];

            var swatchRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
            var c = new Color(layer.color.r / 255f, layer.color.g / 255f, layer.color.b / 255f, 1f);
            DrawTinted(swatchRect, c);

            open = EditorGUILayout.Foldout(open, $"Layer{index}  {layer.layerName}", true);
            _layerFoldouts[index] = open;
            EditorGUILayout.EndHorizontal();

            if (!open)
                return;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("颜色 / 名称（只读，请在 Inspector 中修改对应 SO）", EditorStyles.miniLabel);
            EditorGUILayout.ColorField("层级颜色", layer.color);
            EditorGUILayout.TextField("层级名称", layer.layerName);
            EditorGUILayout.EndVertical();
        }

        private void DrawRoadGenerationConfig()
        {
            var cfg = _project.config;
            EditorGUILayout.LabelField("锚点道路延伸", EditorStyles.boldLabel);
            cfg.roadExtensionStep = Mathf.Max(0.1f, EditorGUILayout.FloatField("延伸步长 (m)", cfg.roadExtensionStep));
            Vector2 curvature = EditorGUILayout.Vector2Field("默认游走曲率 (°/步)", cfg.roadWalkCurvatureRange);
            cfg.roadWalkCurvatureRange = new Vector2(Mathf.Min(curvature.x, curvature.y), Mathf.Max(curvature.x, curvature.y));
            cfg.roadWalkCurvatureDirectionSwitchProbability = EditorGUILayout.Slider(
                "曲率加减方向切换概率", cfg.roadWalkCurvatureDirectionSwitchProbability, 0f, 1f);
            cfg.roadWalkDirectionFlipProbability = EditorGUILayout.Slider(
                "当前偏转角反向概率", cfg.roadWalkDirectionFlipProbability, 0f, 1f);
            cfg.boundaryFollowDistance = Mathf.Max(0f, EditorGUILayout.FloatField("边界跟随范围 (m)", cfg.boundaryFollowDistance));
            cfg.freeMaxTurnAngle = EditorGUILayout.Slider("自由单步最大转向", cfg.freeMaxTurnAngle, 0f, 180f);
            cfg.anchorGuideMaxTurnAngle = EditorGUILayout.Slider("锚点引导单步最大转向", cfg.anchorGuideMaxTurnAngle, 0f, 180f);
            cfg.directionSearchStep = EditorGUILayout.Slider("方向搜索角步长", cfg.directionSearchStep, 1f, 90f);
            cfg.bezierProbeDistance = Mathf.Max(0f, EditorGUILayout.FloatField("贝塞尔探测距离 (m)", cfg.bezierProbeDistance));
            cfg.bezierCompletionDistance = Mathf.Clamp(EditorGUILayout.FloatField("贝塞尔补全距离 (m)", cfg.bezierCompletionDistance), 0f, cfg.bezierProbeDistance);
            cfg.anchorSnapAngle = EditorGUILayout.Slider("锚点吸附角", cfg.anchorSnapAngle, 0f, 180f);
            cfg.maximumRoadSteps = Mathf.Max(1, EditorGUILayout.IntField("最大延伸步数", cfg.maximumRoadSteps));
            DrawRoadAnchorConfig();
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Layer 形状道路骨架", EditorStyles.boldLabel);
            cfg.minimumRoadRegionArea = Mathf.Max(0f, EditorGUILayout.FloatField("最小道路区域面积 (m²)", cfg.minimumRoadRegionArea));
            cfg.minimumCorridorAspect = Mathf.Max(0f, EditorGUILayout.FloatField("最小走廊形状比", cfg.minimumCorridorAspect));
            cfg.minimumSkeletonBranchLength = Mathf.Max(0f, EditorGUILayout.FloatField("最小骨架支路长度 (m)", cfg.minimumSkeletonBranchLength));
            cfg.spurLengthToWidthRatio = Mathf.Max(0f, EditorGUILayout.FloatField("支刺长度/宽度比", cfg.spurLengthToWidthRatio));
            cfg.roadBoundaryMargin = Mathf.Max(0f, EditorGUILayout.FloatField("道路边界留白 (m)", cfg.roadBoundaryMargin));
        }

        private void DrawTextureBlendConfig()
        {
            var cfg = _project.config;
            EditorGUILayout.LabelField("贴图混合", EditorStyles.boldLabel);
            cfg.noiseScale = Mathf.Max(0.01f, EditorGUILayout.FloatField("Noise Scale (m)", cfg.noiseScale));
            cfg.textureSmoothingRadius = Mathf.Max(0, EditorGUILayout.IntField("贴图平滑半径 (alphamap 像素)", cfg.textureSmoothingRadius));
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("全局贴图种子（value-noise）", EditorStyles.boldLabel);
            _project.naturalSeed = EditorGUILayout.IntField("自然贴图种子", _project.naturalSeed);
            _project.roadSeed = EditorGUILayout.IntField("道路贴图种子", _project.roadSeed);
        }

        private void DrawRoadAnchorConfig()
        {
            EditorGUILayout.LabelField("道路锚点（Scene 视图可拖动）", EditorStyles.boldLabel);
            if (_project.roadAnchors == null) _project.roadAnchors = new List<RoadAnchorConfig>();
            for (int i = 0; i < _project.roadAnchors.Count; i++)
            {
                RoadAnchorConfig anchor = _project.roadAnchors[i];
                if (anchor == null) { anchor = new RoadAnchorConfig(); _project.roadAnchors[i] = anchor; }
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("锚点 " + i, EditorStyles.boldLabel);
                if (GUILayout.Button("删除", GUILayout.Width(48f)))
                {
                    Undo.RecordObject(_project, "删除道路锚点");
                    _project.roadAnchors.RemoveAt(i--);
                    EditorUtility.SetDirty(_project);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    continue;
                }
                EditorGUILayout.EndHorizontal();
                anchor.normalizedPosition = new Vector2(
                    Mathf.Clamp01(EditorGUILayout.FloatField("归一化 X", anchor.normalizedPosition.x)),
                    Mathf.Clamp01(EditorGUILayout.FloatField("归一化 Z", anchor.normalizedPosition.y)));
                if (anchor.validDirections == null) anchor.validDirections = new List<Vector2>();
                for (int d = 0; d < anchor.validDirections.Count; d++)
                {
                    EditorGUILayout.BeginHorizontal();
                    Vector2 direction = EditorGUILayout.Vector2Field("方向 " + d, anchor.validDirections[d]);
                    anchor.validDirections[d] = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.right;
                    if (GUILayout.Button("-", GUILayout.Width(24f))) anchor.validDirections.RemoveAt(d--);
                    EditorGUILayout.EndHorizontal();
                }
                if (GUILayout.Button("添加有效方向")) anchor.validDirections.Add(Vector2.right);
                EditorGUILayout.EndVertical();
            }
            if (GUILayout.Button("添加道路锚点"))
            {
                Undo.RecordObject(_project, "添加道路锚点");
                _project.roadAnchors.Add(new RoadAnchorConfig());
                EditorUtility.SetDirty(_project);
            }
        }

        private void DrawRoadAnchorSceneHandles(SceneView sceneView)
        {
            if (_mainTab != MainTab.Road || _project == null || _terrainField == null || _project.roadAnchors == null) return;
            TerrainData data = _terrainField.terrainData;
            if (data == null) return;
            Vector3 origin = _terrainField.transform.position;
            for (int i = 0; i < _project.roadAnchors.Count; i++)
            {
                RoadAnchorConfig anchor = _project.roadAnchors[i];
                if (anchor == null) continue;
                Vector3 world = origin + new Vector3(anchor.normalizedPosition.x * data.size.x, 0f,
                    anchor.normalizedPosition.y * data.size.z);
                world.y = _terrainField.SampleHeight(world) + origin.y + 0.5f;
                Handles.color = new Color(1f, 0.75f, 0.1f, 1f);
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(world, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_project, "移动道路锚点");
                    anchor.normalizedPosition = new Vector2(
                        Mathf.Clamp01((moved.x - origin.x) / Mathf.Max(0.0001f, data.size.x)),
                        Mathf.Clamp01((moved.z - origin.z) / Mathf.Max(0.0001f, data.size.z)));
                    EditorUtility.SetDirty(_project);
                }
                if (anchor.validDirections == null) continue;
                float arrowLength = Mathf.Max(3f, _project.config.bezierCompletionDistance * 0.35f);
                foreach (Vector2 direction in anchor.validDirections)
                {
                    if (direction.sqrMagnitude < 0.0001f) continue;
                    Vector3 dir3 = new Vector3(direction.x, 0f, direction.y).normalized;
                    Handles.ArrowHandleCap(0, world, Quaternion.LookRotation(dir3, Vector3.up),
                        arrowLength, EventType.Repaint);
                }
                Handles.Label(world + Vector3.up, "Road Anchor " + i);
            }
        }

        /// <summary>
        /// 全局 TerrainLayer 池（自然/道路两套）。
        /// 各层级具体用哪些 TerrainLayer 及其权重，在层级配置的 naturalLayerWeights / roadLayerWeights 中设置。
        /// </summary>
        private void DrawGlobalTerrainLayers()
        {
            EditorGUILayout.LabelField("全局 TerrainLayer 池", EditorStyles.boldLabel);

            // ===== 自然贴图 TerrainLayer 池 =====
            EditorGUILayout.LabelField("自然贴图 TerrainLayer 池", EditorStyles.boldLabel);
            DrawTerrainLayerPool(_project.naturalTerrainLayers, "自然");

            EditorGUILayout.Space(8);

            // ===== 道路贴图 TerrainLayer 池 =====
            EditorGUILayout.LabelField("道路贴图 TerrainLayer 池", EditorStyles.boldLabel);
            DrawTerrainLayerPool(_project.roadTerrainLayers, "道路");
        }

        private void DrawRoadLayerConfig(int index, LayerConfigSO layer)
        {
            EditorGUILayout.BeginHorizontal();
            bool open = _layerFoldouts[index];

            var swatchRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
            var c = new Color(layer.color.r / 255f, layer.color.g / 255f, layer.color.b / 255f, 1f);
            DrawTinted(swatchRect, c);

            open = EditorGUILayout.Foldout(open, $"Layer{index}  {layer.layerName}", true);
            _layerFoldouts[index] = open;
            EditorGUILayout.EndHorizontal();

            if (!open)
                return;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("道路生成参数", EditorStyles.boldLabel);
            layer.generateRoad = EditorGUILayout.Toggle("生成道路", layer.generateRoad);
            layer.roadWidth = Mathf.Max(0.01f, EditorGUILayout.FloatField("Road Width (m)", layer.roadWidth));
            EditorGUILayout.EndVertical();

            EditorUtility.SetDirty(layer);
        }

        private void DrawTextureLayerConfig(int index, LayerConfigSO layer)
        {
            EditorGUILayout.BeginHorizontal();
            bool open = _layerFoldouts[index];
            var swatchRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
            var c = new Color(layer.color.r / 255f, layer.color.g / 255f, layer.color.b / 255f, 1f);
            DrawTinted(swatchRect, c);
            open = EditorGUILayout.Foldout(open, $"Layer{index}  {layer.layerName}", true);
            _layerFoldouts[index] = open;
            EditorGUILayout.EndHorizontal();
            if (!open) return;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("自然贴图混合权重（0 = 不纳入；索引对应全局池）", EditorStyles.boldLabel);
            DrawWeightList(layer.naturalLayerWeights, _project.naturalTerrainLayers, "自然");
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(2);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("道路贴图混合权重（0 = 不纳入；索引对应全局池）", EditorStyles.boldLabel);
            DrawWeightList(layer.roadLayerWeights, _project.roadTerrainLayers, "道路");
            EditorGUILayout.EndVertical();

            EditorUtility.SetDirty(layer);
        }

        // ---------- ① 区域编辑 ----------

        /// <summary>信息生成页签：按当前子界面分发到对应核心功能。</summary>
        private void DrawInfoGenView()
        {
            switch (_mainTab)
            {
                case MainTab.AreaEdit: DrawAreaEditView(); break;
                case MainTab.HeightEdit: DrawHeightEditView(); break;
                case MainTab.Road: DrawRoadView(); break;
                case MainTab.Texture: DrawTextureView(); break;
            }
        }

        // ---------- 高度编辑（信息生成） ----------

        private void DrawHeightEditView()
        {
            EditorGUILayout.LabelField("高度图烘焙", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "逐像素按所在层级的高度范围，用 Perlin 噪声（seed + scale）在该范围内插值生成真实高度数组；\n" +
                "直接写入 MapData/height.txt（不归一化）；显示/构建时遍历数据现算范围。",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("烘焙高度图", GUILayout.Width(160)))
                BakeHeightMap();
            if (GUILayout.Button("清空高度图", GUILayout.Width(120)))
            {
                _project.DeleteMap("height");
                if (_heightPreview != null)
                    Object.DestroyImmediate(_heightPreview);
                _heightPreview = null;
                _lastHeightMin = 0f;
                _lastHeightMax = 0f;
                _project.RefreshMapDataRefs(true);
                RebuildMapDataPreviews();
                EditorUtility.SetDirty(_project);
            }
            EditorGUILayout.EndHorizontal();

            if (_heightPreview != null)
            {
                EditorGUILayout.Space(6);
                float previewW = Mathf.Min(320f, position.width * 0.5f - 40f);
                float previewH = previewW * (float)_heightPreview.height / Mathf.Max(1, _heightPreview.width);
                GUILayout.Label(_heightPreview, GUILayout.Width(previewW), GUILayout.Height(previewH));
                EditorGUILayout.LabelField(
                    $"当前范围: [{_lastHeightMin:F2}, {_lastHeightMax:F2}]", EditorStyles.miniLabel);
            }
        }

        /// <summary>烘焙高度数据：噪声生成 → min/max → 归一化 float[][] → 写入 MapData "height" → 内存预览。</summary>
        private void BakeHeightMap()
        {
            EnsurePaintMap();
            if (_map == null)
                return;
            if (_project.layers == null || _project.layers.Count == 0)
            {
                EditorUtility.DisplayDialog("烘焙失败", "配置中没有层级。", "确定");
                return;
            }
            if (_terrainField == null)
            {
                EditorUtility.DisplayDialog("烘焙失败", "烘焙高度需要目标 Terrain：请到「应用」页签选择 Terrain。", "确定");
                return;
            }

            int w = _map.Width;
            int h = _map.Height;
            int[] ids = _layerIdsCache;
            if (ids == null || ids.Length != w * h)
            {
                ids = TerrainRoadGen.ParseLayerIds(_map.Texture, _project.layers);
                _layerIdsCache = ids;
            }

            var data = TerrainRoadGen.BakeHeightData(_project, ids, w, h, TerrainRoadGen.PixelWorldSize(_terrainField, w, h));
            if (data == null)
            {
                EditorUtility.DisplayDialog("烘焙失败", "高度图生成失败，请查看 Console 日志。", "确定");
                return;
            }

            _project.WriteMap("height", data);
            if (_heightPreview != null)
                Object.DestroyImmediate(_heightPreview);
            _heightPreview = MapDataTextureUtils.ToTexture(data, out _lastHeightMin, out _lastHeightMax);
            _project.RefreshMapDataRefs(true);
            RebuildMapDataPreviews();
            EditorUtility.SetDirty(_project);
            Debug.Log($"[Terrain Paint Workflow] 高度数据烘焙完成，已写入 MapData/height.txt，范围 [{_lastHeightMin:F2}, {_lastHeightMax:F2}]");
            Repaint();
        }

        private void DrawAreaEditView()
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

            if (_project.paintOperations == null)
                _project.paintOperations = new List<LayerPaintOperation>();

            if (_project.paintOperations.Count > 0)
            {
                int operationResolution = Mathf.Clamp(_project.mapResolution, 128, 1024);
                _map = new LayerMap(operationResolution, operationResolution);
                _map.RebuildFromPaintOperations(
                    operationResolution,
                    operationResolution,
                    _project.paintOperations,
                    _project.layers);
                return;
            }

            var data = _project.ReadMap("layerMap");
            if (data != null && data.Length > 0)
            {
                _map = new LayerMap(1, 1);
                _map.LoadFromIdArray(data, _project.layers);
                MigrateLegacyLayerMapToPaintOperations(data);
            }
            else
            {
                int res = Mathf.Clamp(_project.mapResolution, 128, 1024);
                _map = new LayerMap(res, res);
            }
        }

        private void MigrateLegacyLayerMapToPaintOperations(float[][] layerIds)
        {
            if (_project.paintOperations.Count > 0)
                return;

            for (int y = 0; y < layerIds.Length; y++)
            {
                float[] row = layerIds[y];
                if (row == null)
                    continue;
                int x = 0;
                while (x < row.Length)
                {
                    int layerIndex = Mathf.RoundToInt(row[x]);
                    if (layerIndex <= 0 || layerIndex >= _project.layers.Count)
                    {
                        x++;
                        continue;
                    }

                    int startX = x++;
                    while (x < row.Length && Mathf.RoundToInt(row[x]) == layerIndex)
                        x++;
                    _project.paintOperations.Add(new LayerPaintOperation
                    {
                        type = LayerPaintOperationType.Rectangle,
                        pointA = new Vector2Int(startX, y),
                        pointB = new Vector2Int(x - 1, y),
                        layerIndex = layerIndex,
                    });
                }
            }

            if (_project.paintOperations.Count > 0)
            {
                EditorUtility.SetDirty(_project);
                Debug.Log($"[Terrain Paint Workflow] 已将旧 layerMap 迁移为 " +
                          $"{_project.paintOperations.Count} 条区域绘画操作");
            }
        }

        /// <summary>把当前画布写入 MapData "layerMap"（只写文件、不刷新资产；每笔完调用的轻量路径）。</summary>
        private void PersistLayerMap()
        {
            if (_project == null || _map == null)
                return;
            _project.WriteMap("layerMap", _map.ToIdArray(_project.layers));
            _layerIdsCache = null;
        }

        /// <summary>提交点：写入 layerMap + 刷新资产并重链 txt 引用（切页/切配置/关窗/显式保存时调用）。</summary>
        private void SavePaintMapIfAny()
        {
            if (_project == null || _map == null)
                return;
            PersistLayerMap();
            _project.RefreshMapDataRefs(true);
        }

        private void DrawPaintToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var toolNames = new[] { "圆形画笔", "矩形", "三角形", "凸多边形", "圆形/椭圆", "扇形" };
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

            GUILayout.Label($"尺寸 {_project.mapResolution}×{_project.mapResolution}", EditorStyles.miniLabel);
            if (GUILayout.Button("重置画布", EditorStyles.toolbarButton))
            {
                Undo.RecordObject(_project, "重置区域绘画操作");
                _project.paintOperations.Clear();
                _map.RebuildFromPaintOperations(
                    _project.mapResolution,
                    _project.mapResolution,
                    _project.paintOperations,
                    _project.layers);
                _triPoints.Clear();
                EditorUtility.SetDirty(_project);
                PersistLayerMap();
                Repaint();
            }

            GUILayout.Space(8);
            if (GUILayout.Button("撤销", EditorStyles.toolbarButton))
            {
                if (UndoLastPaintOperation())
                    PersistLayerMap();
            }
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

            string hint;
            switch (_tool)
            {
                case Tool.CircleBrush:
                    hint = _erase ? "擦除：单击画圆，拖拽画直线条带" : "左键单击画圆，拖拽画直线条带";
                    break;
                case Tool.RectFill:
                    hint = "左键拖拽定义矩形区域";
                    break;
                case Tool.TriangleFill:
                    hint = "依次点击 3 个顶点（已点 " + _triPoints.Count + " 个）";
                    break;
                case Tool.PolygonFill:
                    hint = "依次点击凸多边形顶点，按 Enter 完成（已点 " + _triPoints.Count + " 个）";
                    break;
                case Tool.EllipseFill:
                    hint = "拖拽两个点定义椭圆的 AABB 外切矩形";
                    break;
                default:
                    hint = "依次点击圆心、起始弧点、结束弧点（已点 " + _triPoints.Count + " 个）";
                    break;
            }
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

                EditorGUILayout.LabelField($"Layer{i}", EditorStyles.miniLabel, GUILayout.Width(52));
                EditorGUILayout.LabelField(layer.layerName, EditorStyles.miniLabel, GUILayout.Width(130));

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                _erase ? "当前: 擦除（透明）" : $"当前: Layer{_selectedLayer} {CurrentLayerName}",
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

                if (_tool == Tool.RectFill || _tool == Tool.EllipseFill)
                {
                    var r = new Rect(
                        Mathf.Min(start.x, cur.x), Mathf.Min(start.y, cur.y),
                        Mathf.Abs(cur.x - start.x), Mathf.Abs(cur.y - start.y));
                    if (_tool == Tool.RectFill)
                        DrawTinted(r, color);
                    else
                        DrawEllipseOutline(r, color);
                }
                else if (_tool == Tool.CircleBrush)
                {
                    DrawThickLine(start, cur, _brushRadius * 2f * _canvasScale, color);
                }
            }

            if ((_tool == Tool.TriangleFill || _tool == Tool.PolygonFill || _tool == Tool.SectorFill)
                && _triPoints.Count > 0)
            {
                var color = CurrentLayerColor;
                color.a = 0.5f;
                for (int i = 0; i < _triPoints.Count; i++)
                {
                    Vector2 p = PixToScreen(_triPoints[i], drawRect);
                    DrawCross(p, 5f, color);
                    if (i > 0)
                        DrawThickLine(PixToScreen(_triPoints[i - 1], drawRect), p, 2f, color);
                }
            }
        }

        // ---------- 道路编辑 ----------

        private void DrawRoadView()
        {
            _texScroll = EditorGUILayout.BeginScrollView(_texScroll);

            EditorGUILayout.HelpBox(
                "道路参数、邻接组、锚点与每层道路宽度请在左侧配置。\n" +
                "本页负责距离场 + 锚点路网计算。计算与烘焙（distance/occupancy/road/offRoad）\n" +
                "按目标 Terrain 实际尺寸换算世界尺度：请先在「应用」页签选择 Terrain。",
                MessageType.Info);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("距离场 + 路网计算（RGB 三通道）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "点击计算：按组合层级分组 → 距离场 R → 锚点延伸/贝塞尔连接生成中心线和路面。\n" +
                "结果合成 RGB 图：R=距离场，G=道路中心线，B=路面掩码。",
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

        // ---------- 贴图编辑 ----------

        private void DrawTextureView()
        {
            _texScroll = EditorGUILayout.BeginScrollView(_texScroll);
            EditorGUILayout.LabelField("Terrain 贴图配置", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "此界面只负责自然地表与道路地表的 TerrainLayer 池、每层混合权重、噪声种子和平滑参数。\n" +
                "道路形状、锚点、邻接组与道路数据生成已经迁移到独立的「道路编辑」界面。\n" +
                "贴图实际应用仍在最后的「应用」界面执行。",
                MessageType.Info);
            DrawTerrainLayerSummary(_project.naturalTerrainLayers, "自然 TerrainLayer");
            DrawTerrainLayerSummary(_project.roadTerrainLayers, "道路 TerrainLayer");
            EditorGUILayout.EndScrollView();
        }

        private static void DrawTerrainLayerSummary(List<TerrainLayer> layers, string label)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField($"{label}: {layers.Count}", EditorStyles.boldLabel);
            for (int i = 0; i < layers.Count; i++)
                EditorGUILayout.LabelField($"[{i}] {(layers[i] != null ? layers[i].name : "未设置")}");
        }

        /// <summary>绘制一个 TerrainLayer 池的编辑列表（带添加/删除按钮）。</summary>
        private void DrawTerrainLayerPool(List<TerrainLayer> pool, string label)
        {
            for (int i = 0; i < pool.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                pool[i] = (TerrainLayer)EditorGUILayout.ObjectField(
                    $"{label} TerrainLayer[{i}]", pool[i], typeof(TerrainLayer), false);
                if (GUILayout.Button("-", GUILayout.Width(20)))
                {
                    pool.RemoveAt(i--);
                    _project.SyncAllLayerWeights();
                }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button($"+ 添加{label} TerrainLayer"))
            {
                pool.Add(null);
                _project.SyncAllLayerWeights();
            }
        }

        /// <summary>
        /// 绘制某层级的贴图混合权重列表：每行 = 池中一个 TerrainLayer（缩略图 + 名称 + 权重 IntField）。
        /// 权重 0 = 该层不纳入此 TerrainLayer。
        /// </summary>
        private void DrawWeightList(List<int> weights, List<TerrainLayer> pool, string label)
        {
            if (pool.Count == 0)
            {
                EditorGUILayout.HelpBox($"{label} TerrainLayer 池为空，请先在全局配置中添加。", MessageType.Info);
                return;
            }
            // 池增删后确保长度对齐
            while (weights.Count < pool.Count) weights.Add(0);
            if (weights.Count > pool.Count) weights.RemoveRange(pool.Count, weights.Count - pool.Count);

            const float thumbSize = 28f;
            for (int i = 0; i < pool.Count; i++)
            {
                var tl = pool[i];
                string tlName = tl != null ? tl.name : $"{label}[{i}]";

                EditorGUILayout.BeginHorizontal();
                // 外观缩略图（diffuseTexture；无纹理时灰色占位）
                var thumbRect = GUILayoutUtility.GetRect(thumbSize, thumbSize, GUILayout.Width(thumbSize), GUILayout.Height(thumbSize));
                var diffuse = tl != null ? tl.diffuseTexture : null;
                if (diffuse != null)
                    GUI.DrawTexture(thumbRect, diffuse, ScaleMode.ScaleToFit);
                else
                    DrawTinted(thumbRect, new Color(0.4f, 0.4f, 0.4f, 1f));
                DrawRectOutline(thumbRect, new Color(0f, 0f, 0f, 0.4f), 1f);

                GUILayout.Space(4);
                // TL 名自动占满剩余宽度（不固定，避免截断）
                EditorGUILayout.LabelField($"  [{i}] {tlName}", EditorStyles.label);
                GUILayout.FlexibleSpace();
                GUILayout.Label("权重", EditorStyles.miniLabel, GUILayout.Width(32));
                // 无 label 前缀的输入框，宽度独立，数值完整可见
                weights[i] = Mathf.Max(0, EditorGUILayout.IntField(weights[i], GUILayout.Width(64)));
                EditorGUILayout.EndHorizontal();
            }
        }

        private void ComputeRgb()
        {
            EnsurePaintMap();
            if (_map == null)
                return;
            if (_project.layers == null || _project.layers.Count == 0)
            {
                EditorUtility.DisplayDialog("计算失败", "配置中没有层级（layers 为空）。", "确定");
                return;
            }
            // 邻接组冲突检查：同一层级出现在多个组 → 阻断计算
            var dups = _project.FindDuplicateLayerIndices();
            if (dups.Count > 0)
            {
                string names = string.Join(", ", dups.ConvertAll(i => $"Layer{i}"));
                EditorUtility.DisplayDialog(
                    "计算失败",
                    $"邻接组配置错误：以下层级被加入多个组，计算已阻断。\n{names}\n\n请到「道路编辑 · 全局配置」的邻接组中修正。",
                    "确定");
                return;
            }

            if (_terrainField == null)
            {
                EditorUtility.DisplayDialog("计算失败", "距离场/路网计算需要目标 Terrain：请到「应用」页签选择 Terrain。", "确定");
                return;
            }

            int w = _map.Width;
            int h = _map.Height;
            Vector2 pws = TerrainRoadGen.PixelWorldSize(_terrainField, w, h);
            int[] ids = _layerIdsCache;
            if (ids == null || ids.Length != w * h)
            {
                ids = TerrainRoadGen.ParseLayerIds(_map.Texture, _project.layers);
                _layerIdsCache = ids;
            }

            var tex = TerrainRoadGen.ComputeAll(_project, ids, w, h, pws, out var rArr, out var gArr, out var bArr);
            if (tex == null)
            {
                Debug.LogError("[Terrain Paint Workflow] 计算失败（详见上方错误日志），已中断。");
                return;
            }

            // 距离场以真实值落盘；显示用范围由数据现算（不持久化）
            _lastRMax = 0f;
            for (int i = 0; i < rArr.Length; i++)
                if (rArr[i] > _lastRMax) _lastRMax = rArr[i];

            // 写 MapData：R/G/B + offRoad 四个 key（不再落 PNG；图片仅作内存预览）
            _project.WriteMap("distance", CsvArrayCodec.ToJagged(rArr, w, h));
            _project.WriteMap("occupancy", CsvArrayCodec.ToJagged(gArr, w, h));
            _project.WriteMap("road", CsvArrayCodec.ToJagged(bArr, w, h));
            _project.WriteMap("offRoad", CsvArrayCodec.ToJagged(
                TerrainRoadGen.ComputeOffRoad(ids, bArr, w, h, pws), w, h));
            _project.RefreshMapDataRefs(true);
            RebuildMapDataPreviews();
            EditorUtility.SetDirty(_project);

            if (_resultPreview != null)
                Object.DestroyImmediate(_resultPreview);
            _resultPreview = tex;

            Debug.Log($"[Terrain Paint Workflow] 距离场/路网计算完成，已写入 MapData: distance/occupancy/road/offRoad.txt" +
                      $"（{_project.layers.Count} 层 / {TerrainRoadGen.GroupLayers(_project).Count} 个组合层）");
            Repaint();
        }

        /// <summary>从 MapData 读取 distance/occupancy/road 合成 RGB 预览（仅内存显示用）。</summary>
        private void LoadResultPreview()
        {
            if (_project == null)
                return;
            if (!_project.HasMap("distance") || !_project.HasMap("occupancy") || !_project.HasMap("road"))
                return;
            var r = _project.ReadMap("distance");
            var g = _project.ReadMap("occupancy");
            var b = _project.ReadMap("road");
            if (r == null || g == null || b == null || r.Length == 0)
                return;
            int h = r.Length;
            int w = r[0].Length;
            var rFlat = CsvArrayCodec.ToFlat(r);
            // distance 为真实值，显示合成前按数据现算 max 归一化（范围不持久化）
            _lastRMax = 0f;
            for (int i = 0; i < rFlat.Length; i++)
                if (rFlat[i] > _lastRMax) _lastRMax = rFlat[i];
            if (_lastRMax > 0f)
                for (int i = 0; i < rFlat.Length; i++)
                    rFlat[i] /= _lastRMax;
            _resultPreview = TerrainRoadGen.ComposeRgb(
                rFlat, CsvArrayCodec.ToFlat(g), CsvArrayCodec.ToFlat(b), w, h);
        }

        // ---------- ③ 散布编辑 ----------

        /// <summary>
        /// 散布位置不落盘；构建时由 TerrainBuilder.SetCameraPosition 按生成组逐区块生成与回收。
        /// </summary>
        private void DrawScatterEditView()
        {
            _scatterScroll = EditorGUILayout.BeginScrollView(_scatterScroll);
            EditorGUILayout.LabelField("散布编辑", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "每个生成组在目标层级与离路距离范围的交集内均匀散布。配置资产保存在当前项目的 ScatterConfig 子目录。",
                MessageType.Info);

            _project.scatterSeed = EditorGUILayout.IntField("全局 Seed", _project.scatterSeed);
            EditorGUILayout.Space(8);

            EnsureScatterFoldouts();
            for (int i = 0; i < _project.scatterGroups.Count; i++)
            {
                var group = _project.scatterGroups[i];
                if (group == null) continue;
                DrawScatterGroup(i, group);
            }

            if (GUILayout.Button("+ 添加散布生成组", GUILayout.Height(26f)))
                CreateScatterGroup();

            EditorGUILayout.EndScrollView();
            EditorUtility.SetDirty(_project);
        }

        private void EnsureScatterFoldouts()
        {
            while (_scatterFoldouts.Count < _project.scatterGroups.Count) _scatterFoldouts.Add(true);
            if (_scatterFoldouts.Count > _project.scatterGroups.Count)
                _scatterFoldouts.RemoveRange(_project.scatterGroups.Count,
                    _scatterFoldouts.Count - _project.scatterGroups.Count);
        }

        private void DrawScatterGroup(int index, ScatterConfigSO group)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            _scatterFoldouts[index] = EditorGUILayout.Foldout(
                _scatterFoldouts[index], $"生成组 {index}: {group.groupName}", true);
            if (GUILayout.Button("删除", GUILayout.Width(56f)))
            {
                DeleteScatterGroup(index, group);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            EditorGUILayout.EndHorizontal();

            if (_scatterFoldouts[index])
            {
                group.groupName = EditorGUILayout.TextField("名称", group.groupName);
                group.chunkSize = EditorGUILayout.Vector2Field("区块尺寸（米，x/z）", group.chunkSize);
                group.chunkSize.x = Mathf.Max(0.0001f, group.chunkSize.x);
                group.chunkSize.y = Mathf.Max(0.0001f, group.chunkSize.y);
                group.visibleDistance = EditorGUILayout.FloatField(
                    "可见距离（米，负=无限）", group.visibleDistance);
                group.density = Mathf.Max(0f, EditorGUILayout.FloatField("密度（个/㎡）", group.density));

                var scale = EditorGUILayout.Vector2Field("随机缩放（min/max）", group.randomScale);
                scale.x = Mathf.Max(0f, scale.x);
                scale.y = Mathf.Max(scale.x, scale.y);
                group.randomScale = scale;

                var offRoad = EditorGUILayout.Vector2Field("离路距离范围（米）", group.offRoadDistanceRange);
                offRoad.x = Mathf.Max(0f, offRoad.x);
                offRoad.y = Mathf.Max(offRoad.x, offRoad.y);
                group.offRoadDistanceRange = offRoad;

                group.targetLayers = (TerrainWorkflowLayerMask)EditorGUILayout.EnumFlagsField(
                    "目标层级", group.targetLayers);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Prefab 池（按权重）", EditorStyles.boldLabel);
                DrawScatterPrefabPool(group.prefabs);
                EditorUtility.SetDirty(group);
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawScatterPrefabPool(List<ScatterPrefabEntry> prefabs)
        {
            for (int i = 0; i < prefabs.Count; i++)
            {
                if (prefabs[i] == null) prefabs[i] = new ScatterPrefabEntry();
                EditorGUILayout.BeginHorizontal();
                prefabs[i].prefab = DrawProcessedCandidatePrefabField(
                    $"Prefab[{i}]", prefabs[i].prefab);
                GUILayout.Label("权重", GUILayout.Width(32f));
                prefabs[i].weight = Mathf.Max(0,
                    EditorGUILayout.IntField(prefabs[i].weight, GUILayout.Width(54f)));
                if (GUILayout.Button("-", GUILayout.Width(22f)))
                    prefabs.RemoveAt(i--);
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ 添加 Prefab"))
                prefabs.Add(new ScatterPrefabEntry());
        }

        private static GameObject DrawProcessedCandidatePrefabField(string label, GameObject current)
        {
            EditorGUI.BeginChangeCheck();
            var selected = (GameObject)EditorGUILayout.ObjectField(
                label, current, typeof(GameObject), false);
            if (!EditorGUI.EndChangeCheck() || selected == current)
                return current;
            if (selected == null)
                return null;

            if (PrefabProcessingUtility.IsProcessedCandidatePrefab(selected, out string reason))
                return selected;

            string path = AssetDatabase.GetAssetPath(selected);
            Debug.LogError($"[Terrain Paint Workflow] 拒绝引用未经处理的 Prefab: {path} — {reason}");
            EditorUtility.DisplayDialog(
                "不能使用该 Prefab",
                $"三个摆放模块只能引用本工具生成的备用预制体。\n\n{path}\n{reason}",
                "确定");
            return current;
        }

        private void CreateScatterGroup()
        {
            string projectPath = AssetDatabase.GetAssetPath(_project);
            string projectDir = Path.GetDirectoryName(projectPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(projectDir)) return;

            string folder = projectDir + "/ScatterConfig";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(projectDir, "ScatterConfig");

            var group = CreateInstance<ScatterConfigSO>();
            group.groupName = $"散布生成组 {_project.scatterGroups.Count}";
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/ScatterGroup.asset");
            AssetDatabase.CreateAsset(group, assetPath);
            _project.scatterGroups.Add(group);
            _scatterFoldouts.Add(true);
            EditorUtility.SetDirty(_project);
            AssetDatabase.SaveAssets();
        }

        private void DeleteScatterGroup(int index, ScatterConfigSO group)
        {
            if (!EditorUtility.DisplayDialog("删除散布生成组", $"确定删除“{group.groupName}”及其配置资产？", "删除", "取消"))
                return;

            string assetPath = AssetDatabase.GetAssetPath(group);
            _project.scatterGroups.RemoveAt(index);
            if (index < _scatterFoldouts.Count) _scatterFoldouts.RemoveAt(index);
            EditorUtility.SetDirty(_project);
            if (!string.IsNullOrEmpty(assetPath)) AssetDatabase.DeleteAsset(assetPath);
            _project.DeletePlacementCacheFile(PlacementCache.ScatterFileName(index));
            AssetDatabase.SaveAssets();
        }

        private void DrawPropEditView()
        {
            _propScroll = EditorGUILayout.BeginScrollView(_propScroll);
            EditorGUILayout.LabelField("摆件编辑", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "摆件组保存规则配置；具体实例化与防重叠将在 TerrainBuilder.ApplyProps 中实现。",
                MessageType.Info);
            _project.propSeed = EditorGUILayout.IntField("全局 Seed", _project.propSeed);
            EditorGUILayout.Space(8);

            EnsurePropFoldouts();
            for (int i = 0; i < _project.propGroups.Count; i++)
            {
                var group = _project.propGroups[i];
                if (group == null) continue;
                DrawPropGroup(i, group);
            }
            if (GUILayout.Button("+ 添加摆件生成组", GUILayout.Height(26f))) CreatePropGroup();
            EditorGUILayout.EndScrollView();
            EditorUtility.SetDirty(_project);
        }

        private void EnsurePropFoldouts()
        {
            while (_propFoldouts.Count < _project.propGroups.Count) _propFoldouts.Add(true);
            if (_propFoldouts.Count > _project.propGroups.Count)
                _propFoldouts.RemoveRange(_project.propGroups.Count, _propFoldouts.Count - _project.propGroups.Count);
        }

        private void DrawPropGroup(int index, PropConfigSO group)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            _propFoldouts[index] = EditorGUILayout.Foldout(
                _propFoldouts[index], $"生成组 {index}: {group.groupName}", true);
            if (GUILayout.Button("删除", GUILayout.Width(56f)))
            {
                DeletePropGroup(index, group);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            EditorGUILayout.EndHorizontal();

            if (_propFoldouts[index])
            {
                group.groupName = EditorGUILayout.TextField("名称", group.groupName);
                group.chunkSize = EditorGUILayout.Vector2Field("区块大小（米）", group.chunkSize);
                group.visibleDistance = EditorGUILayout.FloatField("可见距离（米，负=无限）", group.visibleDistance);
                group.maxFailedAttempts = Mathf.Max(0,
                    EditorGUILayout.IntField("失败尝试次数上限", group.maxFailedAttempts));
                group.expectedDensity = Mathf.Max(0f,
                    EditorGUILayout.FloatField("预期密度（个/㎡）", group.expectedDensity));

                var batch = EditorGUILayout.Vector2IntField("生成规模（最少保留/生成数）", group.batchSize);
                batch.x = Mathf.Max(0, batch.x);
                batch.y = Mathf.Max(batch.x, batch.y);
                group.batchSize = batch;

                group.targetLayers = (TerrainWorkflowLayerMask)EditorGUILayout.EnumFlagsField(
                    "目标层级", group.targetLayers);
                group.outOfBoundsTolerance = EditorGUILayout.Slider(
                    "越界宽容", group.outOfBoundsTolerance, 0f, 1f);
                group.arrangementBasis = (PropArrangementBasis)EditorGUILayout.EnumPopup(
                    "排列依据", group.arrangementBasis);

                var range = EditorGUILayout.Vector2Field("排列位置值域", group.arrangementRange);
                if (range.y < range.x) range.y = range.x;
                group.arrangementRange = range;
                group.rotationMode = (PropRotationMode)EditorGUILayout.EnumPopup("旋转", group.rotationMode);
                group.distributionMode = (PropDistributionMode)EditorGUILayout.EnumPopup(
                    "分布形式", group.distributionMode);
                group.distributionSpacing = EditorGUILayout.FloatField(
                    "分布间距（可为负）", group.distributionSpacing);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Prefab 池", EditorStyles.boldLabel);
                DrawPropPrefabPool(group.prefabs);
                EditorUtility.SetDirty(group);
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawPropPrefabPool(List<PropPrefabEntry> prefabs)
        {
            for (int i = 0; i < prefabs.Count; i++)
            {
                if (prefabs[i] == null) prefabs[i] = new PropPrefabEntry();
                EditorGUILayout.BeginHorizontal();
                prefabs[i].prefab = DrawProcessedCandidatePrefabField(
                    $"Prefab[{i}]", prefabs[i].prefab);
                GUILayout.Label("权重", GUILayout.Width(32f));
                prefabs[i].weight = Mathf.Max(0,
                    EditorGUILayout.IntField(prefabs[i].weight, GUILayout.Width(48f)));
                GUILayout.Label("下限", GUILayout.Width(32f));
                prefabs[i].minimumCount = Mathf.Max(0,
                    EditorGUILayout.IntField(prefabs[i].minimumCount, GUILayout.Width(48f)));
                if (GUILayout.Button("-", GUILayout.Width(22f))) prefabs.RemoveAt(i--);
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ 添加 Prefab")) prefabs.Add(new PropPrefabEntry());
        }

        private void CreatePropGroup()
        {
            string projectPath = AssetDatabase.GetAssetPath(_project);
            string projectDir = Path.GetDirectoryName(projectPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(projectDir)) return;
            string folder = projectDir + "/PropConfig";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(projectDir, "PropConfig");

            var group = CreateInstance<PropConfigSO>();
            group.groupName = $"摆件生成组 {_project.propGroups.Count}";
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/PropGroup.asset");
            AssetDatabase.CreateAsset(group, assetPath);
            _project.propGroups.Add(group);
            _propFoldouts.Add(true);
            EditorUtility.SetDirty(_project);
            AssetDatabase.SaveAssets();
        }

        private void DeletePropGroup(int index, PropConfigSO group)
        {
            if (!EditorUtility.DisplayDialog("删除摆件生成组", $"确定删除“{group.groupName}”及其配置资产？", "删除", "取消"))
                return;
            string assetPath = AssetDatabase.GetAssetPath(group);
            _project.propGroups.RemoveAt(index);
            if (index < _propFoldouts.Count) _propFoldouts.RemoveAt(index);
            EditorUtility.SetDirty(_project);
            if (!string.IsNullOrEmpty(assetPath)) AssetDatabase.DeleteAsset(assetPath);
            _project.DeletePlacementCacheFile(PlacementCache.PropFileName(index));
            AssetDatabase.SaveAssets();
        }

        private void DrawFixedPointEditView()
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.5f));
            EditorGUILayout.LabelField("Layer 图（只读）", EditorStyles.boldLabel);
            EnsurePaintMap();
            if (_map != null)
            {
                float size = Mathf.Max(120f, Mathf.Min(position.width * 0.48f, position.height - 90f));
                Rect mapRect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
                GUI.DrawTexture(mapRect, _map.Texture, ScaleMode.ScaleToFit, true);
                DrawRectOutline(mapRect, Color.black, 2f);
                DrawFixedPointMarkers(mapRect);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            _fixedPointScroll = EditorGUILayout.BeginScrollView(_fixedPointScroll);
            EditorGUILayout.LabelField("定点生成组", EditorStyles.boldLabel);
            EnsureFixedPointFoldouts();
            for (int i = 0; i < _project.fixedPointGroups.Count; i++)
            {
                var group = _project.fixedPointGroups[i];
                if (group == null) continue;
                DrawFixedPointGroup(i, group);
            }
            if (GUILayout.Button("+ 添加定点生成组", GUILayout.Height(26f))) CreateFixedPointGroup();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
            EditorUtility.SetDirty(_project);
        }

        private void DrawFixedPointMarkers(Rect mapRect)
        {
            Handles.BeginGUI();
            foreach (var group in _project.fixedPointGroups)
            {
                if (group == null) continue;
                Color marker = group.markerColor;
                marker.a = 1f;
                foreach (var position01 in group.positions)
                {
                    float x = mapRect.x + Mathf.Clamp01(position01.x) * mapRect.width;
                    float y = mapRect.yMax - Mathf.Clamp01(position01.y) * mapRect.height;
                    var center = new Vector3(x, y, 0f);
                    Handles.color = Color.black;
                    Handles.DrawSolidDisc(center, Vector3.forward, 7f);
                    Handles.color = marker;
                    Handles.DrawSolidDisc(center, Vector3.forward, 5f);
                }
            }
            Handles.color = Color.white;
            Handles.EndGUI();
        }

        private void EnsureFixedPointFoldouts()
        {
            while (_fixedPointFoldouts.Count < _project.fixedPointGroups.Count) _fixedPointFoldouts.Add(true);
            if (_fixedPointFoldouts.Count > _project.fixedPointGroups.Count)
                _fixedPointFoldouts.RemoveRange(_project.fixedPointGroups.Count,
                    _fixedPointFoldouts.Count - _project.fixedPointGroups.Count);
        }

        private void DrawFixedPointGroup(int index, FixedPointConfigSO group)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            _fixedPointFoldouts[index] = EditorGUILayout.Foldout(
                _fixedPointFoldouts[index], $"生成组 {index}", true);
            if (GUILayout.Button("删除", GUILayout.Width(56f)))
            {
                DeleteFixedPointGroup(index, group);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            EditorGUILayout.EndHorizontal();

            if (_fixedPointFoldouts[index])
            {
                group.markerColor = EditorGUILayout.ColorField("标识颜色", group.markerColor);
                group.prefab = DrawProcessedCandidatePrefabField("预制体", group.prefab);
                group.chunkSize = EditorGUILayout.Vector2Field("区块大小（米）", group.chunkSize);
                group.visibleDistance = EditorGUILayout.FloatField("可见距离（米，负=无限）", group.visibleDistance);
                group.rotationDegrees = Mathf.Clamp(
                    EditorGUILayout.FloatField("旋转（度）", group.rotationDegrees), 0f, 360f);
                group.scale = Mathf.Max(0f, EditorGUILayout.FloatField("缩放", group.scale));

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("位置列表（归一化 X/Y）", EditorStyles.boldLabel);
                for (int i = 0; i < group.positions.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    Vector2 value = EditorGUILayout.Vector2Field($"位置[{i}]", group.positions[i]);
                    value.x = Mathf.Clamp01(value.x);
                    value.y = Mathf.Clamp01(value.y);
                    group.positions[i] = value;
                    if (GUILayout.Button("-", GUILayout.Width(22f))) group.positions.RemoveAt(i--);
                    EditorGUILayout.EndHorizontal();
                }
                if (GUILayout.Button("+ 添加位置")) group.positions.Add(new Vector2(0.5f, 0.5f));
                EditorUtility.SetDirty(group);
            }
            EditorGUILayout.EndVertical();
        }

        private void CreateFixedPointGroup()
        {
            string projectPath = AssetDatabase.GetAssetPath(_project);
            string projectDir = Path.GetDirectoryName(projectPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(projectDir)) return;
            string folder = projectDir + "/FixedPointConfig";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(projectDir, "FixedPointConfig");

            var group = CreateInstance<FixedPointConfigSO>();
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/FixedPointGroup.asset");
            AssetDatabase.CreateAsset(group, assetPath);
            _project.fixedPointGroups.Add(group);
            _fixedPointFoldouts.Add(true);
            EditorUtility.SetDirty(_project);
            AssetDatabase.SaveAssets();
        }

        private void DeleteFixedPointGroup(int index, FixedPointConfigSO group)
        {
            if (!EditorUtility.DisplayDialog("删除定点生成组", $"确定删除生成组 {index} 及其配置资产？", "删除", "取消"))
                return;
            string assetPath = AssetDatabase.GetAssetPath(group);
            _project.fixedPointGroups.RemoveAt(index);
            if (index < _fixedPointFoldouts.Count) _fixedPointFoldouts.RemoveAt(index);
            EditorUtility.SetDirty(_project);
            if (!string.IsNullOrEmpty(assetPath)) AssetDatabase.DeleteAsset(assetPath);
            _project.DeletePlacementCacheFile(PlacementCache.FixedFileName(index));
            AssetDatabase.SaveAssets();
        }

        /// <summary>保持折叠状态列表长度与 Layer 数量一致（截断或补 false）。</summary>
        private void EnsureFoldoutCount(List<bool> foldouts)
        {
            int n = _project != null ? _project.layers.Count : 0;
            while (foldouts.Count < n) foldouts.Add(false);
            if (foldouts.Count > n) foldouts.RemoveRange(n, foldouts.Count - n);
        }

        // ---------- 区域编辑工具函数（沿用原实现） ----------

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

        private void DrawEllipseOutline(Rect rect, Color color)
        {
            const int segments = 48;
            Vector2 previous = new Vector2(rect.center.x + rect.width * 0.5f, rect.center.y);
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                Vector2 current = new Vector2(
                    rect.center.x + Mathf.Cos(angle) * rect.width * 0.5f,
                    rect.center.y + Mathf.Sin(angle) * rect.height * 0.5f);
                DrawThickLine(previous, current, 2f, color);
                previous = current;
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
                if (UndoLastPaintOperation())
                {
                    PersistLayerMap();
                    Repaint();
                }
                e.Use();
                return;
            }

            if (e.type == EventType.KeyDown && _tool == Tool.PolygonFill
                && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                if (_triPoints.Count >= 3)
                {
                    AddAndApplyPaintOperation(new LayerPaintOperation
                    {
                        type = LayerPaintOperationType.Polygon,
                        points = new List<Vector2Int>(_triPoints),
                        layerIndex = CurrentPaintLayerIndex,
                    });
                    PersistLayerMap();
                }
                _triPoints.Clear();
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

                if (_tool == Tool.TriangleFill || _tool == Tool.PolygonFill || _tool == Tool.SectorFill)
                {
                    Vector2Int px = ScreenToPix(e.mousePosition);
                    _triPoints.Add(px);
                    if (_tool != Tool.PolygonFill && _triPoints.Count == 3)
                    {
                        var a = _triPoints[0];
                        var b = _triPoints[1];
                        var c = _triPoints[2];
                        AddAndApplyPaintOperation(new LayerPaintOperation
                        {
                            type = _tool == Tool.TriangleFill
                                ? LayerPaintOperationType.Triangle
                                : LayerPaintOperationType.Sector,
                            pointA = a,
                            pointB = b,
                            pointC = c,
                            layerIndex = CurrentPaintLayerIndex,
                        });
                        _triPoints.Clear();
                        PersistLayerMap(); // 三角形画完即写
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
                if (_tool == Tool.CircleBrush)
                {
                    AddAndApplyPaintOperation(new LayerPaintOperation
                    {
                        type = LayerPaintOperationType.Line,
                        pointA = _dragStartPx,
                        pointB = _dragCurrentPx,
                        radius = _brushRadius,
                        layerIndex = CurrentPaintLayerIndex,
                    });
                }
                else if (_tool == Tool.RectFill)
                {
                    AddAndApplyPaintOperation(new LayerPaintOperation
                    {
                        type = LayerPaintOperationType.Rectangle,
                        pointA = _dragStartPx,
                        pointB = _dragCurrentPx,
                        layerIndex = CurrentPaintLayerIndex,
                    });
                }
                else if (_tool == Tool.EllipseFill)
                {
                    AddAndApplyPaintOperation(new LayerPaintOperation
                    {
                        type = LayerPaintOperationType.Ellipse,
                        pointA = _dragStartPx,
                        pointB = _dragCurrentPx,
                        layerIndex = CurrentPaintLayerIndex,
                    });
                }

                PersistLayerMap(); // 抬笔时写（圆形/直线/矩形均在本处收尾）
                _dragging = false;
                GUIUtility.hotControl = 0;
                Repaint();
                e.Use();
            }
        }

        private void AddAndApplyPaintOperation(LayerPaintOperation operation)
        {
            if (_project.paintOperations == null)
                _project.paintOperations = new List<LayerPaintOperation>();
            Undo.RecordObject(_project, "添加区域绘画操作");
            _project.paintOperations.Add(operation);
            _map.ApplyPaintOperation(operation, _project.layers);
            EditorUtility.SetDirty(_project);
        }

        private bool UndoLastPaintOperation()
        {
            if (_project == null || _project.paintOperations == null || _project.paintOperations.Count == 0)
                return false;
            Undo.RecordObject(_project, "撤销区域绘画操作");
            _project.paintOperations.RemoveAt(_project.paintOperations.Count - 1);
            _map.RebuildFromPaintOperations(
                _project.mapResolution,
                _project.mapResolution,
                _project.paintOperations,
                _project.layers);
            EditorUtility.SetDirty(_project);
            return true;
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
