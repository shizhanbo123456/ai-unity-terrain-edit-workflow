using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    public enum LayerPaintOperationType
    {
        Line,
        Rectangle,
        Triangle,
        Polygon,
        Ellipse,
        Sector,
    }

    /// <summary>区域编辑的一条可序列化绘画操作；点与半径均使用 LayerMap 像素坐标。</summary>
    [Serializable]
    public class LayerPaintOperation
    {
        public LayerPaintOperationType type;
        public Vector2Int pointA;
        public Vector2Int pointB;
        public Vector2Int pointC;
        public List<Vector2Int> points = new List<Vector2Int>();
        public int radius;
        public int layerIndex;
    }

    /// <summary>
    /// 层图数据：一张 CPU 可读写的 RGBA32 图片（Color32[] 缓冲 + Texture2D 呈现）。
    ///
    /// 提供圆形画笔、矩形、三角形、凸多边形、轴对齐椭圆和变半径扇形绘制。
    /// 所有绘制均为"完全覆盖"（alpha 恒为 1，直接覆盖目标像素，
    /// 不做边缘模糊）；写入透明色 (0,0,0,0) 即擦除为过渡区域。
    ///
    /// 不依赖 UnityEditor，编辑器窗口与后续 bridge 命令行工具共用同一套绘制逻辑。
    /// </summary>
    public class LayerMap
    {
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>当前图片（始终与 _pixels 同步，供显示与导出）。</summary>
        public Texture2D Texture => _texture;

        private Color32[] _pixels;
        private Texture2D _texture;
        private readonly Stack<Color32[]> _undoStack = new Stack<Color32[]>();
        private const int MaxUndoDepth = 32;

        public LayerMap(int width, int height)
        {
            Resize(width, height);
        }

        /// <summary>重建图片并全部清为透明（保留尺寸则保留画布内容）。</summary>
        public void Resize(int width, int height)
        {
            Width = Mathf.Max(1, width);
            Height = Mathf.Max(1, height);
            _pixels = new Color32[Width * Height]; // 默认全透明 (0,0,0,0)
            _undoStack.Clear();
            RebuildTexture();
        }

        /// <summary>读取像素（越界返回透明）。</summary>
        public Color32 GetPixel(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                return new Color32(0, 0, 0, 0);
            return _pixels[y * Width + x];
        }

        /// <summary>写入单个像素（越界忽略）。</summary>
        public void SetPixel(int x, int y, Color32 color)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                return;
            _pixels[y * Width + x] = color;
        }

        /// <summary>整体填充（layer0 透明 = 全部擦除）。</summary>
        public void Clear(Color32 color)
        {
            PushUndo();
            for (int i = 0; i < _pixels.Length; i++)
                _pixels[i] = color;
            Apply();
        }

        /// <summary>圆形画笔：单击调用（画实心圆）；拖拽由 DrawLine 内部逐点调用。</summary>
        public void FillCircle(int cx, int cy, int radius, Color32 color)
        {
            PushUndo();
            StampCircle(cx, cy, radius, color);
            Apply();
        }

        /// <summary>矩形区域填充（两角点任意顺序，自动归一化）。</summary>
        public void FillRect(int x0, int y0, int x1, int y1, Color32 color)
        {
            PushUndo();
            int minX = Mathf.Min(x0, x1), maxX = Mathf.Max(x0, x1);
            int minY = Mathf.Min(y0, y1), maxY = Mathf.Max(y0, y1);
            for (int y = minY; y <= maxY; y++)
            {
                if (y < 0 || y >= Height) continue;
                for (int x = minX; x <= maxX; x++)
                {
                    if (x < 0 || x >= Width) continue;
                    _pixels[y * Width + x] = color;
                }
            }
            Apply();
        }

        /// <summary>三角形区域填充（三个顶点，任意顺序）。</summary>
        public void FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Color32 color)
        {
            PushUndo();
            int minX = Mathf.Min(x0, Mathf.Min(x1, x2));
            int maxX = Mathf.Max(x0, Mathf.Max(x1, x2));
            int minY = Mathf.Min(y0, Mathf.Min(y1, y2));
            int maxY = Mathf.Max(y0, Mathf.Max(y1, y2));
            for (int y = minY; y <= maxY; y++)
            {
                if (y < 0 || y >= Height) continue;
                for (int x = minX; x <= maxX; x++)
                {
                    if (x < 0 || x >= Width) continue;
                    if (PointInTriangle(x, y, x0, y0, x1, y1, x2, y2))
                        _pixels[y * Width + x] = color;
                }
            }
            Apply();
        }

        /// <summary>直线绘制：从 (x0,y0) 到 (x1,y1) 用圆形笔刷沿线盖戳，形成等宽条带。</summary>
        public void DrawLine(int x0, int y0, int x1, int y1, int radius, Color32 color)
        {
            PushUndo();
            StampLine(x0, y0, x1, y1, radius, color);
            Apply();
        }

        /// <summary>把一条记录操作增量应用到当前 LayerMap。</summary>
        public void ApplyPaintOperation(LayerPaintOperation operation, List<LayerConfigSO> layers)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));
            ApplyPaintOperationToPixels(operation, layers);
            Apply();
        }

        /// <summary>清空画布，并按列表顺序重新应用全部操作，完整重建 LayerMap。</summary>
        public void RebuildFromPaintOperations(
            int width,
            int height,
            IList<LayerPaintOperation> operations,
            List<LayerConfigSO> layers)
        {
            Resize(width, height);
            if (operations != null)
            {
                for (int i = 0; i < operations.Count; i++)
                {
                    if (operations[i] != null)
                        ApplyPaintOperationToPixels(operations[i], layers);
                }
            }
            Apply();
        }

        /// <summary>是否可撤销。</summary>
        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>撤销上一步（无快照返回 false）。</summary>
        public bool Undo()
        {
            if (_undoStack.Count == 0)
                return false;
            _pixels = _undoStack.Pop();
            Apply();
            return true;
        }

        /// <summary>把缓冲写入纹理并上传 GPU（绘制后调用一次）。</summary>
        public void Apply()
        {
            if (_texture == null)
                RebuildTexture();
            _texture.SetPixels32(_pixels);
            _texture.Apply();
        }

        /// <summary>导出 PNG 到指定路径（目录不存在自动创建）。</summary>
        public void SavePng(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, _texture.EncodeToPNG());
        }

        /// <summary>从 PNG 加载（成功返回 true，并替换当前画布；失败返回 false）。</summary>
        public bool LoadPng(string path)
        {
            if (!File.Exists(path))
                return false;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[LayerMap] 读取 {path} 失败: {e.Message}");
                return false;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return false;
            }

            if (_texture != null)
                UnityEngine.Object.DestroyImmediate(_texture);
            _texture = tex;
            _texture.filterMode = FilterMode.Point;
            _texture.wrapMode = TextureWrapMode.Clamp;
            Width = _texture.width;
            Height = _texture.height;
            _pixels = _texture.GetPixels32();
            _undoStack.Clear();
            return true;
        }

        // ---------- MapData 持久化（画布 ↔ 层ID float[][]） ----------

        /// <summary>
        /// 画布像素 → 层ID 二维数组（float 值 = 层索引；透明/未匹配 = -1）。
        /// 与 TerrainRoadGen.ParseLayerIds 同规则（按颜色精确匹配），MapData 存储层写 "layerMap" key 用。
        /// </summary>
        public float[][] ToIdArray(List<LayerConfigSO> layers)
        {
            var data = new float[Height][];
            for (int y = 0; y < Height; y++)
            {
                var row = new float[Width];
                int baseIdx = y * Width;
                for (int x = 0; x < Width; x++)
                    row[x] = FindLayerId(_pixels[baseIdx + x], layers);
                data[y] = row;
            }
            return data;
        }

        /// <summary>
        /// 层ID 二维数组 → 画布（id→层级颜色；-1/越界→透明）。
        /// 按 ids 尺寸重建画布（清空撤销栈），供打开配置时恢复绘制现场。
        /// </summary>
        public void LoadFromIdArray(float[][] ids, List<LayerConfigSO> layers)
        {
            if (ids == null || ids.Length == 0 || ids[0] == null)
                return;
            int h = ids.Length;
            int w = ids[0].Length;
            Resize(w, h); // 重建透明画布（同时清撤销栈）

            for (int y = 0; y < h; y++)
            {
                var row = ids[y];
                int baseIdx = y * w;
                for (int x = 0; x < w; x++)
                {
                    int id = Mathf.RoundToInt(row[x]);
                    _pixels[baseIdx + x] = (id >= 0 && id < layers.Count && layers[id] != null)
                        ? layers[id].color
                        : new Color32(0, 0, 0, 0);
                }
            }
            Apply();
        }

        private static float FindLayerId(Color32 c, List<LayerConfigSO> layers)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                var lc = layers[i].color;
                if (lc.r == c.r && lc.g == c.g && lc.b == c.b && lc.a == c.a)
                    return i;
            }
            return -1f;
        }

        // ---------- 内部实现 ----------

        private void RebuildTexture()
        {
            if (_texture != null)
                UnityEngine.Object.DestroyImmediate(_texture);
            _texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            _texture.filterMode = FilterMode.Point;
            _texture.wrapMode = TextureWrapMode.Clamp;
            _texture.SetPixels32(_pixels);
            _texture.Apply();
        }

        private void ApplyPaintOperationToPixels(
            LayerPaintOperation operation,
            List<LayerConfigSO> layers)
        {
            Color32 color = ResolveOperationColor(operation.layerIndex, layers);
            Vector2Int a = operation.pointA;
            Vector2Int b = operation.pointB;
            Vector2Int c = operation.pointC;
            switch (operation.type)
            {
                case LayerPaintOperationType.Line:
                    StampLine(a.x, a.y, b.x, b.y, operation.radius, color);
                    break;
                case LayerPaintOperationType.Rectangle:
                    StampRectangle(a.x, a.y, b.x, b.y, color);
                    break;
                case LayerPaintOperationType.Triangle:
                    StampTriangle(a.x, a.y, b.x, b.y, c.x, c.y, color);
                    break;
                case LayerPaintOperationType.Polygon:
                    StampConvexPolygon(operation.points, color);
                    break;
                case LayerPaintOperationType.Ellipse:
                    StampEllipse(a, b, color);
                    break;
                case LayerPaintOperationType.Sector:
                    StampSector(a, b, c, color);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation.type));
            }
        }

        private static Color32 ResolveOperationColor(int layerIndex, List<LayerConfigSO> layers)
        {
            if (layerIndex <= 0 || layers == null || layerIndex >= layers.Count || layers[layerIndex] == null)
                return LayerPalette.Transparent;
            return layers[layerIndex].color;
        }

        private void StampRectangle(int x0, int y0, int x1, int y1, Color32 color)
        {
            int minX = Mathf.Max(0, Mathf.Min(x0, x1));
            int maxX = Mathf.Min(Width - 1, Mathf.Max(x0, x1));
            int minY = Mathf.Max(0, Mathf.Min(y0, y1));
            int maxY = Mathf.Min(Height - 1, Mathf.Max(y0, y1));
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                _pixels[y * Width + x] = color;
        }

        private void StampTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Color32 color)
        {
            int minX = Mathf.Max(0, Mathf.Min(x0, Mathf.Min(x1, x2)));
            int maxX = Mathf.Min(Width - 1, Mathf.Max(x0, Mathf.Max(x1, x2)));
            int minY = Mathf.Max(0, Mathf.Min(y0, Mathf.Min(y1, y2)));
            int maxY = Mathf.Min(Height - 1, Mathf.Max(y0, Mathf.Max(y1, y2)));
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                if (PointInTriangle(x, y, x0, y0, x1, y1, x2, y2))
                    _pixels[y * Width + x] = color;
        }

        /// <summary>凸多边形按第一个顶点展开为三角扇；调用方应保证顶点沿边界有序。</summary>
        private void StampConvexPolygon(IList<Vector2Int> points, Color32 color)
        {
            if (points == null || points.Count < 3) return;
            Vector2Int origin = points[0];
            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector2Int b = points[i];
                Vector2Int c = points[i + 1];
                StampTriangle(origin.x, origin.y, b.x, b.y, c.x, c.y, color);
            }
        }

        /// <summary>两个任意顺序的点定义 AABB，以该包围盒为外切矩形填充轴对齐椭圆。</summary>
        private void StampEllipse(Vector2Int a, Vector2Int b, Color32 color)
        {
            int minX = Mathf.Max(0, Mathf.Min(a.x, b.x));
            int maxX = Mathf.Min(Width - 1, Mathf.Max(a.x, b.x));
            int minY = Mathf.Max(0, Mathf.Min(a.y, b.y));
            int maxY = Mathf.Min(Height - 1, Mathf.Max(a.y, b.y));
            float cx = (a.x + b.x) * 0.5f;
            float cy = (a.y + b.y) * 0.5f;
            float rx = Mathf.Abs(b.x - a.x) * 0.5f;
            float ry = Mathf.Abs(b.y - a.y) * 0.5f;
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float nx = rx > 0f ? (x - cx) / rx : (Mathf.Approximately(x, cx) ? 0f : float.PositiveInfinity);
                float ny = ry > 0f ? (y - cy) / ry : (Mathf.Approximately(y, cy) ? 0f : float.PositiveInfinity);
                if (nx * nx + ny * ny <= 1f)
                    _pixels[y * Width + x] = color;
            }
        }

        /// <summary>
        /// 圆心、起始弧点、结束弧点定义最短有向扇形；半径沿起止夹角线性插值。
        /// </summary>
        private void StampSector(Vector2Int center, Vector2Int arcStart, Vector2Int arcEnd, Color32 color)
        {
            Vector2 start = arcStart - center;
            Vector2 end = arcEnd - center;
            float startRadius = start.magnitude;
            float endRadius = end.magnitude;
            if (startRadius <= 0.0001f || endRadius <= 0.0001f) return;

            float startAngle = Mathf.Atan2(start.y, start.x) * Mathf.Rad2Deg;
            float endAngle = Mathf.Atan2(end.y, end.x) * Mathf.Rad2Deg;
            float sweep = Mathf.DeltaAngle(startAngle, endAngle);
            if (Mathf.Abs(sweep) <= 0.0001f) return;
            float maxRadius = Mathf.Max(startRadius, endRadius);
            int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - maxRadius));
            int maxX = Mathf.Min(Width - 1, Mathf.CeilToInt(center.x + maxRadius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(center.y - maxRadius));
            int maxY = Mathf.Min(Height - 1, Mathf.CeilToInt(center.y + maxRadius));

            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 offset = new Vector2(x - center.x, y - center.y);
                if (offset.sqrMagnitude <= 0.0001f)
                {
                    _pixels[y * Width + x] = color;
                    continue;
                }
                float angle = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
                float delta = Mathf.DeltaAngle(startAngle, angle);
                bool insideAngle = sweep > 0f
                    ? delta >= 0f && delta <= sweep
                    : delta <= 0f && delta >= sweep;
                if (!insideAngle) continue;
                float t = Mathf.Clamp01(delta / sweep);
                float radius = Mathf.Lerp(startRadius, endRadius, t);
                if (offset.sqrMagnitude <= radius * radius)
                    _pixels[y * Width + x] = color;
            }
        }

        private void PushUndo()
        {
            _undoStack.Push((Color32[])_pixels.Clone());
            while (_undoStack.Count > MaxUndoDepth)
                _undoStack.Pop();
        }

        /// <summary>实心圆盖戳（不推撤销、不 Apply，供批量内部调用）。</summary>
        private void StampCircle(int cx, int cy, int radius, Color32 color)
        {
            int r = Mathf.Max(0, radius);
            int r2 = r * r;
            int minX = Mathf.Max(0, cx - r);
            int maxX = Mathf.Min(Width - 1, cx + r);
            int minY = Mathf.Max(0, cy - r);
            int maxY = Mathf.Min(Height - 1, cy + r);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r2)
                        _pixels[y * Width + x] = color;
                }
            }
        }

        /// <summary>沿直线以半径/2 为步长盖圆戳（保证条带无缝）。</summary>
        private void StampLine(int x0, int y0, int x1, int y1, int radius, Color32 color)
        {
            int r = Mathf.Max(1, radius);
            float dx = x1 - x0;
            float dy = y1 - y0;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / Mathf.Max(1, r / 2f)));
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                int cx = Mathf.RoundToInt(x0 + dx * t);
                int cy = Mathf.RoundToInt(y0 + dy * t);
                StampCircle(cx, cy, r, color);
            }
        }

        private static bool PointInTriangle(int px, int py,
            int x0, int y0, int x1, int y1, int x2, int y2)
        {
            float d1 = Sign(px, py, x0, y0, x1, y1);
            float d2 = Sign(px, py, x1, y1, x2, y2);
            float d3 = Sign(px, py, x2, y2, x0, y0);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }

        private static float Sign(float px, float py, float ax, float ay, float bx, float by)
        {
            return (px - bx) * (ay - by) - (ax - bx) * (py - by);
        }
    }
}
