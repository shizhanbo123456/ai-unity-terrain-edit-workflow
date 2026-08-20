using System;
using System.IO;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// MapData 文件存储：把 float[][] 以 CSV 格式读写到「配置文件夹/MapData/{key}.txt」。
    ///
    /// 职责仅限文件 IO（写/读/删/存在性）；序列化格式见 <see cref="CsvArrayCodec"/>。
    /// 纯 C#（仅 System.IO），编辑器侧由 TerrainPaintProjectSO 委托本类完成写盘；
    /// 运行时不依赖本类（运行时走 SO 持有的 TextAsset → CsvArrayCodec.Decode）。
    /// </summary>
    public class MapDataStore
    {
        /// <summary>MapData 目录的绝对路径（末尾不含分隔符）。</summary>
        public string DirectoryPath { get; }

        public MapDataStore(string directoryPath)
        {
            DirectoryPath = directoryPath?.TrimEnd('\\', '/') ?? "";
        }

        /// <summary>key → 文件绝对路径（key 经 <see cref="SanitizeKey"/> 清洗）。</summary>
        public string GetFilePath(string key)
        {
            return Path.Combine(DirectoryPath, SanitizeKey(key) + ".txt");
        }

        public bool Exists(string key)
        {
            if (string.IsNullOrEmpty(DirectoryPath)) return false;
            return File.Exists(GetFilePath(key));
        }

        /// <summary>写入（目录不存在自动创建；覆盖旧文件）。</summary>
        public void Write(string key, float[][] data)
        {
            if (string.IsNullOrEmpty(DirectoryPath))
                throw new InvalidOperationException("[MapDataStore] 目录路径为空，无法写入");
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(GetFilePath(key), CsvArrayCodec.Encode(data, key));
        }

        /// <summary>读取（文件不存在返回 null）。</summary>
        public float[][] Read(string key)
        {
            string path = GetFilePath(key);
            if (!File.Exists(path)) return null;
            return CsvArrayCodec.Decode(File.ReadAllText(path));
        }

        /// <summary>删除（文件不存在静默忽略）。</summary>
        public void Delete(string key)
        {
            string path = GetFilePath(key);
            if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>把 key 清洗为安全文件名（只保留字母/数字/下划线/连字符）。</summary>
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
            string safe = n > 0 ? new string(chars, 0, n) : "unnamed";
            return safe;
        }
    }
}
