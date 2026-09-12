# 需求、任务与验收账本

本包共64条一期MUST需求、128条强制验收用例、40项依赖任务和25组格式/内容能力。这里的数量是文档条目数，不是已实现功能数或已通过测试数。所有初始执行状态都为未开始/未运行。

requirements.csv：范围和需求主编号。acceptance.csv：每条需求至少正常路径与反例，后续可以增补不可删除降低。tasks.json：依赖有向无环图和可更新任务状态。format-matrix.csv：按实际样本和发行二进制报告，不使用扩展名列表假装支持。

状态更新同时写implementation/PROGRESS.md/HANDOFF.md和证据路径。DONE只能在所属门禁通过后赋值；跨阶段总体验收仍需M7/M8复跑。仅没有直接owner需求的集成任务也必须完成其deliverable和上下游实际集成，不允许跳过T010/T039等关键任务。

CSV为UTF-8 BOM，方便Windows阅读；数字/状态是计划数据，不是财务表或运行成绩。格式Provider和version刻意写RESOLVE_IN_M0/NOT_LOCKED，要求根据实际安装兼容性固定，不填猜测包版本。模板里的null/NOT_RUN不是零性能或成功。
