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
    /// 顶层五个子界面（顶部工具栏靠右）：工作流配置 / 区域编辑 / 高度编辑 / 贴图编辑 / 植被编辑。
    /// 「工作流配置」整页显示：工作流图（层次图/RGB 图）、Layer 数量（2~16）、各层颜色/名称（Layer0 透明锁定）、Terrain 字段（窗口内临时，不入 SO）。
    /// 「植被编辑」为左右两栏配置并排：左栏 = 原树木编辑配置（全局 + 每层），右栏 = 原细节编辑配置（全局 + 每层），各自独立滚动与折叠。
    /// 其余编辑子界面为左右分栏布局：
    ///   左栏（窄）：全局配置（上，该子界面专属的全局字段）+ 层级配置（下，逐层折叠），整体共同滚动
    ///   右栏（宽）：信息生成（区域编辑=画布绘制；高度编辑=烘焙高度图；贴图编辑=距离场/路网计算）
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
        }

        /// <summary>顶层五个子界面（工作流配置无子页签；植被编辑 = 原树木 + 细节合并）。</summary>
        private enum MainTab
        {
            WorkflowConfig,
            AreaEdit,
            HeightEdit,
            Texture,
            VegetationEdit,
        }

        /// <summary>配置根目录（Assets 相对路径）；每个配置一个子文件夹。</summary>
        public const string ConfigRootDirRelative =
            "Assets/ai-unity-terrain-edit-workflow/LayerEditor/TerrainGeneratorConfigs";

        private const string PrefsLastProject = "AiTerrainWorkflow.LastPaintProject";

        private TerrainPaintProjectSO _project;
        private MainTab _mainTab = MainTab.WorkflowConfig;

        /// <summary>工作流配置中填入的 Terrain（仅窗口内临时，不保存到配置 SO）。</summary>
        private Terrain _terrainField;

        // 创建配置 UI
        private bool _creating;
        private string _newConfigName = "";
        private int _createResolution = 512;

        // 左栏配置滚动状态（全局配置 + 层级配置 共同滚动）
        private Vector2 _configScroll;
        private readonly List<bool> _layerFoldouts = new List<bool>();

        // 植被编辑（树木 + 细节左右并排）：两栏各自独立滚动与折叠
        private Vector2 _treeScroll;
        private Vector2 _detailScroll;
        private readonly List<bool> _treeFoldouts = new List<bool>();
        private readonly List<bool> _detailFoldouts = new List<bool>();

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
            string path = EditorPrefs.GetString(PrefsLastProject, "");
            if (!string.IsNullOrEmpty(path))
                _project = AssetDatabase.LoadAssetAtPath<TerrainPaintProjectSO>(path);
            if (_project != null)
            {
                EnsurePaintMap();
                LoadResultPreview();
            }
        }

        private void OnDisable()
        {
            SavePaintMapIfAny();
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

            // 植被编辑：左右两栏配置并排（左=树木，右=细节）
            if (_mainTab == MainTab.VegetationEdit)
            {
                DrawVegetationEditView();
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
                _project = newProject;
                _resultPreview = null;
                _heightPreview = null;
                _map = null;
                _layerIdsCache = null;
                if (_project != null)
                {
                    EnsurePaintMap();
                    LoadResultPreview();
                }
                RememberProject();
                Repaint();
            }
            if (GUILayout.Button("创建新地形配置", EditorStyles.toolbarButton))
                _creating = !_creating;

            GUILayout.FlexibleSpace();

            // 五个子界面切换按钮（靠右；植被编辑 = 原树木 + 细节合并）
            var mainNames = new[] { "工作流配置", "区域编辑", "高度编辑", "贴图编辑", "植被编辑" };
            int newMain = GUILayout.Toolbar((int)_mainTab, mainNames, EditorStyles.toolbarButton);
            if (newMain != (int)_mainTab)
            {
                SavePaintMapIfAny();
                _mainTab = (MainTab)newMain;
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
            project.mapResolution = _createResolution;
            // 默认创建上限数量的层（Layer0 透明 + 其余颜色层），可在工作流配置中调整
            for (int i = 0; i < TerrainPaintProjectSO.MaxLayerCount; i++)
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

            // 工作流图预览（数据来自 MapData，图片仅用于查看）
            EditorGUILayout.LabelField("工作流图预览（仅显示）", EditorStyles.boldLabel);
            EnsurePaintMap();
            if (_map != null)
            {
                float pw = Mathf.Min(220f, position.width - 60f);
                float ph = pw * _map.Height / (float)Mathf.Max(1, _map.Width);
                GUILayout.Label(_map.Texture, GUILayout.Width(pw), GUILayout.Height(ph));
            }
            if (_resultPreview == null)
                LoadResultPreview();
            if (_resultPreview != null)
            {
                float pw = Mathf.Min(220f, position.width - 60f);
                float ph = pw * _resultPreview.height / (float)Mathf.Max(1, _resultPreview.width);
                GUILayout.Label(_resultPreview, GUILayout.Width(pw), GUILayout.Height(ph));
            }

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

            EditorGUILayout.Space(8);

            // Terrain 字段（窗口内临时，不入 SO）
            EditorGUILayout.LabelField("目标 Terrain（仅本次窗口会话，不保存到配置）", EditorStyles.boldLabel);
            _terrainField = (Terrain)EditorGUILayout.ObjectField(
                "Terrain", _terrainField, typeof(Terrain), true);

            EditorGUILayout.EndScrollView();
            EditorUtility.SetDirty(_project);
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

        /// <summary>贴图编辑 · 全局配置：随机游走参数 + 全局种子 + TerrainLayer 池 + 邻接组 + 烘焙结果。</summary>
        private void DrawTextureGlobalConfig()
        {
            EditorGUILayout.LabelField("贴图编辑 · 全局配置", EditorStyles.boldLabel);
            DrawGlobalConfig();
            EditorGUILayout.Space(10);
            DrawGlobalTerrainLayers();
            EditorGUILayout.Space(10);
            DrawAdjacencyGroups();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("烘焙结果（只读）", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"R 通道 Max: {_lastRMax:F2}");
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

        /// <summary>树木 · 全局配置（植被编辑左栏）：种子 / 区块参数 / 树木 Prefab 池。</summary>
        private void DrawTreeGlobalConfig()
        {
            EditorGUILayout.LabelField("树木 · 全局配置", EditorStyles.boldLabel);

            _project.treeSeed = EditorGUILayout.IntField("树木 Seed（全局）", _project.treeSeed);
            _project.treeChunkSize = EditorGUILayout.Vector2Field("区块尺寸（米，x/z）", _project.treeChunkSize);
            _project.treeVisibleDistance = Mathf.Max(0f, EditorGUILayout.FloatField("可见距离（米）", _project.treeVisibleDistance));

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("树木 Prefab 池", EditorStyles.boldLabel);
            DrawPrefabPool(_project.treePrefabs, "树木");
        }

        /// <summary>细节 · 全局配置（植被编辑右栏）：种子 / 区块参数 / 细节 Prefab 池。</summary>
        private void DrawDetailGlobalConfig()
        {
            EditorGUILayout.LabelField("细节 · 全局配置", EditorStyles.boldLabel);

            _project.detailSeed = EditorGUILayout.IntField("细节 Seed（全局）", _project.detailSeed);
            _project.detailChunkSize = EditorGUILayout.Vector2Field("区块尺寸（米，x/z）", _project.detailChunkSize);
            _project.detailVisibleDistance = Mathf.Max(0f, EditorGUILayout.FloatField("可见距离（米）", _project.detailVisibleDistance));

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("细节 Prefab 池", EditorStyles.boldLabel);
            DrawPrefabPool(_project.detailPrefabs, "细节");
        }

        /// <summary>绘制一个 Prefab 池的编辑列表（带添加/删除按钮）。</summary>
        private void DrawPrefabPool(List<GameObject> pool, string label)
        {
            for (int i = 0; i < pool.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                pool[i] = (GameObject)EditorGUILayout.ObjectField(
                    $"{label} Prefab[{i}]", pool[i], typeof(GameObject), false);
                if (GUILayout.Button("-", GUILayout.Width(20)))
                {
                    pool.RemoveAt(i--);
                    _project.SyncAllLayerWeights();
                }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button($"+ 添加{label} Prefab"))
            {
                pool.Add(null);
                _project.SyncAllLayerWeights();
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
                        DrawLayerConfig(i, layer);
                    }
                    break;
            }
            EditorUtility.SetDirty(_project);
        }

        /// <summary>树木 · 单个层级的配置：密度/缩放/离路限制 + 树木生成权重列表（foldouts = 界面独立折叠状态）。</summary>
        private void DrawTreeLayerConfig(int index, LayerConfigSO layer, List<bool> foldouts)
        {
            EditorGUILayout.BeginHorizontal();
            bool open = foldouts[index];

            var swatchRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
            var c = new Color(layer.color.r / 255f, layer.color.g / 255f, layer.color.b / 255f, 1f);
            DrawTinted(swatchRect, c);

            open = EditorGUILayout.Foldout(open, $"Layer{index}  {layer.layerName}", true);
            foldouts[index] = open;
            EditorGUILayout.EndHorizontal();

            if (!open)
                return;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("生成参数", EditorStyles.boldLabel);
            layer.treeDensity = Mathf.Max(0f, EditorGUILayout.FloatField("密度（个/㎡）", layer.treeDensity));
            layer.treeScale = EditorGUILayout.Vector2Field("随机缩放（min~max）", layer.treeScale);
            layer.treeRoadDistanceLimit = Mathf.Max(0f, EditorGUILayout.FloatField("最小离路距离（米，0=不限）", layer.treeRoadDistanceLimit));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("树木生成权重（0 = 不生成；索引对应全局树池）", EditorStyles.boldLabel);
            DrawPrefabWeightList(layer.treeWeights, _project.treePrefabs, "树木");
            EditorGUILayout.EndVertical();
            EditorUtility.SetDirty(layer);
        }

        /// <summary>细节 · 单个层级的配置：密度/缩放/离路限制 + 细节生成权重列表（foldouts = 界面独立折叠状态）。</summary>
        private void DrawDetailLayerConfig(int index, LayerConfigSO layer, List<bool> foldouts)
        {
            EditorGUILayout.BeginHorizontal();
            bool open = foldouts[index];

            var swatchRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
            var c = new Color(layer.color.r / 255f, layer.color.g / 255f, layer.color.b / 255f, 1f);
            DrawTinted(swatchRect, c);

            open = EditorGUILayout.Foldout(open, $"Layer{index}  {layer.layerName}", true);
            foldouts[index] = open;
            EditorGUILayout.EndHorizontal();

            if (!open)
                return;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("生成参数", EditorStyles.boldLabel);
            layer.detailDensity = Mathf.Max(0f, EditorGUILayout.FloatField("密度（个/㎡）", layer.detailDensity));
            layer.detailScale = EditorGUILayout.Vector2Field("随机缩放（min~max）", layer.detailScale);
            layer.detailRoadDistanceLimit = Mathf.Max(0f, EditorGUILayout.FloatField("最小离路距离（米，0=不限）", layer.detailRoadDistanceLimit));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("细节生成权重（0 = 不生成；索引对应全局细节池）", EditorStyles.boldLabel);
            DrawPrefabWeightList(layer.detailWeights, _project.detailPrefabs, "细节");
            EditorGUILayout.EndVertical();
            EditorUtility.SetDirty(layer);
        }

        /// <summary>
        /// 绘制某层级的 Prefab 生成权重列表：每行 = 池中一个 Prefab（灰色占位缩略图 + 名称 + 权重 IntField）。
        /// 权重 0 = 该层不生成此 Prefab。占位图暂为灰色，后续接入实际物体图片。
        /// </summary>
        private void DrawPrefabWeightList(List<int> weights, List<GameObject> pool, string label)
        {
            if (pool.Count == 0)
            {
                EditorGUILayout.HelpBox($"{label} Prefab 池为空，请先在全局配置中添加。", MessageType.Info);
                return;
            }
            // 池增删后确保长度对齐
            while (weights.Count < pool.Count) weights.Add(0);
            if (weights.Count > pool.Count) weights.RemoveRange(pool.Count, weights.Count - pool.Count);

            const float thumbSize = 28f;
            for (int i = 0; i < pool.Count; i++)
            {
                var prefab = pool[i];
                string objName = prefab != null ? prefab.name : $"{label}[{i}]";

                EditorGUILayout.BeginHorizontal();
                // 灰色占位缩略图（后续接入实际物体图片）
                var thumbRect = GUILayoutUtility.GetRect(thumbSize, thumbSize, GUILayout.Width(thumbSize), GUILayout.Height(thumbSize));
                DrawTinted(thumbRect, new Color(0.5f, 0.5f, 0.5f, 1f));
                DrawRectOutline(thumbRect, new Color(0f, 0f, 0f, 0.4f), 1f);

                GUILayout.Space(4);
                // 名称自动占满剩余宽度（不固定，避免截断）
                EditorGUILayout.LabelField($"  [{i}] {objName}", EditorStyles.label);
                GUILayout.FlexibleSpace();
                GUILayout.Label("权重", EditorStyles.miniLabel, GUILayout.Width(32));
                // 无 label 前缀的输入框，宽度独立，数值完整可见
                weights[i] = Mathf.Max(0, EditorGUILayout.IntField(weights[i], GUILayout.Width(64)));
                EditorGUILayout.EndHorizontal();
            }
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

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("全局贴图种子（value-noise）", EditorStyles.boldLabel);
            _project.naturalSeed = EditorGUILayout.IntField("自然贴图种子", _project.naturalSeed);
            _project.roadSeed = EditorGUILayout.IntField("道路贴图种子", _project.roadSeed);
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

        private void DrawLayerConfig(int index, LayerConfigSO layer)
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

            // ① 道路生成参数
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("道路生成参数", EditorStyles.boldLabel);
            layer.generateRoad = EditorGUILayout.Toggle("生成道路", layer.generateRoad);
            layer.roadWidth = Mathf.Max(0.01f, EditorGUILayout.FloatField("Road Width (m)", layer.roadWidth));
            layer.roadSpacingMin = Mathf.Max(0.01f, EditorGUILayout.FloatField("Road Spacing Min (m)", layer.roadSpacingMin));
            layer.roadFinalRemap = EditorGUILayout.CurveField("Road Final Remap", layer.roadFinalRemap);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(2);

            // ② 自然贴图混合权重
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("自然贴图混合权重（0 = 不纳入；索引对应全局池）", EditorStyles.boldLabel);
            DrawWeightList(layer.naturalLayerWeights, _project.naturalTerrainLayers, "自然");
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(2);

            // ③ 道路贴图混合权重
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

            int w = _map.Width;
            int h = _map.Height;
            int[] ids = _layerIdsCache;
            if (ids == null || ids.Length != w * h)
            {
                ids = TerrainRoadGen.ParseLayerIds(_map.Texture, _project.layers);
                _layerIdsCache = ids;
            }

            var data = TerrainRoadGen.BakeHeightData(_project, ids, w, h);
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

            var data = _project.ReadMap("layerMap");
            if (data != null && data.Length > 0)
            {
                _map = new LayerMap(1, 1);
                _map.LoadFromIdArray(data, _project.layers);
            }
            else
            {
                int res = Mathf.Clamp(_project.mapResolution, 128, 1024);
                _map = new LayerMap(res, res);
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

            GUILayout.Label($"尺寸 {_project.mapResolution}×{_project.mapResolution}", EditorStyles.miniLabel);
            if (GUILayout.Button("重置画布", EditorStyles.toolbarButton))
            {
                _map.Resize(_project.mapResolution, _project.mapResolution);
                _triPoints.Clear();
                PersistLayerMap();
                Repaint();
            }

            GUILayout.Space(8);
            if (GUILayout.Button("撤销", EditorStyles.toolbarButton))
            {
                if (_map.Undo())
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

        // ---------- ② 贴图编辑 ----------

        private void DrawTextureView()
        {
            _texScroll = EditorGUILayout.BeginScrollView(_texScroll);

            EditorGUILayout.HelpBox(
                "TerrainLayer 池、贴图种子与层级权重请在上方「全局配置」「层级配置」页签中编辑。\n" +
                "本页签仅负责距离场 + 路网计算。",
                MessageType.Info);

            EditorGUILayout.Space(6);
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
                    $"邻接组配置错误：以下层级被加入多个组，计算已阻断。\n{names}\n\n请到「贴图编辑 · 全局配置」的邻接组中修正。",
                    "确定");
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

            var tex = TerrainRoadGen.ComputeAll(_project, ids, w, h, out var rArr, out var gArr, out var bArr);
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
                TerrainRoadGen.ComputeOffRoad(ids, bArr, w, h, _project.config.worldPerPixel), w, h));
            _project.RefreshMapDataRefs(true);
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

        // ---------- ③ 植被编辑（树木 + 细节合并界面：左栏树木配置，右栏细节配置） ----------

        /// <summary>
        /// 植被编辑子界面：左右两栏配置并排，各自独立滚动与折叠。
        /// 左栏 = 原树木编辑配置（全局：Seed/区块/Prefab 池 + 每层：密度/缩放/离路限制/权重）；
        /// 右栏 = 原细节编辑配置（全局：Seed/区块/Prefab 池 + 每层：密度/缩放/离路限制/权重）。
        /// 树木/细节位置均不在此生成：构建时由 TerrainBuilder.SetCameraPosition 按区块动态生成（见 README 阶段 5/6/7）。
        /// </summary>
        private void DrawVegetationEditView()
        {
            const float colWidth = 360f;

            EditorGUILayout.BeginHorizontal();

            // 左栏：树木配置（全局 + 层级，共同滚动）
            EditorGUILayout.BeginVertical(GUILayout.Width(colWidth));
            _treeScroll = EditorGUILayout.BeginScrollView(_treeScroll);
            DrawTreeGlobalConfig();
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("层级配置 · 树木（每层树木生成权重）", EditorStyles.boldLabel);
            EnsureFoldoutCount(_treeFoldouts);
            for (int i = 0; i < _project.layers.Count; i++)
            {
                var layer = _project.layers[i];
                if (layer == null) continue;
                DrawTreeLayerConfig(i, layer, _treeFoldouts);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // 分隔线
            EditorGUILayout.BeginVertical(GUILayout.Width(6));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();

            // 右栏：细节配置（全局 + 层级，共同滚动）
            EditorGUILayout.BeginVertical(GUILayout.Width(colWidth));
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
            DrawDetailGlobalConfig();
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("层级配置 · 细节（每层细节生成权重）", EditorStyles.boldLabel);
            EnsureFoldoutCount(_detailFoldouts);
            for (int i = 0; i < _project.layers.Count; i++)
            {
                var layer = _project.layers[i];
                if (layer == null) continue;
                DrawDetailLayerConfig(i, layer, _detailFoldouts);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
            EditorUtility.SetDirty(_project);
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

        private void HandleCanvasEvents()
        {
            if (_map == null)
                return;

            var e = Event.current;
            bool inCanvas = _canvasRect.Contains(e.mousePosition);

            if (e.type == EventType.KeyDown && e.control && e.keyCode == KeyCode.Z)
            {
                if (_map.Undo())
                {
                    PersistLayerMap();
                    Repaint();
                }
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

                PersistLayerMap(); // 抬笔时写（圆形/直线/矩形均在本处收尾）
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
