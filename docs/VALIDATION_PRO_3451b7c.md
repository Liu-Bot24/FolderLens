# Pro 3451b7c 审计修复验证

审计来源：<https://chatgpt.com/c/6aaa5353-9654-83ea-9eb2-e6ca740694f7>。审核最初读取 `482bb64`，随后检查至 `3451b7c` 的补测差异。六项发现及内部复查边界已实现修复；本文件仅记录已取得的证据，不宣告一期 GO。

| 发现 | 修复与已验证范围 |
|---|---|
| FL-01 收藏首屏等待全量验证 | 首条先发布，后续有界批次；父目录批首／批尾身份一致才应用；单次 5 秒、整体 2 分钟预算；测试覆盖第 2 条阻塞、取消、超时保留及目录替换后原文件移回 |
| FL-02 前台媒体缺版本检查 | 复用带签名的读取并传前台优先级，位图发布前再次检查；实际 FFmpeg 用例覆盖同路径同大小同 mtime 替换、无封面音频与取消 |
| FL-03 Markdown 失败不重试 | 最多三次退避；实际 WebView 覆盖 16 请求饱和、15 秒超时、永久权限拒绝、重试耗尽；补充在途请求与退避期间导航取消 |
| FL-04 AVIF 误识别 | 有界 `ftyp` 品牌表解析；实际 Worker 红例 `mif1` 被标成 `heic`，修复后主／兼容 AVIF 均通过；夹具为受跟踪的 `health/sample.avif` |
| FL-05 小范围复制无关目录 | 3 万个无关目录下仅复制目标子树及祖先 4 条；根范围保留全部、分组、取消和历史改名隔离通过 |
| FL-06 ZIP 集合和清单校验不完整 | 实际 PowerShell 红例确认重复文件代替缺失、重复清单及错误清单会被接受；修复后 8 个正反例通过 |

第一版修复的完整测试为 **552 PASS，0 FAIL，0 SKIP**，证据 `artifacts/tests/phase1-pro-fixes/phase1-pro-fixes.trx`；原生主回归 **42 PASS**，证据 `artifacts/phase1-pro-fixes-regression/summary.json`。内部复查后追加的边界变更另行复跑，以上结果不冒充后续版本的完整验证。

复查后的最终整批结果为 **553 PASS，0 FAIL，0 SKIP**（`artifacts/tests/phase1-pro-reviewed2/phase1-pro-reviewed2.trx`），原生主回归 **42/42 PASS**、补充回归 **43/43 PASS**（`artifacts/phase1-pro-reviewed-regression/summary.json`、`phase1-pro-reviewed-extra/summary.json`）。两批原生 App DLL SHA-256 相同：`DF55D7F209306F808070C4F2D8CAE7F18B9E3E25B96ED60FD4683F4C82F08938`。构建日志 `phase1-review-app-build.log` 为 0 错误。

首次最终全量有 17 个收藏相关失败，证据 `phase1-pro-reviewed-tests.log`；原因是测试输出未部署真实扫描 Worker，而取消不了的进程内回退已被移除。测试工程现构建并部署扫描组件，再运行取得上述 553 PASS。新增项目引用的 locked-mode 还原通过（`phase1-test-worker-locked-restore.log`）；该还原仅核对依赖锁并复用本地包，不作为漏洞审计结果。

关键证据：`phase1-zip-red/results.json`、`phase1-zip-green/results.json`、`phase1-avif-worker-red.log`、`phase1-avif-worker-green.log`、`phase1-media-version-tests.log`、`phase1-batch-snapshot-green.log`、`phase1-review-edge-tests.log`、`phase1-markdown-retries-green/native-refresh.json`、`phase1-collection-batches-green/native-refresh.json`。均位于本地 `artifacts/`。

两项额外问题也已修复：成功的后台目录核对不再清空正在播放的幻灯片提示；停止音频时解除四个原生事件，消除播放器委托保留。容量窗口原生测试改为等待实际容器实现后检查进度条，没有修改容量生产逻辑或门槛。

## 长稳结论与范围

音频修复后正式运行 **32.66 分钟、10,000 次切图、100 轮混合操作**，流程无崩溃，100 个播放器诊断 GC 后 0 个存活。证据 `artifacts/phase1-audio-fixed-standard-soak/native-refresh.json`、`phase1-audio-fixed-standard-analysis.json`。每千次私有内存均值约 228→243→252→256→261→266→271→271→275→277MiB，诊断 GC 后约 275.5MiB；仍未证明“无持续阶梯增长”，**A054a 保持 FAIL**。该二进制包含音频修复，早于本页六项审计修复，不自动成为最终构建长稳证据。

干净 Windows／低内存机器按用户要求 SKIP。前台输入、DPI、Explorer／外部播放器和安装界面因用户禁止占用前台保持 BLOCKED；真实网络／云范围按既有决定暂缓。完整色彩、GPU、大媒体和性能矩阵未验证项继续保留，不以小图片夹具或离屏测试代替。

可重跑入口为 `scripts/Test-PortableIntegrity.ps1`、`scripts/Verify-Regression.ps1`、`scripts/Verify-Extended.ps1` 和常规单元测试命令。内存归因可加 `--verify-soak-fast --verify-soak-mix=filters|text|markdown|audio --verify-soak-memory`；这些分项只用于诊断，不替代正式 30 分钟混合验收。

## 原入口部署与后续诊断

代码提交 `37d5625` 已更新原便携入口，构建标识 `0.1.0-development-a65f470bcb18`。七项实际部署启动验证与媒体能力探测 PASS；926 个原便携数据文件清单未变。最终运行清单 1498 文件逐项 SHA 校验 PASS，构建符号移入本地证据而不留在运行目录。证据 `artifacts/current-entry-pro-37d5625/update-result.json`、`final-hash-check.json` 及其中各部署日志。

单独筛选负载的诊断在 9200 次采样后中止，不能计为长稳通过。日志记录新图片预览过程中又清空选择并出现未加载行，报错“请选择当前结果中的文件”；证据 `artifacts/phase1-memory-filters-only/native-refresh.json` 与 `logs/scan.jsonl`。需继续核对搜索防抖与测试等待条件，以及这是否包含生产竞态；不凭此前 42＋43 项通过否定新失败，也不把剩余内存增长标为已修复。
