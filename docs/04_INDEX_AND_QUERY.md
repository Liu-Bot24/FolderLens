# 04｜扫描、索引、过滤与固定结果集

## 1. 本地数据位置

常规安装：%LOCALAPPDATA%\FolderLens\{config,catalog,cache,temp,logs,webview}。
便携模式：存在 portable.json 且 exe 所在目录为本地可写位置时使用 .\data；不可写或位于网络共享时明确提示并改用本地用户数据位置，不能偷偷把 SQLite 放 NAS。用户可选择另一块本地磁盘作缓存，但配置/catalog 与可淘汰缓存分开。

单实例：对数据目录路径建立当前用户命名锁，第二次启动把 --root/--open 请求传给首实例。不能让两个实例争写同一 catalog。自定义 --data-dir 仅用于本地路径与测试，不能指定源素材目录。

catalog.sqlite：根、目录、文件、元数据、字段状态、扫描、排除和聚合；config.json/saved-views.json 保存用户状态；sessions.sqlite 为可重建结果快照；磁盘缓存独立目录。SQL 基线见 contracts/catalog-v1.sql 和 contracts/sessions-v1.sql。

## 2. 数据语义与身份

FileEntry 表示“某个根里一个目录项/路径”，不是底层唯一文件。硬链接的两个路径是两个 FileEntry；可共享 physicalIdentity 的内容缓存，但列表默认都显示。physicalIdentity 在可靠时由卷标识/文件 ID/创建身份信息组成；网络不可用时为 NULL，不能编造稳定 ID。

相同 fileId 可在删除后被重用，不能仅凭裸 ID 认定旧内容；结合卷、创建标识、长度、最后写入和变化事件判定。路径重命名不必清掉已有解码缓存，但必须更新 pathRevision。根卷更换/同盘符插入另一盘要产生新 rootEpoch/卷身份，旧索引显示为其他卷，而非套用。

Windows 可存在大小写敏感目录。保存展示路径、规范绝对路径、父目录大小写模式和 canonicalKey；不对整个路径无条件 ToLowerInvariant。无法查询模式时不要合并仅大小写不同的目录项；优先保守保留并通过真实枚举核对。规范路径比较按段，而不是用 SQL NOCASE 假装覆盖所有 Unicode。

时间存 UTC ticks（已知时区的文件时间）；拍摄墙钟与 offset 独立。logicalBytes/allocatedBytes/像素总数/时长用有符号 64 位非负值，NULL 代表未知。反复修改或溢出值标错误，不 wrap。

fileVersion 由应用单调 revision 加 stat signature/变化事件推进；包含解码时读取前后重验。仅 length+mtime 无法证明内容永不变，事件、文件 change time（可获得时）及“强制刷新”能显式使旧缓存失效。默认不全文哈希百万媒体。

## 3. 扫描流程

1. 建立根身份、排除规则、scanId/rootEpoch；先接入 watcher 并有界缓冲变化提示。
2. Breadth-first 分层枚举，有界待扫描队列；基于 System.IO.Enumeration/Win32 官方文件 API 读取便宜属性，不使用 GetFiles(...AllDirectories) 构建全量数组。
3. 每目录记录开始/完整完成/失败/跳过；每 512 条或 50 ms flush 一批（二者先到），写入 SQLite 单写队列，不能为每个文件事务提交一次。
4. 遇到文件扩展名候选入类型，基础信息立即可用；不逐文件读全部 EXIF，不在扫描器内生成图片/视频缩略图。
5. 本轮完整枚举成功的目录才能依据“本轮未见”处理其直接文件；子树递归删除需对应子树完成证据。失败/取消/离线/新排除范围不得执行全根删除差集。
6. 扫描后合并 watcher 提示并针对脏目录重枚举；以当前实际文件状态为准，不按事件数加减统计。
7. 首次扫描完成标记 ready/partial，并发布 catalogRevision；字段补齐仍可 pending，扫描完成不等于元数据完成。

文件还在写入：stat 间隔两次（默认 300 ms）稳定后才解码；仍变动则延后，不与下载进程争夺。读取允许 FileShare.ReadWrite|Delete，处理文件在读取中被替换、截断、移动的错误。

## 4. 监听与核对

FileSystemWatcher 仅作变更提示，缓冲溢出会丢事件。[S06] 每活动根一个 watcher，短时间事件按目录/文件身份去抖（默认 300 ms），超出队列容量把范围标 dirty 并做有界补扫。不得静默丢弃后继续显示“同步完成”。

新增/删除/重命名以受影响目录重验；重命名事件配对不可靠时降级为目录核对，而不是靠“相同大小”把两个文件误认成移动。目录整体重命名一次批量事务修正后代路径，重新计算受影响排序键，保留内容身份。

再次打开、手动刷新、从睡眠/断线恢复和 watcher 错误都做核对。活动目录默认每 15 分钟进行低优先级轮转核对；每次只有一个遍历，未结束不叠加，不反复以零起点重扫大根。窗口最小化可降速，关闭则停止。网络断开先 offline，退避重试 5/15/60 秒，然后最多每 5 分钟一次；不循环 CPU 忙等。

