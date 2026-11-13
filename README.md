# Online-SPC · 制造质量数字化闭环系统

按《在线 Online-SPC 系统技术方案 V1.0》落地，并参考
[liyonghuan/online-statistical-process-control-system](https://github.com/liyonghuan/online-statistical-process-control-system)
的领域建模与图型语义重建内核。

> **当前进度：M0 → M5 全部完成。**
> 已实现「量测写入即聚合 → 冻结限判异 → Rule Out → OCAP + MES LOT/EQP Hold → 回执回写 →
> 人工签核 → 验证子组复判 → CLOSED」全链路，并在 M5 补齐能力报表、
> 日周月周期报表、定时推送、CSV 导出与体系审核取证包。
> 控制限全程只读来自人工审批发布的版本，无生效版本即拒绝判异；过程未受控时拒绝输出 Cpk。
> 方案 A-01 ~ A-08 八项验收标准全部有测试与实测支撑。

---

## 1. 快速开始

环境：.NET 10 SDK（本机 10.0.402）、Node 20+。

```powershell
# 1) 后端：自动建库（SQLite）、执行 DDL、平滑迁移、植入演示控制图
dotnet build SpcOnline.slnx
dotnet run --project src/Spc.WebApi/Spc.WebApi.csproj
#   监听 http://localhost:5080 ，API 清单见 http://localhost:5080/api/meta

# 2) 前端（另一个终端）
cd web
npm install
npm run dev
#   打开 http://localhost:5173

# 3) 验证（57 个用例：算法单测 + M2/M3/M4/M5 真实 SQLite 集成测试）
dotnet test SpcOnline.slnx
```

首次启动会在仓库根目录生成 `spc.db`（已 gitignore）。老库无需删除，M2 的 `CHART_TYPE` 快照列自动迁移、M5 的报表快照表自动创建。
切换生产环境：把 `src/Spc.WebApi/appsettings.json` 的 `Spc:Provider` 改为 `Oracle` 并换 Oracle 连接串（自动执行 `db/ddl_oracle.sql`）；
`Spc:MesGateway:Mode` 改为 `Http` 并设置 `HoldUrl` 即切到真实 MES；`Spc:Reports:IntervalMinutes` 控制定时报表周期。

## 2. 十分钟演示（完整闭环 + 报表取证）

1. 「基线管理」→ **生成 Phase I 候选数据**（25 子组 × n=5）。
2. **① 试算预检** → **② 执行冻结 → PENDING**（系统绝不自动激活）→ 右侧 **审批发布**（生效时刻可填过去 1 分钟，便于后续数据落入生效区间）。
3. 「数据源 / 演练」启动 `drift / trend / oscillation / spike`，或直接 `POST /api/spc/measure/ingest` 投几个明显超 UCL 的点。
4. 「实时监控」判异点标红放大、点击下钻；「告警中心」按 CRITICAL / MAJOR / WARNING 分级展示。
5. 「处置闭环」自动开 OCAP 单并下发 MES Hold → **模拟 MES 解冻 + 全部回执** → **提交签核** →
   采集 corrective action 后的稳定数据 → **取最新子组 → 复判并关闭**（受控 `CLOSED`，仍失控 `ESCALATED`）。
6. 「报表中心」查看能力直方图（**规格线只画在这里**）、Cp/Cpk/Pp/Ppk、日周月周期报表、合规自检清单、
   报表快照与 SHA256 指纹、定时推送状态，并可导出四类 CSV。
7. 「处置闭环」右下「A-08 全链路审计追溯」或 `GET /api/spc/audit/trace?ruleOutId=` 查看六问答案与缺失环节。

```powershell
Invoke-RestMethod http://localhost:5080/api/meta
Invoke-RestMethod "http://localhost:5080/api/spc/reports/compliance?chartId=1"
Invoke-RestMethod "http://localhost:5080/api/spc/reports/schedule"
Invoke-RestMethod "http://localhost:5080/api/spc/audit/trace?ruleOutId=1"
```

## 3. 目录结构

```
SpcOnline.slnx
src/
  Spc.Domain/         POCO 实体 + 整数枚举 + 状态机（零第三方依赖）
  Spc.Algorithm/      AIAG 常数表 / 控制限 / 8 判异规则 / Cp·Cpk·Pp·Ppk /
                      Phase I 稳定性迭代 / SHA256 指纹 / 直方图分箱（纯算法库）
  Spc.Application/    用例 + 端口：BaselineFreeze/Publish、FrozenLimitReader、CapabilityService、
                      EvaluationService、RuleSetCatalog、DispositionService、OocActionPolicy、
                      ReportService、IMesGateway、IDispositionTrigger
  Spc.Infrastructure/ SqlSugar 双 Provider、DbNaming 映射、DDL 初始化与迁移、
                      有序队列、仓储（进程级 DB 闸门串行化）
  Spc.WebApi/         Controllers、SignalR Hub、子组聚合/迟到重扫/报表调度 Worker、
                      MES 模拟器与 HTTP 适配器、数据源模拟器
tests/Spc.Tests/      57 个 xUnit 用例（算法 29 + M2 6 + M3 7 + M4 8 + M5 7）
db/ddl_sqlite.sql     开发态 DDL（10 张表 + 唯一 ACTIVE 部分索引）
db/ddl_oracle.sql     生产态 DDL（含 DECODE 函数唯一索引）
web/                  Vue 3 + Vite + ECharts + SignalR 看板（7 个页面）
.reference_spc/       参考项目只读源码（已 gitignore）
```

## 4. 关键设计（对照方案）

| 方案要求 | 落地情况 |
|---|---|
| 4.2 / A-01 常数表与公式 | `AiagConstants`（n=2~10 全表）、`LimitCalculator` 支持 Xbar-R / Xbar-S / I-MR；E2 由 `3/d₂(n=2)` 推导 |
| 4.3 / A-05 八条判异 | `RuleEngine` 输入标准化 z；Phase I 仅 R1~R4，Phase II 按图配置 `WECO-8-V1 / WECO-4-V1 / WECO-8-STRICT` |
| 4.4 / 4.5 规格限与控制限分离 | `USL/LSL` 只存 `SPC_CHART`；控制限只存 `SPC_LIMIT_VERSION`；**规格线只画在能力直方图**；`Cp/Cpk` 用冻结 σ̂ |
| 5.2 / 5.4 Phase I 六步 | 预检 + 迭代（≥20 组、最多 10 轮、仅 R1~R4、无原因点拒绝）+ 剔除台账 + SHA256 指纹 |
| 5.5 / 5.6 版本与换版 | 冻结快照全字段 + `CHART_TYPE`；`1→2→3→4` 状态机；发布事务仅改生命周期字段；发布闸门 + 唯一索引双保险 |
| 5.7 Phase II 只读契约 | `FrozenLimitReader` 只查版本表，**绝不调用 `LimitCalculator`**；无生效版本直接拒绝判异并留审计 |
| 6.2 / 6.3 Rule Out 与动作策略 | 主图/变异图分别评估，事件带 `LIMIT_VER_ID / RULE_CODE / SEVERITY / Z_VALUE`；`OocActionPolicy` 决定 LOT/EQP Hold、停机、通知 |
| 6.3 / A-07 处置闭环 | OCAP → MES Hold → 回执 → 签核 → 验证 → CLOSED；全部动作收齐回执才允许签核；验证失败升级 `ESCALATED` |
| 6.4 / A-08 审计链 | `/api/spc/audit/trace` 回答六问并显式列出缺失环节 |
| 6.1 / N-01 / N-02 / A-06 实时与顺序 | 按 `ChartId` 分区串行 + 按 `EVENT_TIME` 重取 + 迟到水位线 + 每 5 秒重扫；子组闭合即同步判异推送 |
| 9 M5 报表与合规 | 能力直方图与 Cp/Cpk、日周月周期报表（**按周期结束时刻回放当时冻结限**）、带 SHA256 指纹的报表快照、定时 SignalR 推送、四类 CSV 导出、9 项合规自检清单 |
| 附录 A 过程受控门禁 | 近 24 小时存在**未闭环** Rule Out 时判定过程不稳定，拒绝输出 Cp/Cpk；未完成处置前合规包 `ready=false` |
| 8.2 接口 | `/measure/ingest`、`/baseline/freeze`、`/baseline/publish`、`/evaluate`、`/ruleout/callback`、`/chart/query` 全部实现 |

## 5. 验收状态

| 编号 | 判定 | 状态 |
|---|---|---|
| A-01 | 控制限与 AIAG 常数手算一致（误差 < 0.0001） | ✅ 单测通过（含方案 13.2 的 CL=9.9981 / σ̂=0.0980 复现） |
| A-02 | 同一数据集重算得到相同哈希与控制限 | ✅ 算法单测 + 集成测试 + `/baseline/verify-hash` + 报表快照指纹复核 |
| A-03 | 含无原因异常点的数据集必须拒绝建限 | ✅ 单测通过 |
| A-04 | 并发发布下仍只有一个 ACTIVE 版本 | ✅ 并发集成测试 + 唯一索引拒绝双 ACTIVE + 顺序换版 |
| A-05 | 八条规则构造用例全部命中预期规则号 | ✅ 单测通过 |
| A-06 | 子组完成到告警推送 ≤ 1 分钟 | ✅ 同步判异 + SignalR，集成测试断言 < 5 秒 |
| A-07 | 每条 Rule Out 均有 OCAP 状态与 MES 回执 | ✅ 自动开单、Hold 下发、回执门禁、签核、验证关闭全链路集成测试 |
| A-08 | 可查询限值版本、审批人、命中规则、动作、处置人、结论 | ✅ `/audit/trace` 六问齐备，端到端实测 `complete=True / missing=0` |

实测（2026-11-13）：`dotnet build` **0 警告 0 错误**；`dotnet test` **57/57 通过**；`npm run build` 成功。
M4 闭环冒烟：判异 → OCAP → 3 条 MES 动作 → 3 条回执 → 签核 → 验证 `CLOSED` → 审计 `complete=True / missing=0 / 17 事件`。
M5 报表冒烟：冻结 V1 → 发布 → 能力直方图 `samples=125 / bins=20` → 周期报表 7 期 →
合规自检 **9/9 PASS** → 快照指纹 `dfab932e…`（8667 字节）→ 定时任务 → 四类 CSV 均有内容 →
注入未闭环判异后能力指数立即变为 `available=False`、合规包 `ready=False / failed=1`（受控门禁生效）。

> 迭代中修复的四个真实缺陷：
> ① M2：`SqlSugarScope` 共享事务上下文在并发发布时串扰，现由发布闸门串行化；
> ② M3：迟到水位线拦截的「未来事件时间」点可能永不闭组，现由 `PendingSweepWorker` 周期重扫；
> ③ M4：回执必须收齐全部动作才允许签核，验证子组必须晚于判异且用当时冻结限复判；
> ④ M5：三个后台 Worker 并发访问 `SqlSugarScope` 导致 SQLite DataReader / 实体映射串扰
> （`SpcChart UpdatedAt bind error`）并拖垮 Host —— 现于仓储层用进程级闸门串行化所有 DB 访问，
> SQLite 禁用连接池，后台任务异常配置为 `Ignore`，测试程序集禁用并行以隔离 SqlSugar 静态缓存。

## 6. 与参考项目的取舍

| 继承 | 替换（工业级闭环缺口） |
|---|---|
| 产品/工序/设备/车间/员工/量测建模语义、Xbar-R / Xmedian-R / I-MR 图型、Cpk 分级、失控→受理→处理业务语义 | 控制限每次画图由全量数据现算 → 冻结版本 + 人工审批 + 只读取限契约 |
| 8 条判异规则雏形、常数表思路、备注与导出 | 用工序规格限判「失控」→ 规格限/控制限物理分表 |
| WinForms 交互原型 | 规则只输出图表文字 → Rule Out 事件 + 分级告警 + 下钻 |
| | 人工点按钮改状态、无审批无版本无回执 → OCAP 状态机 + MES Hold + 回执回写 + 验证关闭 |
| Excel 导出思路 | CSV 四类导出 + 带指纹报表快照 + 定时推送 + 合规自检取证包 |
| | Cpk 用整体样本标准差、常数表有错值（`n=5` 的 d2=9.326）、明文口令与拼接 SQL → 全部修正 |

## 7. 后续阶段（M6 扩展能力）

- 产线试点与复审换版演练、Oracle 分区表与性能压测。
- p / np / c / u 计数型控制图与 EWMA / CUSUM 小漂移检测扩展。
- MSA / GRR 量测系统分析、OCAP 签核工作流与 QMS 对接。

> 说明：方案 §13 提到的 `spc/ddl_oracle.sql` 与 `SpcBaselineService.cs` 未随附件提供，本仓库按文档规格重新实现。
