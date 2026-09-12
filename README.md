<div align="center">

# FolderLens

[![Stars](https://img.shields.io/github/stars/Liu-Bot24/FolderLens?style=flat&label=Stars)](https://github.com/Liu-Bot24/FolderLens/stargazers) [![Forks](https://img.shields.io/github/forks/Liu-Bot24/FolderLens?style=flat&label=Forks)](https://github.com/Liu-Bot24/FolderLens/forks) ![Views 14d](https://github-stats.liu-qi.cn/api/badge/Liu-Bot24/FolderLens/views14d.svg?v=4) ![Clones 14d](https://github-stats.liu-qi.cn/api/badge/Liu-Bot24/FolderLens/clones14d.svg?v=4) [![Downloads](https://img.shields.io/github/downloads/Liu-Bot24/FolderLens/total?style=flat&label=Downloads)](https://github.com/Liu-Bot24/FolderLens/releases) [![Release](https://img.shields.io/github/v/release/Liu-Bot24/FolderLens?style=flat&label=Release)](https://github.com/Liu-Bot24/FolderLens/releases)

![FolderLens：多层文件夹，一处浏览；图片、视频、音频与文本的浏览及预览示意](docs/assets/folderlens-hero.svg)

</div>

**在一个视图中浏览文件夹及其子目录，保留文件原有的位置。**

FolderLens 是一款面向 Windows 的文件浏览工具，适合查看分散在多层文件夹中的照片、视频、音频和文本。选择一个文件夹，即可汇总浏览其中的文件，按类型和属性筛选，并在列表与预览之间切换，无需先搬动或导入文件。

目前为 **Windows 11 x64 预览版**。

## 主要功能

- **跨目录浏览**：汇总当前文件夹及子目录的文件，支持缩略图网格、详细列表、文件夹分组和浏览层级设置。
- **筛选与排序**：按文件名搜索，结合类型、扩展名、尺寸、体积、日期等条件缩小范围；常用筛选和浏览位置可保存为视图。
- **图片查看**：适应窗口、实际像素、缩放、平移、全屏、连续切换和幻灯片播放；支持只读旋转及按住鼠标临时放大。
- **视频与音频**：视频封面预览、内置播放和外部播放器打开；音频支持试听、进度控制、音量和倍速。
- **文本阅读**：纯文本自动换行、搜索和按行定位；Markdown 排版阅读及本地图片按需加载。
- **收藏与文件管理**：收藏原文件位置，支持多选、复制、剪切、粘贴、移动、删除、单文件重命名，以及与资源管理器之间的拖放。

## 开始使用

### 系统要求

- Windows 11，x64 处理器。
- 将程序放在本机可写目录中，并保留程序包内的全部文件和子目录。
- 运行所需组件已包含在程序包内。

### 启动程序

**便携版**：完整解压程序包，打开 `app` 文件夹，运行 `FolderLens.App.exe`。不要直接在压缩包内运行，也不要只复制 EXE 文件。

**安装版**：运行安装程序，按提示完成安装，再从开始菜单打开 FolderLens。默认安装位置为 `%LOCALAPPDATA%\Programs\FolderLens`。

仓库的源码压缩包不是可直接运行的程序包。当前预览包未进行代码签名，Windows 可能显示未知发布者提示；请先确认文件来源。

### 第一次浏览

1. 点击 **打开文件夹**，或在地址栏输入路径后按 Enter。
2. 文件会随着扫描逐步显示，可用顶部分类和搜索框缩小范围。
3. 选中文件，在左侧查看预览；图片可进入窗口预览或全屏预览。
4. 通过目录树、上一级或返回按钮切换位置。

切换文件夹时，FolderLens 会停止上一范围的扫描，改为扫描新文件夹及其子目录。顶部的 **查看层级** 控制列表展示的深度；按文件夹分组可帮助辨认文件来自哪里。

## 文件支持

| 文件类型 | 使用方式 |
| --- | --- |
| 常见图片 | JPEG、PNG、BMP、GIF、WebP、TIFF 等格式的缩略图和预览；动画及多页图片提供相应播放或翻页控制 |
| HEIC、AVIF、JPEG XL | 提供图片解码支持，具体编码变体的兼容性可能不同 |
| 相机 RAW | 提供内嵌预览与 RAW 解码；可读性取决于相机型号和文件格式，内嵌预览不一定具有原图分辨率 |
| 视频 | 显示封面，可内置播放或交给外部播放器；内置播放能力取决于系统解码器 |
| 音频 | 支持系统可解码格式的单文件试听，也可使用外部播放器 |
| 文本与 Markdown | 内置阅读、文本搜索；Markdown 支持本地图片及兼容格式的本地视频引用 |
| PDF、Office 文档、压缩文件 | 分类浏览、查看文件属性和外部打开；暂不提供内部内容预览或压缩包内浏览 |

视频列表双击默认使用外部播放器，可在 **视频播放器** 设置中改为内置播放。内置播放器无法打开某个文件时，可选择其他播放器；支持播放列表的外部播放器还可按当前浏览顺序播放多个文件。

FolderLens 专注于文件浏览与预览，暂不支持图片编辑、格式转换或批量重命名。

## 常用操作

| 操作 | 快捷键或手势 |
| --- | --- |
| 打开文件夹 | Ctrl + O |
| 聚焦地址栏 | Ctrl + L |
| 刷新当前文件夹 | F5 |
| 返回 / 前进 / 上一级 | Alt + ← / Alt + → / Alt + ↑ |
| 全屏预览 | F11 |
| 返回文件列表 | 预览中按 Esc |
| 图片缩放 | Ctrl + 滚轮 |
| 图片上下平移 | Alt + 滚轮 |
| 临时放大图片 | 按住鼠标左键，松开还原；默认倍率为 250%，可在设置中调整 |
| 多选文件 | Ctrl / Shift + 单击，或在列表空白处拖动框选 |
| 复制 / 剪切 / 粘贴文件 | 文件列表中按 Ctrl + C / Ctrl + X / Ctrl + V |
| 复制文件路径 | Ctrl + Shift + C |

左侧目录树和预览区之间的分隔条可上下拖动，双击可恢复默认比例。

### 文件操作与收藏

浏览、预览和只读旋转不会改写原文件。**移动、删除和重命名会改变磁盘上的文件**，由 Windows 处理操作过程与冲突提示。

拖入文件时，默认同盘移动、跨盘复制；按住 Ctrl 强制复制，按住 Shift 强制移动。删除采用 Windows 的回收站流程，是否能放入回收站取决于文件所在位置和系统设置，请留意系统提示。

收藏保存的是原文件位置，不会复制文件。原位置被重命名、移动或删除后，确认该位置失效时会移除对应收藏；不会自动追踪到新位置。

## 数据与隐私

FolderLens 在本机保存设置、收藏、文件目录信息和缩略图缓存，用于浏览、筛选和恢复视图。它不会自动上传源文件或这些本地数据，也不会自动联网更新。

| 使用方式 | 默认数据位置 |
| --- | --- |
| 安装版 | `%LOCALAPPDATA%\FolderLens` |
| 便携版 | `FolderLens.App.exe` 所在目录下的 `data` 文件夹 |

备份收藏和设置时，请先退出程序，再备份对应的数据目录。向他人分享便携程序时，请使用原始分发包，避免将自己的 `data` 文件夹一并分享。

Markdown 不会自动加载网络资源；本地图片和视频必须位于当前打开的文件夹范围内。云盘中尚未下载到本机的文件，需要确认后才能读取。

## 使用帮助与反馈

浏览、预览和更新相关问题，请参阅[使用帮助与已知问题](docs/HELP.md)。

如需报告问题或提出建议，欢迎提交 [Issue](https://github.com/Liu-Bot24/FolderLens/issues)。描述遇到的情况、操作步骤和程序版本即可；附上截图时，请遮盖私人内容。

## 第三方声明

第三方组件的版权与许可信息见程序包中的 `THIRD-PARTY-NOTICES.md` 和 `licenses` 文件夹。
