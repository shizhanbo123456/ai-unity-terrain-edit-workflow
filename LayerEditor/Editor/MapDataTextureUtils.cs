#if UNITY_EDITOR
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// float[][] ↔ Texture2D 的编辑器辅助工具（仅用于窗口显示 / 从图片采集数据）。
    ///
    /// 运行时不需要本类——运行时只读 float[][]（来自 MapData 的 TextAsset）；
    /// 图片只是编辑期给用户看的内容，从不作为交付物落盘。
    /// </summary>
    public static class MapDataTextureUtils
    {
        /// <summary>
        /// float[][] → 灰度图（R=G=B=归一化值）。**范围完全由数据现算**（内部先遍历统计真实 min/max，
        /// 不依赖外部记录），并通过 out 参数传出；避免"记录的范围"与数据不同步。
        /// </summary>
        public static Texture2D ToTexture(float[][] data, out float min, out float max)
        {
            int h = data?.Length ?? 0;
            int w = h > 0 && data[0] != null ? data[0].Length : 0;
            min = 0f;
            max = 0f;
            if (w <= 0 || h <= 0)
                return null;

            // 第一遍：统计真实 min/max（范围由数据产生）
            min = float.MaxValue;
            max = float.MinValue;
            for (int y = 0; y < h; y++)
            {
                var row = data[y];
                for (int x = 0; x < w; x++)
                {
                    float v = row[x];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;

            float range = max - min;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                var row = data[y];
                for (int x = 0; x < w; x++)
                {
                    float t = range > 0.0001f ? Mathf.Clamp01((row[x] - min) / range) : 0f;
                    byte b = (byte)Mathf.RoundToInt(t * 255f);
                    px[y * w + x] = new Color32(b, b, b, 255);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Texture2D → float[][]（默认取 R 通道）。channel: 0=R, 1=G, 2=B, 3=A。
        /// scale 用于反归一化（如贴图 0~255 转 0~1 时传 1f/255f）。
        /// </summary>
        public static float[][] ToArray(Texture2D tex, int channel = 0, float scale = 1f)
        {
            if (tex == null)
                return null;
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            var data = new float[h][];
            for (int y = 0; y < h; y++)
            {
                var row = new float[w];
                for (int x = 0; x < w; x++)
                {
                    var c = px[y * w + x];
                    float v = channel == 0 ? c.r : channel == 1 ? c.g : channel == 2 ? c.b : c.a;
                    row[x] = v * scale;
                }
                data[y] = row;
            }
            return data;
        }
    }
}
#endif // UNITY_EDITOR
