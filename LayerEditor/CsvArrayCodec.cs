using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// float[][] 与 CSV 文本之间的手写编解码（无第三方库，InvariantCulture 保证跨平台一致）。
    ///
    /// 格式约定（MapData 存储层）：
    ///   - 首行元数据头（可选）：以 '#' 开头，形如 "#key=height;w=512;h=512"，解析时跳过；
    ///   - 之后每行 = 数据一行，逗号分隔；行数 = h，每行列数 = w（不一致报错）；
    ///   - 数值用 F3（保留 3 位小数）+ InvariantCulture 写出，解析用 float.TryParse(InvariantCulture)；
    ///   - 空行忽略。
    ///
    /// 纯 C# 静态工具类（可进 Player 构建），运行时由 TerrainBuilder 用其解码 TextAsset。
    /// </summary>
    public static class CsvArrayCodec
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>float[][] → CSV 文本（含元数据头）。key 可为 null（头不写 key）。</summary>
        public static string Encode(float[][] data, string key = null)
        {
            int h = data?.Length ?? 0;
            int w = h > 0 && data[0] != null ? data[0].Length : 0;

            var sb = new StringBuilder(h * (w * 5 + 2) + 64);
            if (!string.IsNullOrEmpty(key))
                sb.Append("#key=").Append(key).Append(';');
            sb.Append("w=").Append(w).Append(";h=").Append(h).Append('\n');

            for (int y = 0; y < h; y++)
            {
                var row = data[y];
                for (int x = 0; x < w; x++)
                {
                    if (x > 0) sb.Append(',');
                    sb.Append((row[x]).ToString("F3", Inv));
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>CSV 文本 → float[][]（跳过 # 注释行与空行；列数不一致抛 FormatException）。</summary>
        public static float[][] Decode(string csv)
        {
            if (string.IsNullOrEmpty(csv))
                return null;

            var rows = new List<float[]>(256);
            int width = -1;
            int lineNo = 0;

            foreach (var rawLine in csv.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                lineNo++;
                if (line.Length == 0) continue;
                if (line[0] == '#') continue; // 元数据头 / 注释

                var parts = line.Split(',');
                var row = new float[parts.Length];
                for (int x = 0; x < parts.Length; x++)
                {
                    if (!float.TryParse(parts[x], NumberStyles.Float, Inv, out row[x]))
                        throw new FormatException(
                            $"[CsvArrayCodec] 第 {lineNo} 行第 {x + 1} 列不是合法浮点数: '{parts[x]}'");
                }
                if (width < 0)
                    width = parts.Length;
                else if (parts.Length != width)
                    throw new FormatException(
                        $"[CsvArrayCodec] 第 {lineNo} 行列数 ({parts.Length}) 与首行列数 ({width}) 不一致");
                rows.Add(row);
            }

            return rows.Count == 0 ? null : rows.ToArray();
        }

        /// <summary>一维扁平数组（index = y*width + x）→ 二维锯齿数组。</summary>
        public static float[][] ToJagged(float[] flat, int w, int h)
        {
            if (flat == null || flat.Length != w * h)
                throw new ArgumentException($"[CsvArrayCodec] flat 长度 {flat?.Length ?? 0} 必须为 w*h={w * h}");

            var data = new float[h][];
            for (int y = 0; y < h; y++)
            {
                var row = new float[w];
                Array.Copy(flat, y * w, row, 0, w);
                data[y] = row;
            }
            return data;
        }

        /// <summary>二维锯齿数组 → 一维扁平数组（index = y*width + x）。</summary>
        public static float[] ToFlat(float[][] jagged)
        {
            if (jagged == null || jagged.Length == 0)
                return new float[0];
            int h = jagged.Length;
            int w = jagged[0].Length;
            var flat = new float[w * h];
            for (int y = 0; y < h; y++)
                Array.Copy(jagged[y], 0, flat, y * w, w);
            return flat;
        }
    }
}
