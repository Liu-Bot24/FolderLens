# 11｜环境、构建、打包与用户交付

## 1. 开发环境不等于运行环境

开发需要Windows x64、经过验证的.NET 10 SDK、WinUI/XAML构建工具、Windows SDK以及RawBridge所需MSVC/CMake。先通过dotnet --info、vswhere、MSBuild、CMake和实际小项目编译确认缺什么，再安装；不要重复卸载重建已有环境。具体Visual Studio组件ID从当期官方安装器和模板核查，不在脚本里编造未验证的workload名称。

终端入口兼容Windows自带PowerShell 5.1，避免强迫用户先装PowerShell 7。路径始终引用/参数数组传入，不能拼接执行字符串。安装系统级开发工具需要实际权限；不能修改执行策略全局值、关闭Defender/SmartScreen、启用测试签名或强迫开发者模式来掩盖部署缺陷。普通最终用户无需SDK、VS、Python、Node、Docker、CUDA Toolkit。

## 2. 源码可重复构建

必须提供global.json、Directory.Build.props、Directory.Packages.props、packages.lock.json、native/dependency-lock.json、Git可追溯构建标识。NuGet恢复采用locked mode；首次锁定需记录来源。native锁包含下载URL、版本/commit、SHA256、架构、构建开关、许可证文件、对应源码来源。

RawBridge优先维护小型CMake工程，使用稳定C ABI；导出函数和调用约定有测试。禁止依赖开发机环境PATH碰巧找到某个DLL。FFmpeg/FFprobe必须固定同来源版本并记录-buildconf输出；WebView2固定版和WinAppSDK原生文件逐一入发布清单。

MSBuild初始方向：WindowsPackageType=None，WindowsAppSDKSelfContained=true，SelfContained=true，RuntimeIdentifier=win-x64；应用项目配置，库项目不滥设部署属性。WinAppSDK自包含与.NET自包含是两个独立要求，普通dotnet publish --self-contained不能被当成自动覆盖一切。[S03]

一期不启用PublishTrimmed、NativeAOT或单文件发布，除非以后独立验证。采用目录式自包含，native runtime与DLL保持实际要求的相对布局。CRT依赖按实际原生组件官方部署方式处理，验证无需全局VC安装；不得从其他机器随机拷贝系统DLL。

## 3. 必须实现的脚本契约

以下是Codex需要创建并测试的交付脚本，不是本规划包已经提供的软件。每个脚本支持-Help，遇错返回非零exit code，保留原始日志；不得catch后打印成功。外部工具退出码逐项检查。

| 脚本 | 参数/行为 |
|---|---|
| scripts/Check-Environment.ps1 | 只读检查；输出artifacts/environment.json/md；不自动安装 |
| scripts/Bootstrap.ps1 | -NonInteractive；仅安装已批准缺项、核哈希、可重入；需权限时失败并说明 |
| scripts/Build.ps1 | -Configuration Debug或Release；完整managed+native构建 |
| scripts/Test.ps1 | -Suite Unit/Integration/Media/UI/All；输出逐套日志与总退出码 |
| scripts/Benchmark.ps1 | -Scenario All或单场景 -FixtureRoot路径；输出原始CSV和摘要JSON |
| scripts/Publish.ps1 | -Configuration Release -Runtime win-x64；独立清洁输出并检查native/runtime |
| scripts/Package.ps1 | 同构建产物生成portable ZIP与per-user setup EXE；不在打包时悄悄联网更新依赖 |
| scripts/Verify-Release.ps1 | -ArtifactRoot路径；哈希、必需文件、能力报告一致性；不能取代人工实机检查 |
| scripts/Run-Local.ps1 | 只启动已构建应用，找不到产物明确失败 |

阶段完成后，用户在源码根目录的一条构建入口应为：

```powershell
powershell -NoProfile -File .\scripts\Build.ps1 -Configuration Release
```

发布应另有build-release.cmd顺序执行环境检查→构建→自动测试→发布→打包→清单校验，错误即停。交互UI、真实硬件和清洁机未运行部分保留NOT_RUN，不让脚本仅因存在exe就输出GO。

## 4. 便携包

FolderLens-<version>-win-x64-portable.zip包含app/native/runtime/第三方通知和使用说明。带portable.json后使用本地可写data目录；介质不可写或是SMB时提示改用本机目录并征得选择，不悄悄把SQLite放网络上。可执行文件和用户数据目录分离，包内不带开发者私有索引、缓存、Token、PDB绝对源码路径泄漏或测试素材。

最终便携包必须能在干净Windows离线运行。WebView2优先用存在的Evergreen；缺少时用包内Fixed Version作为browserExecutableFolder，不把固定版误当系统安装程序。[S20] 固定版的安全更新随应用发版处理，生成版本报告。完整离线包可能较大，不能为缩包去掉Markdown的必需运行时而仍称完整。

## 5. 安装包

采用Inno Setup制作每用户EXE，默认%LOCALAPPDATA%\Programs\FolderLens，PrivilegesRequired=lowest，固定AppId。官方许可证允许包括商业应用在内的使用，但必须保留相应声明；构建使用的实际版本和条款仍需记录，商业采购请求与许可证文件分开核查。[S24] 不自动付费、不把软件许可证选择替用户决定。

不强改文件关联、不加入开机启动、不要求管理员、不捆绑PotPlayer。开始菜单快捷方式默认开，桌面图标可选。安装至版本目录后再切换启动入口，升级失败保留旧版；用户数据不随程序升级覆盖。

卸载默认保留用户设置与索引，提供明确的“同时删除应用数据”选项；删除只限应用自有真实目录，检查重解析点。不能触碰最近访问的素材根。更新数据库先备份、事务迁移，失败保留旧文件；较旧程序遇到较新schema明确提示，不自动破坏性重建用户预设。

首次不假定拥有代码签名证书。无签名时报告unsigned，不能宣称已签名/不会触发SmartScreen。签名凭据需要用户授权，不能生成测试证书并偷偷装进信任根。

## 6. 必须实际测试

清洁Windows无SDK、无全局WebView2（测试专用环境）、无额外图像编解码商店扩展，网络关闭；安装和便携两条路径。用户目录中文/空格；普通权限；重复升级；取消安装；卸载保留/删除应用数据；大图/ARW/HEIC/MD/音频实际打开；默认播放器缺失时清晰提示。

至少一台参考高配机和一台内存较低/共享显存环境测资源保护；后者不要求达到高配绝对延迟，但不能崩溃或无界吃内存。不能把开发机启动一次当清洁机验证。

## 7. 最终交付清单

完整源码及精确commit；依赖锁及native源码/构建开关；portable ZIP；setup EXE；SHA256SUMS；用户手册和快捷键；格式能力报告；需求/测试结果；原始TRX/日志/性能CSV；构建环境报告；已知限制；THIRD-PARTY-NOTICES和SBOM；最终GO/NO-GO报告。

源码压缩包不能被当作便携程序，ZIP中有exe也不等于已经运行验证。无法在当前执行环境做Windows工作时，交付剩余可执行工程和明确缺口，不伪造产物/日志/安装成功。规划包本身不包含这些应用产物。
