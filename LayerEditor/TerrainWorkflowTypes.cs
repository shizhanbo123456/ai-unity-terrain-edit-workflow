using System;

namespace AiTerrainWorkflow.LayerEditor
{
    /// <summary>从高度编辑开始，可依次应用到 Terrain 的工作流阶段。</summary>
    public enum TerrainWorkflowStage
    {
        HeightEdit = 0,
        TextureEdit = 1,
        ScatterEdit = 2,
        PropEdit = 3,
        FixedPointEdit = 4,
    }

    /// <summary>工作流中最多 16 个语义层的位掩码。</summary>
    [Flags]
    public enum TerrainWorkflowLayerMask : ushort
    {
        None = 0,
        Layer0 = 1 << 0,
        Layer1 = 1 << 1,
        Layer2 = 1 << 2,
        Layer3 = 1 << 3,
        Layer4 = 1 << 4,
        Layer5 = 1 << 5,
        Layer6 = 1 << 6,
        Layer7 = 1 << 7,
        Layer8 = 1 << 8,
        Layer9 = 1 << 9,
        Layer10 = 1 << 10,
        Layer11 = 1 << 11,
        Layer12 = 1 << 12,
        Layer13 = 1 << 13,
        Layer14 = 1 << 14,
        Layer15 = 1 << 15,
        All = ushort.MaxValue,
    }
}
