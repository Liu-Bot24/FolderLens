# 2a85ac5 审计反馈修复与切图验证

基线：`2a85ac54b7dccddd804d3aac16a4f3b2252ec38a`。
本记录区分确定缺陷的回归、局部性能测量与完整验收；不构成一期 GO。
原始报告保存在本地 `artifacts`，私人图片、路径与开发日志不随源码发布。

## R1–R7

| 项 | 修复后的行为 | 反例与复验 |
|---|---|---|
| R1 | 扫描组件启动、连接或 hello 超时归为组件不可用；调用方取消仍为取消；握手后源 I/O 超时仍为 TimeoutException | `resume-scan/scan-delivery-red.trx` 2 失败；`scan-delivery-all-clients.trx` 13 通过，含读取、stat、解析图片与根身份 |
| R2 | OpenRoot 与核对路径都使用完整组件定位器 | `scan-audit-red-r2` 失败；`scan-audit-green-r2` 通过，残缺新布局与完整旧布局并存 |
| R3 | 有部分结果时，扫描错误仍由独立错误条呈现，不被查询或元数据进度覆盖 | `scan-audit-red-r3` 失败；`scan-audit-green-r3` 通过，真实保留 12 项及在途查询 |
| R4 | 同根、同 epoch 的完整强制扫描达到 ready 后清旧扫描错误；查询或局部核对成功不足以清除 | `scan-audit-{red,green}-r4-{empty,results}`，原生调用与 F5 共用入口 |
| R5 | 缩略图优先级使用当前控件投影的容器索引，并排除隐藏祖先 | 原 snapshot ordinal 100 / 控件 index 0 反例失败；`lifecycle-green-thumbnail-priority` 通过，屏内先于缓冲项、隐藏列表不抢占 |
| R6 | 清单独立要求三个 worker 的 exe/dll/deps/runtimeconfig；启动检查实际读取 TXT、MD，并禁开发目录回退 | `worker-manifest-{red,green}`；`strict-startup-green` 四场景通过；实际缺 content DLL 被门禁拒绝 |
| R7 | 初始化失败写出 FAIL 并退出；脚本超时回收进程树、保留输出，不无限等待 | `initialization-failure-red.supervisor.json` 原 8 秒不退出；`strict-missing-media` 实际缺 EXE 约 903 ms 报失败退出；`logged-process-{green,winps-green}` 验证超时 124、正常 0、普通失败 7 和子进程清理 |

扫描进程退出还有一项真实清理竞争：原取消后只在 `!HasExited` 分支等待，
20 次快速启动取消的夹具无法立即删除 exe。增加退出观察任务等待的实验无效，
已撤销。始终在后台等待实际退出信号后，同一 20 次反例通过，没有删除重试。
此前全量测试首次 257 通过、2 个夹具清理失败，修正后
`tests/resume-audit/all-tests-green.trx` 为 261/261 通过。
之后 `tests/current/current-tests.trx` 再次 261/261 通过。
原生批量 `regression-current-25b` 为 25/25 通过。第一次批次保留了失败记录：
音频验证漏写状态、关闭验证最终结果在 `native-close.json`、视频缺样本参数，
胶片条滚动后旧自动化 peer 失效；已分别修正报告、生成片段与重新取当前 peer。

R4 另一个竞态由批次暴露：F5 遇到正在执行的后台核对时跳过强制扫描。
`scan-busy-red` 在真实核对任务的屏障中稳定失败，`scan-busy-green` 通过。
F5 现在先等待当前任务，确认未换根或取消，再完整扫描；批处理已加入此第 26 项。
`menu-firstpage-red2` 还复现完整快照不可用时下一张禁用、最后一张无动作，
统一使用真实首批序列长度后 `menu-firstpage-green` 通过。

## 菜单、输入与缓存生命周期

- `menu-availability-red2` 的 9 个不可用状态反例失败，修复后
  `menu-availability-green` 通过。覆盖无选择、图片未就绪、首尾导航、返回列表，
  以及有文件/已就绪的正例；主菜单、右键和侧栏状态同步。
- `wheel-distance-red` 复现一次十刻度只移动一张；`check-wheel-distance` 通过，
  覆盖反向、半刻度累计与首尾边界。直接定位最终目标，不丢导航距离。
- `prefetch-adoption-red` 原会取消同目标并再次 worker fit；最终
  `final-prefetch-adoption` 复用已解码但显示准备尚未完成的结果。
  等待任意低优先级解码的 200 ms 实验拖慢冷切，已撤除该适用范围。
- `turnaround-red` 复现 2→1→0→1 后下一张缓存被另一侧预取淘汰；命中更新
  使用顺序后 `final-prefetch-turnaround` 通过。
- `lifecycle-green-prepared-cache` 验证真实位图所有权转移、内存压力释放缓存而
  保留当前图、源版本变更拒绝、设备替换恢复与换根释放。
  设备替换走 CanvasControl 的真实 NewDevice 回调，未模拟驱动故障或禁用显卡。

## 像素与性能证据

