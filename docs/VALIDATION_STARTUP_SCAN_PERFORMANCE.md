# 启动、后台扫描及收藏触发器性能复验

## 已确认根因

v4 收藏模式用 `Directories_Location_Update` 维护位置标签。当目录大小写模式变化时，它原先只按 `directory_id` 更新 Files；已有索引是 `(root_id,directory_id,entry_state)`。查询计划为 `SCAN Files`，会遍历全部索引，而不是该目录文件。

扫描排队的 `AddDirectory` 还会把已知目录大小写模式重置为 `unknown`，收到实际观察后又改回来。于是反复打开一个很小的目录，也能触发两次全表扫描；临时大小写折叠还可能错误合并位置标签。

修复同时约束 `root_id` 和 `directory_id`，复用现有索引；排队时保留既有观察，实际扫描新观察仍正常更新。已有 v4 数据库以一次原子触发器 DDL 修复，不新增文件格式、不重写文件表、不新建冗余索引。正常扫描、加载和解码不以窗口是否在前台为条件。

## 可重放反例

`directory-case-red.trx` 中两个测试真实失败：实际触发器查询计划出现 `SCAN Files`；已知目录重扫发生一次不必要的 `unknown` 重置。

修复后 `directory-case-green.trx` 三项通过，覆盖目录索引、重复扫描、已存在 v4 触发器升级，以及改变实际大小写模式后收藏仍保留、排除未收藏文件的负例。测试证据位于 `artifacts/tests/startup-performance`。

## 同一大索引副本测量

使用本机既有约 7.6 GB 索引及约 1.2 GB 缓存的隔离副本；仅扫描其中生成的 12 个图片文件。用户源文件未写入，窗口在屏幕外运行。此结果用于暴露索引规模放大效应，不能外推完整用户归档的扫描时间。

| 暖态同场景 | 修复前 | 修复后 |
|---|---:|---:|
| 打开目录并扫描 | 2866.5 ms | 605.4 ms（后续 539.7 ms） |
| 两个最慢数据库步骤 | 1132.6 / 1146.5 ms | 不再出现秒级步骤 |
| 显示后的初始化 UI 心跳最大间隔 | 原测量未分离 Loaded 边界 | 38.7 ms |
| 后台扫描/元数据/预览 UI 心跳最大间隔 | 98.5 ms | 92.8 ms（后续复验） |

证据在 `artifacts/startup-profile-real-catalog`：`database-trace.json`、`index-fix.json`、`loaded-boundary.json`。打开目录并扫描这一小场景减少约 79%，不是整产品提速 79%。后台扫描得到全部 12 文件，元数据得到真实尺寸，目标图片解码成功，窗口未成为前台。

最初计时从窗口构造前开始，约 200–212 ms 的第一次计时间隔包含 XAML 构造，导致原 200 ms 断言失败。保留 `baseline.json`、`wide-baseline.json`、`database-trace.json`、`index-fix.json` 原始结果。后来将 Loaded 前创建时间与 Loaded 后响应分别记录，原始间隔仍保留，没有把它改小。Loaded 后及后台工作仍使用同一个 200 ms 门槛；这不是显示器 Present 或整个进程冷启动时间的验收。

空索引一次初始化约 1718 ms，其中组件版本指纹约 1286 ms；已有缓存的后续初始化约 500–670 ms。文件系统冷热差异明显，不将其全部归因于本次代码修改。

## 其他本轮范围

- 星标缩为 24 px 圆形按钮，距缩略图边框 8 px，图形 14 px；浅/深色实际控件截图和位置检查见 `artifacts/rounded-star-light`、`artifacts/rounded-star-dark`。
- 前台所有权限制只作用于窗口激活和控件 Focus，扫描、元数据和预览任务继续执行。
- 对此前同一组较大 JPEG 的 111 次冷切换复验见 `artifacts/startup-fix-cold-switch`：目标 Draw P50 308.4 ms、P95 505.8 ms、max 576.7 ms；与既有原生流测量接近，本次未新增冷解码提速。数据不能替代同图同配置的 FastStone 对照，也不等于显示器实际 Present。
- 同组 111 次预取切换复验见 `artifacts/startup-fix-prepared-switch`：等待预取后的 104 次目标 Draw P50 4.8 ms、P95 11.1 ms、max 45.3 ms；最初连续切换的 7 次单独报告。之前 434.9 ms 的个别 Draw 长尾本轮未复现，不等于已经归因或永久消除。

## 本轮完整回归

Release 构建 0 错误；286 项单元测试、35 项隔离原生回归全部通过。证据：artifacts/tests/startup-performance/startup-performance-final.trx、artifacts/startup-performance-final-regression/summary.json。NU1900 表示漏洞信息源不可达，不作为依赖安全检查通过。

其他程序的响应、豆包语音实际输入及整机资源争用尚未完成独立测量；应用自身心跳通过不能证明这些体验已修复。后台扫描/加载继续工作是必要行为。
