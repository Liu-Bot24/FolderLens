# 扫描调度修复验证

## 已运行

- `ScanSchedulingTests.LateSiblingReceivesFilesBeforeLargeFirstBranchFinishes`：原代码失败，后置小分支首文件出现于第 31 个文件；修改后通过前 5 个文件内出现的断言，总文件数仍为 31。
- 动态优先范围与持续优先范围两种情况均不饿死其他分支；越界优先路径被拒绝。旧深度优先专用测试改为验证新的同级优先要求，总扫描集合不变。
- 应用持有扫描的取消、并发槽和退出等待；同一路径复用 epoch 仍做物理身份核验，目录被替换时建立新根。
- SQL 按目录聚合与原文件流汇总一致，包括未知占用、缺失文件排除；冻结结果容量保持原合同。
- Release 单元测试 355 项通过。最后增加临时分支排序索引后，39 项相关测试通过；最终 WinUI Release 编译 0 错误，7 项 NU1900（包漏洞源不可达）警告。

日志：`artifacts/scan-scheduling-red.log`、`scan-scheduling-final.log`、`scan-scheduling-targeted-final.log`、`scan-scheduling-build-final.log`，TRX 在 `artifacts/tests/scan-scheduling/`。这些本地产物不随产品发布。

## 未运行

用户禁止再次启动原生测试窗口，故真实 WinUI 导航、实际大目录耗时与整机资源占用复验均未运行。没有复制真实索引，也没有新建全库备份。不能依据合成用例宣称实机提速百分比、达到 MFT 工具速度或正式发布 GO。
