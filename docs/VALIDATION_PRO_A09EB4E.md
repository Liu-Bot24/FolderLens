# a09eb4e 审计问题修复验证

日期：2026-09-22。基线：`a09eb4e5172fdac6eca2e7c9f6c2611540772da3`。

## 修复及反例

| 问题 | 根因与修复 | 验证结果 |
| --- | --- | --- |
| 不完整扫描签名误报文件变化 | 无 ID 枚举与完整句柄观测直接比较；改为有父目录与版本约束的按需绑定 | 原反例失败；修复后完整签名可用于真实缩略图和预览解码。文件/父目录替换、根代次变化及取消拒绝旧结果；已接受的身份不自动替换 |
| 文本索引复用旧内容 | 索引键没有包含完整源签名 | 同大小、时间和条目版本、不同旧式文件身份的键不再相同；正常相同观测仍复用 |
| 拖入、粘贴准备影响新视图 | 延迟数据提供方和默认操作探测完成后缺少原视图约束 | 切目录、筛选及选择均在数据提供方交付前结束旧等待，不清除新选择、不启动文件操作。未变化视图可进入提交阶段 |
| Markdown 关闭失败后无法恢复 | 失败的退出观察被永久保留，同时失去可重试的进程观察资源 | 首次故障仍报告失败；保留原进程句柄，下一次重试证明退出后才复用环境。独立 WebView 实例继续可用 |
| 媒体子进程初始化异常后残留 | 启动进程后的 Job 创建/加入未受清理范围保护 | 两个真实子进程故障注入反例原先残留，修复后均退出；原始错误保留 |

收尾自查另发现粘贴准备与扫描取消耦合：停止扫描后正常粘贴被取消。已先复现，再改为独立准备生命周期，保留目录、视图、选择和筛选约束。扫描停止前后均可接受正常传入操作；已提交的 Shell 操作仍保留固定目的地和部分完成语义。

## 已执行

- Release 构建成功，0 错误。3 条 NU1900 警告来自包漏洞数据源不可达；本轮不能据此声明依赖漏洞检查通过。
- 最终全量单元测试：612 通过，0 失败，0 跳过。
- 真实 WinUI 离屏：不完整签名补全后的图片解码、延迟传入操作、Shell 操作后的导航刷新、预览取消、待定类别筛选及 Markdown 生命周期恢复均通过。
- 实际 SMB 图库：39 张图片，5 个滚动位置，所需缩略图均可用，未出现缩略图错误或结果发布错误。
- Markdown 退出故障恢复验证覆盖初始化尚未完成时关闭，以及独立浏览器环境不受影响。

本地证据保存在忽略目录 `artifacts/a09-fixes-20260922/`：`initial-red.log`、`binding-red-corrected.log`、`native-incoming-stop-red/native-refresh.json`、`full-tests-verified.log`、`final-build2.log`、`final-incoming-transfer-navigation/native-refresh.json`、`final-shell-refresh-navigation/native-refresh.json`、`final-preview-state-cancellation/native-refresh.json`、`final-category-pending/native-refresh.json`、`native-read-binding-final/native-refresh.json`、`native-markdown-runtime-lifetime/native-refresh.json`、`native-u-gallery/native-refresh.json`。证据含本机路径，不随仓库发布。

## 未验证与保留边界

- 云占位符批准、预期属性变化及身份保护已测试；真实云提供程序水合为 **NOT_RUN**。
- 本轮传入操作原生验证停在 Shell 提交边界，未实际执行前台复制/移动；完整单元回归覆盖自有夹具的 Shell 文件操作。
- 前台 Explorer 打开并选中目标、多屏/DPI、完整网络故障矩阵未执行。进入原生 Shell 的不可取消调用仍是既有边界，本轮未声称消除该限制。
- 本轮不操作系统全屏、不占用鼠标键盘；离屏验证不能替代全部物理交互验收。
- 历史 A054a 长稳、发布与环境验收状态不因本轮通过而改变。用户已跳过的干净 Windows/低内存环境保持 **SKIP**；本轮不是一期完成或发布 GO 的结论。
