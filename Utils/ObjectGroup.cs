using System.Collections.Generic;
using UnityEngine;

namespace AiTerrainWorkflow
{
    /// <summary>
    /// 对象组（ObjectGroup）：一组 GameObject 的命名集合。
    ///
    /// 作为 ScriptableObject 资产保存在 Assets 下（右键 Create → AiTerrainWorkflow → ObjectGroup），
    /// 用于地形编辑工作流中把若干场景物体（如一组树木/岩石预制体实例）归组管理，
    /// 供后续工具批量操作或记录。
    /// </summary>
    [CreateAssetMenu(fileName = "ObjectGroup", menuName = "AiTerrainWorkflow/ObjectGroup")]
    public class ObjectGroup : ScriptableObject
    {
        /// <summary>组名：用于标识该对象组（如 "Forest Trees"、"Rocks"）。</summary>
        [Tooltip("组名：用于标识该对象组")]
        public string groupName;

        /// <summary>组内 GameObject 列表：该组包含的场景物体引用。</summary>
        [Tooltip("组内 GameObject 列表")]
        public List<GameObject> gameObjects = new List<GameObject>();
    }
}
