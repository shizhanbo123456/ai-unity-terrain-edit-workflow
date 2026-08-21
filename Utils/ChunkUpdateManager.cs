using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow
{
    /// <summary>
    /// 区块更新管理器（普通类，非 MonoBehaviour，运行时可用）。
    /// 维护"当前已激活区块集合"，每次 <see cref="MoveTo"/> 根据观察点与区块中心的距离，
    /// 增 / 减激活区块（新进入范围的加入 activeChunks，离开范围的加入 inactiveChunks）。
    ///
    /// 坐标约定（地形俯视平面）：
    ///   区块以 Vector2Int(xIndex, zIndex) 索引，其中 **y 分量即地形平面的 z 方向**；
    ///   例如 (0,0) 覆盖范围 x∈[0, chunkSize.x)、z∈[0, chunkSize.y)；
    ///   区块中心 = ((index.x+0.5)*chunkSize.x, (index.y+0.5)*chunkSize.y)（y 即 z）。
    ///   观察点 pos 为 Vector2(x, z)。
    /// </summary>
    public class ChunkUpdateManager
    {
        private readonly Vector2 _chunkSize;
        private readonly float _distance;
        private readonly HashSet<Vector2Int> _active = new HashSet<Vector2Int>();

        /// <summary>
        /// 构造区块更新管理器。
        /// </summary>
        /// <param name="chunkSize">区块尺寸（x / y=z 方向）；分量必须 &gt; 0（内部会兜底为极小正数）。</param>
        /// <param name="distance">激活半径：区块中心到观察点的距离 ≤ 该值则激活；&lt; 0 按 0 处理。</param>
        public ChunkUpdateManager(Vector2 chunkSize, float distance)
        {
            _chunkSize = new Vector2(Mathf.Max(0.0001f, chunkSize.x), Mathf.Max(0.0001f, chunkSize.y));
            _distance = Mathf.Max(0f, distance);
        }

        /// <summary>当前激活的区块索引集合（只读视图；修改请走 MoveTo）。</summary>
        public IReadOnlyCollection<Vector2Int> ActiveChunks => _active;

        /// <summary>
        /// 将观察点移动到 pos（Vector2(x, z)）。
        /// 对比移动前后的激活集合：
        ///   - 移动后进入激活半径的区块 → 写入 <paramref name="activeChunks"/>；
        ///   - 移动后超出激活半径的区块 → 写入 <paramref name="inactiveChunks"/>。
        /// 完成后内部记录的激活集合更新为最新状态。初始集合为空，**第一次 MoveTo 会一次性激活范围内全部区块**。
        /// </summary>
        public void MoveTo(Vector2 pos, out List<Vector2Int> activeChunks, out List<Vector2Int> inactiveChunks)
        {
            activeChunks = new List<Vector2Int>();
            inactiveChunks = new List<Vector2Int>();

            // 候选范围：以 pos 为中心、半径 distance 的矩形覆盖到的区块（±1 保险后按中心距精确过滤）
            int xMin = Mathf.FloorToInt((pos.x - _distance) / _chunkSize.x) - 1;
            int xMax = Mathf.FloorToInt((pos.x + _distance) / _chunkSize.x) + 1;
            int zMin = Mathf.FloorToInt((pos.y - _distance) / _chunkSize.y) - 1;
            int zMax = Mathf.FloorToInt((pos.y + _distance) / _chunkSize.y) + 1;

            float distSqr = _distance * _distance;
            var next = new HashSet<Vector2Int>();
            for (int i = xMin; i <= xMax; i++)
            {
                for (int j = zMin; j <= zMax; j++)
                {
                    var idx = new Vector2Int(i, j);
                    if (ChunkCenterDistanceSqr(idx, pos) <= distSqr)
                        next.Add(idx);
                }
            }

            // 新进入范围的区块
            foreach (var idx in next)
                if (!_active.Contains(idx))
                    activeChunks.Add(idx);

            // 离开范围的区块
            foreach (var idx in _active)
                if (!next.Contains(idx))
                    inactiveChunks.Add(idx);

            _active.Clear();
            _active.UnionWith(next);
        }

        /// <summary>
        /// 传入区块 index，传出其世界范围（y 方向即地形 z 方向）。
        /// </summary>
        /// <param name="index">区块索引（x / z 方向）。</param>
        /// <param name="xMin">区块 x 方向下界。</param>
        /// <param name="yMin">区块 z 方向下界（命名为 y 以与 Vector2Int 的 y 分量一致）。</param>
        /// <param name="xMax">区块 x 方向上界（不含）。</param>
        /// <param name="yMax">区块 z 方向上界（不含）。</param>
        public void GetChunkBounds(Vector2Int index, out float xMin, out float yMin, out float xMax, out float yMax)
        {
            xMin = index.x * _chunkSize.x;
            xMax = (index.x + 1) * _chunkSize.x;
            yMin = index.y * _chunkSize.y;
            yMax = (index.y + 1) * _chunkSize.y;
        }

        /// <summary>区块中心（y=z）到观察点的平方距离。</summary>
        private float ChunkCenterDistanceSqr(Vector2Int idx, Vector2 pos)
        {
            float cx = (idx.x + 0.5f) * _chunkSize.x;
            float cz = (idx.y + 0.5f) * _chunkSize.y;
            float dx = cx - pos.x;
            float dz = cz - pos.y;
            return dx * dx + dz * dz;
        }
    }
}
