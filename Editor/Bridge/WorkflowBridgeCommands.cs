#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using AiTerrainWorkflow.LayerEditor;
using UnityEditor;
using UnityEngine;
using UnityPythonBridge;

namespace AiTerrainWorkflow.Editor.Bridge
{
    [Serializable]
    public sealed class WorkflowBridgeResult
    {
        public string operation;
        public string projectPath;
        public string message;
        public int count;
        public bool valid;
        public string[] errors;
    }

    [Serializable] public sealed class IntGroupSpec { public int[] values; }
    [Serializable] public sealed class WeightedPrefabSpec { public string path; public int weight = 1; public int minimumCount; }
    [Serializable]
    public sealed class LayerSpec
    {
        public int index;
        public string name;
        public float[] color;
        public float[] heightRange;
        public bool generateRoad = true;
        public float roadWidth = 2f;
        public float roadSpacingMin = 4f;
        public int[] naturalWeights;
        public int[] roadWeights;
    }
    [Serializable]
    public sealed class PrefabSpec
    {
        public string path;
        public string billboardMode;
        public bool twoPointHeightAdaptation;
    }
    [Serializable]
    public sealed class PaintOperationSpec
    {
        public string type;
        public int[] a;
        public int[] b;
        public int[] c;
        public int radius;
        public int layerIndex;
    }
    [Serializable]
    public sealed class ScatterGroupSpec
    {
        public string name;
        public float[] chunkSize;
        public float visibleDistance = 60f;
        public float density = 0.05f;
        public float[] randomScale;
        public float[] offRoadRange;
        public int layerMask = -2;
        public WeightedPrefabSpec[] prefabs;
    }
    [Serializable]
    public sealed class PropGroupSpec
    {
        public string name;
        public int maxFailedAttempts = 20;
        public float density = 0.01f;
        public int[] batchSize;
        public int layerMask = -2;
        public float outOfBoundsTolerance = 1f;
        public string basis;
        public float[] range;
        public string rotation;
        public string distribution;
        public float spacing;
        public WeightedPrefabSpec[] prefabs;
    }
    [Serializable]
    public sealed class FixedGroupSpec
    {
        public string prefab;
        public float[] positions;
        public float rotation;
        public float scale = 1f;
    }
    [Serializable]
    public sealed class WorkflowManifest
    {
        public string projectPath;
        public string projectName;
        public int resolution = 512;
        public int heightSeed;
        public float heightScale = 1f;
        public int naturalSeed;
        public int roadSeed;
        public int scatterSeed;
        public int propSeed;
        public float previewWorldPerPixel = 0.4f;
        public string[] naturalTerrainLayers;
        public string[] roadTerrainLayers;
        public LayerSpec[] layers;
        public IntGroupSpec[] adjacencyGroups;
        public PrefabSpec[] prefabs;
        public PaintOperationSpec[] areaOperations;
        public ScatterGroupSpec[] scatterGroups;
        public PropGroupSpec[] propGroups;
        public FixedGroupSpec[] fixedGroups;
        public bool bake = true;
        public string terrain;
        public string applyThrough = "FixedPointEdit";
    }

    public static class WorkflowBridgeCommands
    {
        private const string ConfigRoot = "Assets/ai-unity-terrain-edit-workflow/TerrainGeneratorConfigs";

        [BridgeCommand("workflow.project.create", "创建工作流项目。参数: name, width(128/256/512/1024)")]
        public static object CreateProject(BridgeContext context, BridgeArgs args)
        {
            int resolution = args.width > 0 ? args.width : 512;
            TerrainPaintProjectSO project = CreateProjectAsset(args.name, resolution);
            return Result("project.create", AssetDatabase.GetAssetPath(project), "created", 1);
        }

        [BridgeCommand("workflow.configure", "用 message 中的 JSON manifest 配置项目资产与生成组")]
        public static object Configure(BridgeContext context, BridgeArgs args)
        {
            WorkflowManifest manifest = ParseManifest(args.message);
            TerrainPaintProjectSO project = ResolveOrCreateProject(args.path, manifest);
            ApplyManifest(project, manifest);
            SaveProject(project);
            return Result("configure", AssetDatabase.GetAssetPath(project), "configured", 1);
        }

