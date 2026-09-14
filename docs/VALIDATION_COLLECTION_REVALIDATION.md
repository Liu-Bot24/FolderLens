# 收藏选择与身份中断回归

以 `f05fde5484b257d43abd81f176b75601909d0e7f` 为反例基线。旧版允许旧选择重新收藏已确认删除或已经改名的文件，且 A→未知→B 的身份观察链会把原收藏转给 B。无变化重扫也会重复计算并写入位置键。

## 修复行为

- 普通菜单和快捷星标提交观察到的文件版本、路径修订、目录、源根与 epoch；事务内发现失效项就拒绝整批修改并提示刷新。旧快照的批量入口使用相同核验，保留 ordinal 范围索引。设备离线本身不使本地标记失效。
- catalog v6 单独保存身份中断时最后确认的身份，当前未知值仍是未知。A→未知→A 保留、A→未知→B 移除，跨重启有效。此前已遗失的身份历史无法补推。
- v5→v6 先备份，再原子添加历史表、替换触发器、移除旧版重新加入的已确认 missing 归属，不重写 Files 或位置键。v4 升级继续做原有位置键转换，直接进入 v6。sessions 仍为 v4，位置键仍为 v5 摘要格式。
- 路径与身份均未改变时不触发位置键写入；真正改名仍移除归属。
- 目录改名的子树查询带上根目录约束，使用已有根目录/父目录、根目录/文件目录索引，避免无关索引数据增多时反复全表扫描。

## 验证

| 检查 | 证据 |
|---|---|
| 旧选择收藏 missing / 改名文件 | `artifacts/tests/pro-f05fde5/stale-selection-red.trx` 三项失败；修复后的状态、版本、路径、整批拒绝与正常选择用例通过 |
| 无变化重扫 | `unchanged-rescan-red.trx` 实际扫描写入一次位置键；`unchanged-rescan-green.trx` 无写入，随后真实改名仍移除收藏 |
| 身份中断跨重启 | `identity-gap-red.trx` A→未知→B 失败，A→未知→A 正例通过；`identity-gap-green.trx` 两条路径及已有身份回归通过 |
| v5 兼容 | `LegacyV5CollectionSchema` 保留 f05fde5 的实际触发器；升级保留正常收藏，不重写文件键，第二次初始化不重复 DDL；备份存在 |
| 旧版重新加入的 missing | `v5-missing-upgrade-red.trx` 复现；修复后移除失效项、保留正常项与原 Files 记录 |
| 目录改名规模 | `directory-rename-scale-red.trx`：增加 5,000 个无关文件后，同样子树改名从约 1,700 条 SQLite 指令增至约 76,700 条；修复后规模上限断言通过，真实改名移除收藏，无关文件全部保留 |
| 全量单元 | `f05-complete-unit.trx`：307 通过，0 失败/跳过 |
| 原生回归 | `artifacts/pro-f05-rename-native-final/summary.json`：38 通过；包含失效缩略图快捷收藏拒绝、不错误亮星、随后正常收藏成功 |
| Release 构建 | `artifacts/pro-f05-rename-final-build.log`：0 错误；NU1900 表示漏洞源不可达，不是漏洞检查通过 |

受限执行上下文中的 Markdown 控制器初始化连续超时，`pro-f05-markdown-diagnostic` 已定位在 controller 阶段。同一构建、同一 5 秒超时设置在正常 Windows 用户上下文的 `pro-f05-markdown-user-context` 通过，最终整套原生回归也通过。未以延长超时或原文回退掩盖失败；保留阶段诊断。

## 大库与外部调度

`pro-f05-v6-external-large-corrected` 使用既有隔离副本，约 7.6 GB、1,153,891 个文件索引项、零收藏。初始化 184.45 秒，其中 catalog 183.51 秒，包含一次性完整备份。该测量发生在增加备份百分比和清理旧 missing 归属之前；此大库没有收藏，不能据此证明非空归属迁移，后者由上述用例单独覆盖。

只读迁移检查：schema 6，文件数前后一致，quick_check 为 ok、外键检查通过、位置键格式和收藏映射差异均为 0。外部正常优先级探针在应用运行期间间隔 P95 16.07 ms、最大 16.82 ms，基线最大 18.06 ms。

同库升级后再次启动 `pro-f05-v6-external-warm`：初始化 1,214 ms，catalog 201 ms，Loaded 后 UI 最大间隔 60.51 ms，后台工作最大间隔 102.05 ms；外部探针最大 16.52 ms，基线 19.18 ms。扫描、元数据、预览继续完成，未通过失焦停工降低负载。

最初 `pro-f05-v6-external-large` 的数据目录参数多了一层 catalog，实际测到新建小库。该结果标记为无效的大库证据，不能混入上述结果。

这些采样只覆盖生成夹具、缓存读取和调度间隔，不等同真实豆包语音、ETW CPU Ready/DPC/ISR、冷盘、GPU Present、完整归档长稳、前台弹窗 deferral 或干净机验收。上述项目仍不能记 PASS。

已尝试用系统 WPR 做短时 CPU/Ready/DPC/ISR/磁盘采样，配置为 128 MiB 内存缓冲。正常用户执行上下文返回 `0xc5585011`（无法启用系统性能采样权限），录制未开始，测试应用未启动，也没有改变系统权限。`artifacts/pro-f05-rename-etw/start.log` 保存失败证据；ETW 项仍为未验证。
