using UnityEngine;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>
    /// 构建端组件：接收主配置 SO，将一个真实 Terrain 构建为工作流编辑器中的样子
    /// （高度 / 纹理 / 植被 / 树 + 实例化摆件，详见 README「阶段 7 · TerrainBuilder 构建」）。
    ///
    /// 构建时机由实际项目按需决定，本组件只暴露唯一的 Build 入口：
    ///   - Editor 模式：可预先烘焙（离线生成 TerrainData 资产）；
    ///   - 游戏运行时：亦可按需现场构建。
    /// 组件本身不内置「双模式」，调用方自行决定何时调用 Build()。
    /// </summary>
    public class TerrainBuilder : MonoBehaviour
    {
        /// <summary>
        /// 将 <paramref name="terrain"/> 构建为 <paramref name="projectConfig"/> 所描述的样子。
        /// </summary>
        /// <param name="projectConfig">主配置 SO（素材池 / 规则 / 邻接组 / MapData 接口）。</param>
        /// <param name="terrain">目标 Terrain 组件（通常为挂载本组件的 Terrain GameObject 上的 Terrain）。</param>
        public void Build(TerrainPaintProjectSO projectConfig, Terrain terrain)
        {
            // 当前仅保留签名；构建逻辑（PrepareTerrain / ApplyHeight / ApplyAlphamap / ApplyDetail / ApplyTrees / PlaceProps / PostProcess）后续按需实现。
        }
    }
}
