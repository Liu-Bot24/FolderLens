# 03｜架构、进程与接口合同

## 1. 解决方案结构

```text
FolderLens.sln
src/
  FolderLens.App/                 WinUI 3、MVVM、输入、可视虚拟化、Win2D
  FolderLens.Core/                领域类型、过滤/排序/会话、调度、预算、接口
  FolderLens.Infrastructure/      SQLite、文件系统、缓存、设置、外部程序、Windows API
  FolderLens.Media.Worker/        NetVips、Magick.NET、LibRaw 适配和动画会话
  FolderLens.Content.Worker/      FFprobe/FFmpeg 监督、Markdown 解析、受控文件探测
  FolderLens.RawBridge/           最小 C ABI C++ DLL，仅当稳定现有绑定不满足合同
  FolderLens.Contracts/           版本化 IPC DTO、错误类型、像素/帧描述
  FolderLens.Diagnostics/         无界面基准和诊断 CLI
 tests/
  FolderLens.UnitTests/
  FolderLens.IntegrationTests/
  FolderLens.MediaTests/
  FolderLens.UiTests/
  FolderLens.PerformanceTests/
 tools/                          测试样本生成器/文档和证据验证器，优先 .NET
 scripts/                        PowerShell 构建/测试/打包入口
 assets/                         自有图标和样式，不含私人素材
 contracts/ planning/ docs/      本规划及随实现更新的契约
 implementation/                 进度、交接、ADR、环境与验收状态
 artifacts/                      实际构建产物和日志，默认不提交大文件
```

App 依赖 Core/Contracts，Windows 接入由 Infrastructure 提供；Core 不依赖 WinUI、具体 SQL 或原生解码器。Worker 不引用 App，不能操作 UI、修改索引或读取用户设置数据库。禁止循环引用和两个模块各持一套根目录/过滤状态。

## 2. 状态与所有权

一个 AppSession 拥有活动 RootSession、BrowserQuerySession、SelectionState、PreviewSession。文件集合不是 ObservableCollection<完整文件>；只存在有界页缓存和结果会话句柄。

所有权链：AppSession 只扫描当前选择的文件夹及其子目录。切换文件夹时取消并等待旧范围扫描退出，再启动新范围；进入子目录也按此边界处理，不复用仍在扫描的父目录范围。进度只统计当前根的新一轮扫描。显式停止取消当前根扫描，退出时取消并等待全部任务。BrowserQuerySession 释放时取消查询、列表预取和当前页面元数据请求；PreviewSession 释放时取消图片/文本/音频；SurfaceLease 释放 CPU/GPU/共享内存资源；worker 退出立即使其所有 lease 失效。资源不能靠最终 GC 才释放。见 ADR `2026-09-16-selected-directory-scanning.md`；该决策替代旧 ADR 中跨导航继续整根扫描的行为。

### 版本标识

| 标识 | 变更条件 | 作用 |
|---|---|---|
| rootEpoch | 新建或重建根上下文；返回此前目录建立新观察，同目录筛选保持不变 | 拒绝旧根任务回写；页面切换另外使用页面版本 |
| queryGeneration | 筛选、排序、排除或显式刷新查询 | 防旧结果页覆盖新列表 |
| resultSessionId | 每份固定有序快照 | 导航、全选、播放列表、容量同源 |
| selectionGeneration | 选择或页码/质量请求改变 | 防旧预览覆盖新文件 |
| fileVersion | 内容或影响显示的属性发生变化 | 缓存失效与请求重验 |
| pathRevision | 同一目录项重命名/路径变化 | 不把内容缓存与路径耦合 |
| workerInstanceId | 工作进程重启 | 防旧共享内存/旧响应被复用 |
| decoderVersion/colorPolicyVersion | 引擎或处理政策变更 | 区分缓存与金样测试版本 |

UI 应用结果时逐项核验适用标识，而不是只比较文件路径。索引写入也必须带 expectedFileVersion，采用 compare-and-set；文件变化后的旧元数据不能覆写新版本。

## 3. 进程模型

App：UI、状态协调、后台数据库调度、Windows 音频播放、只读文本页布局。
Media Worker：每实例同一时刻一个重任务；一个前台、最多两个后台，延迟启动，不在空闲时全部常驻加载原生 DLL。重解码一进程一任务方便真正取消。
Content Worker：FFprobe/FFmpeg 子进程监督、Markdown 转换；最多一个视频封面任务；文本/目录网络阻塞操作使用独立可终止工作任务，不占用 UI。

Win2D 在 App 中负责 GPU 资源和渲染；原始不可信媒体尽量在 worker 中解码。Worker 崩溃可隔离故障，但普通同用户进程并非强安全沙箱，不能对外宣传“完全沙箱化”。

