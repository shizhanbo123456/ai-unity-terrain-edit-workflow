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
        public string json;
    }

    [Serializable] public sealed class IntGroupSpec { public int[] values; }
    [Serializable] public sealed class WeightedPrefabSpec { public string path; public int weight = 1; public int minimumCount; }
    [Serializable] public sealed class CurveKeySpec { public float time; public float value; public float inTangent; public float outTangent; public float inWeight; public float outWeight; public int weightedMode; }
    [Serializable]
    public sealed class PaintConfigSpec
    {
        public float roadStep = 2f;
        public int walkStartTries = 10;
        public int walkCandidateCount = 8;
        public int startCoverStopSamples = 8;
        public int walkSeed;
        public int maxStepsPerPath = 256;
        public float noiseScale = 1f;
        public int textureSmoothingRadius;
    }
    [Serializable]
    public sealed class LayerSpec
    {
        public int index;
        public string name;
        public float[] color;
        public float[] heightRange;
        public bool generateRoad = true;
        public float roadWidth = 2f;
        public float antiCurl = 0.5f;
        public CurveKeySpec[] roadFinalRemap;
        public int[] naturalWeights;
        public int[] roadWeights;
    }
    [Serializable]
    public sealed class PrefabSpec
    {
        public string path;
        public string billboardMode;
        public bool twoPointHeightAdaptation;
        public float lodTransition = 0.1f;
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
        public float[] chunkSize;
        public float visibleDistance = 60f;
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
        public float[] markerColor;
        public string prefab;
        public float[] positions;
        public float rotation;
        public float scale = 1f;
        public float[] chunkSize;
        public float visibleDistance = 60f;
        public int positionCount;
        public int nonZeroCount;
        public string positionsPath;
    }
    [Serializable]
    public sealed class WorkflowManifest
    {
        public string projectPath;
        public string projectName;
        public int resolution = 512;
        public int heightSeed;
        public float heightScale = 1f;
        public int smoothStep = 1;
        public int smoothIterations;
        public PaintConfigSpec paintConfig;
        public int naturalSeed;
        public int roadSeed;
        public int scatterSeed;
        public int propSeed;
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

        [BridgeCommand("workflow.configure", "以 message 中的完整 JSON manifest 覆盖工作流配置；这是唯一配置写入口")]
        public static object Configure(BridgeContext context, BridgeArgs args)
        {
            WorkflowManifest manifest = ParseManifest(args.message);
            ValidateCompleteManifest(manifest);
            TerrainPaintProjectSO project = ResolveOrCreateProject(args.path, manifest);
            foreach (PrefabSpec spec in manifest.prefabs) BuildPrefabSpec(spec);
            ApplyManifest(project, manifest);
            SaveProject(project);
            return Result("configure", AssetDatabase.GetAssetPath(project), "configured", 1);
        }

        [BridgeCommand("workflow.export", "将当前项目完整配置导出为 manifest JSON。参数: path=项目")]
        public static object Export(BridgeContext context, BridgeArgs args)
        {
            TerrainPaintProjectSO project = LoadProject(args.path);
            WorkflowManifest manifest = BuildManifest(project);
            return new WorkflowBridgeResult
            {
                operation = "export",
                projectPath = AssetDatabase.GetAssetPath(project),
                message = "exported",
                count = 1,
                valid = true,
                errors = Array.Empty<string>(),
                json = JsonUtility.ToJson(manifest, true),
            };
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

        [BridgeCommand("workflow.bake", "重建 layerMap，并烘焙 height/distance/occupancy/road/offRoad。参数: path=项目, terrain=场景Terrain名(空=第一个)")]
        public static object Bake(BridgeContext context, BridgeArgs args)
        {
            TerrainPaintProjectSO project = LoadProject(args.path);
            Terrain terrain = FindTerrain(args.terrain); // 烘焙需真实 Terrain：直接场景搜索（按名或第一个）
            BakeAll(project, terrain);
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

        [BridgeCommand("workflow.run", "执行完整 JSON manifest：创建/配置/Prefab/区域/烘焙/验证/Build；terrain 为空时自动寻找")]
        public static object Run(BridgeContext context, BridgeArgs args)
        {
            WorkflowManifest manifest = ParseManifest(args.message);
            ValidateCompleteManifest(manifest);
            TerrainPaintProjectSO project = ResolveOrCreateProject(args.path, manifest);
            if (manifest.prefabs != null)
                foreach (PrefabSpec spec in manifest.prefabs) BuildPrefabSpec(spec);
            // Generation groups may reference processed prefabs created above.
            ApplyManifest(project, manifest);
            Terrain terrain = FindTerrain(manifest.terrain); // bake 与 build 均需真实 Terrain：场景搜索
            if (manifest.bake) BakeAll(project, terrain); else BakeArea(project);
            SaveProject(project);
            List<string> errors = ValidateProject(project);
            if (errors.Count > 0)
                return new WorkflowBridgeResult { operation = "run", projectPath = AssetDatabase.GetAssetPath(project), valid = false, errors = errors.ToArray(), count = errors.Count, message = "validation failed" };
            TerrainBuilder builder = terrain.GetComponent<TerrainBuilder>() ?? terrain.gameObject.AddComponent<TerrainBuilder>();
            builder.Build(project, terrain, ParseEnum(manifest.applyThrough, TerrainWorkflowStage.FixedPointEdit));
            EditorUtility.SetDirty(terrain.gameObject);
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
            for (int i = 0; i < TerrainPaintProjectSO.DefaultLayerCount; i++)
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
            ValidateCompleteManifest(manifest);
            project.mapResolution = manifest.resolution > 0 ? manifest.resolution : project.mapResolution;
            project.heightSeed = manifest.heightSeed; project.heightScale = manifest.heightScale;
            project.smoothStep = manifest.smoothStep; project.smoothIterations = manifest.smoothIterations;
            project.naturalSeed = manifest.naturalSeed; project.roadSeed = manifest.roadSeed;
            project.scatterSeed = manifest.scatterSeed; project.propSeed = manifest.propSeed;
            ApplyPaintConfig(project.config, manifest.paintConfig);
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
            SetAreaOperations(project, manifest.areaOperations);
            project.SyncAllLayerWeights();
        }

        private static WorkflowManifest BuildManifest(TerrainPaintProjectSO project)
        {
            TerrainPaintConfig cfg = project.config ?? new TerrainPaintConfig();
            return new WorkflowManifest
            {
                projectPath = AssetDatabase.GetAssetPath(project),
                projectName = project.name,
                resolution = project.mapResolution,
                heightSeed = project.heightSeed,
                heightScale = project.heightScale,
                smoothStep = project.smoothStep,
                smoothIterations = project.smoothIterations,
                paintConfig = new PaintConfigSpec
                {
                    roadStep = cfg.roadStep,
                    walkStartTries = cfg.walkStartTries,
                    walkCandidateCount = cfg.walkCandidateCount,
                    startCoverStopSamples = cfg.startCoverStopSamples,
                    walkSeed = cfg.walkSeed,
                    maxStepsPerPath = cfg.maxStepsPerPath,
                    noiseScale = cfg.noiseScale,
                    textureSmoothingRadius = cfg.textureSmoothingRadius,
                },
                naturalSeed = project.naturalSeed,
                roadSeed = project.roadSeed,
                scatterSeed = project.scatterSeed,
                propSeed = project.propSeed,
                naturalTerrainLayers = ToPaths(project.naturalTerrainLayers),
                roadTerrainLayers = ToPaths(project.roadTerrainLayers),
                layers = ExportLayers(project.layers),
                adjacencyGroups = ExportAdjacencyGroups(project.adjacencyGroups),
                // 已处理的备用 Prefab 可被用户自由编辑，无法可靠反推其源 Prefab；导出时保留为空，
                // 生成组则直接引用已处理的 Generated/Prefabs 资产。
                prefabs = Array.Empty<PrefabSpec>(),
                areaOperations = ExportOperations(project.paintOperations),
                scatterGroups = ExportScatterGroups(project.scatterGroups),
                propGroups = ExportPropGroups(project.propGroups),
                fixedGroups = ExportFixedGroups(project.fixedPointGroups),
                bake = true,
                terrain = "",
                applyThrough = TerrainWorkflowStage.FixedPointEdit.ToString(),
            };
        }

        private static string[] ToPaths<T>(IEnumerable<T> assets) where T : UnityEngine.Object
        {
            if (assets == null) return Array.Empty<string>();
            var paths = new List<string>();
            foreach (T asset in assets) paths.Add(asset == null ? "" : AssetDatabase.GetAssetPath(asset));
            return paths.ToArray();
        }

        private static LayerSpec[] ExportLayers(List<LayerConfigSO> layers)
        {
            var result = new LayerSpec[TerrainPaintProjectSO.MaxLayerCount];
            for (int i = 0; i < result.Length; i++)
            {
                LayerConfigSO layer = layers != null && i < layers.Count ? layers[i] : null;
                Color color = layer != null ? layer.color : Color.clear;
                result[i] = new LayerSpec
                {
                    index = i,
                    name = layer != null ? layer.layerName : "Layer" + i,
                    color = new[] { color.r, color.g, color.b, color.a },
                    heightRange = layer != null ? new[] { layer.heightRange.x, layer.heightRange.y } : new[] { 0f, 1f },
                    generateRoad = layer != null && layer.generateRoad,
                    roadWidth = layer != null ? layer.roadWidth : 0f,
                    antiCurl = layer != null ? layer.antiCurl : 0.5f,
                    roadFinalRemap = ExportCurve(layer != null ? layer.roadFinalRemap : null),
                    naturalWeights = layer != null ? layer.naturalLayerWeights.ToArray() : Array.Empty<int>(),
                    roadWeights = layer != null ? layer.roadLayerWeights.ToArray() : Array.Empty<int>(),
                };
            }
            return result;
        }

        private static CurveKeySpec[] ExportCurve(AnimationCurve curve)
        {
            Keyframe[] keys = curve != null ? curve.keys : AnimationCurve.Linear(0f, 0f, 1f, 1f).keys;
            var result = new CurveKeySpec[keys.Length];
            for (int i = 0; i < keys.Length; i++)
                result[i] = new CurveKeySpec { time = keys[i].time, value = keys[i].value, inTangent = keys[i].inTangent, outTangent = keys[i].outTangent, inWeight = keys[i].inWeight, outWeight = keys[i].outWeight, weightedMode = (int)keys[i].weightedMode };
            return result;
        }

        private static IntGroupSpec[] ExportAdjacencyGroups(List<List<int>> groups)
        {
            if (groups == null) return Array.Empty<IntGroupSpec>();
            var result = new IntGroupSpec[groups.Count];
            for (int i = 0; i < groups.Count; i++) result[i] = new IntGroupSpec { values = groups[i]?.ToArray() ?? Array.Empty<int>() };
            return result;
        }

        private static PaintOperationSpec[] ExportOperations(List<LayerPaintOperation> operations)
        {
            if (operations == null) return Array.Empty<PaintOperationSpec>();
            var result = new PaintOperationSpec[operations.Count];
            for (int i = 0; i < operations.Count; i++)
            {
                LayerPaintOperation op = operations[i];
                result[i] = new PaintOperationSpec { type = op.type.ToString(), a = new[] { op.pointA.x, op.pointA.y }, b = new[] { op.pointB.x, op.pointB.y }, c = new[] { op.pointC.x, op.pointC.y }, radius = op.radius, layerIndex = op.layerIndex };
            }
            return result;
        }

        private static ScatterGroupSpec[] ExportScatterGroups(List<ScatterConfigSO> groups)
        {
            if (groups == null) return Array.Empty<ScatterGroupSpec>();
            var result = new List<ScatterGroupSpec>();
            foreach (ScatterConfigSO group in groups)
            {
                if (group == null) continue;
                var prefabs = new List<WeightedPrefabSpec>();
                foreach (ScatterPrefabEntry entry in group.prefabs) prefabs.Add(new WeightedPrefabSpec { path = entry.prefab == null ? "" : AssetDatabase.GetAssetPath(entry.prefab), weight = entry.weight });
                result.Add(new ScatterGroupSpec { name = group.groupName, chunkSize = new[] { group.chunkSize.x, group.chunkSize.y }, visibleDistance = group.visibleDistance, density = group.density, randomScale = new[] { group.randomScale.x, group.randomScale.y }, offRoadRange = new[] { group.offRoadDistanceRange.x, group.offRoadDistanceRange.y }, layerMask = (int)group.targetLayers, prefabs = prefabs.ToArray() });
            }
            return result.ToArray();
        }

        private static PropGroupSpec[] ExportPropGroups(List<PropConfigSO> groups)
        {
            if (groups == null) return Array.Empty<PropGroupSpec>();
            var result = new List<PropGroupSpec>();
            foreach (PropConfigSO group in groups)
            {
                if (group == null) continue;
                var prefabs = new List<WeightedPrefabSpec>();
                foreach (PropPrefabEntry entry in group.prefabs) prefabs.Add(new WeightedPrefabSpec { path = entry.prefab == null ? "" : AssetDatabase.GetAssetPath(entry.prefab), weight = entry.weight, minimumCount = entry.minimumCount });
                result.Add(new PropGroupSpec { name = group.groupName, chunkSize = new[] { group.chunkSize.x, group.chunkSize.y }, visibleDistance = group.visibleDistance, maxFailedAttempts = group.maxFailedAttempts, density = group.expectedDensity, batchSize = new[] { group.batchSize.x, group.batchSize.y }, layerMask = (int)group.targetLayers, outOfBoundsTolerance = group.outOfBoundsTolerance, basis = group.arrangementBasis.ToString(), range = new[] { group.arrangementRange.x, group.arrangementRange.y }, rotation = group.rotationMode.ToString(), distribution = group.distributionMode.ToString(), spacing = group.distributionSpacing, prefabs = prefabs.ToArray() });
            }
            return result.ToArray();
        }

        private static FixedGroupSpec[] ExportFixedGroups(List<FixedPointConfigSO> groups)
        {
            if (groups == null) return Array.Empty<FixedGroupSpec>();
            var result = new List<FixedGroupSpec>();
            foreach (FixedPointConfigSO group in groups)
            {
                if (group == null) continue;
                Color color = group.markerColor;
                int nonZero = 0;
                if (group.positions != null)
                    foreach (Vector2 position in group.positions)
                        if (position.x != 0f || position.y != 0f) nonZero++;
                // 定点位置列表不写入 JSON（可能大量），只输出数据条数/非0数量与资产路径；
                // 导入时若 positions 为空且 positionsPath 指向现存资产，则从该资产复制位置。
                result.Add(new FixedGroupSpec
                {
                    markerColor = new[] { color.r, color.g, color.b, color.a },
                    prefab = group.prefab == null ? "" : AssetDatabase.GetAssetPath(group.prefab),
                    positions = Array.Empty<float>(),
                    positionCount = group.positions != null ? group.positions.Count : 0,
                    nonZeroCount = nonZero,
                    positionsPath = AssetDatabase.GetAssetPath(group),
                    rotation = group.rotationDegrees,
                    scale = group.scale,
                    chunkSize = new[] { group.chunkSize.x, group.chunkSize.y },
                    visibleDistance = group.visibleDistance,
                });
            }
            return result.ToArray();
        }

        private static void ApplyPaintConfig(TerrainPaintConfig target, PaintConfigSpec source)
        {
            target.roadStep = source.roadStep; target.walkStartTries = source.walkStartTries;
            target.walkCandidateCount = source.walkCandidateCount; target.startCoverStopSamples = source.startCoverStopSamples;
            target.walkSeed = source.walkSeed; target.maxStepsPerPath = source.maxStepsPerPath;
            target.noiseScale = source.noiseScale;
            target.textureSmoothingRadius = Mathf.Max(0, source.textureSmoothingRadius);
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
                layer.generateRoad = spec.generateRoad; layer.roadWidth = spec.roadWidth; layer.antiCurl = spec.antiCurl;
                layer.roadFinalRemap = ToCurve(spec.roadFinalRemap);
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
                if (s.chunkSize?.Length >= 2) group.chunkSize = new Vector2(s.chunkSize[0], s.chunkSize[1]);
                group.visibleDistance = s.visibleDistance;
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
            // JSON 只含定点位置摘要（导出约定：位置不写入 JSON）。若某组 positions 为空但 positionsPath
            // 指向现存资产，需在删除旧资产前先从源资产复制位置，否则删除后加载不到。
            var sourcePositions = new List<List<Vector2>>();
            if (specs != null)
            {
                for (int i = 0; i < specs.Length; i++)
                {
                    var list = new List<Vector2>();
                    FixedGroupSpec s = specs[i];
                    if (s != null && (s.positions == null || s.positions.Length == 0) &&
                        !string.IsNullOrEmpty(s.positionsPath))
                    {
                        var source = AssetDatabase.LoadAssetAtPath<FixedPointConfigSO>(NormalizeAssetPath(s.positionsPath));
                        if (source != null && source.positions != null)
                            list.AddRange(source.positions);
                    }
                    sourcePositions.Add(list);
                }
            }

            DeleteReferencedAssets(project.fixedPointGroups); project.fixedPointGroups.Clear();
            string folder = ProjectDirectory(project) + "/FixedPointConfig"; EnsureFolder(folder);
            for (int i = 0; i < specs.Length; i++)
            {
                FixedGroupSpec s = specs[i]; if (s == null) continue;
                var group = ScriptableObject.CreateInstance<FixedPointConfigSO>(); group.prefab = LoadPrefab(s.prefab); group.rotationDegrees = s.rotation; group.scale = s.scale;
                if (s.chunkSize?.Length >= 2) group.chunkSize = new Vector2(s.chunkSize[0], s.chunkSize[1]);
                group.visibleDistance = s.visibleDistance;
                if (s.markerColor?.Length >= 4) group.markerColor = new Color(s.markerColor[0], s.markerColor[1], s.markerColor[2], s.markerColor[3]);
                if (s.positions != null && s.positions.Length > 0)
                {
                    for (int n = 0; n + 1 < s.positions.Length; n += 2)
                        group.positions.Add(new Vector2(s.positions[n], s.positions[n + 1]));
                }
                else if (i < sourcePositions.Count)
                {
                    group.positions.AddRange(sourcePositions[i]);
                }
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

        private static void BakeAll(TerrainPaintProjectSO project, Terrain terrain)
        {
            BakeArea(project);
            float[][] layerMap = project.ReadMap("layerMap"); int h = layerMap.Length, w = layerMap[0].Length;
            int[] ids = new int[w * h]; for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) ids[y * w + x] = Mathf.RoundToInt(layerMap[y][x]);
            // 除 layerMap 外的全部 MapData 都按真实 Terrain 尺寸换算像素世界间距
            Vector2 pws = TerrainRoadGen.PixelWorldSize(terrain, w, h);
            project.WriteMap("height", TerrainRoadGen.BakeHeightData(project, ids, w, h, pws));
            Texture2D preview = TerrainRoadGen.ComputeAll(project, ids, w, h, pws, out var r, out var g, out var b);
            if (preview == null) throw new InvalidOperationException("道路数据计算失败"); UnityEngine.Object.DestroyImmediate(preview);
            project.WriteMap("distance", CsvArrayCodec.ToJagged(r, w, h)); project.WriteMap("occupancy", CsvArrayCodec.ToJagged(g, w, h)); project.WriteMap("road", CsvArrayCodec.ToJagged(b, w, h));
            project.WriteMap("offRoad", CsvArrayCodec.ToJagged(TerrainRoadGen.ComputeOffRoad(ids, b, w, h, pws), w, h));
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
            PrefabProcessingUtility.BuildCandidatePrefab(source, ParseEnum(spec.billboardMode, BillboardMode.None), spec.twoPointHeightAdaptation, spec.lodTransition);
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
        private static void ValidateCompleteManifest(WorkflowManifest manifest)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(manifest.projectPath) && string.IsNullOrWhiteSpace(manifest.projectName)) missing.Add("projectName 或 projectPath");
            if (Array.IndexOf(TerrainPaintProjectSO.AllowedResolutions, manifest.resolution) < 0) missing.Add("resolution(128/256/512/1024)");
            if (manifest.paintConfig == null) missing.Add("paintConfig");
            if (manifest.naturalTerrainLayers == null) missing.Add("naturalTerrainLayers");
            if (manifest.roadTerrainLayers == null) missing.Add("roadTerrainLayers");
            if (manifest.layers == null || manifest.layers.Length != TerrainPaintProjectSO.MaxLayerCount) missing.Add("layers(必须完整提供16层)");
            if (manifest.adjacencyGroups == null) missing.Add("adjacencyGroups");
            if (manifest.prefabs == null) missing.Add("prefabs");
            if (manifest.areaOperations == null) missing.Add("areaOperations");
            if (manifest.scatterGroups == null) missing.Add("scatterGroups");
            if (manifest.propGroups == null) missing.Add("propGroups");
            if (manifest.fixedGroups == null) missing.Add("fixedGroups");
            if (manifest.layers != null && manifest.layers.Length == TerrainPaintProjectSO.MaxLayerCount)
            {
                var seen = new bool[TerrainPaintProjectSO.MaxLayerCount];
                foreach (LayerSpec layer in manifest.layers)
                {
                    if (layer == null || layer.index < 0 || layer.index >= seen.Length) { missing.Add("layers 中存在空项或非法 index"); continue; }
                    if (seen[layer.index]) missing.Add("layers index 重复: " + layer.index);
                    seen[layer.index] = true;
                    if (layer.color == null || layer.color.Length < 4) missing.Add($"layers[{layer.index}].color");
                    if (layer.heightRange == null || layer.heightRange.Length < 2) missing.Add($"layers[{layer.index}].heightRange");
                    if (layer.roadFinalRemap == null || layer.roadFinalRemap.Length == 0) missing.Add($"layers[{layer.index}].roadFinalRemap");
                    if (layer.naturalWeights == null || manifest.naturalTerrainLayers == null || layer.naturalWeights.Length != manifest.naturalTerrainLayers.Length) missing.Add($"layers[{layer.index}].naturalWeights 长度");
                    if (layer.roadWeights == null || manifest.roadTerrainLayers == null || layer.roadWeights.Length != manifest.roadTerrainLayers.Length) missing.Add($"layers[{layer.index}].roadWeights 长度");
                }
                for (int i = 0; i < seen.Length; i++) if (!seen[i]) missing.Add("缺少 layer index: " + i);
            }
            if (missing.Count > 0) throw new ArgumentException("manifest 必须是完整配置，缺少或无效: " + string.Join(", ", missing));
        }
        private static AnimationCurve ToCurve(CurveKeySpec[] specs)
        {
            if (specs == null || specs.Length == 0) return AnimationCurve.Linear(0f, 0f, 1f, 1f);
            var keys = new Keyframe[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                CurveKeySpec s = specs[i];
                keys[i] = new Keyframe(s.time, s.value, s.inTangent, s.outTangent, s.inWeight, s.outWeight) { weightedMode = (WeightedMode)s.weightedMode };
            }
            return new AnimationCurve(keys);
        }
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
