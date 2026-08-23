using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 放置缓存（散布 / 摆件 / 定点）——编辑器计算 → 运行时复用。
    ///
    /// 组织方式：**每类编辑的每个生成组一个文件**，放在「配置目录/PlacementCache/」下
    /// （与 MapData 同级，不混放）。文件命名：
    ///   Scatter_00.txt / Scatter_01.txt ...（散布生成组，像素坐标）
    ///   Prop_00.txt    / Prop_01.txt    ...（摆件生成组，世界坐标）
    ///   Fixed_00.txt   / Fixed_01.txt   ...（定点生成组，世界坐标 + 缩放）
    /// 三类格式独立（参数不同），因此各自编解码。
    ///
    /// 每个文件头部记录：指纹 + 该生成组的区块大小（chunkSize）+ 可见距离（visibleDistance），
    /// 便于管理与核对；任一变化（改参数/改种子/改 prefab/改 layerMap 等）都会使指纹失配，
    /// 运行时回退重新计算（只留内存，不写盘）。
    ///
    /// 物体标识：直接记录 prefab 的 GetInstanceID()（同一 Unity 会话内稳定，含编辑器→Play 切换）；
    /// 跨会话/Player 构建后 instanceId 会变，指纹随之失配，自动回退重新计算——这是预期行为。
    /// </summary>
    public static class PlacementCache
    {
        public const string HeaderLine = "#placementCache;v=2";

        // ---------- 数据条目 ----------

        [Serializable]
        public sealed class ScatterPlacement
        {
            public int prefabInstanceId;
            public int pixelX;
            public int pixelZ;
            public float scale;
            public float yaw;
        }

        [Serializable]
        public sealed class PropPlacement
        {
            public int prefabInstanceId;
            public float worldX;
            public float worldZ;
            public float yaw;
        }

        [Serializable]
        public sealed class FixedPlacement
        {
            public int prefabInstanceId;
            public float worldX;
            public float worldZ;
            public float yaw;
            public float scale;
        }

        /// <summary>单个生成组的缓存数据（含该组区块参数）。</summary>
        public sealed class CacheData
        {
            public string fingerprint;
            public Vector2 chunkSize;
            public float visibleDistance;
            public readonly List<ScatterPlacement> scatter = new List<ScatterPlacement>();
            public readonly List<PropPlacement> props = new List<PropPlacement>();
            public readonly List<FixedPlacement> fixedPoints = new List<FixedPlacement>();
        }

        // ---------- 文件命名 ----------

        public static string ScatterFileName(int groupIndex) => $"Scatter_{groupIndex:00}.txt";
        public static string PropFileName(int groupIndex) => $"Prop_{groupIndex:00}.txt";
        public static string FixedFileName(int groupIndex) => $"Fixed_{groupIndex:00}.txt";

        /// <summary>key → 安全文件名（只保留字母/数字/下划线/连字符）。</summary>
        public static string SanitizeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "unnamed";
            var chars = new char[key.Length];
            int n = 0;
            foreach (var c in key)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                    chars[n++] = c;
            }
            return n > 0 ? new string(chars, 0, n) : "unnamed";
        }

        // ---------- 编码 ----------

        /// <summary>编码为散布格式（Scatter_xx.txt：instanceId,px,pz,scale,yaw）。</summary>
        public static string EncodeScatter(CacheData data)
        {
            var sb = BeginEncode(data, "Scatter");
            for (int i = 0; i < data.scatter.Count; i++)
            {
                ScatterPlacement p = data.scatter[i];
                sb.Append(p.prefabInstanceId).Append(',')
                  .Append(p.pixelX).Append(',')
                  .Append(p.pixelZ).Append(',')
                  .Append(F(p.scale)).Append(',')
                  .Append(F(p.yaw)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>编码为摆件格式（Prop_xx.txt：instanceId,wx,wz,yaw）。</summary>
        public static string EncodeProps(CacheData data)
        {
            var sb = BeginEncode(data, "Prop");
            for (int i = 0; i < data.props.Count; i++)
            {
                PropPlacement p = data.props[i];
                sb.Append(p.prefabInstanceId).Append(',')
                  .Append(F(p.worldX)).Append(',')
                  .Append(F(p.worldZ)).Append(',')
                  .Append(F(p.yaw)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>编码为定点格式（Fixed_xx.txt：instanceId,wx,wz,yaw,scale）。</summary>
        public static string EncodeFixed(CacheData data)
        {
            var sb = BeginEncode(data, "Fixed");
            for (int i = 0; i < data.fixedPoints.Count; i++)
            {
                FixedPlacement p = data.fixedPoints[i];
                sb.Append(p.prefabInstanceId).Append(',')
                  .Append(F(p.worldX)).Append(',')
                  .Append(F(p.worldZ)).Append(',')
                  .Append(F(p.yaw)).Append(',')
                  .Append(F(p.scale)).Append('\n');
            }
            return sb.ToString();
        }

        private static StringBuilder BeginEncode(CacheData data, string kind)
        {
            var sb = new StringBuilder();
            sb.Append(HeaderLine).Append(";kind=").Append(kind).Append('\n');
            sb.Append("#fingerprint=").Append(data.fingerprint).Append('\n');
            sb.Append("#chunkSize=").Append(F(data.chunkSize.x)).Append(';').Append(F(data.chunkSize.y)).Append('\n');
            sb.Append("#visibleDistance=").Append(F(data.visibleDistance)).Append('\n');
            return sb;
        }

        // ---------- 解码 ----------

        /// <summary>解码散布文件（Scatter_xx.txt）。格式损坏或指纹缺失返回 null。</summary>
        public static CacheData DecodeScatter(string text)
        {
            CacheData data = Decode(text, "Scatter");
            if (data == null) return null;
            data.scatter.AddRange(DecodeScatterRows(Headerless(text)));
            return data;
        }

        /// <summary>解码摆件文件（Prop_xx.txt）。</summary>
        public static CacheData DecodeProps(string text)
        {
            CacheData data = Decode(text, "Prop");
            if (data == null) return null;
            data.props.AddRange(DecodePropRows(Headerless(text)));
            return data;
        }

        /// <summary>解码定点文件（Fixed_xx.txt）。</summary>
        public static CacheData DecodeFixed(string text)
        {
            CacheData data = Decode(text, "Fixed");
            if (data == null) return null;
            data.fixedPoints.AddRange(DecodeFixedRows(Headerless(text)));
            return data;
        }

        /// <summary>解析头部（版本/指纹/区块参数），校验 kind 匹配；返回 CacheData 或 null。</summary>
        private static CacheData Decode(string text, string expectedKind)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] lines = text.Split('\n');
            var data = new CacheData();
            bool headerSeen = false;
            bool fingerprintSeen = false;

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li].Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    if (line.StartsWith(HeaderLine, StringComparison.Ordinal))
                    {
                        if (!line.Contains(";kind=" + expectedKind)) return null;
                        headerSeen = true;
                        continue;
                    }
                    if (line.StartsWith("#fingerprint=", StringComparison.Ordinal))
                    {
                        data.fingerprint = line.Substring("#fingerprint=".Length);
                        fingerprintSeen = true;
                        continue;
                    }
                    if (line.StartsWith("#chunkSize=", StringComparison.Ordinal))
                    {
                        ParseVector2(line.Substring("#chunkSize=".Length), ref data.chunkSize);
                        continue;
                    }
                    if (line.StartsWith("#visibleDistance=", StringComparison.Ordinal))
                    {
                        TryFloat(line.Substring("#visibleDistance=".Length), out data.visibleDistance);
                        continue;
                    }
                    continue; // 未知头行
                }
                break; // 数据行开始，头部解析结束
            }
            return headerSeen && fingerprintSeen ? data : null;
        }

        /// <summary>截取数据行（跳过 # 开头行与空行）。</summary>
        private static List<string> Headerless(string text)
        {
            var rows = new List<string>();
            string[] lines = text.Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                rows.Add(line);
            }
            return rows;
        }

        private static List<ScatterPlacement> DecodeScatterRows(List<string> rows)
        {
            var result = new List<ScatterPlacement>();
            foreach (string row in rows)
            {
                string[] parts = row.Split(',');
                if (parts.Length != 5) continue;
                var p = new ScatterPlacement();
                if (!TryInt(parts[0], out p.prefabInstanceId)) continue;
                if (!TryInt(parts[1], out p.pixelX)) continue;
                if (!TryInt(parts[2], out p.pixelZ)) continue;
                if (!TryFloat(parts[3], out p.scale)) continue;
                if (!TryFloat(parts[4], out p.yaw)) continue;
                result.Add(p);
            }
            return result;
        }

        private static List<PropPlacement> DecodePropRows(List<string> rows)
        {
            var result = new List<PropPlacement>();
            foreach (string row in rows)
            {
                string[] parts = row.Split(',');
                if (parts.Length != 4) continue;
                var p = new PropPlacement();
                if (!TryInt(parts[0], out p.prefabInstanceId)) continue;
                if (!TryFloat(parts[1], out p.worldX)) continue;
                if (!TryFloat(parts[2], out p.worldZ)) continue;
                if (!TryFloat(parts[3], out p.yaw)) continue;
                result.Add(p);
            }
            return result;
        }

        private static List<FixedPlacement> DecodeFixedRows(List<string> rows)
        {
            var result = new List<FixedPlacement>();
            foreach (string row in rows)
            {
                string[] parts = row.Split(',');
                if (parts.Length != 5) continue;
                var p = new FixedPlacement();
                if (!TryInt(parts[0], out p.prefabInstanceId)) continue;
                if (!TryFloat(parts[1], out p.worldX)) continue;
                if (!TryFloat(parts[2], out p.worldZ)) continue;
                if (!TryFloat(parts[3], out p.yaw)) continue;
                if (!TryFloat(parts[4], out p.scale)) continue;
                result.Add(p);
            }
            return result;
        }

        // ---------- 小工具 ----------

        private static void ParseVector2(string text, ref Vector2 value)
        {
            string[] parts = text.Split(';');
            if (parts.Length == 2)
            {
                if (TryFloat(parts[0], out float x) && TryFloat(parts[1], out float y))
                    value = new Vector2(x, y);
            }
        }

        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool TryInt(string s, out int value)
        {
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryFloat(string s, out float value)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
