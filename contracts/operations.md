# Operation专用DTO与状态机

这是worker-message.schema.json公共信封的补充。宿主和worker共享同一Contracts程序集生成的严格DTO；拒绝未知字段/operation、范围溢出、不适用的参数组合。表内参数为合同，C#命名可遵循项目惯例但JSON名字固定。

## 专用请求/响应

| operation | parameters必需字段与限制 | ok响应载荷 |
|---|---|---|
| capabilities | 无fileRef，parameters为空ImageParameters默认值；deadline≤30s；使用同一worker预算 | isFinal、RuntimeCapabilities，实际版本、每格式providerAvailable/basicDecodeStatus/fixtureSha256/fullMatrixStatus；加载或基础样本通过不等于完整格式兼容 |
| probe | fieldGroups：从FieldStates允许组中选；不得为空 | metadata仅对应已请求组；每组state/providerVersion/sourceVersion与可空字段 |
| thumbnail / fit | targetWidth/targetHeight正整数；frameIndex≥0；fit只contain，不隐式crop | quality、surface或assetToken二选一；actual尺寸/原始尺寸/方向/Provider |
| fullTile | level≥0，tileX/tileY≥0，tileSize=约定等级（初始1024），pageIndex≥0；动画使用frameIndex≥0且pageIndex=0；静态frameIndex=0；范围实际验证 | 全分辨率层或已标明level的surface；源坐标和边界裁剪；动画返回合成后指定帧及frameIndex，不推进播放游标 |
| rawEmbedded | targetWidth/targetHeight正整数 | quality=rawEmbedded；实际预览尺寸，不能报告rawDeveloped |
| rawDevelop | policyVersion、whiteBalance=asShot；不暴露自由命令/用户RAW编辑参数 | quality=rawDeveloped；原始开发尺寸；受控tile集assetToken或surface |
| animationOpen | targetWidth/targetHeight；frameIndex≥0（起始帧）；completedLoops≥0（此前完成的播放次数，默认0）；范围实际校验 | animationSessionToken、原画布、frameCount、totalPlays（0为无限）、completedLoops、completed、指定合成帧；恢复时原生provider重建合成状态 |
| animationFrame | 同一worker与已批准input/context；按活动游标推进，frameIndex参数=0 | 合成后surface或assetToken、durationMs、frameIndex、endOfLoop、frameCount、totalPlays、completedLoops、completed；会话丢失返回AnimationSessionLost，宿主可按最后已呈现帧之后的游标重开一次 |
| animationClose | animationSessionToken | 幂等关闭；不得释放其他session |
| page | pageIndex≥0、targetWidth/targetHeight、requestedQuality=fit/full | 页图、pageIndex、pageCount、quality；与文件ordinal无关 |
| videoProbe | 允许的媒体字段组 | 规范化stream列表/容器/时长；最大4MiB原始输出，返回控制帧需更小 |
| videoCover | targetWidth/targetHeight、timePolicy=representative | 静态缩略图和实际时间点；不回传整段视频 |
| markdownRender | maxInputBytes≤8388608、maxAstNodes≤100000、maxHtmlBytes≤16777216 | 受控HTML资产token、资源引用列表或fallbackReason；HTML不塞控制JSON |
| scanDirectory | scanId、directoryToken、batchSize≤512 | 每批entries、batchSequence、isFinal、enumerationOutcome；单控制帧≤1MiB，不足则缩批 |
| textRead | offsetBytes≥0的Int64、maxBytes≤1048576、encoding、checkpointToken可空 | 输入范围、下一安全编码边界、字符/字节映射token；大文本不塞超大JSON |
| textSearch | searchId、literal长度≤4096、encoding、startOffsetBytes、maxHits≤10000 | 每批最多100命中，hitOffset/lineKnown及终止原因；超限标partial |

所有实际尺寸、offset、行号和frame计数在业务边界进行checked计算，视频时间使用毫秒；负索引/过期会话/大于真实页数返回错误，不让native库做未约束解析。

操作是否final是operation响应DTO必需信息；图片一次性任务恰好一个terminal响应；scan/textSearch可以多个progress响应但只有一个terminal。公共status=ok仅代表该次消息，不能直接当整个stream完成；必须同时核对isFinal和终止原因。每条progress包含严格递增sequence；重复消息幂等处理，漏序标脏/失败重试，禁止悄悄跳过扫描批次。

## 状态机

任务：Queued→Running→Completed/Failed/Unsupported/TimedOut/Cancelled。取消请求单独记CancelRequested；原生调用或worker真实退出前不能把资源槽归还为Idle。可缓存结果需read-before/read-after版本一致；文件变化转Stale，重新调度新版本。

图像预览：None→Queued→PreviewLoading→PreviewReady→FullLoading→FullReady。允许加载失败保留PreviewReady但显示原图失败，不改名FullReady。选择变化使旧流程Closed。动画/多页请求额外带序号，标题/图像/页码同一context更新。

扫描：Queued→Enumerating→Completed/Partial/Cancelled/Failed/Offline。只有真实完成目录枚举和扫描版本一致才允许删除差集。isFinal=true且enumerationOutcome=offline不是completed。

会话：Building→Ready/Cancelled/Failed。重复提交同一(sessionId,ordinal)必须幂等校验entryId/version，冲突则失败；Ready核验连续区间和完整读取。旧session可被用户继续查看，后台不原地重新排序。

租约：Allocated→Produced→Acknowledged→Released，或任意阶段因实例死亡变Invalid。宿主收到surface后校验并ackLease；ACK只是接管读取所有权，不是复制成功或立刻释放。缓存/纹理不再引用映射时releaseLease。消息必须有leaseId和workerInstanceId，重复释放无副作用。回收worker时旧实例全部租约不可再映射。

## 返回错误

errorCode使用稳定枚举：AccessDenied/Offline/NotFound/FileChanged/InvalidFormat/UnsupportedCodec/ResourceLimit/DecodeFailed/Timeout/Cancelled/ProtocolViolation/DependencyUnavailable/DiskFull/OutOfMemory/AnimationSessionLost。面向用户消息由App本地化生成，native原始错误仅脱敏日志。Timeout不得永久归类Unsupported。非ok响应禁止surface或assetToken进入显示/缓存流程。
