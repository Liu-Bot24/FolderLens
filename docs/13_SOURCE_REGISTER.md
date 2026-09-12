# 13｜官方来源与核查边界

核查日期：2026-09-12。来源只用于技术事实和接口约束；产品阈值、资源预算、里程碑、性能目标是本项目设计决定，不是上游保证。网页之后可能变化，Codex在M0保存实际版本/URL/检查结果。没有在本包中完成任何Windows编译或真实媒体性能验证。

## S01｜.NET 10 SDK下载

来源：https://dotnet.microsoft.com/en-us/download/dotnet/10.0

适用说明：核查SDK 10.0.401；候选版本仍须实际组合编译。

## S02｜Windows App SDK下载与频道

来源：https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads

适用说明：稳定2.4.0与实验频道分开；不把最新实验包当稳定。

## S03｜Windows App SDK自包含部署

来源：https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps

适用说明：WindowsAppSDKSelfContained和.NET自包含分别配置；原生文件目录式部署。

## S04｜SQLite WAL

来源：https://www.sqlite.org/wal.html

适用说明：读写并行、单写入者、网络文件系统限制、长读事务checkpoint影响。

## S05｜Microsoft.Data.Sqlite异步限制

来源：https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async

适用说明：ADO.NET异步方法同步执行；不能在UI上仅靠await避免阻塞。

## S06｜FileSystemWatcher

来源：https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0

适用说明：事件和缓冲溢出；须有重扫核对机制。

## S07｜SQLite Row Values

来源：https://sqlite.org/rowvalue.html

适用说明：滚动窗口/键集分页；OFFSET成本。

## S08｜Windows文件属性

来源：https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants

适用说明：OFFLINE、RECALL、SPARSE、REPARSE等不是同一语义。

## S09｜分配/压缩存储大小

来源：https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getcompressedfilesizew

适用说明：逻辑体积与存储占用区别；不能据此承诺删除释放量。

## S10｜NetVips与Magick.NET上游

来源：https://github.com/kleisauke/net-vips

适用说明：补充：https://github.com/dlemstra/Magick.NET；绑定和实际native构建分别检查。

## S11｜libvips线程

来源：https://www.libvips.org/API/8.16/using-threads.html

适用说明：内部线程需与外层并发一起预算；API文档版本不代表锁定依赖版本。

## S12｜LibRaw C API

来源：https://www.libraw.org/docs/API-C.html

适用说明：缩略图、原始解包、处理、内存释放接口；补充API-CXX.html。

## S13｜Win2D ColorManagementEffect

来源：https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Effects_ColorManagementEffect.htm

适用说明：色彩变换显式管理；必须显示端验证，不能自动推断HDR支持。

## S14｜Win2D CanvasVirtualBitmap

来源：https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasVirtualBitmap.htm

适用说明：按需加载受文件格式约束；不等于所有压缩格式随机块解码。

## S15｜WinUI ListView/GridView

来源：https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/listview-and-gridview

适用说明：虚拟化控件及交互；应用仍需数据分页。

## S16｜FFprobe

来源：https://ffmpeg.org/ffprobe.html

适用说明：媒体探测参数、输出选择；具体启动参数需实测锁定版本。

## S17｜Windows MediaPlayer

来源：https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/play-audio-and-video-with-mediaplayer

适用说明：原生音频播放通路，实际codec能力逐项验证。

## S18｜WebView2安全

来源：https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security

适用说明：导航、脚本、宿主暴露和不可信内容边界。

## S19｜ImageMagick Security Policy

来源：https://imagemagick.org/security-policy/

适用说明：显式coder/delegate和内存/磁盘/时间策略，不能照抄过小像素限制。

## S20｜WebView2发行

来源：https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution

适用说明：Evergreen与Fixed部署；补充concepts/evergreen-vs-fixed-version。

## S21｜FFmpeg许可

来源：https://ffmpeg.org/legal.html

适用说明：取决于实际构建和链接；不是本项目法律意见。

## S22｜Codex AGENTS.md官方规则

来源：https://developers.openai.com/codex/guides/agents-md

适用说明：核查时转至https://learn.chatgpt.com/docs/agent-configuration/agents-md；路径覆盖和默认32KiB限制，不把全部长规范塞根AGENTS。

## S23｜Markdig

来源：https://github.com/xoofx/markdig

适用说明：Markdown解析器及扩展；安全策略由宿主实施。

## S24｜Inno Setup实际许可证

来源：https://jrsoftware.org/files/is/license.txt

适用说明：允许包括商业用途，保留所需声明；实际采用版本重新核查并存档。

## S25｜FlaUI

来源：https://github.com/FlaUI/FlaUI

适用说明：Windows UI自动化候选，早期用实际WinUI窗口验证。

## S26｜Win2D概述

来源：https://learn.microsoft.com/en-us/windows/apps/develop/win2d/

适用说明：GPU加速二维图形接口；不是通用GPU图片解码承诺。

## 仍须M0/实机证明的事项

WinUI/Win2D/SDK/NuGet完整版本组合；实际HEIC/AVIF/JXL和动画能力；LibRaw对用户具体ARW的处理；显示端ICC与高级色彩不重复变换；PotPlayer当前版本的启动/播放列表行为；Native DLL与WebView离线部署；所有性能数字。上游说支持不等于本安装包已支持。
