# 快捷收藏与后台焦点修复

## 行为

- 缩略图右上角采用系统字体的空心/实心星标。实心表示该文件属于至少一个收藏夹，读取本地持久化归属；图片、视频及其他类型共用卡片行为。
- 本次应用会话第一次点击星标打开普通收藏对话框。取消或提交失败不记住目标；成功后，快捷点击直接加入上次成功选择的收藏夹，重复添加幂等。
- 右键和顶层菜单每次打开对话框。普通菜单成功添加也更新下一次快捷操作的目标；移除不覆盖该目标。上次目标已删除时重新选择。支持已有的多收藏夹选择，状态仅在当前应用会话保留。
- 星标按钮不修改列表选择；删除一个归属后，仍有其他归属时保持实心。收藏状态随可见行重新读取，异步旧查询不能覆盖收藏变更后的状态。

## 焦点诊断与修复

旧代码在窗口构造时执行 `Maximize()`，在等待数据目录/单实例 IPC 后无条件 `Activate()`，恢复浏览位置时无条件 `UIElement.Focus()`。这些调用存在激活窗口和改变输入焦点的路径。后台工作进程已设置 `CreateNoWindow=true`，本次没有把工作进程启动当作已证实的原因。

启动前记录前台窗口；启动完成时若前台已经变化则使用 `AppWindow.Show(false)`。最大化延迟到用户激活本窗口后执行。单实例转发保留最初的前台标识，容量窗口异步打开/返回主窗口同样核对前台；应用后台恢复与异步快捷键处理在设置控件焦点前核对窗口所有权。

参考微软的 [XAML 焦点设计说明](https://github.com/microsoft/microsoft-ui-xaml/blob/main/docs/design-notes/focus.md) 和 [RequestedStartupState 定义](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.overlappedpresenter.requestedstartupstate)。后者是只读的启动状态查询，不能作为可设置的最大化属性。

验证模式另外禁止调用激活和控件 Focus；因此选择颜色回归检查的是后台原生控件状态与渲染，不代表实际跨应用输入焦点切换已经验收。没有为复现故意打断用户输入，也没有操作豆包语音输入法；只能确认并修正上述机制风险，不能断言它解释了用户每一次中断。

## 本地证据（2026-09-14）

- Release 构建：`artifacts/resume-baseline/quick-focus-final-build.log`，0 错误。7 个 NU1900 表示漏洞源无法访问，不能算漏洞检查通过。
- 全量单测：`artifacts/tests/quick-focus/quick-focus.trx`，283 PASS、0 FAIL、0 SKIP。新增归属持久化/未收藏负例/多归属移除，以及启动前台不变/已切换/未知/目标已在前台和 IPC 兼容性检查。
- 原生回归：`artifacts/quick-focus-final-regression/summary.json`，33/33 PASS，包含之前目录、筛选、预览、选择与收藏路径。
- 快捷收藏原生测试调用真实按钮 AutomationPeer，使用实际数据事务和对话框提交函数；首次取消、新建、后续复用、普通菜单更换目标、删除目标、重叠归属、选中项保持以及右上角几何位置均通过。
- 深色补验：`artifacts/quick-focus-dark-quick-collections`、`artifacts/quick-focus-dark-selection-appearance`。浅色和深色星标截图由真实卡片 RenderTargetBitmap 生成。
- 所有原生验证使用隔离生成数据、屏幕外窗口；对话框提交通过测试呈现接口驱动。真实前台弹窗交互、豆包语音会话均为 NOT_RUN。原完整性能/硬件/干净机验收和整体发布结论不因此变成 GO。
