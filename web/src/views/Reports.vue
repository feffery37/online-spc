<script setup>
import { ref, onMounted, onUnmounted, watch, computed, nextTick } from 'vue'
import * as echarts from 'echarts'
import { SPC } from '../api'
import { store, log } from '../store'

const chartId = computed(() => store.selectedChartId)
const histEl = ref(null)
let histChart = null

const cap = ref(null)
const periods = ref(null)
const compliance = ref(null)
const audit = ref(null)
const snapshots = ref([])
const schedule = ref(null)
const granularity = ref('day')
const count = ref(7)
const reportType = ref('DAILY')
const busy = ref(false)
const message = ref('')

const indices = computed(() => cap.value?.capability?.indices ?? null)
const readyCount = computed(() => (compliance.value?.checklist ?? []).filter(c => c.pass).length)

function binIndexFor(v) {
  const bins = cap.value?.histogram?.bins ?? []
  if (!bins.length || v == null) return null
  const i = bins.findIndex(b => v >= b.from && v <= b.to)
  return i < 0 ? (v < bins[0].from ? 0 : bins.length - 1) : i
}

function renderHistogram() {
  if (!histChart) return
  const bins = cap.value?.histogram?.bins ?? []
  const lines = []
  const add = (value, name, color, type = 'dashed') => {
    const idx = binIndexFor(value)
    if (idx !== null) lines.push({ xAxis: idx, name, lineStyle: { color, type } })
  }
  add(cap.value?.usl, 'USL 规格上限', '#f87171')
  add(cap.value?.lsl, 'LSL 规格下限', '#f87171')
  add(cap.value?.target, '目标值', '#34d399', 'solid')

  histChart.setOption({
    backgroundColor: 'transparent',
    animation: false,
    grid: { left: 55, right: 24, top: 34, bottom: 46 },
    title: {
      text: '能力直方图 · 规格线只画在这里', left: 0, top: 0,
      textStyle: { color: '#8b9bb4', fontSize: 12, fontWeight: 500 }
    },
    tooltip: { trigger: 'axis', backgroundColor: '#131c2e', borderColor: '#26344d', textStyle: { color: '#e6edf7' } },
    xAxis: {
      type: 'category', data: bins.map(b => Number(b.from).toFixed(3)),
      axisLabel: { color: '#8b9bb4', fontSize: 10, rotate: 45 },
      axisLine: { lineStyle: { color: '#26344d' } }
    },
    yAxis: {
      type: 'value', axisLabel: { color: '#8b9bb4', fontSize: 10 },
      splitLine: { lineStyle: { color: '#1e2a40' } }
    },
    series: [{
      type: 'bar', data: bins.map(b => b.count),
      itemStyle: { color: '#22d3ee', opacity: 0.75 },
      barCategoryGap: '8%',
      markLine: { silent: true, symbol: 'none', label: { color: '#e6edf7', fontSize: 10 }, data: lines }
    }]
  }, true)
}

async function load() {
  if (!chartId.value) return
  try {
    const [c, p, k, a, s, sc] = await Promise.all([
      SPC.reportCapability(chartId.value),
      SPC.reportPeriods(chartId.value, granularity.value, count.value),
      SPC.reportCompliance(chartId.value),
      SPC.reportAudit(chartId.value),
      SPC.reportSnapshots(chartId.value),
      SPC.reportSchedule()
    ])
    cap.value = c; periods.value = p; compliance.value = k
    audit.value = a; snapshots.value = s.items ?? []; schedule.value = sc
    nextTick(renderHistogram)
  } catch (e) { log(`读取报表失败：${e.message}`, 'warn') }
}

async function reloadPeriods() {
  try { periods.value = await SPC.reportPeriods(chartId.value, granularity.value, count.value) }
  catch (e) { log(e.message, 'warn') }
}