        [BridgeCommand("workflow.prefab.build", "构建备用 Prefab。参数: path=源Prefab, type=BillboardMode, placed=两点高度适应")]
        public static object BuildPrefab(BridgeContext context, BridgeArgs args)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(NormalizeAssetPath(args.path));
            if (source == null) throw new ArgumentException("找不到源 Prefab: " + args.path);
            BillboardMode mode = ParseEnum(args.type, BillboardMode.None);
            GameObject result = PrefabProcessingUtility.BuildCandidatePrefab(source, mode, args.placed);
            return Result("prefab.build", AssetDatabase.GetAssetPath(result), "built", 1);
        }

        [BridgeCommand("workflow.prefab.update_bounds", "批量更新备用 Prefab Bounds。active=1 强制刷新")]
        public static object UpdateBounds(BridgeContext context, BridgeArgs args)
        {
            int count = PrefabProcessingUtility.UpdateAllBounds(args.active == 1);
            return Result("prefab.update_bounds", null, "updated", count);
        }

        [BridgeCommand("workflow.prefab.update_billboards", "批量更新备用 Prefab Billboard")]
        public static object UpdateBillboards(BridgeContext context, BridgeArgs args)
        {
            int count = PrefabProcessingUtility.UpdateAllBillboards();
            return Result("prefab.update_billboards", null, "updated", count);
        }

        [BridgeCommand("workflow.area.rebuild", "用 message JSON 操作数组完整重建区域图。参数: path=项目")]
        public static object RebuildArea(BridgeContext context, BridgeArgs args)
        {
            TerrainPaintProjectSO project = LoadProject(args.path);
            PaintOperationSpec[] operations = ParseOperations(args.message);
            SetAreaOperations(project, operations);
            BakeArea(project);
            SaveProject(project);
            return Result("area.rebuild", AssetDatabase.GetAssetPath(project), "rebuilt", operations.Length);
        }

        [BridgeCommand("workflow.bake", "重建 layerMap，并烘焙 height/distance/occupancy/road/offRoad。参数: path=项目")]
        public static object Bake(BridgeContext context, BridgeArgs args)
        {
            TerrainPaintProjectSO project = LoadProject(args.path);
            BakeAll(project);
            SaveProject(project);
            return Result("bake", AssetDatabase.GetAssetPath(project), "baked", 5);
        }

        [BridgeCommand("workflow.validate", "验证项目及所有摆放 Prefab。参数: path=项目")]
        public static object Validate(BridgeContext context, BridgeArgs args)
        {
            TerrainPaintProjectSO project = LoadProject(args.path);
            string[] errors = ValidateProject(project).ToArray();
            return new WorkflowBridgeResult
            {
                operation = "validate", projectPath = AssetDatabase.GetAssetPath(project),
                message = errors.Length == 0 ? "valid" : "invalid", valid = errors.Length == 0,
                errors = errors, count = errors.Length,
            };
        }

        [BridgeCommand("workflow.build", "构建 Terrain。参数: path=项目, terrain=场景Terrain名, type=最终阶段")]
        public static object BuildTerrain(BridgeContext context, BridgeArgs args)
        {
            TerrainPaintProjectSO project = LoadProject(args.path);
            List<string> errors = ValidateProject(project);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
            Terrain terrain = FindTerrain(args.terrain);
            TerrainWorkflowStage stage = ParseEnum(args.type, TerrainWorkflowStage.FixedPointEdit);
            TerrainBuilder builder = terrain.GetComponent<TerrainBuilder>();
            if (builder == null) builder = terrain.gameObject.AddComponent<TerrainBuilder>();
            builder.Build(project, terrain, stage);
            EditorUtility.SetDirty(terrain.gameObject);
            return Result("build", AssetDatabase.GetAssetPath(project), "built " + terrain.name, 1);
        }

        [BridgeCommand("workflow.run", "执行完整 JSON manifest：创建/配置/Prefab/区域/烘焙/验证/可选Build")]
        public static object Run(BridgeContext context, BridgeArgs args)
        {
            WorkflowManifest manifest = ParseManifest(args.message);
            TerrainPaintProjectSO project = ResolveOrCreateProject(args.path, manifest);
            if (manifest.prefabs != null)
                foreach (PrefabSpec spec in manifest.prefabs) BuildPrefabSpec(spec);
            // Generation groups may reference processed prefabs created above.
            ApplyManifest(project, manifest);
            if (manifest.areaOperations != null) SetAreaOperations(project, manifest.areaOperations);
            if (manifest.bake) BakeAll(project); else BakeArea(project);
            SaveProject(project);
            List<string> errors = ValidateProject(project);
            if (errors.Count > 0)
                return new WorkflowBridgeResult { operation = "run", projectPath = AssetDatabase.GetAssetPath(project), valid = false, errors = errors.ToArray(), count = errors.Count, message = "validation failed" };
            if (!string.IsNullOrWhiteSpace(manifest.terrain))
            {
                Terrain terrain = FindTerrain(manifest.terrain);
                TerrainBuilder builder = terrain.GetComponent<TerrainBuilder>() ?? terrain.gameObject.AddComponent<TerrainBuilder>();
                builder.Build(project, terrain, ParseEnum(manifest.applyThrough, TerrainWorkflowStage.FixedPointEdit));
                EditorUtility.SetDirty(terrain.gameObject);
            }
            return Result("run", AssetDatabase.GetAssetPath(project), "complete", 1);
        }