应用重启后不能假装 watcher 捕获了停机期间变化，必须重新扫描核对。USN/MFT 加速不在一期必需路径，不依赖管理员权限/Everything 服务。

## 5. 云占位与路径边界

检测 OFFLINE/RECALL_ON_OPEN/RECALL_ON_DATA_ACCESS 等属性，Windows 文档指出读取或枚举可能触发远端提取。[S08] 首次默认 LocalOnly：基础条目可收录，但不为缩略图/尺寸遍历触发大量下载；这部分标 deferredOffline。用户显式打开一个云文件时，提示会下载并仅处理该文件；可按根显式授权读取在线内容，默认关闭。

不能把所有 reparse point 都当符号链接而丢掉云文件；区分 tag 与 provider/占位语义。默认不跟随 directory junction/mount point/symlink；记录“跳过链接目录”范围。命名管道、设备路径、GLOBALROOT、ADS 和不是普通文件的对象不做媒体读取。长路径使用规范绝对路径与 Win32 长路径支持；不能修改全局系统长路径策略作为正常启动前提。

## 6. SQLite 执行策略

本地 WAL + foreign_keys=ON；catalog 可用 synchronous=NORMAL（索引可重建），用户配置采用安全原子替换及备份；busy timeout 5秒仅后台线程；temp_store=FILE；单连接 cache_size 默认 -32768（32 MiB），最多 4 个读连接。不要给每个任务新开无限连接。

单写入通道，批量事务；读端独立连接。WAL 读写可并行但仍是单写者，并且 WAL 不适用于网络文件系统。[S04] 定期轻量 checkpoint；不能在 UI 操作中执行 VACUUM。迁移前备份、事务迁移、失败回滚，不能删除 catalog 来替代迁移。

字段组状态独立保存 sourceVersion、providerVersion、attemptCount、retryAfter 和 errorCode；字段变更后计算 image/display 派生量，缺失不填零。WHERE/ORDER BY 使用允许字段映射和参数化；严禁把路径/搜索文字直接拼进 SQL。

## 7. 查询计划与稳定快照（重要）

两条路径：

**交互首屏查询**：给定 filter/sort/catalog revision，在 DB 线程查询最多 256 项。具备索引的常见排序走有界 keyset，避免深 OFFSET。[S07] 包含式名称搜索可能全扫描；必须可取消、标明计算中，不承诺所有任意组合都是 200 ms。

**正式固定结果会话**：在后台持有一个有限时长 catalog 读事务，流式读取排序好的匹配结果，写入独立 sessions.sqlite 的 ResultItem(sessionId, ordinal, entryId, observedVersion, snapshotInfo)。主库只读、会话库单写，每 512 条提交会话页。最大内存仅一批；SQL 临时排序在磁盘。已写入页可预览，但会话标 Building，未构建区间不显示假数据。

会话 Ready 后，任意位置直接按(sessionId,ordinal) 范围读取；页大小 256，默认内存最多 16 页。左右导航、Home/End、Ctrl+A、播放列表和当前结果容量都以同一 sessionId 为准。仅有 keyset 不能解决滚动条直接跳第 80 万项的问题，不能遗漏结果 ordinal 层。

快照生成的长读事务会延迟 WAL 回收：设默认 15秒软阈值、30秒硬阈值与 WAL 256 MiB 警戒。先取消过时快照、降低后台元数据写频率；触及硬阈值中止快照并标 Failed/Retryable，不把部分快照写 Ready。百万快照必须在基准中优化通过，不能以永远重试取代功能。若实测必须改为数据库备份/版本化表方案，先 ADR 和一致性测试，禁止分段新读事务却声称同一时刻快照。

一个活动结果+最多两个历史缓存结果，合计最多 1 GiB（独立于缩略图配额）；超额时清理非活动会话。当前会话不能被清理器删掉。关闭根后释放活动引用，再清理。会话内文件属性按观察版本保存用于计数/容量；预览时重新解析当前 FileEntry 路径并核验文件版本，变化显示徽标，不按旧位置打开另一个文件。

## 8. SQL 口径与查询优化

核心索引包含 root/status/nameSortKey、root/kind/format、root/logicalBytes、root/mtime、root/longEdge、root/pixelCount、root/duration；按实测建立组合索引，不把所有字段组合全部建一遍。

NOT/NULL 的三值逻辑显式处理：ExcludeRAW 要保留“未确认”计数，不能粗写 is_raw != 1 丢掉 NULL；降序排序也要 ORDER BY (value IS NULL) ASC, value DESC, pathSortKey, entryId。方向独立的长短边预计算，避免常用索引失效。

所有筛选行为要有内存参考实现与 SQL 结果逐项比较的随机测试。自然排序 key 版本升级要重建对应索引和会话，不让旧 key 与新 key 混排。

## 9. 缓存键

CacheKey = hash(cacheSchemaVersion + physicalIdentityOrEntryId + fileVersion + representation + dimensionsOrTile + orientationPolicy + colorPolicy + decoderVersion + frameOrPage + quality).

thumbnail、fit preview、full tile、RAW embedded、RAW developed 必须不同 representation。路径不作为唯一内容键。磁盘缓存原子临时写入后 rename；文件与元数据不一致时视为 miss，重新生成。不得对私人素材默认全文 SHA-256。
