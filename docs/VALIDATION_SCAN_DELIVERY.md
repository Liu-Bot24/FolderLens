# 扫描组件发布与浏览流程验证（2026-09-14）

## 故障与原因

1f3d8f6 发布目录根部含 `FolderLens.Scan.Worker.exe`、deps 和 runtimeconfig，却没有对应 DLL；完整组件在 `FolderLens.Scan.Worker/` 下。SDK 发布流程带入了非程序集引用项目的入口文件，原组件定位顺序优先选择根部残缺入口。

直接启动根部入口得到 `.dll does not exist`；完整子目录组件立即读出 12 个文件。原客户端只等待命名管道连接，没有同时检查进程退出，所以入口已退出仍等待 20 秒。后续重试的组件启动异常又可能被普通目录 I/O 错误处理，界面提示被元数据进度覆盖。

## 修复

- 应用发布列表排除三个后台组件的根部孤立入口和配置；扫描组件统一发布到 `scan-worker/`。
- 组件定位优先完整专用目录，同时检查 EXE、DLL、deps、runtimeconfig；兼容旧版完整目录结构。
- 组件缺失、提前退出或握手失败明确报告组件错误，不伪装成源目录离线。进程连接同时等待退出信号。
- 扫描错误独立保存；刷新空结果和元数据进度不能抹去。打开另一目录失败时，不用旧根目录的结果冒充新目录。
- 发布门禁从单纯主窗口和缩放测试升级为真实扫描、缩略图、预览、目录切换与返回的原生回归。

## 定位与回归证据

- `artifacts/published-browser-red/native-refresh.json`：旧发布版对 12 个自动生成图片的目录超时，60 秒内浏览列表未就绪。
- `artifacts/scan-pipeline-red/native-refresh.json`：定位到根部入口的 workerRead 阶段，连接超时。
- `artifacts/scan-pipeline-green/native-refresh.json`：保持旧残缺入口存在，修复后选中完整子目录；扫描 12 文件、3 目录、0 错误，目录错误显示及恢复通过（整段 2.6 秒）。
- `ScanWorkerDeliveryTests`：残缺入口不遮挡完整组件；缺 DLL 立即失败；组件提前退出立即失败；保持旧完整目录兼容。
- `artifacts/logs/scan-delivery-unit.*`：247 单元测试通过；`artifacts/logs/scan-delivery-app.*`：应用构建通过。

测试不修改源文件。最终发布版的原生验证记录保存在该候选目录 `startup-verification/`；硬件、网络和长时间负载验收仍须分别记录，不能由上述检查推断通过。

## 真实目录中追加发现的位置偏移

Downloads 目录在扫描修复后能产生 4,686 张图片，但原生静止视口检查仍失败。记录显示无缩略图清空、无容器替换、无索引发布异常；第一行从 7.333 DIP 移到 2.333 DIP，滚动偏移变为 5 DIP。

原因是首批结果升级已执行像素位置恢复，后面用于普通替换的 `ScrollIntoView(Leading)` 又执行一次。现在排除已恢复位置的首批升级分支，普通替换仍保留原滚动行为。

`promotion-viewport-red` 用 12 张生成图片稳定复现同样的 5 DIP 位移；`promotion-viewport-green` 在相同检查下前后均为 7.333 DIP，约 1.3 秒完成。原生扫描错误恢复和此位置回归均加入发布脚本，不能只检查主窗口能否加载。
