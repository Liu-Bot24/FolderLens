# 05｜图片查看、RAW、色彩与动画

## 1. 三层图像质量

Thumbnail：256/512/1024 物理像素阶梯，以实际卡片和 DPI 选最小足够等级，不过度解码。
FitPreview：覆盖当前可见物理像素，允许先用缓存/RAW 内嵌预览过渡；加载结束必须达到视窗所需清晰度或明确显示降级。
FullResolution：来自真正原始分辨率的像素/tiles，可缩放至 100% 和以上。高倍缩放允许看到像素，但不能使用 AI 超分“补细节”。

必须把质量级别写进响应和 UI；FullReady 只能由完整原图/原分辨率 tile 路径进入。Thumbnail 或 embedded RAW JPEG 即使尺寸较大也不自动变成 RAW FullReady。

## 2. 格式能力与一期底线

能力不是扩展名布尔开关。格式报告至少有 identify/probe/thumbnail/fit/full/animation/multipage/icc/bitDepth/errorFallback。

| 格式/变体 | 一期要求 | 备注 |
|---|---|---|
| JPEG/JPG | MUST 静态全链路 | 基线/渐进、EXIF 1–8、sRGB/AdobeRGB/CMYK JPEG、有无 ICC |
| PNG | MUST 静态全链路 | 8/16位、透明、ICC；不把 16位输入错误显示为全白/全黑 |
| GIF | MUST 静态与动画 | 帧时间、循环、透明、disposal；一个文件一个列表项 |
| WebP | MUST 静态与动画 | lossy/lossless/alpha、blend/dispose |
| APNG | MUST 动画 | 不只展示第一帧后宣称完整支持 |
| BMP | MUST 常见静态 | 24/32位及常见压缩型样本 |
| TIFF/TIF | MUST 静态与多页 | 8/16位、常见 LZW/ZIP、页导航；BigTIFF 有真实样本才列 supported |
| ICO | MUST 查看可用最大图标帧 | 图片变体不是时间动画 |
| HEIC/HEIF | MUST 常见单图查看 | 8/10位 SDR 映射、旋转；Live Photo 动态容器不做 |
| AVIF | MUST 常见单图查看 | 动画 AVIF 不作一期门禁，但标明实际能力 |
| JPEG XL | MUST 静态查看 | 不要求一期动画 JXL；缺失原生 coder 不能静默降级成支持 |
| Sony ARW | MUST 内嵌预览 + 真 RAW | A7R IV A 真实 61MP 样本是核心验收 |
| DNG/CR2/CR3/NEF/RAF/RW2/ORF/PEF | MUST 接入 LibRaw 能力 | 每家至少一项有合法样本；具体相机/压缩变体仅对实测集合保证 |
| SVG/PSD/EXR/HDR/其他 | 非门禁扩展 | 首版可显示属性并外部打开；禁止写成已完整支持 |

“常见”不是免测条款：交付的 capabilities.json 必须列具体测试文件、编码/位深、引擎版本和结果。所有 MUST 的测试样本和 provider 可用性在 M0 定案，不把发现缺少 HEIC/APNG 的问题留到打包时。

NetVips 为常规缩略图/图片主通路，Magick.NET 为兼容和动画/多页补充，LibRaw 专门 RAW；参考 [S10][S11][S12]。动态格式若锁定版本不支持增量帧读取，允许切换到已随包分发且实测支持的 FFmpeg/native provider，但要经过同一 FrameSession 合同，不能在主窗口起视频播放器装作图片动画。

## 3. Provider 路由与安全读取

启动时在 worker 中枚举实际 coder/loader 支持，并以小型样本自检建立运行时能力表；缺 DLL/架构不匹配要定位为组件故障，而非“文件损坏”。扩展名只是候选，read header 确认 magic/container；文件尺寸合法后再申请解码预算。

每格式仅一条默认路由，失败根据失败类型最多一次兼容 provider；Unsupported 可回退，资源上限/安全策略不能换引擎绕过，损坏文件不能无限循环重试。负缓存记录 fileVersion/providerVersion，升级或文件改变可重试。

禁用 ImageMagick 远程 coder、脚本/间接读取、外部 delegate；使用 Stream 或显式格式选定避免文件名中的 []/:/@ 被解析为指令。安全策略需保留真实 61MP 图片和长图能力，不照抄小型 websafe 的尺寸上限。[S19]

## 4. RAW 合同

RawProbe 返回相机/编码尺寸、可用原始有效区、内嵌预览尺寸、方向、位深等。
EmbeddedPreview 从 LibRaw 提取并标来源；可能与原始有效尺寸不同，不能用于决定最终 RAW 分辨率。
DevelopedRaw 使用 LibRaw 原始解包和处理：默认相机白平衡、版本化的固定基础去马赛克和曝光/高光策略、输出带可解释色彩的 16位或浮点中间结果；不做自动审美增强、不追求复刻相机 JPEG 风格。[S12]

首屏先 embedded；真正 RAW 后台升级。100% 请求提升 full 优先级，即使 embedded 看起来足够清楚也必须完成 raw 原像素。RAW 渲染失败时保留 embedded 并明确质量降级，允许继续左右切换。用户看到“预览”不应误以为原文件损坏。

