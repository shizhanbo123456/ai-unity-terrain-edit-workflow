using System;
using System.Collections.Generic;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 一次地形生成过程使用的 MapData 内存集合。
    /// Player 中的新计算结果只写入本对象，不写入磁盘，也不修改配置资产。
    /// </summary>
    public sealed class TerrainMapData
    {
        private readonly Dictionary<string, float[][]> _values =
            new Dictionary<string, float[][]>(StringComparer.Ordinal);

        public IEnumerable<string> Keys => _values.Keys;

        public static TerrainMapData Load(TerrainPaintProjectSO project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            var result = new TerrainMapData();
            if (project.mapDataFiles == null) return result;

            foreach (var entry in project.mapDataFiles)
            {
                if (entry == null || string.IsNullOrEmpty(entry.key) || result.Contains(entry.key)) continue;
                float[][] value = project.ReadMap(entry.key);
                if (value != null) result.Set(entry.key, value);
            }
            return result;
        }

        public bool Contains(string key)
        {
            return !string.IsNullOrEmpty(key) && _values.ContainsKey(key);
        }

        public float[][] Get(string key)
        {
            return !string.IsNullOrEmpty(key) && _values.TryGetValue(key, out float[][] value)
                ? value
                : null;
        }

        public void Set(string key, float[][] value)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("MapData key 不能为空。", nameof(key));
            if (value == null) throw new ArgumentNullException(nameof(value));
            _values[key] = value;
        }

        public bool Remove(string key)
        {
            return !string.IsNullOrEmpty(key) && _values.Remove(key);
        }

        public void Clear()
        {
            _values.Clear();
        }
    }
}