function exportCsv(kind) {
  const a = document.createElement('a')
  a.href = SPC.reportExportUrl(chartId.value, kind, granularity.value, count.value)
  a.download = ''
  document.body.appendChild(a); a.click(); a.remove()
  log(`已导出 CSV：${kind}`, 'ok')
}

async function generate() {
  busy.value = true
  try {
    const r = await SPC.reportGenerate(chartId.value, reportType.value)
    message.value = `${r.snapshot.reportType} 快照已生成，指纹 ${r.snapshot.dataHash.slice(0, 12)}…`
    log(message.value, 'ok')
    await load()
  } catch (e) { message.value = e.message; log(e.message, 'warn') }
  finally { busy.value = false }
}

async function runSchedule() {
  busy.value = true
  try { await SPC.reportRunNow(); message.value = '定时任务已手动触发'; log(message.value, 'ok'); await load() }
  catch (e) { log(e.message, 'warn') }
  finally { busy.value = false }
}

onMounted(async () => {
  histChart = echarts.init(histEl.value)
  window.addEventListener('resize', () => histChart?.resize())
  await load()
})
onUnmounted(() => histChart?.dispose())
watch(chartId, () => load())
watch([granularity, count], () => reloadPeriods())
watch(() => store.reportEvents, () => load())
</script>