`pixel-audit-green/display-pixels.json` 为 26/26 通过：8 位/16 位透明边缘、
ICC、八种 EXIF 方向、非对称四角色块的 JPEG 缩略图，以及缩略图读取前后
full 输出的像素哈希和尺寸一致。源文件哈希不变。此处不包含显示器 ICC 验收。

预制缓存决策与固定版本参考见
[ADR](adr/2026-09-14-prepared-image-prefetch.md)。初次单变量实验中，充分预取
后的选择完成由约 55–120 ms 降为约 3.5–6 ms。后续计时直接关联目标位图身份，
修正了等待加载结束标志可能漏记早先 Draw 的问题。

`switch-corrected-series` 的 27 次充分预取切换首次目标 Draw 约 4–18 ms，
反向缓存未命中已消除。后续 `switch-111-series` 暴露测试前置条件错误：
没有先选图，SetImmersive 被正常入口拒绝，实际是 280 DIP 宽的侧栏预览。
这些旧数字只能作为侧栏和缓存实验，不能证明大图查看性能。
测量代码已改为真实选图后进入查看模式，断言尺寸并清理预取，再从下一张开始。
最终大图统计须以修正后的单独报告为准。Draw 不是显示器 Present；未清 OS 缓存，
不能将这些数据称为磁盘冷读或完整参考机验收。

`switch-immersive-111` 实际大图 2525×1125 DIP、150%：初始 7 次快速切换
P50 471.3 ms / max 739.4 ms；随后等待预取的 104 次 P50 5.0 ms /
P95 8.6 ms / P99 17.9 ms / max 375.2 ms，其中一次未命中。不能称全部热命中。
进程树采样约 0.9–2.0 GB，采样均完整；不含完整 GPU committed 证据。
PNG-only 对照 `switch-immersive-encoded-111` 后 104 次 P50 69.1 ms /
P95 150.6 ms，期间有一次发布文件哈希检查重叠，不作为严格速度倍率验收。

### 冷路径实验与撤回

`switch-cold-png-baseline-111` 禁用应用预取、同一组 20–60 MP JPEG，111 次
首次目标 Draw P50 408.1 / P95 635.5 / max 908.1 ms。仅改 fit 中间 PNG
无损压缩与滤波的 `switch-cold-fast-png-111` 为 P50 451.3 / P95 651.9 /
max 965.1 ms，没有收益，WorkerServer 的实验调用已撤回。像素相同不能替代性能通过。

`bitmap-path-probe` 用同一已生成 JPEG 适屏资产，三种加载方式交替各 100 次：
PNG 托管流 P50 95.2 / P95 118.5 ms；PNG 原生路径 P50 20.9 / P95 26.5 ms；
BGRA 文件读取和位图创建 P50 4.1 / P95 8.0 ms。此处仅位图加载，不含解码、IPC
或 Present，不能把差值直接当作完整切图收益。
据此源码将 `LoadRenderedBitmap` 改为 Win2D 原生路径加载工作进程自有 PNG，
保留释放 finally 和选择代次检查；没有修改源图读取方式或生产 IPC。
**此最后改动已完成 Release 构建，尚未完成端到端复验。** 构建 0 错误，7 个
NU1900 警告，证据为 `resume-baseline/native-path-final-build.log`。
同一源码的完整单元测试 `tests/native-path-current/native-path-current.trx`
为 266/266 通过，0 跳过；8 个本批 PowerShell 脚本语法检查通过。
最终冷切、取消生命周期及同入口启动仍待原生复验，当前不能给出完整性能或交付结论。

诊断输出目录的检查原在初始化之后且漏掉与源目录相等的情况。现在在实例锁与
目录创建之前拒绝相等/子目录；`diagnostic-paths-red.trx` 四个反例失败，
`diagnostic-paths-green.trx` 五项正反例通过，未写入测试源目录。

`pixel-transport-probe-run.stdout.log` 对比同一六张 JPEG，直接 RGBA 与 PNG
像素哈希一致，仅部分节省约 13–87 ms，且传输字节变多。
生产 IPC 保持原受控 PNG 资产，没有把小样本收益作为大改协议的依据。

FastStone 的实际对照使用同一组 20–60 MP JPEG。观察了单次相邻切换与十刻度
滚动，后者先捕获到中间图片，再确认最终位置。原生控制往返含输入与截图开销，
不是解码或 Present 延迟，不能据此宣布两产品速度相等或某方快若干倍。
没有证据确认 FastStone 内部的缓存格式、数量或 GPU 策略。

## 保留的验收边界

小夹具不能证明真实归档静止刷新已彻底归因；没有重开全归档反复复现。
网络/SMB/云真实恢复按既定范围暂缓。实际耳机拔插、混合 DPI、系统高对比、
真实驱动丢失、睡眠恢复、10000 次切图及 30 分钟混合长稳、完整 GPU committed
预算、无 SDK 干净安装/升级/卸载与离线依赖矩阵仍需各自证据。
NU1900 漏洞源不可达不等于漏洞审计通过。新一轮审计与用户实际入口更新情况
须由最终提交和同入口验证记录确认，不能从开发输出的 PASS 推断。
