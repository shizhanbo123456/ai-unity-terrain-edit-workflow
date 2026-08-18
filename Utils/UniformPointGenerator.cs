#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// 均匀分布随机点生成器（网格抖动 Jittered Grid，属分层采样）。
    ///
    /// 在矩形区域 [min, max] 内生成 count 个均匀分布的随机点（Vector2，浮点坐标）：
    ///   1) 区域按 count 自适应切分成 cols×rows 网格（列数随区域宽高比调整，避免网格形变）；
    ///   2) 每格恰好放一个点，点在该格内均匀随机偏移 → 全局均匀、不聚簇；
    ///   3) 使用 System.Random(seed)（确定性伪随机）→ 相同 seed 输出完全一致（可复现）；
    ///   4) 区域为连续浮点空间，无离散容量限制；仅当区域退化（宽或高为 0）
    ///      时点收敛到边/线上（仍均匀），宽高为负时抛错。
    ///
    /// 纯静态工具类，不注册为桥接命令，供其他命令/代码调用
    /// （如地形种树/撒点前生成均匀点位，坐标可再自行归一化或取整）。
    /// </summary>
    public static class UniformPointGenerator
    {
        /// <summary>缺省随机种子：seed 参数省略时使用此固定值（保证默认行为也可复现）。</summary>
        public const int DefaultSeed = 20260818;

        /// <summary>
        /// 在矩形区域 [min, max] 内生成 count 个均匀分布的随机点。
        /// </summary>
        /// <param name="count">点数，必须 &gt;= 0（0 返回空列表）。</param>
        /// <param name="min">区域左下角。</param>
        /// <param name="max">区域右上角。</param>
        /// <param name="seed">随机种子；省略用 <see cref="DefaultSeed"/>（固定值，可复现）。</param>
        /// <returns>count 个点的列表（网格抖动模式，无重复）。</returns>
        /// <exception cref="ArgumentException">区域无效（max 任一轴 &lt; min）。</exception>
        public static List<Vector2> Generate(int count, Vector2 min, Vector2 max, int seed = DefaultSeed)
        {
            var result = new List<Vector2>(Math.Max(0, count));
            if (count <= 0)
            {
                return result;
            }

            float w = max.x - min.x;
            float h = max.y - min.y;
            if (w < 0f || h < 0f)
            {
                throw new ArgumentException($"区域无效: max={max} 必须 >= min={min}");
            }

            var rng = new System.Random(seed);

            // 网格抖动：列数随区域宽高比自适应，保证网格形状贴近区域、分布均匀
            // 退化区域：w==0 或 h==0 时退化为线/点分布（仍均匀，避免除零）
            int cols;
            if (w > 0f && h > 0f)
                cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)count * w / h)));
            else if (w > 0f) // h==0：退化为横线，count 个点沿 x 一维均匀排布
                cols = count;
            else             // w==0：退化为竖线（rows=count）或单点
                cols = 1;
            if (cols < 1) cols = 1;
            int rows = Math.Max(1, (int)Math.Ceiling((double)count / cols));
            if (rows < 1) rows = 1;
            double cellW = (double)w / cols;
            double cellH = (double)h / rows;

            int placed = 0;
            for (int r = 0; r < rows && placed < count; r++)
            {
                for (int c = 0; c < cols && placed < count; c++)
                {
                    // 格内均匀随机偏移（double 精度，避免整除误差）
                    double px = min.x + c * cellW + rng.NextDouble() * cellW;
                    double py = min.y + r * cellH + rng.NextDouble() * cellH;
                    result.Add(new Vector2((float)px, (float)py));
                    placed++;
                }
            }
            return result;
        }
    }
}
#endif // UNITY_EDITOR