<template>
  <div class="note info">
    <b>M5 报表中心与合规取证：</b>能力直方图（<b>USL/LSL 规格线只画在这里</b>，绝不画在 Xbar 控制图上）、
    Cp/Cpk/Pp/Ppk、日周月周期报表、带 SHA256 指纹的报表快照、定时推送与 CSV 导出。
    <b>过程未受控时拒绝输出 Cpk</b>：近 24 小时存在未闭环 Rule Out 即判定不稳定（方案 4.4 / 附录 A）。
  </div>

  <div class="grid cols-4" style="margin-bottom:16px">
    <div class="card kpi">
      <div class="label">报表快照</div><div class="value" style="color:var(--cyan)">{{ snapshots.length }}</div>
      <div class="sub">只增不改 · 带数据指纹</div>
    </div>
    <div class="card kpi">
      <div class="label">合规自检</div>
      <div class="value" :style="{ color: compliance?.ready ? 'var(--green)' : 'var(--amber)' }">
        {{ readyCount }} / {{ compliance?.checklist?.length ?? 0 }}
      </div>
      <div class="sub">{{ compliance?.ready ? '取证包就绪' : '仍有不符合项' }}</div>
    </div>
    <div class="card kpi">
      <div class="label">审计事件</div><div class="value">{{ audit?.total ?? 0 }}</div>
      <div class="sub">全链路留痕</div>
    </div>
    <div class="card kpi">
      <div class="label">定时推送</div>
      <div class="value" style="font-size:19px;padding-top:6px">
        {{ schedule?.enabled ? `每 ${schedule.intervalMinutes} 分钟` : '已停用' }}
      </div>
      <div class="sub">已生成 {{ schedule?.generatedCount ?? 0 }} 份 · {{ store.reportEvents }} 次推送</div>
    </div>
  </div>

  <div class="grid cols-2" style="margin-bottom:16px">
    <div class="card"><div ref="histEl" class="chart-box" style="height:320px" /></div>
    <div class="card">
      <h3>过程能力 · 受控门禁</h3>
      <template v-if="cap?.capability?.available && indices">
        <div class="grid cols-2" style="gap:10px">
          <div class="kpi"><div class="label">Cp</div><div class="value" style="font-size:20px">{{ Number(indices.cp ?? 0).toFixed(3) }}</div></div>
          <div class="kpi"><div class="label">Cpk</div><div class="value" style="font-size:20px;color:var(--cyan)">{{ Number(indices.cpk ?? 0).toFixed(3) }}</div></div>
          <div class="kpi"><div class="label">Pp / Ppk</div><div class="value" style="font-size:18px">{{ Number(indices.pp ?? 0).toFixed(3) }} / {{ Number(indices.ppk ?? 0).toFixed(3) }}</div></div>
          <div class="kpi"><div class="label">等级</div><div class="value" style="font-size:14px;padding-top:8px">{{ indices.grade }}</div></div>
        </div>
        <p class="hint" style="margin-top:12px">{{ cap.capability.controlBasis }}</p>
      </template>
      <div v-else class="note" style="margin:10px 0">
        <b>能力指数不可用：</b>{{ cap?.capability?.reason || '过程未受控或无生效冻结版本，禁止计算 Cpk。' }}
      </div>
      <h3 style="margin-top:16px">规格限 / 统计量</h3>
      <div class="detail-row"><span>USL / LSL（设计）</span><span>{{ cap?.usl }} / {{ cap?.lsl }} {{ cap?.unit }}</span></div>
      <div class="detail-row"><span>目标值</span><span>{{ cap?.target }}</span></div>
      <div class="detail-row"><span>样本数 / 窗口</span><span>{{ cap?.sampleCount }} · {{ cap?.from ? new Date(cap.from).toLocaleDateString('zh-CN') : '—' }} ~ {{ cap?.to ? new Date(cap.to).toLocaleDateString('zh-CN') : '—' }}</span></div>
      <div class="detail-row"><span>均值 / 整体 σ</span><span>{{ Number(cap?.histogram?.mean ?? 0).toFixed(4) }} / {{ Number(cap?.histogram?.sigmaOverall ?? 0).toFixed(5) }}</span></div>
      <p class="hint" style="margin-top:12px">{{ cap?.specLineNote }}</p>
      <button class="btn ghost" style="margin-top:10px" @click="exportCsv('capability')">导出直方图 CSV</button>
    </div>
  </div>

  <div class="card" style="margin-bottom:16px">
    <div class="toolbar">
      <h3 style="margin:0">周期能力报表（按事件时间回放当时冻结限）</h3>
      <select v-model="granularity" style="margin-left:auto">
        <option value="day">日报</option><option value="week">周报</option><option value="month">月报</option>
      </select>
      <select v-model.number="count"><option :value="7">7 期</option><option :value="14">14 期</option><option :value="24">24 期</option></select>
      <button class="btn ghost" @click="exportCsv('periods')">导出 CSV</button>
    </div>
    <table>
      <thead><tr><th>周期</th><th>样本数</th><th>均值</th><th>整体 σ</th><th>Cp</th><th>Cpk</th><th>Ppk</th><th>限值版本</th><th>受控门禁</th></tr></thead>
      <tbody>
        <tr v-for="p in periods?.periods ?? []" :key="p.period">
          <td><b>{{ p.period }}</b></td>
          <td>{{ p.sampleCount }}</td>
          <td>{{ Number(p.mean).toFixed(4) }}</td>
          <td>{{ Number(p.sigmaOverall).toFixed(5) }}</td>
          <td>{{ p.cp != null ? Number(p.cp).toFixed(3) : '—' }}</td>
          <td>{{ p.cpk != null ? Number(p.cpk).toFixed(3) : '—' }}</td>
          <td>{{ p.ppk != null ? Number(p.ppk).toFixed(3) : '—' }}</td>
          <td><span v-if="p.limitVersionNo" class="pill active">V{{ p.limitVersionNo }}</span><span v-else class="pill pending">无</span></td>
          <td><span :class="p.isControlled ? '' : 'sev-warning'" class="hint">{{ p.controlGateReason }}</span></td>
        </tr>
      </tbody>
    </table>
    <p class="hint" style="margin-top:10px">{{ periods?.note }}</p>
  </div>

  <div class="grid cols-2" style="margin-bottom:16px">
    <div class="card">
      <div class="toolbar">
        <h3 style="margin:0">合规取证包 · 体系审核自检</h3>
        <span class="pill" :class="compliance?.ready ? 'active' : 'pending'" style="margin-left:auto">
          {{ compliance?.ready ? 'READY' : 'NOT READY' }}
        </span>
        <button class="btn ghost" @click="exportCsv('compliance')">导出 CSV</button>
      </div>
      <table>
        <thead><tr><th>检查项</th><th>结论</th><th>证据</th></tr></thead>
        <tbody>
          <tr v-for="c in compliance?.checklist ?? []" :key="c.item">
            <td>{{ c.item }}</td>
            <td><span :class="c.pass ? '' : 'sev-critical'">{{ c.pass ? 'PASS' : 'FAIL' }}</span></td>
            <td class="hint">{{ c.evidence }}</td>
          </tr>
        </tbody>
      </table>
      <p class="hint" style="margin-top:10px">{{ compliance?.note }}</p>
      <div class="grid cols-4" style="gap:10px;margin-top:12px">
        <div class="kpi"><div class="label">限值版本</div><div class="value" style="font-size:18px">{{ compliance?.versions?.length ?? 0 }}</div></div>
        <div class="kpi"><div class="label">Rule Out</div><div class="value" style="font-size:18px">{{ compliance?.ruleOutCount ?? 0 }}</div></div>
        <div class="kpi"><div class="label">OCAP</div><div class="value" style="font-size:18px">{{ compliance?.ocapCount ?? 0 }}</div></div>
        <div class="kpi"><div class="label">MES 动作</div><div class="value" style="font-size:18px">{{ compliance?.mesActionCount ?? 0 }}</div></div>
      </div>
    </div>

    <div class="card">
      <h3>报表快照与定时推送</h3>
      <div class="toolbar">
        <select v-model="reportType"><option>DAILY</option><option>WEEKLY</option><option>MONTHLY</option><option>COMPLIANCE</option></select>
        <button class="btn" :disabled="busy" @click="generate">生成快照</button>
        <button class="btn ghost" :disabled="busy" @click="runSchedule">立即执行定时任务</button>
      </div>
      <p v-if="message" class="hint" style="color:var(--green)">{{ message }}</p>
      <p class="hint">定时配置：{{ schedule?.enabled ? `启用，每 ${schedule.intervalMinutes} 分钟` : '停用' }}；
        上次运行 {{ schedule?.lastRun ? new Date(schedule.lastRun).toLocaleString('zh-CN', { hour12:false }) : '—' }}</p>
      <table style="margin-top:12px">
        <thead><tr><th>类型</th><th>生成时间</th><th>SHA256 指纹</th><th>生成者</th></tr></thead>
        <tbody>
          <tr v-for="s in snapshots.slice(0, 12)" :key="s.id">
            <td><span class="pill active">{{ s.reportType }}</span></td>
            <td>{{ new Date(s.generatedAt).toLocaleString('zh-CN', { hour12:false }) }}</td>
            <td><code>{{ s.dataHash.slice(0, 16) }}…</code></td>
            <td>{{ s.generatedBy }}</td>
          </tr>
        </tbody>
      </table>
      <div v-if="!snapshots.length" class="hint" style="padding:12px 0">暂无快照。点击「生成快照」或等待定时任务。</div>

      <h3 style="margin-top:18px">审计视图导出</h3>
      <p class="hint">共 {{ audit?.total ?? 0 }} 条审计事件；可按事件类型统计并导出 CSV 供体系审核取证。</p>
      <button class="btn ghost" style="margin-top:8px" @click="exportCsv('audit')">导出审计 CSV</button>
      <div class="log" style="height:150px;margin-top:10px">
        <div v-for="e in (audit?.events ?? []).slice(-30).reverse()" :key="e.id">
          [{{ new Date(e.eventTime).toLocaleTimeString('zh-CN', { hour12:false }) }}] {{ e.eventType }} · {{ e.entityType }}#{{ e.entityId }} · {{ e.actor }}
        </div>
      </div>
    </div>
  </div>

  <div class="card">
    <h3>实时事件日志</h3>
    <div class="log">
      <div v-for="(l, i) in store.logs" :key="i" :class="l.kind">[{{ l.time }}] {{ l.message }}</div>
    </div>
  </div>
</template>
