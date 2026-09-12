# 一期候选发布报告模板

构建版本/源码精确commit/构建日期/脏工作区说明：
总体判定：NO-GO（模板初始状态，所有检查未执行）。
应用状态与文档状态分开；不把规范已写完当产品已完成。

## 覆盖
P1需求：已验收数/64；未验收ID：
验收用例：PASS/FAIL/NOT_RUN/BLOCKED/SKIP分别计数，基线128条；新增用例另列：
格式矩阵：每行Provider版本/样本/能力/失败：
BLOCKER/MAJOR/NON-BLOCKING及复现：

## 实际验证平台
Windows/CPU/GPU/RAM/磁盘/DPI/ICC：
参考机、低资源机、清洁离线机分别写：
真实Sony ARW和各格式样本范围：
不能验证的项及原因：

## 性能
逐场景样本数、P50/P95/P99/max、冷热定义、原始数据路径：
目标与实际对比；硬门禁失败单列：

## 打包
portable ZIP路径/大小/SHA256/实际启动证据：
setup EXE路径/大小/SHA256/安装升级卸载证据：
完整native/WinAppSDK/.NET/WebView/CRT清单：
签名：unsigned或实际证书信息，不能猜测：
许可证/SBOM/源码义务核对：

## 安全与数据
原件内容哈希/mtime和写入观察：
网络/日志隐私检查：
缓存、设置和迁移恢复：

## 交付
源码/锁/脚本/手册/格式报告/测试TRX/性能CSV/NOTICE/SBOM/哈希位置：
剩余问题的用户影响、最小修复顺序和需要重新跑的门禁：
