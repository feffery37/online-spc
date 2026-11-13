<script setup>
import { ref, onMounted, watch, computed } from 'vue'
import { SPC } from '../api'
import { store, log, severityText } from '../store'

const chartId = computed(() => store.selectedChartId)
const items = ref([])
const alerts = ref(null)
const filter = ref('all')
const detail = ref(null)
const replayCount = ref(20)
const busy = ref(false)
const message = ref('')

const filtered = computed(() => filter.value === 'all'
  ? items.value
  : items.value.filter(r => severityClass(r.severity) === filter.value))

function severityClass(s) {
  return s >= 4 ? 'critical' : s === 3 ? 'major' : s <= 2 ? 'warning' : 'info'
}

async function load() {
  if (!chartId.value) return
  try {
    const [r, a] = await Promise.all([SPC.ruleOuts(chartId.value, 200), SPC.alerts(chartId.value)])
    items.value = r.items ?? []
    alerts.value = a
  } catch (e) { log(`读取告警失败：${e.message}`, 'warn') }
}

async function openDetail(r) {
  try { detail.value = await SPC.ruleOutDetail(r.id) }
  catch (e) { log(`下钻失败：${e.message}`, 'warn') }
}

async function replay() {
  busy.value = true
  try {
    const res = await SPC.evaluate({ chartId: chartId.value, replayCount: Number(replayCount.value) })
    const results = res.results ?? []
    const evaluated = results.filter(x => x.evaluated).length
    const hits = results.reduce((n, x) => n + (x.ruleOuts?.length ?? 0), 0)
    message.value = `重放 ${results.length} 个子组：已判异 ${evaluated}，新增 Rule Out ${hits}`
    log(message.value, 'ok')
    await load()
  } catch (e) { message.value = e.message; log(e.message, 'warn') }
  finally { busy.value = false }
}

onMounted(load)
watch(chartId, () => { items.value = []; detail.value = null; load() })
</script>

