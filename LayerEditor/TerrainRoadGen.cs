using System;
using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 地形贴图核心算法（不依赖 UnityEditor，供编辑器窗口与后续复用）。
    ///
    /// 链路：层次图 → 层ID数组 → 组合层级分组 → 世界欧氏距离场 R
    ///      → 连通区域形状筛选 → 中轴细化与支刺剪枝 → 安全宽度路面 B。
    /// G 为剪枝后的骨架调试图，保留原 RGB/MapData 通道以兼容现有资产。
    ///
    /// 参数语义见设计文档《混合距离场与路面生成工具_设计文档(2).md》最终版。
    /// </summary>
    public static class TerrainRoadGen
    {
        // ---------- 层ID解析（颜色 → 层ID，只此一步；颜色信息不再进入后续流程） ----------

        public static int[] ParseLayerIds(Texture2D layerMap, List<LayerConfigSO> layers)
        {
            int w = layerMap.width, h = layerMap.height;
            var src = layerMap.GetPixels32();
            var ids = new int[w * h];
            for (int i = 0; i < ids.Length; i++)
            {
                var c = src[i];
                int id = -1;
                for (int l = 0; l < layers.Count; l++)
                {
                    var lc = layers[l].color;
                    if (lc.r == c.r && lc.g == c.g && lc.b == c.b && lc.a == c.a)
                    {
                        id = l;
                        break;
                    }
                }
                ids[i] = id;
            }
            return ids;
        }

        // ---------- 组合层级分组（全局 adjacencyGroups；仅 generateRoad=true 层参与） ----------

        /// <summary>
        /// 按全局邻接组（project.adjacencyGroups）分组，返回有效组合层组。
        /// 仅保留 generateRoad=true 的层；未出现在任何组中的有效层自动单独成组。
        /// 注：重复出现在多个组中的层会被跳过（冲突应在调用前用 project.HasAdjacencyConflict 检查并阻断）。
        /// </summary>
        public static List<List<int>> GroupLayers(TerrainPaintProjectSO project)
        {
            var layers = project.layers;
            int n = layers.Count;
            var groups = new List<List<int>>();
            var seen = new bool[n];

            foreach (var group in project.adjacencyGroups)
            {
                if (group == null) continue;
                var g = new List<int>();
                foreach (var idx in group)
                {
                    if (idx < 0 || idx >= n) continue;
                    if (layers[idx] == null || !layers[idx].generateRoad) continue;
                    if (seen[idx]) continue; // 已归入前面组（冲突），跳过避免重复计算
                    g.Add(idx);
                    seen[idx] = true;
                }
                if (g.Count > 0)
                {
                    g.Sort();
                    groups.Add(g);
                }
            }

            // 未出现在任何组中的有效层：单独成组
            for (int i = 0; i < n; i++)
            {
                if (layers[i] != null && layers[i].generateRoad && !seen[i])
                {
                    groups.Add(new List<int> { i });
                }
            }
            return groups;
        }

        // ---------- 距离场（二值欧氏距离变换） ----------

        /// <summary>按像素中心在世界 X/Z 的实际间距计算世界空间欧氏距离场。</summary>
        public static float[] ComputeR(
            int[] layerIds, int w, int h, List<int> group, Vector2 pixelWorldSize, out float maxD)
        {
            const float Big = 1e20f;
            float spacingX = Mathf.Max(0.0001f, pixelWorldSize.x);
            float spacingZ = Mathf.Max(0.0001f, pixelWorldSize.y);
            var frow = new float[w];
            var tmp = new float[w * h];

            // 行 pass：每行到最近背景（组外）的平方距离
            for (int y = 0; y < h; y++)
            {
                int baseIdx = y * w;
                for (int x = 0; x < w; x++)
                    frow[x] = InGroup(layerIds[baseIdx + x], group) ? Big : 0f;
                var gx = Edt1D(frow, spacingX);
                for (int x = 0; x < w; x++)
                    tmp[baseIdx + x] = gx[x];
            }

            // 列 pass + 归一化
            var fcol = new float[h];
            var r = new float[w * h];
            maxD = 0f;
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                    fcol[y] = tmp[y * w + x];
                var gy = Edt1D(fcol, spacingZ);
                for (int y = 0; y < h; y++)
                {
                    float d = Mathf.Sqrt(Mathf.Max(0f, gy[y]));
                    int idx = y * w + x;
                    if (InGroup(layerIds[idx], group))
                    {
                        r[idx] = d;
                        if (d > maxD) maxD = d;
                    }
                    else
                    {
                        r[idx] = 0f;
                    }
                }
            }

            return r;
        }

        /// <summary>
        /// 计算 offRoad 距离场：语义层（层 ID ≥1，排除 -1 透明与 Layer0）拼合区域内，
        /// 非道路像素到最近道路像素的世界空间欧氏距离；道路像素=0，拼合区域外=0。
        /// 散布生成时按生成组的 offRoadDistanceRange 过滤。
        /// </summary>
        public static float[] ComputeOffRoad(
            int[] layerIds, float[] road, int w, int h, Vector2 pixelWorldSize)
        {
            const float Big = 1e20f;
            float spacingX = Mathf.Max(0.0001f, pixelWorldSize.x);
            float spacingZ = Mathf.Max(0.0001f, pixelWorldSize.y);
            var frow = new float[w];
            var tmp = new float[w * h];

            // 行 pass：道路=背景(0)，其余=前景(Big)
            for (int y = 0; y < h; y++)
            {
                int baseIdx = y * w;
                for (int x = 0; x < w; x++)
                    frow[x] = road[baseIdx + x] > 0.5f ? 0f : Big;
                var gx = Edt1D(frow, spacingX);
                for (int x = 0; x < w; x++)
                    tmp[baseIdx + x] = gx[x];
            }

            // 列 pass + 语义层区域筛选 + 米换算
            var fcol = new float[h];
            var off = new float[w * h];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                    fcol[y] = tmp[y * w + x];
                var gy = Edt1D(fcol, spacingZ);
                for (int y = 0; y < h; y++)
                {
                    int idx = y * w + x;
                    if (layerIds[idx] > 0) // 语义层（不包含 Layer0 / 透明 -1）
                    {
                        float d = Mathf.Sqrt(Mathf.Max(0f, gy[y]));
                        // 无道路覆盖（EDT 距离为无穷大 Big=1e20 的平方根 ≈ 1e10）→ 视为 0，
                        // 避免散布/摆件按 offRoad 范围过滤时把"无道路区域"全部剔除
                        off[idx] = d >= 1e9f ? 0f : d;
                    }
                    else
                    {
                        off[idx] = 0f;
                    }
                }
            }
            return off;
        }

        /// <summary>1D 平方距离变换（Felzenszwalb & Huttenlocher O(n)）。f[i]=0 为背景，大值代表前景。</summary>
        private static float[] Edt1D(float[] f, float sampleSpacing = 1f)
        {
            int n = f.Length;
            float coefficient = sampleSpacing * sampleSpacing;
            var d = new float[n];
            var v = new int[n];
            var z = new float[n + 1];
            int k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                float s = ((f[q] + coefficient * q * q) -
                           (f[v[k]] + coefficient * v[k] * v[k])) /
                          (2f * coefficient * (q - v[k]));
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + coefficient * q * q) -
                         (f[v[k]] + coefficient * v[k] * v[k])) /
                        (2f * coefficient * (q - v[k]));
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q)
                    k++;
                float dv = q - v[k];
                d[q] = coefficient * dv * dv + f[v[k]];
            }
            return d;
        }

        // ---------- Layer 形状感知道路（连通域 → 中轴骨架 → 剪枝 → 路面） ----------

        /// <summary>
        /// 从组合 Layer 的形状自动提取道路。G 是剪枝后的单像素骨架调试图，
        /// B 是按各 Layer roadWidth 栅格化且严格裁剪在组合区域内的路面硬掩码。
        /// </summary>
        public static void GenerateRoads(int[] layerIds, float[] r, int w, int h, List<int> group,
            TerrainPaintConfig cfg, List<LayerConfigSO> layers, Vector2 pixelWorldSize,
            List<RoadAnchorConfig> anchors, int roadSeed,
            out float[] g, out float[] b)
        {
            var usableAnchors = CollectGroupAnchors(anchors, layerIds, w, h, group, pixelWorldSize);
            if (usableAnchors.Count == 0)
            {
                GenerateSkeletonRoads(layerIds, r, w, h, group, cfg, layers, pixelWorldSize, out g, out b);
                return;
            }

            GenerateAnchorRoads(layerIds, r, w, h, group, cfg, layers, pixelWorldSize,
                usableAnchors, roadSeed, out g, out b);
        }

        private sealed class RoadPort
        {
            public int anchorIndex;
            public Vector2 position;
            public Vector2 outward;
            public bool connected;
        }

        private static List<RoadPort> CollectGroupAnchors(List<RoadAnchorConfig> anchors,
            int[] layerIds, int w, int h, List<int> group, Vector2 pixelWorldSize)
        {
            var ports = new List<RoadPort>();
            if (anchors == null) return ports;
            float extentX = Mathf.Max(pixelWorldSize.x, pixelWorldSize.x * (w - 1));
            float extentZ = Mathf.Max(pixelWorldSize.y, pixelWorldSize.y * (h - 1));
            for (int a = 0; a < anchors.Count; a++)
            {
                RoadAnchorConfig anchor = anchors[a];
                if (anchor == null || anchor.validDirections == null) continue;
                Vector2 normalized = new Vector2(Mathf.Clamp01(anchor.normalizedPosition.x),
                    Mathf.Clamp01(anchor.normalizedPosition.y));
                int px = Mathf.Clamp(Mathf.RoundToInt(normalized.x * (w - 1)), 0, w - 1);
                int py = Mathf.Clamp(Mathf.RoundToInt(normalized.y * (h - 1)), 0, h - 1);
                if (!InGroup(layerIds[py * w + px], group)) continue;
                Vector2 position = new Vector2(normalized.x * extentX, normalized.y * extentZ);
                for (int d = 0; d < anchor.validDirections.Count; d++)
                {
                    Vector2 direction = anchor.validDirections[d];
                    if (direction.sqrMagnitude < 0.0001f) continue;
                    ports.Add(new RoadPort
                    {
                        anchorIndex = a,
                        position = position,
                        outward = direction.normalized,
                    });
                }
            }
            return ports;
        }

        private static void GenerateAnchorRoads(int[] layerIds, float[] clearance, int w, int h,
            List<int> group, TerrainPaintConfig cfg, List<LayerConfigSO> layers,
            Vector2 pixelWorldSize, List<RoadPort> ports, int roadSeed,
            out float[] centerline, out float[] road)
        {
            centerline = new float[w * h];
            road = new float[w * h];
            var allowed = new bool[w * h];
            for (int i = 0; i < allowed.Length; i++) allowed[i] = InGroup(layerIds[i], group);

            for (int startIndex = 0; startIndex < ports.Count; startIndex++)
            {
                RoadPort start = ports[startIndex];
                if (start.connected) continue;
                var points = ExtendRoad(startIndex, ports, clearance, layerIds, w, h, cfg, layers,
                    pixelWorldSize, roadSeed);
                if (points.Count < 2) continue;
                RasterizePolyline(points, centerline, road, allowed, clearance, layerIds, w, h,
                    cfg, layers, pixelWorldSize);
            }
        }

        private static List<Vector2> ExtendRoad(int startIndex, List<RoadPort> ports,
            float[] clearance, int[] layerIds, int w, int h, TerrainPaintConfig cfg,
            List<LayerConfigSO> layers, Vector2 pixelWorldSize, int roadSeed)
        {
            RoadPort start = ports[startIndex];
            var points = new List<Vector2> { start.position };
            Vector2 position = start.position;
            Vector2 direction = start.outward;
            float step = Mathf.Max(0.1f, cfg.roadExtensionStep);
            int maxSteps = Mathf.Max(1, cfg.maximumRoadSteps);
            int lastAvoidanceSign = 1;
            var random = new System.Random(unchecked(roadSeed * 486187739 + startIndex * 16777619 + 31));
            int curvatureDirection = random.NextDouble() < 0.5 ? -1 : 1;
            float walkTurnAngle = 0f;

            if (!IsSegmentLegal(position, position, clearance, layerIds, w, h, layers,
                    cfg.roadBoundaryMargin, pixelWorldSize))
                return points;

            for (int iteration = 0; iteration < maxSteps; iteration++)
            {
                bool usedFreeWalk = false;
                int targetIndex = FindAnchorTarget(startIndex, position, direction, ports, cfg);
                if (targetIndex >= 0)
                {
                    RoadPort target = ports[targetIndex];
                    float distance = Vector2.Distance(position, target.position);
                    var bezier = BuildBezier(position, direction, target.position, -target.outward);
                    bool curveLegal = IsBezierLegal(bezier, clearance, layerIds, w, h, layers,
                        cfg.roadBoundaryMargin, pixelWorldSize, step);
                    if (curveLegal && distance <= Mathf.Max(0f, cfg.bezierCompletionDistance))
                    {
                        AppendBezier(points, bezier, step);
                        start.connected = true;
                        target.connected = true;
                        return points;
                    }

                    Vector2 desired;
                    if (curveLegal)
                    {
                        Vector2 guide = EvaluateBezier(bezier, Mathf.Clamp(cfg.bezierGuideLookAhead, 0.01f, 0.5f));
                        desired = (guide - position).normalized;
                    }
                    else
                    {
                        float cross = Cross(direction, target.position - position);
                        int sign = Mathf.Abs(cross) > 0.0001f ? (cross > 0f ? -1 : 1) : lastAvoidanceSign;
                        lastAvoidanceSign = sign;
                        desired = Rotate(direction, sign * Mathf.Max(0f, cfg.failedProbeAvoidanceAngle));
                    }
                    direction = RotateTowards(direction, desired, cfg.anchorGuideMaxTurnAngle);
                }
                else
                {
                    float required = RequiredClearanceAt(position, layerIds, w, h, layers,
                        cfg.roadBoundaryMargin, pixelWorldSize);
                    float boundarySpace = Sample(clearance, position, w, h, pixelWorldSize) - required;
                    Vector2 baseDirection = direction;
                    float maxTurn = Mathf.Max(0f, cfg.freeMaxTurnAngle);
                    if (boundarySpace <= Mathf.Max(0f, cfg.boundaryFollowDistance))
                    {
                        Vector2 normal = ClearanceGradient(clearance, position, w, h, pixelWorldSize);
                        if (normal.sqrMagnitude > 0.0001f)
                        {
                            Vector2 tangent = new Vector2(-normal.y, normal.x).normalized;
                            if (Vector2.Dot(tangent, direction) < 0f) tangent = -tangent;
                            baseDirection = tangent;
                        }
                        maxTurn = 0f;
                    }
                    else
                    {
                        // 普通游走才应用累计偏转；锚点引导和边界切线拥有更高优先级。
                        baseDirection = Rotate(direction,
                            Mathf.Clamp(walkTurnAngle, -maxTurn, maxTurn)).normalized;
                        usedFreeWalk = true;
                    }
                    direction = FindLegalDirection(position, baseDirection, maxTurn, step,
                        Mathf.Max(1f, cfg.directionSearchStep), clearance, layerIds, w, h,
                        layers, cfg.roadBoundaryMargin, pixelWorldSize);
                    if (direction.sqrMagnitude < 0.0001f) break;
                }

                Vector2 next = position + direction.normalized * step;
                if (!IsSegmentLegal(position, next, clearance, layerIds, w, h, layers,
                        cfg.roadBoundaryMargin, pixelWorldSize))
                {
                    Vector2 alternative = FindLegalDirection(position, direction,
                        Mathf.Max(0f, cfg.freeMaxTurnAngle), step,
                        Mathf.Max(1f, cfg.directionSearchStep), clearance, layerIds, w, h,
                        layers, cfg.roadBoundaryMargin, pixelWorldSize);
                    if (alternative.sqrMagnitude < 0.0001f) break;
                    direction = alternative;
                    next = position + direction * step;
                }

                if (CreatesShortLoop(points, next, step)) break;
                points.Add(next);
                position = next;

                if (usedFreeWalk)
                {
                    float minCurvature = Mathf.Min(cfg.roadWalkCurvatureRange.x, cfg.roadWalkCurvatureRange.y);
                    float maxCurvature = Mathf.Max(cfg.roadWalkCurvatureRange.x, cfg.roadWalkCurvatureRange.y);
                    float sampled = Mathf.Lerp(minCurvature, maxCurvature, (float)random.NextDouble());
                    walkTurnAngle += curvatureDirection * Mathf.Abs(sampled);

                    // 先决定下一次累计是加还是减，再按独立概率直接翻转当前累计偏转角。
                    if (random.NextDouble() < Mathf.Clamp01(cfg.roadWalkCurvatureDirectionSwitchProbability))
                        curvatureDirection = -curvatureDirection;
                    if (random.NextDouble() < Mathf.Clamp01(cfg.roadWalkDirectionFlipProbability))
                        walkTurnAngle = -walkTurnAngle;
                    walkTurnAngle = Mathf.Clamp(walkTurnAngle,
                        -Mathf.Max(0f, cfg.freeMaxTurnAngle), Mathf.Max(0f, cfg.freeMaxTurnAngle));
                }
            }
            return points;
        }

        private struct BezierCurve { public Vector2 p0, p1, p2, p3; }

        private static BezierCurve BuildBezier(Vector2 p0, Vector2 startTangent,
            Vector2 p3, Vector2 endTangent)
        {
            float length = Vector2.Distance(p0, p3);
            float handle = Mathf.Clamp(length * 0.35f, 0.5f, Mathf.Max(0.5f, length * 0.6f));
            return new BezierCurve
            {
                p0 = p0,
                p1 = p0 + startTangent.normalized * handle,
                p2 = p3 - endTangent.normalized * handle,
                p3 = p3,
            };
        }

        private static Vector2 EvaluateBezier(BezierCurve c, float t)
        {
            float u = 1f - t;
            return u*u*u*c.p0 + 3f*u*u*t*c.p1 + 3f*u*t*t*c.p2 + t*t*t*c.p3;
        }

        private static bool IsBezierLegal(BezierCurve curve, float[] clearance, int[] layerIds,
            int w, int h, List<LayerConfigSO> layers, float margin, Vector2 pixelWorldSize, float step)
        {
            float estimate = Vector2.Distance(curve.p0, curve.p1) + Vector2.Distance(curve.p1, curve.p2)
                + Vector2.Distance(curve.p2, curve.p3);
            int samples = Mathf.Max(4, Mathf.CeilToInt(estimate / Mathf.Max(0.1f, step * 0.5f)));
            Vector2 previous = curve.p0;
            for (int i = 1; i <= samples; i++)
            {
                Vector2 current = EvaluateBezier(curve, i / (float)samples);
                if (!IsSegmentLegal(previous, current, clearance, layerIds, w, h, layers,
                        margin, pixelWorldSize)) return false;
                previous = current;
            }
            return true;
        }

        private static void AppendBezier(List<Vector2> points, BezierCurve curve, float step)
        {
            float estimate = Vector2.Distance(curve.p0, curve.p1) + Vector2.Distance(curve.p1, curve.p2)
                + Vector2.Distance(curve.p2, curve.p3);
            int samples = Mathf.Max(2, Mathf.CeilToInt(estimate / Mathf.Max(0.1f, step)));
            for (int i = 1; i <= samples; i++) points.Add(EvaluateBezier(curve, i / (float)samples));
        }

        private static int FindAnchorTarget(int sourceIndex, Vector2 position, Vector2 direction,
            List<RoadPort> ports, TerrainPaintConfig cfg)
        {
            int best = -1;
            float bestScore = float.PositiveInfinity;
            float probe = Mathf.Max(cfg.bezierProbeDistance, cfg.bezierCompletionDistance);
            for (int i = 0; i < ports.Count; i++)
            {
                RoadPort candidate = ports[i];
                if (i == sourceIndex || candidate.connected || candidate.anchorIndex == ports[sourceIndex].anchorIndex) continue;
                Vector2 delta = candidate.position - position;
                float distance = delta.magnitude;
                if (distance < 0.001f || distance > probe) continue;
                float arrivalAngle = Vector2.Angle(direction, -candidate.outward);
                if (arrivalAngle > cfg.anchorSnapAngle) continue;
                float forward = Vector2.Dot(direction, delta / distance);
                if (forward <= 0f) continue;
                float score = distance + arrivalAngle * 0.2f - forward * probe * 0.25f;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        private static Vector2 FindLegalDirection(Vector2 position, Vector2 baseDirection,
            float maxTurn, float step, float angleStep, float[] clearance, int[] layerIds,
            int w, int h, List<LayerConfigSO> layers, float margin, Vector2 pixelWorldSize)
        {
            int count = Mathf.CeilToInt(maxTurn / angleStep);
            for (int ring = 0; ring <= count; ring++)
            {
                int variants = ring == 0 ? 1 : 2;
                for (int variant = 0; variant < variants; variant++)
                {
                    float angle = ring == 0 ? 0f : ring * angleStep * (variant == 0 ? 1f : -1f);
                    if (Mathf.Abs(angle) > maxTurn + 0.001f) continue;
                    Vector2 candidate = Rotate(baseDirection, angle).normalized;
                    if (IsSegmentLegal(position, position + candidate * step, clearance,
                            layerIds, w, h, layers, margin, pixelWorldSize)) return candidate;
                }
            }
            return Vector2.zero;
        }

        private static bool IsSegmentLegal(Vector2 a, Vector2 b, float[] clearance,
            int[] layerIds, int w, int h, List<LayerConfigSO> layers, float margin,
            Vector2 pixelWorldSize)
        {
            float length = Vector2.Distance(a, b);
            float sampleStep = Mathf.Max(0.1f, Mathf.Min(pixelWorldSize.x, pixelWorldSize.y) * 0.5f);
            int samples = Mathf.Max(1, Mathf.CeilToInt(length / sampleStep));
            for (int i = 0; i <= samples; i++)
            {
                Vector2 p = Vector2.Lerp(a, b, i / (float)samples);
                float required = RequiredClearanceAt(p, layerIds, w, h, layers, margin, pixelWorldSize);
                if (required < 0f || Sample(clearance, p, w, h, pixelWorldSize) + 0.0001f < required)
                    return false;
            }
            return true;
        }

        private static float RequiredClearanceAt(Vector2 position, int[] layerIds, int w, int h,
            List<LayerConfigSO> layers, float margin, Vector2 pixelWorldSize)
        {
            int x = Mathf.RoundToInt(position.x / Mathf.Max(0.0001f, pixelWorldSize.x));
            int y = Mathf.RoundToInt(position.y / Mathf.Max(0.0001f, pixelWorldSize.y));
            if (x < 0 || x >= w || y < 0 || y >= h) return -1f;
            int layer = layerIds[y * w + x];
            if (layer < 0 || layer >= layers.Count || layers[layer] == null || !layers[layer].generateRoad) return -1f;
            return Mathf.Max(0f, layers[layer].roadWidth) + Mathf.Max(0f, margin);
        }

        private static float Sample(float[] values, Vector2 position, int w, int h, Vector2 pixelWorldSize)
        {
            int x = Mathf.RoundToInt(position.x / Mathf.Max(0.0001f, pixelWorldSize.x));
            int y = Mathf.RoundToInt(position.y / Mathf.Max(0.0001f, pixelWorldSize.y));
            if (x < 0 || x >= w || y < 0 || y >= h) return 0f;
            return values[y * w + x];
        }

        private static Vector2 ClearanceGradient(float[] values, Vector2 position, int w, int h,
            Vector2 pixelWorldSize)
        {
            Vector2 dx = new Vector2(pixelWorldSize.x, 0f);
            Vector2 dz = new Vector2(0f, pixelWorldSize.y);
            return new Vector2(Sample(values, position + dx, w, h, pixelWorldSize) - Sample(values, position - dx, w, h, pixelWorldSize),
                Sample(values, position + dz, w, h, pixelWorldSize) - Sample(values, position - dz, w, h, pixelWorldSize)).normalized;
        }

        private static bool CreatesShortLoop(List<Vector2> points, Vector2 next, float step)
        {
            int ignoreRecent = 8;
            float thresholdSquared = step * step * 0.5f;
            for (int i = 0; i < points.Count - ignoreRecent; i++)
                if ((points[i] - next).sqrMagnitude < thresholdSquared) return true;
            return false;
        }

        private static Vector2 RotateTowards(Vector2 from, Vector2 to, float maxDegrees)
        {
            float signed = Vector2.SignedAngle(from, to);
            return Rotate(from, Mathf.Clamp(signed, -Mathf.Max(0f, maxDegrees), Mathf.Max(0f, maxDegrees))).normalized;
        }

        private static Vector2 Rotate(Vector2 value, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            return new Vector2(value.x * c - value.y * s, value.x * s + value.y * c);
        }

        private static float Cross(Vector2 a, Vector2 b) { return a.x * b.y - a.y * b.x; }

        private static void RasterizePolyline(List<Vector2> points, float[] centerline, float[] road,
            bool[] allowed, float[] clearance, int[] layerIds, int w, int h, TerrainPaintConfig cfg,
            List<LayerConfigSO> layers, Vector2 pixelWorldSize)
        {
            float sampleStep = Mathf.Max(0.1f, Mathf.Min(pixelWorldSize.x, pixelWorldSize.y) * 0.5f);
            for (int segment = 1; segment < points.Count; segment++)
            {
                Vector2 a = points[segment - 1], b = points[segment];
                int samples = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / sampleStep));
                for (int i = 0; i <= samples; i++)
                {
                    Vector2 p = Vector2.Lerp(a, b, i / (float)samples);
                    int x = Mathf.RoundToInt(p.x / pixelWorldSize.x);
                    int y = Mathf.RoundToInt(p.y / pixelWorldSize.y);
                    if (x < 0 || x >= w || y < 0 || y >= h) continue;
                    int idx = y * w + x;
                    centerline[idx] = 1f;
                    float preferredRadius = RequiredClearanceAt(p, layerIds, w, h, layers, 0f, pixelWorldSize);
                    float safeRadius = Mathf.Max(0f, clearance[idx] - cfg.roadBoundaryMargin);
                    StampRoadEllipse(road, allowed, w, h, x, y,
                        Mathf.Min(Mathf.Max(0f, preferredRadius), safeRadius), pixelWorldSize);
                }
            }
        }

        private static void GenerateSkeletonRoads(int[] layerIds, float[] r, int w, int h, List<int> group,
            TerrainPaintConfig cfg, List<LayerConfigSO> layers, Vector2 pixelWorldSize,
            out float[] g, out float[] b)
        {
            g = new float[w * h];
            b = new float[w * h];
            pixelWorldSize.x = Mathf.Max(0.0001f, pixelWorldSize.x);
            pixelWorldSize.y = Mathf.Max(0.0001f, pixelWorldSize.y);
            var groupMask = new bool[w * h];
            for (int i = 0; i < groupMask.Length; i++)
                groupMask[i] = InGroup(layerIds[i], group);

            foreach (var component in FindComponents(groupMask, w, h))
            {
                if (!IsRoadLikeComponent(component, r, cfg, pixelWorldSize)) continue;
                var componentMask = new bool[w * h];
                for (int i = 0; i < component.Count; i++) componentMask[component[i]] = true;
                bool[] skeleton = ThinZhangSuen(componentMask, w, h);
                PruneSkeletonSpurs(skeleton, r, w, h, cfg, pixelWorldSize);

                for (int idx = 0; idx < skeleton.Length; idx++)
                {
                    if (!skeleton[idx]) continue;
                    g[idx] = 1f;
                    int layerId = layerIds[idx];
                    float preferredRadius = layerId >= 0 && layerId < layers.Count && layers[layerId] != null
                        ? Mathf.Max(0f, layers[layerId].roadWidth) : 0f;
                    float safeRadius = Mathf.Max(0f, r[idx] - cfg.roadBoundaryMargin);
                    StampRoadEllipse(b, groupMask, w, h, idx % w, idx / w,
                        Mathf.Min(preferredRadius, safeRadius), pixelWorldSize);
                }
            }
        }

        private static List<List<int>> FindComponents(bool[] mask, int w, int h)
        {
            var result = new List<List<int>>();
            var visited = new bool[mask.Length];
            var queue = new Queue<int>();
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || visited[start]) continue;
                var component = new List<int>();
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    component.Add(idx);
                    int x = idx % w, y = idx / w;
                    for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        int nx = x + ox, ny = y + oy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int ni = ny * w + nx;
                        if (!mask[ni] || visited[ni]) continue;
                        visited[ni] = true;
                        queue.Enqueue(ni);
                    }
                }
                result.Add(component);
            }
            return result;
        }

        private static bool IsRoadLikeComponent(List<int> component, float[] distance,
            TerrainPaintConfig cfg, Vector2 pixelWorldSize)
        {
            float area = component.Count * pixelWorldSize.x * pixelWorldSize.y;
            if (area < cfg.minimumRoadRegionArea) return false;
            float maxClearance = 0f;
            for (int i = 0; i < component.Count; i++)
                maxClearance = Mathf.Max(maxClearance, distance[component[i]]);
            if (maxClearance <= 0.0001f) return false;
            float corridorAspect = area / (4f * maxClearance * maxClearance);
            return corridorAspect >= cfg.minimumCorridorAspect;
        }

        private static bool[] ThinZhangSuen(bool[] source, int w, int h)
        {
            var image = (bool[])source.Clone();
            var remove = new List<int>();
            bool changed;
            do
            {
                changed = false;
                for (int pass = 0; pass < 2; pass++)
                {
                    remove.Clear();
                    for (int y = 1; y < h - 1; y++)
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = y * w + x;
                        if (!image[i]) continue;
                        bool p2=image[(y-1)*w+x], p3=image[(y-1)*w+x+1];
                        bool p4=image[y*w+x+1], p5=image[(y+1)*w+x+1];
                        bool p6=image[(y+1)*w+x], p7=image[(y+1)*w+x-1];
                        bool p8=image[y*w+x-1], p9=image[(y-1)*w+x-1];
                        int neighbours = BoolInt(p2)+BoolInt(p3)+BoolInt(p4)+BoolInt(p5)+
                            BoolInt(p6)+BoolInt(p7)+BoolInt(p8)+BoolInt(p9);
                        if (neighbours < 2 || neighbours > 6) continue;
                        bool[] ring = {p2,p3,p4,p5,p6,p7,p8,p9,p2};
                        int transitions = 0;
                        for (int k = 0; k < 8; k++) if (!ring[k] && ring[k+1]) transitions++;
                        if (transitions != 1) continue;
                        bool removable = pass == 0
                            ? !(p2 && p4 && p6) && !(p4 && p6 && p8)
                            : !(p2 && p4 && p8) && !(p2 && p6 && p8);
                        if (removable) remove.Add(i);
                    }
                    if (remove.Count == 0) continue;
                    changed = true;
                    for (int i = 0; i < remove.Count; i++) image[remove[i]] = false;
                }
            } while (changed);
            return image;
        }

        private static int BoolInt(bool value) { return value ? 1 : 0; }

        private static void PruneSkeletonSpurs(bool[] skeleton, float[] distance, int w, int h,
            TerrainPaintConfig cfg, Vector2 pixelWorldSize)
        {
            for (int iteration = 0; iteration < 64; iteration++)
            {
                var endpoints = new List<int>();
                for (int i = 0; i < skeleton.Length; i++)
                    if (skeleton[i] && SkeletonDegree(skeleton, i, w, h) == 1) endpoints.Add(i);
                var remove = new HashSet<int>();
                for (int e = 0; e < endpoints.Count; e++)
                {
                    int start=endpoints[e], previous=-1, current=start;
                    var chain = new List<int> {start};
                    float length=0f, widthSum=2f*distance[start];
                    int terminalDegree=1;
                    while (true)
                    {
                        int next=-1, choices=0, cx=current%w, cy=current/w;
                        for (int oy=-1; oy<=1; oy++)
                        for (int ox=-1; ox<=1; ox++)
                        {
                            if (ox==0 && oy==0) continue;
                            int nx=cx+ox, ny=cy+oy;
                            if (nx<0 || nx>=w || ny<0 || ny>=h) continue;
                            int ni=ny*w+nx;
                            if (ni==previous || !skeleton[ni]) continue;
                            next=ni; choices++;
                        }
                        terminalDegree=SkeletonDegree(skeleton,current,w,h);
                        if (current!=start && terminalDegree!=2) break;
                        if (choices!=1 || next<0) break;
                        int nxp=next%w, nyp=next/w;
                        float dx=(nxp-cx)*pixelWorldSize.x, dz=(nyp-cy)*pixelWorldSize.y;
                        length+=Mathf.Sqrt(dx*dx+dz*dz);
                        previous=current; current=next;
                        chain.Add(current); widthSum+=2f*distance[current];
                    }
                    if (terminalDegree<3) continue;
                    float meanWidth=widthSum/Mathf.Max(1,chain.Count);
                    float required=Mathf.Max(cfg.minimumSkeletonBranchLength,
                        meanWidth*cfg.spurLengthToWidthRatio);
                    if (length>=required) continue;
                    for (int i=0; i<chain.Count-1; i++) remove.Add(chain[i]);
                }
                if (remove.Count==0) break;
                foreach (int idx in remove) skeleton[idx]=false;
            }
        }

        private static int SkeletonDegree(bool[] skeleton, int idx, int w, int h)
        {
            int x=idx%w, y=idx/w, degree=0;
            for (int oy=-1; oy<=1; oy++)
            for (int ox=-1; ox<=1; ox++)
            {
                if (ox==0 && oy==0) continue;
                int nx=x+ox, ny=y+oy;
                if (nx>=0 && nx<w && ny>=0 && ny<h && skeleton[ny*w+nx]) degree++;
            }
            return degree;
        }

        private static void StampRoadEllipse(float[] road, bool[] allowed, int w, int h,
            int cx, int cy, float radiusWorld, Vector2 pixelWorldSize)
        {
            int radiusX=Mathf.CeilToInt(radiusWorld/pixelWorldSize.x);
            int radiusY=Mathf.CeilToInt(radiusWorld/pixelWorldSize.y);
            float radiusSquared=radiusWorld*radiusWorld;
            for (int y=cy-radiusY; y<=cy+radiusY; y++)
            {
                if (y<0 || y>=h) continue;
                for (int x=cx-radiusX; x<=cx+radiusX; x++)
                {
                    if (x<0 || x>=w) continue;
                    int idx=y*w+x;
                    if (!allowed[idx]) continue;
                    float dx=(x-cx)*pixelWorldSize.x, dz=(y-cy)*pixelWorldSize.y;
                    if (dx*dx+dz*dz<=radiusSquared) road[idx]=1f;
                }
            }
        }

        // ---------- 一键计算（多组合层） ----------

        /// <summary>使用 Terrain 推导的 X/Z 像素中心间距，输出世界单位距离场。</summary>
        public static Texture2D ComputeAll(
            TerrainPaintProjectSO project,
            int[] layerIds,
            int w,
            int h,
            Vector2 pixelWorldSize,
            out float[] rOut,
            out float[] gOut,
            out float[] bOut)
        {
            rOut = null;
            gOut = null;
            bOut = null;

            // 邻接组冲突检查：同一层级出现在多个组 → 阻断计算
            var dups = project.FindDuplicateLayerIndices();
            if (dups.Count > 0)
            {
                Debug.LogError(
                    $"[Terrain Road Gen] 邻接组配置错误：以下层级被加入多个邻接组，已阻断计算：{string.Join(", ", dups)}");
                return null;
            }

            var groups = GroupLayers(project);

            var r = new float[w * h];
            var g = new float[w * h];
            var b = new float[w * h];

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                var rg = ComputeR(layerIds, w, h, group, pixelWorldSize, out _);
                for (int i = 0; i < r.Length; i++)
                    r[i] = Mathf.Max(r[i], rg[i]);

                GenerateRoads(layerIds, rg, w, h, group, project.config, project.layers,
                    pixelWorldSize, project.roadAnchors, project.roadSeed, out var gg, out var bb);
                for (int i = 0; i < g.Length; i++)
                {
                    g[i] = Mathf.Max(g[i], gg[i]);
                    b[i] = Mathf.Max(b[i], bb[i]);
                }
            }

            // distance 以真实距离值输出（不归一化）；显示图 R 通道用现算 max 归一化（范围由数据产生，不持久化）
            rOut = r;
            gOut = g;
            bOut = b;

            float rMax = 0f;
            for (int i = 0; i < r.Length; i++)
                if (r[i] > rMax) rMax = r[i];

            var rDisplay = r;
            if (rMax > 0f)
            {
                rDisplay = new float[w * h];
                for (int i = 0; i < r.Length; i++)
                    rDisplay[i] = r[i] / rMax;
            }
            return ComposeRgb(rDisplay, g, b, w, h);
        }

        /// <summary>合成 RGB 图：R=距离场，G=道路中心骨架，B=路面掩码。R 由调用方保证已归一化。</summary>
        public static Texture2D ComposeRgb(float[] r, float[] g, float[] b, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(r[i]) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(g[i]) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(b[i]) * 255f),
                    255);
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        // ---------- 高度图烘焙（高度编辑子界面） ----------

        /// <summary>
        /// <summary>烘焙高度数据（float[][]）：逐像素按所在层的高度范围，用 Perlin 噪声在该范围内插值生成
        /// **真实高度**（单位与层 heightRange 一致），不归一化、不持久化范围。
        /// MapData 存储层直接写本方法结果到 "height" key；显示/构建时遍历数据现算 min/max。
        /// 使用像素中心的世界 X/Z 间距采样连续高度噪声。</summary>
        public static float[][] BakeHeightData(
            TerrainPaintProjectSO project, int[] layerIds, int w, int h, Vector2 pixelWorldSize)
        {
            if (layerIds == null || layerIds.Length != w * h)
            {
                Debug.LogError("[Terrain Road Gen] 烘焙高度图失败：layerIds 与尺寸不匹配");
                return null;
            }

            float scale = Mathf.Max(0.001f, project.heightScale);
            float seedOff = project.heightSeed * 13.37f;
            float spacingX = Mathf.Max(0.0001f, pixelWorldSize.x);
            float spacingZ = Mathf.Max(0.0001f, pixelWorldSize.y);

            int smoothIterations = Mathf.Max(0, project.smoothIterations);
            int smoothStep = Mathf.Max(1, project.smoothStep);

            var data = new float[h][];

            for (int y = 0; y < h; y++)
            {
                var row = new float[w];
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;

                    // 图层范围：默认取中心像素所在层；开启平滑时用十字滤波采样周围一圈图层，
                    // 按样本占比加权各层 heightRange（等价于 layer 权重混合）。区域内部样本
                    // 全为同一层 → 权重 100% → 与原行为完全一致；仅图层交界处产生加权过渡。
                    Vector2 range = HeightRangeAt(project, layerIds, i);
                    if (smoothIterations > 0)
                    {
                        float sumMin = range.x, sumMax = range.y;
                        int samples = 1;
                        for (int k = 1; k <= smoothIterations; k++)
                        {
                            int d = k * smoothStep;
                            if (x - d >= 0) { Vector2 r = HeightRangeAt(project, layerIds, y * w + x - d); sumMin += r.x; sumMax += r.y; samples++; }
                            if (x + d < w) { Vector2 r = HeightRangeAt(project, layerIds, y * w + x + d); sumMin += r.x; sumMax += r.y; samples++; }
                            if (y - d >= 0) { Vector2 r = HeightRangeAt(project, layerIds, (y - d) * w + x); sumMin += r.x; sumMax += r.y; samples++; }
                            if (y + d < h) { Vector2 r = HeightRangeAt(project, layerIds, (y + d) * w + x); sumMin += r.x; sumMax += r.y; samples++; }
                        }
                        range = new Vector2(sumMin / samples, sumMax / samples);
                    }

                    // Perlin 噪声（seed 偏移 + 空间频率 scale），在层级高度范围内插值（真实高度，不归一化）
                    float n = Mathf.PerlinNoise(
                        x * spacingX * scale + seedOff,
                        y * spacingZ * scale + seedOff);
                    row[x] = Mathf.Lerp(range.x, range.y, n);
                }
                data[y] = row;
            }

            return data;
        }

        /// <summary>取某像素所在层的 heightRange；无效/透明层（-1）按 (0,0) 处理。</summary>
        private static Vector2 HeightRangeAt(TerrainPaintProjectSO project, int[] layerIds, int idx)
        {
            int lid = layerIds[idx];
            return (lid >= 0 && lid < project.layers.Count && project.layers[lid] != null)
                ? project.layers[lid].heightRange
                : Vector2.zero;
        }

        /// <summary>按 Terrain 实际 X/Z 尺寸与 Map 分辨率计算像素中心的世界间距（两轴独立）。</summary>
        public static Vector2 PixelWorldSize(Terrain terrain, int mapW, int mapH)
        {
            Vector3 size = terrain != null && terrain.terrainData != null ? terrain.terrainData.size : Vector3.one;
            return new Vector2(
                size.x / Mathf.Max(1, mapW - 1),
                size.z / Mathf.Max(1, mapH - 1));
        }

        // ---------- 内部工具 ----------

        private static bool InGroup(int id, List<int> group)
        {
            if (id < 0)
                return false;
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i] == id)
                    return true;
            }
            return false;
        }

    }
}
