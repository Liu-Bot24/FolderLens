# 34a7f5f 审计修复与目录导航验证

2026-09-16，源码基线 `34a7f5fdd6e457ff7bdd699b2b03a837b588acdf`。本轮没有一期 GO 结论。

## 目录扫描

旧实现进入子目录时只改变查询范围，继续枚举父根，因此地址改变后父根计数仍持续增长。按用户确认的新规则，导航取消并等待旧扫描退出，再扫描所选目录及其子目录；过期进度不能写入新视图。见 [目录扫描 ADR](adr/2026-09-16-selected-directory-scanning.md)。

- 红例：`artifacts/phase1-scan-navigation-red/native-refresh.json`，父扫描仍在。
- 绿例：`artifacts/phase1-audit-round2-regression/scan-navigation/native-refresh.json`；覆盖在途取消、快速连续导航、返回父级、完成后的计数隔离、旧保存视图的位置与选择恢复。
- 真实枚举：`artifacts/phase1-navigation-scale/native-refresh.json`。生成 65,548 个文件，父扫描发现 140 个文件时切到含 12 个文件的子目录；旧扫描完成退出，当前结果与计数均为 12。导航及随后 500ms 迟到回调观察共 930ms。未使用完成屏障模拟该大目录验证；不代表用户磁盘冷缓存或物理首帧性能。
- 目录准入失败保留旧监视器、历史记录、树导航及容量导航由扩展回归覆盖。

## 第二轮审计发现

原会话完成了基线复审，新增三项问题。更正此前记录：另一独立会话也已完成，通过任务读取接口取得两份完整报告；旧浏览器页面的“已停止思考”被误判为没有终稿。两份报告的合并发现和后续修复见 [独立审计修复验证](VALIDATION_INDEPENDENT_AUDIT_2026-09-16.md)。

| 项目 | 修复与证据（位于 artifacts） |
| --- | --- |
| R-01 Markdown 超时正常返回漏记失败 | 分离文档所有权失效与当前请求超时，后者进入有界重试。`phase1-r01-red` → `phase1-r01-scan-green-markdown-retries`；覆盖真实 15 秒期限、忽略令牌后正常返回及导航失效对照 |
| R-02 Shell 后续验证污染新视图并长期占用忙状态 | 捕获根、视图与取消令牌，分批验证仅向所属视图发布；Shell 完成即释放忙状态。`phase1-r02-red` → `phase1-r02-green`；旧收藏尾部阻塞后导航并多选，旧验证不触发新查询或缩减选择 |
| R-03 视频原生事件保留播放器 | 正常停止、失败及超时回调先解除播放器与计时器订阅，再释放。`phase1-r03-red` 中 4/4 存活，`phase1-r03-green` 中 5/5 释放；超时分支通过缩短诊断计时器触发，不冒充真实打开超时性能验证 |

这些原生用例已纳入 `scripts/Verify-Regression.ps1`。R-02 新测试模拟已完成的 Shell 结果，不代替真实 Explorer 操作验收。

## 整批验证

- Release 构建 0 错误。NU1900 表示网络无法取得 NuGet 漏洞数据，漏洞检查不能记为通过。
- 553/553 单元测试：`artifacts/tests/phase1-audit-round2/phase1-audit-round2.trx`。
- 45/45 主回归：`artifacts/phase1-audit-round2-regression/summary.json`。
- 43/43 扩展回归：`artifacts/phase1-audit-round2-extra/summary.json`。
- 两批原生 App DLL SHA256 均为 `63A41B40FDB8CE922D071CFCE0FC8FF6AC6ECC19911E9402435E7103440C342A`。之后仅添加大目录验证入口并重新构建，生产逻辑未变；大目录证据独立列出。

原生测试使用独立数据目录，离屏且不激活窗口；没有操作用户运行中的旧实例。源码和磁盘文件更新不会自动更新运行中的旧程序。

## 未闭合项

A054a 保持 **FAIL**。音频事件修复后的 10,000 次、32.66 分钟正式长稳仍有私有内存增长；视频修复不能解释没有视频负载的该曲线。

筛选诊断存在夹具等待错误：固定 350ms 不保证搜索防抖和结果发布结束。等待实际查询代次及目标筛选生效后，10,000 次筛选诊断完成（`artifacts/phase1-memory-filters-ready/native-refresh.json`），约 400 秒，诊断 GC 后约 240MiB。该结果仅证明流程完成，不满足 30 分钟门禁，也不证明没有内存增长。

追加隔离诊断没有改变生产逻辑：

- `phase1-query-row-retention/native-refresh.json`：200 次平面/分组查询替换后，200 个旧结果对象、200 个旧视图均零存活；2,200 次文件行弱引用观察中，当前结果之外有 1 个存活。该数据不支持旧结果/视图成批保留的猜测，单个文件行的所有者尚未追踪，不能宣布所有资源释放。
- `phase1-memory-text-isolated`：10,000 次切图与 100 次文本混合，183 秒；每千次私有内存均值从 206MiB 到 218MiB，后半段约 216–218MiB；诊断 GC 后约 218MiB。
- `phase1-memory-markdown-isolated`：10,000 次切图与 100 次 Markdown 混合，184 秒；每千次均值从 206MiB 到 217MiB，诊断 GC 后约 218MiB。

这些短诊断不能直接与不同节奏的正式 32.66 分钟测试判等，也未证明混合路径的增长根因。下一步仍需定位筛选与混合操作下的原生分配，而非凭曲线添加强制回收。

诊断入口：`--verify-refresh --verify-deployed --verify-query-retention --data-dir <新的隔离目录>`；隔离混合入口追加 `--verify-soak --verify-soak-fast --verify-soak-mix=text`（或 `markdown`）和 `--verify-soak-memory`。诊断 GC 仅发生在测量结束后。

干净 Windows、低内存机器按用户要求 **SKIP**；占用前台的物理 UI/DPI/Explorer/外部播放器/安装器验收仍 **BLOCKED**；网络与云盘范围继续延期。以上均不是 PASS。
