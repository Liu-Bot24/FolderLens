# 默认导航、容量状态、选中色与点击复制

2026-09-14，基线 06b3635。本批不改变原文件内容，也不声明整体一期验收完成。

## 本批修改

- 默认目录导航在索引初始化之前建立，避免索引打开或迁移期间左侧空白；收藏内容随后异步加载。`startup-navigation-red` 在数据库前缺少入口而失败，`startup-navigation-green-navigation` 同一断言通过。
- 目录树保留 WinUI TreeView 的展开、虚拟化、键盘与选择行为，用节点模板添加系统 Fluent 字体图标，压缩行距、对齐名称，去掉文字内的星号和文件夹表情。关闭目录树拖动与重排，避免暗示可移动源文件。参考 [TreeView 指南](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/tree-view) 与 [Segoe Fluent Icons](https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font)。
- 详情列表补充浅色/深色蓝色选中背景，保留原生悬停与高对比度资源。验证失焦、选中悬停与普通选中状态，并以非选中行为负例；不是只检查缩略图。真实红证据 `selection-capacity-red-selection-appearance`。
- 容量看板显式显示扫描取消或失败，不把未完成统计中的零值断言为目录为空，也不计算不完整占比。整目录范围每 5 秒检查本地索引版本，数据变化至少间隔 15 秒重新汇总；扫描状态变化可立即触发下一次轮询更新。后台计算保留旧结果，不重新扫描原文件。固定筛选快照范围不自动变动。没有逐目录完整性证据时采用保守标识，不虚构“已确认空目录”。
- 预览文件名和路径使用可键盘访问的按钮；点击分别复制文件名、完整源路径，并显示短暂反馈。复制源不依赖被省略或相对路径文本。

## 验证

Release 构建 0 错误，7 个 NU1900 为漏洞信息源不可达。全量单测 276 PASS / 0 FAIL / 0 SKIP：`artifacts/tests/browser-feedback/browser-feedback.trx`。容量单测覆盖取消改变实时版本而不改变固定结果。

`artifacts/browser-feedback-final-regression/summary.json` 的 32 项原生回归全部通过。另跑最终默认导航、深色详情选中、容量看板和复制内容场景，证据为 `artifacts/browser-feedback-{navigation-final,selection-dark-final,capacity-final,copy-final}`。目录图标、行距与蓝色选中截图已人工检查。

容量红证据 `selection-capacity-red-capacity-ui` 未能明确展示取消状态；修复后实际看板检查通过自动更新后的取消文本及未完成零值。深色初次失败是测试用亮度门限漏掉深蓝，修正亮度条件，保留蓝色相对红色差异和非选中负例，最终通过。

所有本轮原生运行均为隔离生成数据及离屏窗口。复制测试执行真实按钮处理函数但截取输出文本，明确记录 `systemClipboardWrite=NOT_RUN`，避免干扰用户剪贴板；前台右键菜单交互仍为 NOT_RUN。这些边界不被计为真实前台操作完成。

## 实际归档诊断边界

用户截图所指扫描只读核对为 09:56:07 开始、10:00:37 取消，160 个目录未完成。两个零值目录均为 Cancelled / entry_count=0；看板停留在 09:58:42 的旧统计。取消前还发生过 CatalogWalLimit 查询失败，现有记录不能证明其导致取消。没有重扫整个归档、修改用户索引或自行恢复扫描。

扫描取消触发者、完整大归档扫描结果、此前 Draw 长尾/Present/长稳/硬件/干净机器仍需分别跟进，不能因本批通过而关闭这些事项。
