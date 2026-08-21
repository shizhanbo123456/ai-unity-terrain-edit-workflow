#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 地形贴图核心算法（不依赖 UnityEditor，供编辑器窗口与后续复用）。
    ///
    /// 链路：层次图 → 层ID数组 → 组合层级分组 → 欧氏距离场 R（maxD 自动归一化）
    ///      → 随机游走路网（G 占用/间隔缓冲 + B 路面硬掩码）→ RGB 合成图。
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

        /// <summary>
        /// 计算组合层级的 R 通道：区域内像素到最近区域边界（组外像素）的欧氏距离（像素单位，**真实值不归一化**；
        /// 边界=0，最深内陆=最大值）；区域外 R=0。maxD = 组内最大距离（真实值）。
        /// </summary>
        public static float[] ComputeR(int[] layerIds, int w, int h, List<int> group, out float maxD)
        {
            const float Big = 1e7f; // 前景（区域内）的初始值，远大于任何像素距离平方
            var frow = new float[w];
            var tmp = new float[w * h];

            // 行 pass：每行到最近背景（组外）的平方距离
            for (int y = 0; y < h; y++)
            {
                int baseIdx = y * w;
                for (int x = 0; x < w; x++)
                    frow[x] = InGroup(layerIds[baseIdx + x], group) ? Big : 0f;
                var gx = Edt1D(frow);
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
                var gy = Edt1D(fcol);
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
        /// 非道路像素到最近道路像素的欧氏距离（像素 → × worldPerPixel 转**米**）；道路像素=0，拼合区域外=0。
        /// 树木生成时按所在层 roadDistanceLimit 过滤（offRoad &lt; limit 的位置不生成）。
        /// </summary>
        public static float[] ComputeOffRoad(int[] layerIds, float[] road, int w, int h, float worldPerPixel)
        {
            const float Big = 1e7f; // 前景（非道路）初始值，远大于任何像素距离平方
            float m = worldPerPixel > 0f ? worldPerPixel : 1f;
            var frow = new float[w];
            var tmp = new float[w * h];

            // 行 pass：道路=背景(0)，其余=前景(Big)
            for (int y = 0; y < h; y++)
            {
                int baseIdx = y * w;
                for (int x = 0; x < w; x++)
                    frow[x] = road[baseIdx + x] > 0.5f ? 0f : Big;
                var gx = Edt1D(frow);
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
                var gy = Edt1D(fcol);
                for (int y = 0; y < h; y++)
                {
                    int idx = y * w + x;
                    if (layerIds[idx] > 0) // 语义层（不包含 Layer0 / 透明 -1）
                    {
                        float d = Mathf.Sqrt(Mathf.Max(0f, gy[y]));
                        off[idx] = d * m;
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
        private static float[] Edt1D(float[] f)
        {
            int n = f.Length;
            var d = new float[n];
            var v = new int[n];
            var z = new float[n + 1];
            int k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                float s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
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
                d[q] = dv * dv + f[v[k]];
            }
            return d;
        }

        // ---------- 随机游走（G 占用/间隔 + B 路面掩码） ----------

        /// <summary>
        /// 在单个组合层级内生成路网：写 G（防卷曲占用缓冲）与 B（路面硬掩码）。
        /// 所有游走点必须与起点同组合层；候选点跨组直接跳过。
        /// </summary>
        public static void GenerateRoads(int[] layerIds, float[] r, int w, int h, List<int> group,
            TerrainPaintConfig cfg, List<LayerConfigSO> layers, float worldPerPixel,
            out float[] g, out float[] b)
        {
            g = new float[w * h];
            b = new float[w * h];
            var rng = new System.Random(cfg.walkSeed);

            float stepPx = Mathf.Max(1f, cfg.roadStep / worldPerPixel);
            float spacingPx = Mathf.Max(1f, cfg.gApplySpacing / worldPerPixel);
            int candRadiusPx = Mathf.Max(1, Mathf.RoundToInt(stepPx));

            var allPoints = new List<Vector2Int>();

            while (true)
            {
                var start = FindStart(layerIds, r, w, h, group, cfg, rng, g);
                if (start == null)
                    break;
                if (CoverageStop(start.Value, w, h, cfg, rng, stepPx, g))
                    break;

                var path = WalkPath(start.Value, layerIds, r, w, h, group, cfg, layers,
                    worldPerPixel, stepPx, spacingPx, candRadiusPx, rng, g);

                if (path.Count > 0)
                {
                    var last = path[path.Count - 1];
                    // 闭环合并：末点附近的历史点 → 接入网络
                    Vector2Int? join = null;
                    foreach (var p in allPoints)
                    {
                        if (Dist(p, last) < stepPx * 2f)
                        {
                            join = p;
                            break;
                        }
                    }
                    if (join.HasValue)
                    {
                        path.Add(join.Value);
                        // 连接段 G 按防卷曲规则补画（沿途各点所在层的 roadSpacingMin）
                        StampLineFloat(g, w, h, last, join.Value,
                            idx => GRadiusAt(layerIds, layers, worldPerPixel, idx, spacingPx), 1f);
                    }

                    // B：路径所有边统一画胶囊（半径 = 边所在层 roadWidth）
                    for (int i = 0; i + 1 < path.Count; i++)
                    {
                        var a = path[i];
                        var c = path[i + 1];
                        StampLineFloat(b, w, h, a, c,
                            idx => BRadiusAt(layerIds, layers, worldPerPixel, idx, stepPx), 1f);
                    }

                    foreach (var p in path)
                        allPoints.Add(p);
                }
            }
        }

        private static Vector2Int? FindStart(int[] layerIds, float[] r, int w, int h,
            List<int> group, TerrainPaintConfig cfg, System.Random rng, float[] g)
        {
            for (int t = 0; t < cfg.walkStartTries; t++)
            {
                int x = rng.Next(w);
                int y = rng.Next(h);
                int idx = y * w + x;
                if (r[idx] > 0.001f && g[idx] < 0.5f && InGroup(layerIds[idx], group))
                    return new Vector2Int(x, y);
            }
            for (int i = 0; i < r.Length; i++)
            {
                if (r[i] > 0.001f && g[i] < 0.5f && InGroup(layerIds[i], group))
                    return new Vector2Int(i % w, i / w);
            }
            return null;
        }

        private static bool CoverageStop(Vector2Int start, int w, int h,
            TerrainPaintConfig cfg, System.Random rng, float radiusPx, float[] g)
        {
            int n = Mathf.Max(1, cfg.startCoverStopSamples);
            int radius = Mathf.Max(1, Mathf.RoundToInt(radiusPx));
            int occupied = 0;
            for (int i = 0; i < n; i++)
            {
                int x = start.x + rng.Next(-radius, radius + 1);
                int y = start.y + rng.Next(-radius, radius + 1);
                if (x < 0 || x >= w || y < 0 || y >= h)
                    continue;
                if (g[y * w + x] > 0.5f)
                    occupied++;
            }
            return occupied > n / 2;
        }

        private static List<Vector2Int> WalkPath(Vector2Int start, int[] layerIds, float[] r,
            int w, int h, List<int> group, TerrainPaintConfig cfg, List<LayerConfigSO> layers,
            float worldPerPixel, float stepPx, float spacingPx, int candRadiusPx,
            System.Random rng, float[] g)
        {
            var path = new List<Vector2Int> { start };
            var cur = start;
            var anchor = start;

            for (int step = 0; step < cfg.maxStepsPerPath; step++)
            {
                var cands = SampleCandidates(cur, w, h, cfg, candRadiusPx, rng);
                var valid = new List<Vector2Int>();
                foreach (var c in cands)
                {
                    int idx = c.y * w + c.x;
                    if (r[idx] <= 0.001f) continue;
                    if (!InGroup(layerIds[idx], group)) continue;
                    if (g[idx] > 0.5f) continue;
                    if (Dist(c, cur) < stepPx - 0.5f) continue;
                    valid.Add(c);
                }
                if (valid.Count == 0)
                    break;

                var next = WeightedPick(valid, r, w, rng);
                path.Add(next);

                // G 应用：与锚点距离超过 gApplySpacing（防卷曲距离）才批量回填 G 胶囊
                if (Dist(next, anchor) > spacingPx)
                {
                    StampLineFloat(g, w, h, anchor, next,
                        idx => GRadiusAt(layerIds, layers, worldPerPixel, idx, spacingPx), 1f);
                    anchor = next;
                }
                cur = next;
            }
            return path;
        }

        private static List<Vector2Int> SampleCandidates(Vector2Int cur, int w, int h,
            TerrainPaintConfig cfg, int candRadiusPx, System.Random rng)
        {
            var list = new List<Vector2Int>(cfg.walkCandidateCount);
            for (int i = 0; i < cfg.walkCandidateCount; i++)
            {
                double ang = rng.NextDouble() * 2.0 * Math.PI;
                double rad = Math.Sqrt(rng.NextDouble()) * candRadiusPx;
                int x = cur.x + (int)Math.Round(Math.Cos(ang) * rad);
                int y = cur.y + (int)Math.Round(Math.Sin(ang) * rad);
                if (x >= 0 && x < w && y >= 0 && y < h)
                    list.Add(new Vector2Int(x, y));
            }
            return list;
        }

        private static Vector2Int WeightedPick(List<Vector2Int> cands, float[] r, int w, System.Random rng)
        {
            float total = 0f;
            foreach (var c in cands)
                total += Mathf.Max(0.0001f, r[c.y * w + c.x]);
            double roll = rng.NextDouble() * total;
            float acc = 0f;
            foreach (var c in cands)
            {
                acc += Mathf.Max(0.0001f, r[c.y * w + c.x]);
                if (acc >= roll)
                    return c;
            }
            return cands[cands.Count - 1];
        }

        // ---------- 一键计算（多组合层） ----------

        /// <summary>解析层ID并按全部组合层计算 R/G/B，返回合成 RGB 图；rOut 为真实距离值（可直接写 distance MapData）。
        /// 若邻接组存在重复层级则报错返回 null。</summary>
        public static Texture2D ComputeAll(TerrainPaintProjectSO project, int[] layerIds,
            out float[] rOut, out float[] gOut, out float[] bOut)
        {
            int w = project.layerMap != null ? project.layerMap.width : project.mapResolution;
            int h = project.layerMap != null ? project.layerMap.height : project.mapResolution;
            return ComputeAll(project, layerIds, w, h, out rOut, out gOut, out bOut);
        }

        /// <summary>ComputeAll 的显式尺寸重载（不再依赖 project.layerMap 存在）。</summary>
        public static Texture2D ComputeAll(TerrainPaintProjectSO project, int[] layerIds, int w, int h,
            out float[] rOut, out float[] gOut, out float[] bOut)
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
                var rg = ComputeR(layerIds, w, h, group, out _);
                for (int i = 0; i < r.Length; i++)
                    r[i] = Mathf.Max(r[i], rg[i]);

                GenerateRoads(layerIds, rg, w, h, group, project.config, project.layers,
                    project.config.worldPerPixel, out var gg, out var bb);
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

        /// <summary>合成 RGB 图：R=距离场，G=占用/间隔，B=路面掩码。R 由调用方保证已归一化（显示语义）。</summary>
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
        /// 烘焙高度数据（float[][]）：逐像素按所在层的高度范围，用 Perlin 噪声在该范围内插值生成
        /// **真实高度**（单位与层 heightRange 一致），不归一化、不持久化范围。
        /// MapData 存储层直接写本方法结果到 "height" key；显示/构建时遍历数据现算 min/max。
        /// </summary>
        public static float[][] BakeHeightData(TerrainPaintProjectSO project, int[] layerIds, int w, int h)
        {
            if (layerIds == null || layerIds.Length != w * h)
            {
                Debug.LogError("[Terrain Road Gen] 烘焙高度图失败：layerIds 与尺寸不匹配");
                return null;
            }

            float scale = Mathf.Max(0.001f, project.heightScale);
            float seedOff = project.heightSeed * 13.37f;

            var data = new float[h][];

            for (int y = 0; y < h; y++)
            {
                var row = new float[w];
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int lid = layerIds[i];
                    Vector2 range = (lid >= 0 && lid < project.layers.Count && project.layers[lid] != null)
                        ? project.layers[lid].heightRange
                        : new Vector2(0f, 0f);

                    // Perlin 噪声（seed 偏移 + 空间频率 scale），在层级高度范围内插值（真实高度，不归一化）
                    float n = Mathf.PerlinNoise(x * scale + seedOff, y * scale + seedOff);
                    row[x] = Mathf.Lerp(range.x, range.y, n);
                }
                data[y] = row;
            }

            return data;
        }

        /// <summary>
        /// 烘焙高度图（Texture2D，兼容旧接口）：调用 <see cref="BakeHeightData"/> 后，
        /// 内部现算 min/max 把真实高度归一化写入 R 通道（显示语义；范围不持久化）。
        /// 窗口已改用数据路径，本方法保留供外部/调试使用。
        /// </summary>
        public static Texture2D BakeHeightMap(TerrainPaintProjectSO project, int[] layerIds, int w, int h)
        {
            var data = BakeHeightData(project, layerIds, w, h);
            if (data == null)
                return null;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;

            // 现算真实 min/max（范围由数据产生，不持久化）
            float hmin = float.MaxValue, hmax = float.MinValue;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = data[y][x];
                    if (v < hmin) hmin = v;
                    if (v > hmax) hmax = v;
                }
            float range = hmax - hmin;
            if (range < 0.0001f) range = 1f;

            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte b = (byte)Mathf.RoundToInt(Mathf.Clamp01((data[y][x] - hmin) / range) * 255f);
                    px[y * w + x] = new Color32(b, 0, 0, 255);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
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

        private static float Dist(Vector2Int a, Vector2Int b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static float GRadiusAt(int[] layerIds, List<LayerConfigSO> layers, float worldPerPixel, int idx, float fallback)
        {
            int id = layerIds[idx];
            if (id < 0 || id >= layers.Count || layers[id] == null)
                return fallback;
            return Mathf.Max(1f, layers[id].roadSpacingMin / worldPerPixel);
        }

        private static float BRadiusAt(int[] layerIds, List<LayerConfigSO> layers, float worldPerPixel, int idx, float fallback)
        {
            int id = layerIds[idx];
            if (id < 0 || id >= layers.Count || layers[id] == null)
                return fallback;
            return Mathf.Max(1f, layers[id].roadWidth / worldPerPixel);
        }

        /// <summary>沿线盖圆戳写入 float 缓冲（步长 1 像素，半径按沿途各点所在层取值）。</summary>
        private static void StampLineFloat(float[] buf, int w, int h, Vector2Int a, Vector2Int b,
            Func<int, float> radiusAt, float value)
        {
            float dx = b.x - a.x;
            float dy = b.y - a.y;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist < 0.5f)
            {
                StampCircleFloat(buf, w, h, a.x, a.y, radiusAt(a.y * w + a.x), value);
                return;
            }
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist));
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                int x = Mathf.RoundToInt(a.x + dx * t);
                int y = Mathf.RoundToInt(a.y + dy * t);
                int idx = y * w + x;
                StampCircleFloat(buf, w, h, x, y, radiusAt(idx), value);
            }
        }

        private static void StampCircleFloat(float[] buf, int w, int h, int cx, int cy, float radius, float value)
        {
            int r = Mathf.CeilToInt(radius);
            float r2 = radius * radius;
            for (int y = cy - r; y <= cy + r; y++)
            {
                if (y < 0 || y >= h) continue;
                int rowBase = y * w;
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (x < 0 || x >= w) continue;
                    float ddx = x - cx;
                    float ddy = y - cy;
                    if (ddx * ddx + ddy * ddy <= r2)
                        buf[rowBase + x] = value;
                }
            }
        }
    }
}
#endif
