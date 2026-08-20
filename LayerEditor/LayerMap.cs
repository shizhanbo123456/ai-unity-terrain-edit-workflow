#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 层图数据：一张 CPU 可读写的 RGBA32 图片（Color32[] 缓冲 + Texture2D 呈现）。
    ///
    /// 提供三类基础绘画：圆形画笔（单击画圆 / 拖拽画直线条带）、矩形区域填充、
    /// 三角形区域填充。所有绘制均为"完全覆盖"（alpha 恒为 1，直接覆盖目标像素，
    /// 不做边缘模糊）；写入透明色 (0,0,0,0) 即擦除为过渡区域。
    ///
    /// 不依赖 UnityEditor，编辑器窗口与后续 bridge 命令行工具共用同一套绘制逻辑。
    /// </summary>
    public class LayerMap
    {
        /// <summary>地形配置根目录（Assets 相对路径）；每个配置一个子文件夹，内含总 SO + 层级 SO + 层次图。</summary>
        public const string ConfigRootDirRelative =
            "Assets/ai-unity-terrain-edit-workflow/LayerEditor/TerrainGeneratorConfigs";

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
#endif
