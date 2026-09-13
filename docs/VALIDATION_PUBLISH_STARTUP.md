# 发布版主窗口启动修复（2026-09-14）

49b0c6d 的发布版在 `MainWindow.InitializeComponent()` 抛出 `XamlParseException`。同一次构建输出含 `App.xbf`、`MainWindow.xbf`、`FolderLens.App.pri`，但 `dotnet publish` 的文件列表没有包含这些应用资源；媒体组件加载和文件清单哈希检查不能发现这种遗漏。

应用项目现在将当前 `TargetDir` 中生成的 XBF 和应用 PRI 加入 `ResolvedFileToPublish`，支持发布脚本的独立 `OutDir`。发布清单检查独立要求三个入口资源，旧清单不能绕过检查。`Publish.ps1` 在发布实际主程序上运行 `Verify-Startup.ps1`，成功后才继续生成候选清单。

验证证据：

- `artifacts/startup-regression-red-startup.stderr.log`：旧发布版实际启动，退出 1，主窗口 XAML 加载失败。
- `artifacts/logs/startup-fix-publish.*`：全新输出目录执行 publish，退出 0，三个应用资源存在。
- `artifacts/startup-regression-green/native-refresh.json`：同一原生测试启动发布版，主窗口、图片显示和缩放控件通过。
- 新版 `Verify-Release.ps1` 对旧候选失败：缺少必需资源 `App.xbf`。
- `Test-ReleaseRules.ps1`：10 个发布文件路径规则通过。

测试使用独立数据目录和生成的图片，没有扫描用户归档。该验证证明本机发布版主窗口可加载，不替代干净机器安装、混合 DPI 或用户大目录性能验收。