本地基本枚举可用专用后台线程。UNC/可移动盘/云端 provider 枚举可能不可及时取消，使用可终止扫描工作进程/内容 worker 的独立实例；不得把整个扫描挂在不可回收的 Task.Run 上。工作进程用 Job Object 监督并在主程序退出时回收；不要把外部用户播放器加入此 Job。

## 4. 关键接口：语义合同而非要求照抄签名

```csharp
IRootScanner.ScanAsync(ScanRequest, CancellationToken) -> IAsyncEnumerable<ScanBatch>
ICatalogWriter.ApplyBatchAsync(ScanBatch, CancellationToken) -> BatchCommit
IMetadataScheduler.EnsureAsync(FileRef, FieldMask, Priority, CancellationToken)
IQueryService.OpenAsync(FilterSpec, SortSpec, CancellationToken) -> QueryHandle
IQuerySession.ReadRangeAsync(long firstOrdinal, int count, CancellationToken) -> ResultPage
IQuerySession.GetAdjacentAsync(FileEntryId, NavigationDirection, CancellationToken)
IPreviewService.RequestAsync(PreviewRequest, CancellationToken) -> PreviewLease
IImageSurfaceProvider.GetTileAsync(TileRequest, CancellationToken) -> TileLease
ITextDocument.ReadWindowAsync(TextWindowRequest, CancellationToken) -> TextWindow
ITextDocument.SearchAsync(TextSearchRequest, CancellationToken) -> IAsyncEnumerable<SearchBatch>
ICapacityService.BuildAsync(CapacityRequest, CancellationToken) -> CapacityHandle
IExternalPlayer.OpenAsync(ExternalOpenRequest, CancellationToken) -> OpenResult
```

接口可返回异步操作，但底层 SQLite 不能在 UI 上直接调用 ExecuteReaderAsync 并认为不会阻塞：Microsoft.Data.Sqlite 的 ADO.NET async 实际同步执行。[S05] 使用有界专用 DB 执行器，每连接串行使用，查询与写入分连接，SQLite 中断绑定实际查询生命周期。

## 5. IPC 合同

使用只允许当前用户连接的 Named Pipe，不监听 HTTP/TCP 端口。每帧 4 字节 little-endian 长度 + UTF-8 JSON，控制消息最大 1 MiB。握手验证协议主版本、worker build、实例 ID、会话随机 nonce；限制连接、拒绝未知命令和畸形长度。

请求包含 requestId、operation、fileRef、selection/query 代次、deadlineUtc、resourceBudget、任务参数。响应包含同一上下文、status、errorCode、质量级别、实际尺寸/色彩/帧信息、surfaceDescriptor 或受控缓存产物。contracts/worker-message.schema.json 描述公共信封。

像素不通过 JSON/base64 传送。小缩略图可以写应用缓存并返回受控 assetToken；大图/tiles 使用共享内存句柄或有名映射，含宽高、stride、pixelFormat、byteLength、colorSpace、alphaMode、leaseId。分配前 checked 计算 stride×height，验证容量和预算；父进程校验后 ACK，再释放或续租。worker 不能任意决定要让父进程打开的外部路径。

优先 worker 接收只读打开的文件句柄；无法使用句柄的库必须使用父进程批准的规范绝对路径，并在读取前后核验 fileVersion。不可传带解释语法的文件名给 ImageMagick，优先 Stream API。缓存/临时文件写在父进程分配的本地任务目录内。

取消是 Cancel(requestId)。合作式取消超时后终止该重任务 worker，丢弃其全部 lease，并保留其他 worker。超时区别于不支持格式；不能把一次超时写成永久 Unsupported。

## 6. 依赖与加载规则

原生 DLL 仅从安装目录的可信运行时子目录加载，不从素材目录/当前工作目录搜索。两套库的 native 依赖可能同名，M0 必须验证版本冲突；必要时让兼容解码器进入独立 worker，不用替换系统 DLL 解决。

RawBridge 优先只暴露 open/probe/extractPreview/decode/cancel/release，错误码和 POD 结构固定，字符串 UTF-8/UTF-16约定一致；不要在 C# 中猜 LibRaw 私有结构偏移。RAII、SafeHandle、跨 ABI 缓冲释放函数和实例线程所有权都必须有测试。

禁止在每张缩略图上进程启停、重复加载多个 WebView2、原图经过 JPEG 中转、每一动画帧反复复制成 XAML BitmapImage。

## 7. 状态与日志可观察性

每个任务记录排队/运行/取消/完成时刻、耗时、来源引擎、读入/解码/上传字节、缓存命中和资源峰值；生产默认不记录明文路径或全文。UI 有诊断页可查看实际 GPU、图像 provider、外部播放器、数据库/缓存位置及活动任务。

日志每文件 10 MiB，最多 5 份；错误摘要默认仅本地保存且路径去标识；含内存的崩溃dump默认关闭。诊断导出需用户点击，内容预览/说明，不能主动上传。