RawBridge 把详细 LibRaw 配置写到 RAW_RENDER_POLICY.md 并做金样，不在不同文件上随机切参数。兼容路径与主路径需方向/色彩一致。不得分发不存在的自造 ARW；真实样本不足就 NOT_RUN。

## 5. 几何、DPI与纹理

先统一 EXIF 1–8（含镜像）与有效裁切定义，再生成 displayWidth/height。所有元数据、thumbnail、Fit、FullTile 采用同一方向，不双重旋转。只读 R/Shift+R 为视图变换，不改源像素/EXIF，不污染其他会话缓存。

Viewport 使用 DIP，采样/解码选择使用物理像素；100% 时 scaleDip = 1 / rasterizationScale。切换显示器触发 DPI 和颜色目的空间重建，不重新扫描目录。

大图不能假设能装进一张 GPU texture；读取设备 MaxBitmapSize。超过上限按固定 tile（起点 1024×1024，有边缘采样重叠）和 LOD 绘制；仅当前视区+小边界驻留。Win2D CanvasVirtualBitmap 的按需能力与格式有关，不能当成任何格式的随机局部解码保证。[S14]

对无法直接区域解码的 JPEG/RAW，允许一次完整解码到受控本地临时 tile/pyramid 存储，再按需上传；这属于查看缓存，不是二期格式转换功能。必须申请完整解码工作集和临时磁盘预算，不能用“分块显示”掩盖全图解码 OOM。

保持源分辨率 tile；Fit 使用足够质量重采样，缩小时默认高质量线性/三次采样，100%像素映射不额外锐化，超过100%提供平滑/最近邻切换。采样模式需固定并测试，不照搬浏览器默认插值。

## 6. 色彩管理（SDR）

全流程写清 SourceEncoding → WorkingColorSpace → DisplayTransform → Compositor 的唯一转换位置。推荐保持源 ICC/位深直到工作空间转换，统一到可表述的高精度工作空间，再使用 Win2D ColorManagementEffect/Direct2D 进行最终显示变换；Win2D 提供显式颜色管理效果，不意味着自动读取并应用所有 ICC。[S13]

缩略图可归一化 sRGB 并保存此事实；显示时仍走正确目的空间，不把它再按原始 AdobeRGB 解读。无 ICC 默认按明确 sRGB政策，CMYK 无 profile 使用版本化兜底并提示，不能把 CMYK 四字节当 RGBA。

处理透明通道时确定 premultiplied/straight；ICC 变换必须在正确 alpha 语义下执行，测试半透明边缘，避免黑白边。高精度源至少在转换/缩放前不提前截断到8位；一期 SDR 最终输出允许8位，但不能宣称 HDR/10bit端到端已完成。

Windows 的高级颜色/自动色彩管理可能影响最终合成；M0/M3 必须验证避免应用和系统重复做显示 ICC。记录所测 OS、显示模式、显示器 profile 和渲染路径。HDR 开启时采用已验证的 SDR 合成策略，并标 SDR；不以截一张屏幕图作为实际色差计量证据。

ICC测试包含等视觉的 sRGB/AdobeRGB/P3图、损坏/缺失 profile、多屏profile变化；以受信任独立参考生成的数值/像素期望进行对照，不能用自己的输出给自己生成金样。

## 7. 动画与多页的有界实现

仅当前选中/沉浸查看图片允许活动动画，网格静态。FrameSession 在 worker 内保持解码和合成状态；按 disposal/blend/时间戳推进，渲染使用单调时钟，不把最短延时统一钳成100ms。提供播放/暂停、重播、循环信息；时间进度不要求无成本随机跳到任意帧。

不得对整张长动画一次 Coalesce 所有帧后持有全部 RGBA。只保留必要的前一合成帧/恢复帧、当前帧和少量预取；资源预算默认256MiB（软），超出走更小预览/受控磁盘帧缓存，并明确显示预览分辨率。真正查看某帧100%按单帧full路径，不将低清动画当原图。

动画暂停、切换文件、最小化且不可见时停止帧调度；关闭释放worker会话。损坏尾帧显示当前已解码内容+错误，不冻结主UI。多页TIFF按单页加载，页数未知可渐进，但不得一次加载全部页面。

## 8. 相邻预取与结果提交

当前图 P0；相邻方向两项Fit P1，反方向一项Fit P2；默认仅当前图申请真正RAW Full，下一张可在空闲且有预算时预取Full。快速按键（间隔<100ms）合并中间请求，只保证最后选中响应，不排长队。

UI先显示新文件的自有预览/占位，不能继续显示旧照片却让标题变成新文件。保留旧画面做过渡时必须有覆盖遮罩和“加载新图”状态，不能误导。后台旧结果做代次检查后丢弃并释放资源。

缓存命中不是省略fileVersion验证的理由。文件读取前后不一致，丢弃结果并最多重试一次；持续变化显示“文件正在变化”。GPU DeviceLost 重建纹理后按当前会话恢复，不清空目录/筛选。
