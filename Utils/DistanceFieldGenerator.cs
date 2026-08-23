using System;
using System.Collections.Generic;

namespace AiTerrainWorkflow
{
    /// <summary>使用四邻域广度优先遍历生成整数曼哈顿距离场。</summary>
    public static class DistanceFieldGenerator
    {
        private struct Point
        {
            public int x;
            public int y;

            public Point(int x, int y)
            {
                this.x = x;
                this.y = y;
            }
        }

        /// <summary>
        /// 将 false 位置作为距离 0 的起点，逐层遍历所有 true 位置并返回距离。
        /// 开启 boundaryAsZeroDistance 时，相当于数组边界外存在距离 0 的区域，
        /// 因而最外圈 true 位置的距离为 1；最外圈 false 位置仍为 0。
        /// 若关闭边界源且输入中没有 false，所有 true 位置返回 int.MaxValue，表示不可达。
        /// </summary>
        public static int[][] Generate(bool[][] mask, bool boundaryAsZeroDistance = false)
        {
            Validate(mask, out int width, out int height);
            var result = new int[height][];
            var queue = new Queue<Point>(Math.Max(4, width * height));

            for (int y = 0; y < height; y++)
            {
                result[y] = new int[width];
                for (int x = 0; x < width; x++)
                {
                    if (mask[y][x])
                    {
                        result[y][x] = int.MaxValue;
                    }
                    else
                    {
                        result[y][x] = 0;
                        queue.Enqueue(new Point(x, y));
                    }
                }
            }

            if (boundaryAsZeroDistance)
                EnqueueBoundary(mask, result, width, height, queue);

            int[] dx = { -1, 1, 0, 0 };
            int[] dy = { 0, 0, -1, 1 };
            while (queue.Count > 0)
            {
                Point current = queue.Dequeue();
                int nextDistance = result[current.y][current.x] + 1;
                for (int i = 0; i < 4; i++)
                {
                    int nx = current.x + dx[i];
                    int ny = current.y + dy[i];
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                    if (!mask[ny][nx] || result[ny][nx] != int.MaxValue) continue;
                    result[ny][nx] = nextDistance;
                    queue.Enqueue(new Point(nx, ny));
                }
            }

            return result;
        }

        private static void EnqueueBoundary(bool[][] mask, int[][] result, int width, int height,
            Queue<Point> queue)
        {
            for (int x = 0; x < width; x++)
            {
                EnqueueBoundaryPoint(x, 0, mask, result, queue);
                if (height > 1) EnqueueBoundaryPoint(x, height - 1, mask, result, queue);
            }
            for (int y = 1; y < height - 1; y++)
            {
                EnqueueBoundaryPoint(0, y, mask, result, queue);
                if (width > 1) EnqueueBoundaryPoint(width - 1, y, mask, result, queue);
            }
        }

        private static void EnqueueBoundaryPoint(int x, int y, bool[][] mask, int[][] result,
            Queue<Point> queue)
        {
            if (!mask[y][x] || result[y][x] != int.MaxValue) return;
            result[y][x] = 1;
            queue.Enqueue(new Point(x, y));
        }

        private static void Validate(bool[][] mask, out int width, out int height)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            height = mask.Length;
            if (height == 0) throw new ArgumentException("距离场输入不能为空。", nameof(mask));
            if (mask[0] == null || mask[0].Length == 0)
                throw new ArgumentException("距离场输入行不能为空。", nameof(mask));

            width = mask[0].Length;
            for (int y = 0; y < height; y++)
            {
                if (mask[y] == null || mask[y].Length != width)
                    throw new ArgumentException("距离场输入必须是非空矩形数组。", nameof(mask));
            }
        }
    }
}