<template>
  <div class="note info">
    <b>Phase II 实时判异：</b>子组闭合即以「当时生效的冻结限值版本」把主图与变异图标准化为 z，
    按图配置的 WECO / Nelson 规则集评估，命中即生成 <b>Rule Out</b>（携带 <code>LIMIT_VER_ID</code>）
    并经 SignalR 分级推送。无生效版本时<b>直接拒绝判异</b>，绝不现算控制限。
    处置动作（LOT / EQP Hold、OCAP 回执）在 M4 接入。
  </div>

  <div class="grid cols-4" style="margin-bottom:16px">
    <div class="card kpi">
      <div class="label">CRITICAL</div>
      <div class="value" style="color:var(--red)">{{ alerts?.critical ?? 0 }}</div>
      <div class="sub">R1 或变异图高等级命中 → M4 LOT Hold</div>
    </div>
    <div class="card kpi">
      <div class="label">MAJOR</div>
      <div class="value" style="color:#fb923c">{{ alerts?.major ?? 0 }}</div>
      <div class="sub">连续类规则：R2 / R3 / R8</div>
    </div>
    <div class="card kpi">
      <div class="label">WARNING</div>
      <div class="value" style="color:var(--amber)">{{ alerts?.warning ?? 0 }}</div>
      <div class="sub">R4 / R5 / R6 / R7</div>
    </div>
    <div class="card kpi">
      <div class="label">Rule Out 总数 / 实时推送</div>
      <div class="value" style="color:var(--cyan)">{{ alerts?.total ?? 0 }} <span style="font-size:14px">/ {{ store.ruleOutEvents }}</span></div>
      <div class="sub">{{ alerts?.contract ?? '取限契约加载中' }}</div>
    </div>
  </div>

  <div class="grid cols-3-1">
    <div class="card">
      <div class="toolbar">
        <h3 style="margin:0">Rule Out 事件台账</h3>
        <select v-model="filter" style="margin-left:auto">
          <option value="all">全部级别</option>
          <option value="critical">仅 CRITICAL</option>
          <option value="major">仅 MAJOR</option>
          <option value="warning">仅 WARNING</option>
        </select>
        <button class="btn ghost" @click="load">刷新</button>
      </div>
      <table>
        <thead><tr><th>时间</th><th>版本 ID</th><th>子组 ID</th><th>规则</th><th>级别</th><th>z</th><th>统计量</th><th>处置状态</th><th /></tr></thead>
        <tbody>
          <tr v-for="r in filtered" :key="r.id">
            <td>{{ new Date(r.eventTime).toLocaleTimeString('zh-CN', { hour12: false }) }}</td>
            <td><span class="pill active">{{ r.limitVerId }}</span></td>
            <td>{{ r.subgroupId }}</td>
            <td><b>R{{ r.ruleCode }}</b></td>
            <td :class="'sev-' + severityClass(r.severity)">{{ severityText(r.severity) }}</td>
            <td>{{ Number(r.zValue).toFixed(2) }}</td>
            <td>{{ Number(r.statValue).toFixed(4) }}</td>
            <td><span class="pill pending">{{ r.ocapStatus === 1 ? 'DETECTED' : r.ocapStatus }}</span></td>
            <td><button class="btn ghost" style="padding:4px 9px" @click="openDetail(r)">下钻</button></td>
          </tr>
        </tbody>
      </table>
      <div v-if="!filtered.length" class="hint" style="padding:16px 0">
        暂无 Rule Out。确认已审批发布冻结版本，并启动模拟器产生<b>版本生效之后</b>的新数据；
        也可用右侧「历史重放」对未评估子组补判。
      </div>
    </div>

    <div class="card">
      <h3>手动判异 / 历史重放</h3>
      <p class="hint">
        规则引擎只依赖 z 序列与规则集，可在<b>不重算控制限</b>的前提下更换规则集并重放历史事件（方案 3.1）。
        重复评估同一子组不会产生重复告警。
      </p>
      <div class="toolbar" style="margin-top:12px">
        <label>子组数</label>
        <input v-model.number="replayCount" type="number" min="1" max="200" style="width:90px" />
        <button class="btn" :disabled="busy" @click="replay">执行重放</button>
      </div>
      <p v-if="message" class="hint" style="color:var(--green)">{{ message }}</p>

      <h3 style="margin-top:20px">当前生效版本</h3>
      <div v-if="alerts?.activeVersion" class="code-block">
        V{{ alerts.activeVersion.versionNo }} · ID={{ alerts.activeVersion.id }}<br />
        HASH {{ alerts.activeVersion.dataHash }}
      </div>
      <p v-else class="hint">无 ACTIVE 版本：判异会被拒绝（只读契约）。请到「基线管理」审批发布。</p>

      <h3 style="margin-top:20px">审计链</h3>
      <p class="hint">
        SPC_MEASURE → SPC_SUBGROUP → SPC_LIMIT_VERSION（哈希 / 审批人）→
        SPC_RULE_OUT（LIMIT_VER_ID / RULE_CODE / SEVERITY）→ SPC_AUDIT_LOG。
        M4 补齐处置动作与回执后即可回答「谁处置、结论是什么」。
      </p>
    </div>
  </div>

  <div v-if="detail" class="card" style="margin-top:16px">
    <div class="toolbar">
      <h3 style="margin:0">判异点下钻 · {{ detail.ruleCode }} / {{ detail.severityLabel }}</h3>
      <span class="badge" style="margin-left:auto">{{ detail.ocapStatus }}</span>
      <button class="btn ghost" @click="detail = null">关闭</button>
    </div>
    <div class="grid cols-2">
      <div>
        <h3>判定依据</h3>
        <div class="detail-row"><span>命中规则</span><span>{{ detail.ruleCode }} · {{ detail.description }}</span></div>
        <div class="detail-row"><span>标准化 z</span><span>{{ Number(detail.ruleOut.zValue).toFixed(4) }}</span></div>
        <div class="detail-row"><span>描点统计量</span><span>{{ Number(detail.ruleOut.statValue).toFixed(4) }}</span></div>
        <div class="detail-row"><span>规则集</span><span>{{ detail.ruleSet?.version }} · {{ detail.ruleSet?.rules?.join(', ') }}</span></div>
        <div class="detail-row"><span>处置状态</span><span>{{ detail.ocapStatus }}（M4 推进到 CLOSED）</span></div>
        <div class="detail-row"><span>批次 / SN</span><span>{{ detail.ruleOut.lotId || '—' }} / {{ detail.ruleOut.sn || '—' }}</span></div>

        <h3 style="margin-top:18px">原始量测值（含工艺参数标签）</h3>
        <table>
          <thead><tr><th>时间</th><th>值</th><th>SN</th><th>PROC_TAG</th></tr></thead>
          <tbody>
            <tr v-for="m in detail.measures" :key="m.id">
              <td>{{ new Date(m.eventTime).toLocaleTimeString('zh-CN', { hour12: false }) }}</td>
              <td>{{ Number(m.measureValue).toFixed(4) }}</td>
              <td>{{ m.sn }}</td>
              <td class="hint">{{ m.procTag }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <div>
        <h3>限值版本（审计关键）</h3>
        <div v-if="detail.limitVersion">
          <div class="detail-row"><span>版本</span><span>V{{ detail.limitVersion.versionNo }} · ID {{ detail.limitVersion.id }}</span></div>
          <div class="detail-row"><span>CL / UCL / LCL</span><span>{{ Number(detail.limitVersion.cl).toFixed(4) }} / {{ Number(detail.limitVersion.ucl).toFixed(4) }} / {{ Number(detail.limitVersion.lcl).toFixed(4) }}</span></div>
          <div class="detail-row"><span>σ̂</span><span>{{ Number(detail.limitVersion.sigmaHat).toFixed(5) }}</span></div>
          <div class="detail-row"><span>审批人</span><span>{{ detail.limitVersion.approvedBy }}</span></div>
          <div class="detail-row"><span>生效 / 失效</span><span>{{ detail.limitVersion.effectiveFrom }} / {{ detail.limitVersion.expiredTime || '—' }}</span></div>
          <div class="detail-row"><span>参与子组数</span><span>{{ detail.limitVersion.subgroupCount }}</span></div>
          <div class="code-block" style="margin-top:10px">SHA256 {{ detail.limitVersion.dataHash }}</div>
        </div>
        <p v-else class="hint">限值版本信息不可用。</p>
        <h3 style="margin-top:18px">子组</h3>
        <div v-if="detail.subgroup">
          <div class="detail-row"><span>SEQ</span><span>#{{ detail.subgroup.seq }}</span></div>
          <div class="detail-row"><span>X̄ / R / s / MR</span><span>{{ Number(detail.subgroup.xbar).toFixed(4) }} / {{ Number(detail.subgroup.rangeVal).toFixed(4) }} / {{ Number(detail.subgroup.stddevVal).toFixed(4) }} / {{ Number(detail.subgroup.mrVal).toFixed(4) }}</span></div>
          <div class="detail-row"><span>状态 / 限值版本 ID</span><span>{{ detail.subgroup.status }} / {{ detail.subgroup.limitVerId }}</span></div>
        </div>
        <p class="hint" style="margin-top:16px">{{ detail.auditChain }}</p>
      </div>
    </div>
  </div>
</template>
