# 一期补充验证（2026-09-16）

本轮基线为 `5958904d78b914fffa9216d98154a4647555c90f`。本页记录可追溯的实测范围；整体仍为 **NO-GO**，不把部分测试通过等同全部一期验收。

## 已确认的问题和修复

- 增量查询保留同一个文件行时，预取仍因结果集合对象变化被丢弃。现在复核行的观察身份，接纳仍有效的预取，同时禁止继续按旧集合的序号读取邻居。
- 预取只比较长度和 mtime，会接受同路径、同长度、同 mtime 的替换文件。缓存现在保存并复核完整 SourceFileStamp。真实替换文件的反例在修复前失败、修复后通过。
- 冷打开多页静态 TIFF 时，异步分类晚于图片显示。现在先完成多页分类，再释放解码资产并显示普通文件状态，不进入图片翻页预览。已分类和旧索引尚未分类两种路径均验证。
- 五万文件夹组逐项逆序搬移超过原有 100ms 门槛。大范围变更采用一次有界重置，保留组对象和当前项；详见对应 ADR。相同原生检查从约 182–187ms 降到 50–58ms，没有调整原门槛。

## 执行结果与证据

所有下列路径相对仓库根目录；含运行时数据的 artifacts 仅留本地。

| 项目 | 实际结果 | 证据 |
|---|---|---|
| 工具链和运行组件 | 9 项存在性／版本检查通过；不是干净机器部署 | `artifacts/phase1-environment.json` |
| 自动化 Unit／Integration／Media | 532 PASS，0 FAIL，0 SKIP；最终再次执行 | `artifacts/tests/phase1-final/phase1-final.trx`、`artifacts/phase1-final-tests.log` |
| 原生主回归 | 39 PASS | `artifacts/phase1-current-regression/summary.json` |
| 原生补充回归 | 43 PASS，包含受控崩溃后重新启动 | `artifacts/phase1-current-extra/summary.json` |
| 原生综合启动 | 7 PASS；首轮及诊断阶段失败保留，最终固定被测快照并等待所需容器实现后复验 | `artifacts/phase1-startup-final/native-refresh.json` 及同前缀六项专项 |
| 打包及执行脚本 | 6 PASS：发布规则、隐私、worker 清单、进程日志、孤儿进程、独立原生编译 | `artifacts/phase1-scripts-summary.json` |
| 8 个公开 RAW 样本 | 48 次真实 worker 操作 PASS，源文件前后 SHA 不变；不代表所有相机／色彩变体 | `artifacts/phase1-raw-family/raw-family-results.json` |
| 百万条真实 SQLite 查询库 | 快照 13.935s；100 次首批查询 P95 0.843ms、深页 P95 0.642ms；并发 59 批写入；查询阶段 WAL 峰值 9.0MiB | `artifacts/phase1-benchmarks/Database-20260916T074451352Z/query-20260916-074452-545/result.json` |
| 1GiB／5GiB 有效文本 | 非稀疏 UTF-8；100 次窗口读取 P95 0.937／0.959ms；5GiB 文件实际定位超过 4GiB | `artifacts/phase1-benchmarks/Text1GiB-20260916T074536937Z/result.json`、`Text5GiB-20260916T074538306Z/result.json` |
| 10,000 次切图／32.7 分钟混合操作 | 流程完成且无崩溃、未超测试预算；100 轮文本、Markdown 本地图片、静音 WAV 和筛选切换 | `artifacts/phase1-soak-30min/native-refresh.json` |

39＋43 项原生回归共同使用的 App DLL SHA-256 为 `1CE806E5C430A84B06FF8B9AE03A5F08EE2F1F325DD2ABC075E39A5EF4385DF0`。构建日志为 `artifacts/phase1-validation-build4.log`，0 个错误；NU1900（漏洞数据源不可达）仍存在，不能声称依赖漏洞检查通过。

长稳运行使用本轮较早的预取修复构建，后续多页 TIFF／大规模分组修复另有针对性验证及短混合回归，不能把长稳结果自动继承为最终构建的完整验收。夹具图片只有 64×48，不代表大图／RAW／视频长稳。应用私有内存约 207–322MiB，末次 313MiB；进程树峰值约 862MiB，句柄 2421–2774、线程 167–176。私有内存有上升，未做静置回收和堆归属分析，**A054a 的“无持续阶梯增长”仍未证明**。

数据库和文本测量使用生成后的热缓存，期间并发轻量长稳工作；不是冷 OS 缓存，也不是 WinUI 实际首屏呈现。百万条是索引记录，不是百万实体文件枚举。参考机全性能门禁保留 NOT_RUN。

## 保留的失败证据

`phase1-20260916-regression`、`phase1-regression-green`、`phase1-extra-native` 和 `phase1-extra-recheck` 保留首次失败。沙箱启动失败 `phase1-regression-final` 的根因是 WinUI SetShownInSwitchers 不可用，未进入测试；正常桌面权限下仍采用禁止激活、离屏模式复验。

测试夹具也做了修正：F5 等待实际新快照、查询日志使用运行时数据目录、元数据按需触发、首批注入仅触发一次合并刷新、根切换使用实际属性请求取消、恢复测试先生成受控崩溃遗留。综合启动的租约检查固定被测快照，避免正常后台发布合法释放旧快照；中部插入检查等待至少十个所需容器实现，不以刚出现的任意一个容器作为准备完成。超 4096 项发布按现行 Reset 合同验收，小范围仍检查容器保留。上述修改不删除生产功能断言、不降低性能门槛。

## 暂停、跳过和未覆盖

- 干净 Windows 和低内存机器测试：用户明确要求 **SKIP**，不是 PASS。
- 物理键鼠、WebView 焦点、跨不同 DPI、系统主题／文字缩放、Explorer／外部播放器交互及安装界面：用户要求停止占用前台后暂停，**BLOCKED**。
- 网络位置、云占位和离线真实环境：按已确认范围暂缓，未使用 M 盘；这轮不以本地夹具冒充 Y 盘网络验证。
- 大图 GPU 预算、睡眠／设备拔插、独立色彩金样、完整格式变体和全部参考机呈现性能：**NOT_RUN** 或只有局部证据。
- 审计和部署状态以本页后续补充和实际提交为准；本节不代表 Pro 审计已提交或已通过。

## 重跑

使用 `scripts/Verify-Regression.ps1` 与 `scripts/Verify-Extended.ps1`，分别提供实际 AppRoot 和新的 EvidenceRoot。长稳为应用参数 `--verify-refresh --verify-deployed --verify-soak --data-dir <新的隔离目录>`；`--verify-soak-smoke` 只做短检查。部署七项验证使用 `scripts/Verify-Startup.ps1`。这些原生程序默认离屏、不激活，不代替物理 UI 验收。

长稳可另加 `--verify-soak-memory` 记录托管堆与 GC 次数，并在全部计时操作结束后单独记录静置、诊断性完整 GC 前后的内存。该 GC 只用于分析，不在计时工作负载中运行，也不作为正常运行会自动回收的证明。
