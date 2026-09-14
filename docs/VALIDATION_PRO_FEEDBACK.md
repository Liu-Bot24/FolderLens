# 2026-09-14 Pro 审计修复与验证

本轮以 `e7d580ef8a1052b3dc01b5c1995aca0dfc06bfe6` 为基线，处理该版本的 Pro NO-GO 反馈。下述证据属于本地生成的隔离测试，私有目录、媒体及完整日志不上传。最新用户契约是改名、移动、确认删除后移除收藏，不跟随迁移。

## 修复范围

- 收藏位置键使用父目录身份、叶名及文件身份的 SHA-256 定长摘要。目录大小写规则只用于叶名；不同硬链接名称保持区分。改名、移动、路径换成另一文件、确认 missing 均移除原归属；离线不视为删除。catalog v4 → v5 在备份后原子迁移，sessions 保持 v4。
- 稀疏批量收藏以规范化 ordinal 范围驱动会话索引；一次整理所选位置供所有目标收藏夹复用。取消或任一目标失败仍整笔回滚。
- 收藏对话框的等待可随窗口关闭取消；提交任务独立登记，关闭会等待其 finally 完成。异步读库后仅在应用重新成为前台时显示对话框。
- Markdown 内嵌图片在打开内容前检查占位状态，用无数据读取权限的句柄验证真实路径；解码使用源版本戳，前后复核。收藏 Markdown 使用条目的物理源根。远程图片不加载。
- 删除收藏标签即时移除筛选条件并重查；停止扫描不再取消格式选项和已有索引的收藏状态读取；收藏范围禁用物理目录容量。
- 每个预取任务纳入关闭收尾；位图准备串行执行，准备中预留量与已缓存量受 64 MiB 预算约束。不会以失焦为理由暂停扫描、元数据或预览。
- 后台扫描、FFprobe/FFmpeg 工具设置 BelowNormal；极短进程已退出时保留真实退出结果。启动组件哈希按 128 KiB 检查取消，在同步后台线程作用域执行。APNG 解码线程选项置于 `-i` 前，来自请求 CPU 预算，输出/滤镜线程也受限。
- 脚本监督使用 Windows Job 和 stdin 启动门：启动器先纳入 Job 后才启动实际命令。父进程先退出也能清理持有输出管道的后代。

## 已保留的反例与回归

| 检查 | 本地证据与结果 |
|---|---|
| 循环改名、混合大小写路径 | `artifacts/tests/pro-e7d580e/identity-red-confirmed.trx` 原版失败；新版移除归属测试及十轮重复通过 |
| 云端占位内嵌图 | `markdown-cloud-red.trx` 在独占内容锁前未拒绝；修复后提前拒绝，普通本地图片正例通过 |
| 稀疏范围索引 | `sparse-red.trx` 未使用 ordinal 范围；`sparse-green.trx` 六项通过 |
| 停止扫描后的浏览、删除标签、容量范围 | `pro-feedback-red` 失败；`pro-feedback-green/native-refresh.json` 通过 |
| 父退出、子持输出管道 | `orphan-red` 复现；`orphan-green` 及 `supervision-green` 通过，含正常/非零/超时退出 |
| 全量单元初轮 | 293 项中 292 通过，短命进程优先级回归失败；修复后 `pro-feedback-all2.trx` 293 项全部通过 |
| 原生初轮 | `pro-feedback-native-all` 38 项中 37 通过，新增 Markdown 测试的空根准备错误；修正测试顺序后 `collection-markdown-green` 实际内嵌图片加载通过 |
| 分批备份 | `migration-compact.trx` 16 项通过，含页间取消、重新备份、完整性检查及集合迁移 |
| 最终全量单元 | `artifacts/tests/pro-feedback/pro-feedback-final.trx`：294 通过、0 失败、0 跳过 |
| 最终原生回归 | `artifacts/pro-feedback-native-final/summary.json`：38 项全部通过 |

## 大索引与外部响应

现有隔离副本大小约 7.6 GB，含 1,153,891 个文件索引项、零个收藏夹。首轮全库改键超过 240 秒上限，已保留失败；改为定长位置摘要、事务内一次重建位置索引及可取消分批备份后，升级完成。该比较包含不同缓存状态，不能据此给出稳定加速百分比。

`pro-feedback-external-large-upgrade2`：初始化约 230.1 秒，其中备份 77.5 秒、迁移 151.9 秒。这是一次性升级，不是日常启动目标。界面会显示备份百分比与升级阶段，取消/失败释放未交付给窗口的数据库对象。升级后同一次运行的扫描约 960 ms、元数据 561 ms、查询 23 ms、预览 248 ms。

升级前后索引数量一致，`migration-check.json` 的 quick_check 为 ok，外键检查通过，位置键格式错误为零。此大库零收藏；非空收藏的迁移依靠专门用例验证，不能由大库零差异推断。

同一已升级副本再次启动：初始化约 793 ms，其中打开 catalog 约 193 ms；扫描 12 个生成文件约 581 ms、元数据约 671 ms、查询约 51 ms、预览约 201 ms。`pro-feedback-external-warm` 的外部调度间隔最大 16.39 ms，基线为 16.49 ms。数据为单次暖态场景，不能外推任意目录或冷盘性能。

独立监督进程在同机记录 10 ms 定时唤醒和 64 KiB 已缓存读取。基线/应用工作期间的间隔 P95 为 16.06/16.08 ms，最大 16.90/16.75 ms；应用工作期间缓存读取 P95 为 0.06 ms、最大 0.18 ms。这支持该场景没有观测到外部调度恶化，不能代替真实豆包语音、磁盘冷读、ETW CPU Ready、DPC/ISR 或 GPU Present 测量。

分批备份遵循 [SQLite Online Backup API](https://www.sqlite.org/backup.html)。后台线程模式同时影响 CPU、I/O、内存调度；单独 CPU 优先级不构成整机流畅保证，见 [Windows SetThreadPriority](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriority)。

## 尚待真实环境核验

真实前台 ContentDialog 的按钮 deferral/Hide 交互、豆包语音输入联测、A→B→A 焦点切换历史、硬件 Present、完整归档长稳及干净机器验证不能视为已完成。屏幕外回归没有通过强行把窗口激活来覆盖这些项目。代码修复和证据仍须固定提交的 Pro 复审，本文不声明整体产品 GO。
