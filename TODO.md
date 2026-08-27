# TODO

## 区域后处理操作

复杂区域完全依靠手工绘制成本较高。后续应为区域对象增加非破坏性 Modifier，并保证编辑器窗口与命令行调用共用同一套处理逻辑。

- [ ] **Expand / Dilate**：向外扩张区域。
- [ ] **Shrink / Erode**：向内收缩区域。
- [ ] **Smooth**：平滑区域边界的锯齿。
- [ ] **Remove Small Islands**：删除面积小于阈值的孤立区域。
- [ ] **Fill Holes**：填充区域内部的孔洞。
- [ ] **Simplify**：在保持整体轮廓的前提下简化多边形。
- [ ] **Noise Distortion**：使用可配置、可复现的噪声自然化边界。
- [ ] **Distance Band**：从区域边界向内或向外生成指定宽度的带状区域。
- [ ] **Keep Largest Component**：只保留面积最大的连通区域。

### 实现约束

- Modifier 应保存为区域对象的可序列化配置，不直接破坏原始形状。
- 多个 Modifier 按列表顺序执行，并允许启用、禁用和调整顺序。
- 编辑器预览、最终烘焙与 Bridge/manifest 命令必须得到一致结果。
- 涉及随机性的操作必须提供 seed，确保命令行重复执行结果一致。
- 最终 `layerMap` 仍保持离散 Layer ID；边缘覆盖率或距离数据应使用独立 MapData 表达。