        private static TerrainPaintProjectSO ResolveOrCreateProject(string argumentPath, WorkflowManifest manifest)
        {
            string path = !string.IsNullOrWhiteSpace(argumentPath) ? argumentPath : manifest.projectPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                TerrainPaintProjectSO loaded = AssetDatabase.LoadAssetAtPath<TerrainPaintProjectSO>(NormalizeAssetPath(path));
                if (loaded != null) return loaded;
            }
            return CreateProjectAsset(manifest.projectName, manifest.resolution);
        }

        private static TerrainPaintProjectSO CreateProjectAsset(string name, int resolution)
        {
            name = string.IsNullOrWhiteSpace(name) ? "TerrainWorkflow" : SanitizeName(name);
            if (Array.IndexOf(TerrainPaintProjectSO.AllowedResolutions, resolution) < 0)
                throw new ArgumentException("resolution 必须是 128/256/512/1024");
            EnsureFolder(ConfigRoot);
            string directory = ConfigRoot + "/" + name;
            EnsureFolder(directory);
            EnsureFolder(directory + "/ScatterConfig");
            EnsureFolder(directory + "/PropConfig");
            EnsureFolder(directory + "/FixedPointConfig");
            string projectPath = directory + "/" + name + ".asset";
            if (AssetDatabase.LoadAssetAtPath<TerrainPaintProjectSO>(projectPath) != null)
                throw new InvalidOperationException("项目已存在: " + projectPath);
            var project = ScriptableObject.CreateInstance<TerrainPaintProjectSO>();
            project.name = name; project.mapResolution = resolution;
            for (int i = 0; i < TerrainPaintProjectSO.MaxLayerCount; i++)
            {
                var layer = ScriptableObject.CreateInstance<LayerConfigSO>();
                layer.color = i == 0 ? LayerPalette.Transparent : LayerPalette.PresetColors[Mathf.Min(i - 1, LayerPalette.PresetColors.Length - 1)];
                layer.layerName = i == 0 ? "过渡(透明)" : LayerPalette.PresetDefaultNames[Mathf.Min(i - 1, LayerPalette.PresetDefaultNames.Length - 1)];
                AssetDatabase.CreateAsset(layer, directory + $"/Layer_{i:00}.asset");
                project.layers.Add(layer);
            }
            AssetDatabase.CreateAsset(project, projectPath);
            SaveProject(project);
            return project;
        }

        private static void ApplyManifest(TerrainPaintProjectSO project, WorkflowManifest manifest)
        {
            project.mapResolution = manifest.resolution > 0 ? manifest.resolution : project.mapResolution;
            project.heightSeed = manifest.heightSeed; project.heightScale = manifest.heightScale > 0 ? manifest.heightScale : project.heightScale;
            project.naturalSeed = manifest.naturalSeed; project.roadSeed = manifest.roadSeed;
            project.scatterSeed = manifest.scatterSeed; project.propSeed = manifest.propSeed;
            project.config.worldPerPixel = manifest.previewWorldPerPixel > 0 ? manifest.previewWorldPerPixel : project.config.worldPerPixel;
            ReplaceAssets(project.naturalTerrainLayers, manifest.naturalTerrainLayers);
            ReplaceAssets(project.roadTerrainLayers, manifest.roadTerrainLayers);
            ApplyLayers(project, manifest.layers);
            project.adjacencyGroups.Clear();
            if (manifest.adjacencyGroups != null)
                foreach (IntGroupSpec group in manifest.adjacencyGroups)
                    project.adjacencyGroups.Add(group?.values != null ? new List<int>(group.values) : new List<int>());
            if (manifest.scatterGroups != null) ReplaceScatterGroups(project, manifest.scatterGroups);
            if (manifest.propGroups != null) ReplacePropGroups(project, manifest.propGroups);
            if (manifest.fixedGroups != null) ReplaceFixedGroups(project, manifest.fixedGroups);
            project.SyncAllLayerWeights();
        }

        private static void ApplyLayers(TerrainPaintProjectSO project, LayerSpec[] specs)
        {
            if (specs == null) return;
            foreach (LayerSpec spec in specs)
            {
                if (spec == null || spec.index < 0 || spec.index >= project.layers.Count) continue;
                LayerConfigSO layer = project.layers[spec.index];
                if (!string.IsNullOrWhiteSpace(spec.name)) layer.layerName = spec.name;
                if (spec.index > 0 && spec.color?.Length >= 4) layer.color = new Color(spec.color[0], spec.color[1], spec.color[2], spec.color[3]);
                if (spec.heightRange?.Length >= 2) layer.heightRange = new Vector2(spec.heightRange[0], spec.heightRange[1]);
                layer.generateRoad = spec.generateRoad; layer.roadWidth = spec.roadWidth; layer.roadSpacingMin = spec.roadSpacingMin;
                if (spec.naturalWeights != null) layer.naturalLayerWeights = new List<int>(spec.naturalWeights);
                if (spec.roadWeights != null) layer.roadLayerWeights = new List<int>(spec.roadWeights);
                EditorUtility.SetDirty(layer);
            }
        }

        private static void ReplaceScatterGroups(TerrainPaintProjectSO project, ScatterGroupSpec[] specs)
        {
            DeleteReferencedAssets(project.scatterGroups); project.scatterGroups.Clear();
            string folder = ProjectDirectory(project) + "/ScatterConfig"; EnsureFolder(folder);
            for (int i = 0; i < specs.Length; i++)
            {
                ScatterGroupSpec s = specs[i]; if (s == null) continue;
                var group = ScriptableObject.CreateInstance<ScatterConfigSO>(); group.groupName = s.name;
                if (s.chunkSize?.Length >= 2) group.chunkSize = new Vector2(s.chunkSize[0], s.chunkSize[1]);
                group.visibleDistance = s.visibleDistance; group.density = s.density;
                if (s.randomScale?.Length >= 2) group.randomScale = new Vector2(s.randomScale[0], s.randomScale[1]);
                if (s.offRoadRange?.Length >= 2) group.offRoadDistanceRange = new Vector2(s.offRoadRange[0], s.offRoadRange[1]);
                group.targetLayers = (TerrainWorkflowLayerMask)(ushort)s.layerMask;
                if (s.prefabs != null) foreach (WeightedPrefabSpec p in s.prefabs) group.prefabs.Add(new ScatterPrefabEntry { prefab = LoadPrefab(p.path), weight = p.weight });
                AssetDatabase.CreateAsset(group, folder + $"/Scatter_{i:00}.asset"); project.scatterGroups.Add(group);
            }
        }

        private static void ReplacePropGroups(TerrainPaintProjectSO project, PropGroupSpec[] specs)
        {
            DeleteReferencedAssets(project.propGroups); project.propGroups.Clear();
            string folder = ProjectDirectory(project) + "/PropConfig"; EnsureFolder(folder);
            for (int i = 0; i < specs.Length; i++)
            {
                PropGroupSpec s = specs[i]; if (s == null) continue;
                var group = ScriptableObject.CreateInstance<PropConfigSO>(); group.groupName = s.name;
                group.maxFailedAttempts = s.maxFailedAttempts; group.expectedDensity = s.density;
                if (s.batchSize?.Length >= 2) group.batchSize = new Vector2Int(s.batchSize[0], s.batchSize[1]);
                group.targetLayers = (TerrainWorkflowLayerMask)(ushort)s.layerMask; group.outOfBoundsTolerance = s.outOfBoundsTolerance;
                group.arrangementBasis = ParseEnum(s.basis, PropArrangementBasis.OffRoad);
                if (s.range?.Length >= 2) group.arrangementRange = new Vector2(s.range[0], s.range[1]);
                group.rotationMode = ParseEnum(s.rotation, PropRotationMode.Random); group.distributionMode = ParseEnum(s.distribution, PropDistributionMode.Scatter); group.distributionSpacing = s.spacing;
                if (s.prefabs != null) foreach (WeightedPrefabSpec p in s.prefabs) group.prefabs.Add(new PropPrefabEntry { prefab = LoadPrefab(p.path), weight = p.weight, minimumCount = p.minimumCount });
                AssetDatabase.CreateAsset(group, folder + $"/Prop_{i:00}.asset"); project.propGroups.Add(group);
            }
        }

        private static void ReplaceFixedGroups(TerrainPaintProjectSO project, FixedGroupSpec[] specs)
        {
            DeleteReferencedAssets(project.fixedPointGroups); project.fixedPointGroups.Clear();
            string folder = ProjectDirectory(project) + "/FixedPointConfig"; EnsureFolder(folder);
            for (int i = 0; i < specs.Length; i++)
            {
                FixedGroupSpec s = specs[i]; if (s == null) continue;
                var group = ScriptableObject.CreateInstance<FixedPointConfigSO>(); group.prefab = LoadPrefab(s.prefab); group.rotationDegrees = s.rotation; group.scale = s.scale;
                if (s.positions != null) for (int n = 0; n + 1 < s.positions.Length; n += 2) group.positions.Add(new Vector2(s.positions[n], s.positions[n + 1]));
                AssetDatabase.CreateAsset(group, folder + $"/Fixed_{i:00}.asset"); project.fixedPointGroups.Add(group);
            }
        }

        private static void SetAreaOperations(TerrainPaintProjectSO project, PaintOperationSpec[] specs)
        {
            project.paintOperations.Clear();
            if (specs == null) return;
            foreach (PaintOperationSpec spec in specs)
            {
                if (spec == null) continue;
                project.paintOperations.Add(new LayerPaintOperation
                {
                    type = ParseEnum(spec.type, LayerPaintOperationType.Line), pointA = Point(spec.a), pointB = Point(spec.b), pointC = Point(spec.c), radius = spec.radius, layerIndex = spec.layerIndex,
                });
            }
        }

        private static void BakeArea(TerrainPaintProjectSO project)
        {
            var map = new LayerMap(project.mapResolution, project.mapResolution);
            map.RebuildFromPaintOperations(project.mapResolution, project.mapResolution, project.paintOperations, project.layers);
            project.WriteMap("layerMap", map.ToIdArray(project.layers));
            UnityEngine.Object.DestroyImmediate(map.Texture);
        }

        private static void BakeAll(TerrainPaintProjectSO project)
        {
            BakeArea(project);
            float[][] layerMap = project.ReadMap("layerMap"); int h = layerMap.Length, w = layerMap[0].Length;
            int[] ids = new int[w * h]; for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) ids[y * w + x] = Mathf.RoundToInt(layerMap[y][x]);
            project.WriteMap("height", TerrainRoadGen.BakeHeightData(project, ids, w, h));
            Texture2D preview = TerrainRoadGen.ComputeAll(project, ids, w, h, out var r, out var g, out var b);
            if (preview == null) throw new InvalidOperationException("道路数据计算失败"); UnityEngine.Object.DestroyImmediate(preview);
            project.WriteMap("distance", CsvArrayCodec.ToJagged(r, w, h)); project.WriteMap("occupancy", CsvArrayCodec.ToJagged(g, w, h)); project.WriteMap("road", CsvArrayCodec.ToJagged(b, w, h));
            project.WriteMap("offRoad", CsvArrayCodec.ToJagged(TerrainRoadGen.ComputeOffRoad(ids, b, w, h, project.config.worldPerPixel), w, h));
            project.RefreshMapDataRefs(true);
        }

        private static List<string> ValidateProject(TerrainPaintProjectSO project)
        {
            var errors = new List<string>();
            if (project.layers == null || project.layers.Count < TerrainPaintProjectSO.MinLayerCount) errors.Add("layers 数量不足");
            foreach (int duplicate in project.FindDuplicateLayerIndices()) errors.Add("Layer 出现在多个邻接组: " + duplicate);
            ValidatePrefabs(project.scatterGroups, g => g?.prefabs, e => e?.prefab, "Scatter", errors);
            ValidatePrefabs(project.propGroups, g => g?.prefabs, e => e?.prefab, "Prop", errors);
            if (project.fixedPointGroups != null) foreach (FixedPointConfigSO group in project.fixedPointGroups) ValidatePrefab(group?.prefab, "Fixed", errors);
            return errors;
        }

        private static void ValidatePrefabs<TGroup, TEntry>(IEnumerable<TGroup> groups, Func<TGroup, IEnumerable<TEntry>> entries, Func<TEntry, GameObject> prefab, string label, List<string> errors)
        {
            if (groups == null) return; foreach (TGroup group in groups) { IEnumerable<TEntry> list = entries(group); if (list == null) continue; foreach (TEntry entry in list) ValidatePrefab(prefab(entry), label, errors); }
        }
        private static void ValidatePrefab(GameObject prefab, string label, List<string> errors)
        {
            if (prefab == null) { errors.Add(label + " Prefab 为空"); return; }
            if (!PrefabProcessingUtility.IsProcessedCandidatePrefab(prefab, out string reason)) errors.Add(label + ": " + AssetDatabase.GetAssetPath(prefab) + " — " + reason);
            PrefabStructureInfo info = prefab.GetComponent<PrefabStructureInfo>();
            if (info != null && info.billboardMode != BillboardMode.None && prefab.GetComponent<LODGroup>() == null) errors.Add(label + ": 缺少 LODGroup — " + AssetDatabase.GetAssetPath(prefab));
        }

        private static void BuildPrefabSpec(PrefabSpec spec)
        {
            if (spec == null) return; GameObject source = LoadPrefab(spec.path); if (source == null) throw new ArgumentException("找不到 Prefab: " + spec.path);
            PrefabProcessingUtility.BuildCandidatePrefab(source, ParseEnum(spec.billboardMode, BillboardMode.None), spec.twoPointHeightAdaptation);
        }
        private static Terrain FindTerrain(string name)
        {
            Terrain[] terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            if (string.IsNullOrWhiteSpace(name)) { if (terrains.Length == 0) throw new InvalidOperationException("场景中没有 Terrain"); return terrains[0]; }
            foreach (Terrain terrain in terrains) if (terrain.name == name) return terrain;
            throw new InvalidOperationException("找不到 Terrain: " + name);
        }
        private static TerrainPaintProjectSO LoadProject(string path)
        {
            TerrainPaintProjectSO project = AssetDatabase.LoadAssetAtPath<TerrainPaintProjectSO>(NormalizeAssetPath(path));
            if (project == null) throw new ArgumentException("找不到项目配置: " + path); return project;
        }
        private static WorkflowManifest ParseManifest(string json) { if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("message JSON 不能为空"); return JsonUtility.FromJson<WorkflowManifest>(json) ?? throw new ArgumentException("manifest JSON 无效"); }
        [Serializable] private sealed class OperationArray { public PaintOperationSpec[] operations; }
        private static PaintOperationSpec[] ParseOperations(string json) { OperationArray wrapper = JsonUtility.FromJson<OperationArray>(json); return wrapper?.operations ?? Array.Empty<PaintOperationSpec>(); }
        private static T ParseEnum<T>(string value, T fallback) where T : struct { return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out T parsed) ? parsed : fallback; }
        private static Vector2Int Point(int[] value) { return value != null && value.Length >= 2 ? new Vector2Int(value[0], value[1]) : Vector2Int.zero; }
        private static GameObject LoadPrefab(string path) { return string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(NormalizeAssetPath(path)); }
        private static void ReplaceAssets(List<TerrainLayer> target, string[] paths) { if (paths == null) return; target.Clear(); foreach (string path in paths) target.Add(AssetDatabase.LoadAssetAtPath<TerrainLayer>(NormalizeAssetPath(path))); }
        private static string NormalizeAssetPath(string path) { return (path ?? "").Replace('\\', '/'); }
        private static string ProjectDirectory(TerrainPaintProjectSO project) { return Path.GetDirectoryName(AssetDatabase.GetAssetPath(project)).Replace('\\', '/'); }
        private static string SanitizeName(string value) { foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_'); return value.Trim(); }
        private static void EnsureFolder(string path) { string[] parts = NormalizeAssetPath(path).Split('/'); string current = parts[0]; for (int i = 1; i < parts.Length; i++) { string next = current + "/" + parts[i]; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]); current = next; } }
        private static void DeleteReferencedAssets<T>(IEnumerable<T> assets) where T : UnityEngine.Object { if (assets == null) return; foreach (T asset in assets) { string path = asset != null ? AssetDatabase.GetAssetPath(asset) : null; if (!string.IsNullOrEmpty(path) && path.StartsWith(ConfigRoot + "/", StringComparison.OrdinalIgnoreCase)) AssetDatabase.DeleteAsset(path); } }
        private static void SaveProject(TerrainPaintProjectSO project) { EditorUtility.SetDirty(project); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }
        private static WorkflowBridgeResult Result(string operation, string path, string message, int count) { return new WorkflowBridgeResult { operation = operation, projectPath = path, message = message, count = count, valid = true, errors = Array.Empty<string>() }; }
    }
}
#endif
