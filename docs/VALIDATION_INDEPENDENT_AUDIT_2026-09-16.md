# 独立审计后续修复验证

2026-09-16。审计阅读的源码为 `34a7f5f`，本轮修复基线为 `c788ff9`。独立会话有两份完整报告，合并后新增八类待修复问题；视频事件释放已在 `98dac89` 修复。本轮没有一期 GO 结论。

## 修复与反例

证据路径均相对于本地 `artifacts`；报告和测试只使用生成的测试文件。

| 问题 | 修复 | 失败与修复后证据 |
| --- | --- | --- |
| 扫描身份混用及过期完成 | 目录身份与条目元数据来自同一目录句柄；批次、完成前核对路径身份，索引接收完成消息时再次核验，失败保留未见记录 | `pro-independent-scan-red.log` 两个替换边界失败；`pro-independent-scan-green.log` 38 项通过；`pro-scan-completion-red.log` 错误 ready → `pro-scan-completion-green.log` 四项通过 |
| 搜索防抖及选择竞态 | 显式查询使旧定时器失效；延迟 TextChanged 不重复提交相同文本；回调核对修订号及导航；长稳选择绑定查询句柄和结果对象，变化时明确失败 | `pro-independent-query-red`；最终主回归 `independent-query` 覆盖过期回调、延迟事件、选择保持及新搜索发布 |
| 收藏验证尾部饥饿 | 记住超时前尝试的位置，下轮从后续条目开始并回绕；成员变化重置游标，父目录观察仍仅在单批内复用 | `pro-playlist-fair-red.log` 三轮均未探测健康尾部 → `pro-playlist-fair-green.log` 28 项通过 |
| 音频打开未核验新鲜度 | 复用隔离源探测及索引版本校验，创建 MediaSource 前核对选择所有权和取消，限制重复打开 | `pro-audio-freshness-red` 被替换音频仍创建 Source → `pro-audio-freshness-green` 替换与过期选择均拦截；`pro-audio-recovery-green` 实际失败后重试播放通过 |
| 文本首次打开丢失完整签名 | 授权描述携带 SourceSignature，读取编码样本前校验实际打开句柄；后续请求保留签名 | `pro-independent-text-budget-red.log` 首次读、搜、索引三个替换反例失败 → `pro-independent-text-budget-green.log` 相关 25 项通过 |
| 外部文件传入缺少总预算 | 拖入、粘贴和 Shell 适配器在分配请求数组或 COM 对象前核对数量和路径总量，含目标及新名称 | 同上，过量条目及超长路径两个反例由失败转通过；已有正常边界例保持通过 |
| 外层包清单错误放行 | 强制便携 ZIP、安装器、源码包的精确集合、唯一安全名称、大小及哈希，便携包不能靠改名跳过内容检查 | `pro-outer-package-red/results.json` 八种错误放行 → `pro-outer-package-green/results.json` 16 项通过 |
| RAW 打开抛异常泄漏 | RawSession 用 unique_ptr 管理，只有成功交付句柄时 release | `raw-ownership-red` 构造后注入异常、析构未执行；`raw-ownership-green` 异常、正常错误返回、显式关闭均正确释放 |

额外发现原生格式选择测试把后台核对导致的 scanTask 引用更换误判为恢复视图重扫。`pro-format-race-red` 确定性证明根版本、epoch、层级均正确而任务引用变化；修正断言为实际恢复不变量，并将并发场景纳入主回归。

## 验证范围

- 全量单元及工作进程测试：563/563，通过记录 `tests/pro-independent/pro-independent.trx`。
- 原生主回归 48/48：`pro-independent-regression-final/summary.json`；扩展回归 43/43：`pro-independent-extra-final/summary.json`。
- 两批使用同一 App DLL：`B1A6C335174CC43C803E18CFDD6A1F31FDDD1DD104CF6863BD097E194BB87A92`。第一批主回归曾有格式选择断言失败，保留在 `pro-independent-regression`；修正并发断言后才重新完成整批，未以局部重跑替代。
- 原生均离屏、禁激活，未操作用户运行中的旧实例。
- 构建 NU1900 表示无法取得 NuGet 漏洞数据，不能据此宣布依赖漏洞检查通过。

## 尚未闭合

A054a 长稳内存门禁仍为 **FAIL**，原正式混合负载有持续私有内存增长，尚无根因已修复的证据。本轮局部功能回归不代替正式长稳。

干净 Windows、低内存机器按用户要求 **SKIP**；前台物理 UI/DPI/Explorer/外部播放器/安装器验收 **BLOCKED**；真实网络、云盘和离线范围延期。生成的本地文件测试不代表这些环境验收通过。
