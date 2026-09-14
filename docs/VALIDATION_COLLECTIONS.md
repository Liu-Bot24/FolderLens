# 文件收藏与路径显示验证记录

以下为历史验证记录。用户于 2026-09-14 随后明确要求改名、移动或确认删除后移除收藏；本文旧版“跟随重命名/保留已确认 missing”结论已废止。当前契约见 `adr/2026-09-14-file-collections.md`，本轮回归见 `VALIDATION_PRO_FEEDBACK.md`。

本记录对应 2026-09-14 的本地开发修改，以 d6dc592 为可回退基线。它不代表整体一期 GO，也不代替真实前台操作、完整格式矩阵、长稳或干净机器验收。

## 改动与反证

- 详情路径列和底部卡片路径选项原来独立，造成开关无作用。新增断言在 `artifacts/collections-path-red-actual` 失败，修复后 `collections-path-green` 通过；详情同步列状态并禁用，网格保留独立偏好。
- 收藏持久化、跨根来源、同名路径定位、多归属、类型分类、标签包含/排除和已知失效引用均有覆盖。普通目录的范围条件继续适用，不会因收藏标签变成全盘搜索。
- 独立代码审查发现批量选择展开全部行、重叠根代表项和重命名归属、收藏范围目录规则三个问题。改为保留会话区间的数据库事务、按旧位置归属迁移、实际成员来源目录求值；复审确认这些发现闭合。
- 正例：相同位置从父/子目录建索引仍共享标签，重命名后排除继续生效；反例：未选中成员、无标签文件及不匹配的目录规则不会被误收藏或误排除。PPT/PDF 可解析外部打开目标，EXE/CMD 保持禁止；没有在测试中启动 Office。

## 已执行

| 检查 | 结果与证据 |
|---|---|
| Release 构建 | 0 错误；7 个 NU1900 表示漏洞数据源不可达，不能算漏洞检查通过。`artifacts/resume-baseline/collections-popup-safe-build.log` |
| 全量单元测试 | 275 通过、0 失败、0 跳过。`artifacts/tests/collections-final/collections-final.trx` |
| 原生回归批次 | 32 项中 31 通过、1 失败，原始汇总保留：`artifacts/collections-final-regression/summary.json` |
| 收藏原生场景 | 跨根图片/文本、PPT 分类和目标解析、同名定位、刷新、元数据补齐、标签排除、失效引用通过。实际收藏面板内容离屏渲染已检查，无截断。该批次 `collections/native-refresh.json` |
| 严格启动 | 开发输出的默认浏览、扫描管线、视口保持、文本阅读四项通过。`artifacts/collections-final-startup*` |

首次全量测试的 7 个失败保留在 `artifacts/tests/collections-all`：4 个旧迁移 fixture 仅降版本号却保留新列，修正为真实旧表；3 个普通目录播放列表根路径语义被改坏，恢复普通调用方根参数。相关 12 项和最终全量复验通过。

## 未通过项的边界

原生批次的 viewer-information 在离屏真实弹出右键菜单时失败。此方式可能让 Windows 把弹窗放到前台，因此未重复弹窗，也未把失败改成通过。改后的单独复验 `artifacts/collections-popup-safe/native-refresh.json` 验证真实 EXIF 显示及菜单提示生命周期处理函数，结果通过，但显式记录 `foregroundPopupInteraction: NOT_RUN`。前台弹窗与路径提示覆盖关系仍需真实交互验证。

收藏对话框内容、业务路径和布局已经验证；不宣称已完成所有前台鼠标交互。已知 Draw 434.9 ms 异常、Present、完整长稳、硬件及干净机器检查继续按此前性能/验收记录跟进。
