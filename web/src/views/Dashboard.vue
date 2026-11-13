<script setup>
import { ref, onMounted, onUnmounted, watch, nextTick, computed } from 'vue'
import * as echarts from 'echarts'
import { SPC } from '../api'
import { store, log, chartTypeLabel, severityText } from '../store'

const xbarEl = ref(null)
const rangeEl = ref(null)
let xbarChart = null
let rangeChart = null
let timer = null

const contract = ref('')
const specNote = ref('')
const versionCount = ref(0)
const activeVersionNo = ref(null)
const mainLimits = ref(null)
const varLimits = ref(null)
const capability = ref(null)
const subgroupLimits = ref([])
const detail = ref(null)
const selected = computed(() => store.charts.find(c => c.id === store.selectedChartId))

function versionOf(seq) {
  return subgroupLimits.value.find(v => v.seq === seq)?.versionNo ?? null
}

function severityColor(s) {
  return s >= 4 ? '#f87171' : s === 3 ? '#fb923c' : s === 2 ? '#fbbf24' : '#22d3ee'
}

function buildOption(points, field, color, title, limits) {
  const seqs = points.map(p => `#${p.seq}`)
  const values = points.map(p => Number(p[field]))
  const data = points.map(p =>
  {
    const hit = (p.ruleOuts?.length ?? 0) > 0 || (p.maxSeverity ?? 0) > 0
    return {
      value: Number(p[field]),
      symbolSize: hit ? 11 : 6,
      itemStyle: hit
        ? { color: severityColor(p.maxSeverity), borderColor: '#fff', borderWidth: 1 }
        : undefined
    }
  })

  const series = [{
    type: 'line', data, symbol: 'circle',
    lineStyle: { color, width: 2 }, itemStyle: { color },
    areaStyle: { color: color + '12' }
  }]

  if (limits?.active) {
    // 冻结限：CL / UCL / LCL 与 Zone 分区；规格限绝不画在 Xbar 图上（方案 4.5 / 8.3）。
    series[0].markLine = {
      silent: true, symbol: 'none',
      label: { color: '#8b9bb4', fontSize: 10 },
      data: [
        { yAxis: limits.ucl, name: 'UCL', lineStyle: { color: '#f87171', type: 'dashed' } },
        { yAxis: limits.cl, name: 'CL', lineStyle: { color: '#34d399' } },
        { yAxis: limits.lcl, name: 'LCL', lineStyle: { color: '#f87171', type: 'dashed' } }
      ]
    }
    series[0].markArea = {
      silent: true,
      data: [
        [{ yAxis: limits.warnLcl, itemStyle: { color: '#22d3ee08' } }, { yAxis: limits.warnUcl }],
        [{ yAxis: limits.ucl, itemStyle: { color: '#f871710d' } }, { yAxis: limits.warnUcl }],
        [{ yAxis: limits.lcl, itemStyle: { color: '#f871710d' } }, { yAxis: limits.warnLcl }]
      ]
    }
  } else if (values.length > 1) {
    const avg = values.reduce((a, b) => a + b, 0) / values.length
    series[0].markLine = {
      silent: true, symbol: 'none',
      label: { color: '#6b7c96', fontSize: 10, formatter: '参考均值（非冻结 CL）' },
      data: [{ yAxis: avg, lineStyle: { color: '#3b4a63', type: 'dotted' } }]
    }
  }

  return {
    backgroundColor: 'transparent',
    animation: false,
    title: { text: title, left: 0, top: 0, textStyle: { color: '#8b9bb4', fontSize: 12, fontWeight: 500 } },
    grid: { left: 62, right: 24, top: 36, bottom: 32 },
    tooltip: { trigger: 'axis', backgroundColor: '#131c2e', borderColor: '#26344d', textStyle: { color: '#e6edf7' } },
    xAxis: {
      type: 'category', data: seqs, boundaryGap: false,
      axisLabel: { color: '#8b9bb4', fontSize: 10 },
      axisLine: { lineStyle: { color: '#26344d' } }
    },
    yAxis: {
      type: 'value', scale: true,
      axisLabel: { color: '#8b9bb4', fontSize: 10 },
      splitLine: { lineStyle: { color: '#1e2a40' } }
    },
    series
  }
}

function toMainLimits(L) {
  return { active: true, cl: L.cl, ucl: L.ucl, lcl: L.lcl, warnUcl: L.warnUcl, warnLcl: L.warnLcl }
}

function toVarLimits(L) {
  const sigma = (L.varUcl - L.varCl) / 3
  return {
    active: true, cl: L.varCl, ucl: L.varUcl, lcl: L.varLcl,
    warnUcl: L.varCl + 2 * sigma, warnLcl: L.varCl - 2 * sigma
  }
}

function render() {
  const points = store.subgroups.slice(-60)
  if (!xbarChart || !rangeChart) return
  xbarChart.setOption(buildOption(points, 'xbar', '#22d3ee', '主图 · X̄（子组均值）', mainLimits.value), true)
  rangeChart.setOption(buildOption(points, 'range', '#a78bfa', '变异图 · R / s / MR', varLimits.value), true)
}

async function openPoint(point) {
  const ro = store.ruleOuts.find(r => r.chartId === store.selectedChartId && r.seq === point.seq)
  if (ro) {
    try { detail.value = await SPC.ruleOutDetail(ro.ruleOutId); return }
    catch (e) { log(`下钻失败：${e.message}`, 'warn') }
  }
  detail.value = { simple: point }
}

function bindClicks(chart) {
  chart.on('click', p => {
    const points = store.subgroups.slice(-60)
    if (points[p.dataIndex]) openPoint(points[p.dataIndex])
  })
}

async function loadHistory() {
  if (!store.selectedChartId) return
  try {
    const [subs, query] = await Promise.all([
      SPC.subgroups(store.selectedChartId, 60),
      SPC.chartQuery(store.selectedChartId, 60)
    ])
    const raw = subs.items ?? []
    store.subgroups = raw.map(s => {
      const ro = store.ruleOuts.filter(r => r.chartId === s.chartId && r.seq === s.seq)
      return {
        chartId: s.chartId, id: s.id, seq: s.seq,
        eventTime: s.eventTimeTo, xbar: s.xbar, range: s.rangeVal,
        stddev: s.stddevVal, mr: s.mrVal, sampleCount: s.sampleCount,
        ruleOuts: ro.map(r => r.ruleCode),
        maxSeverity: ro.length ? Math.max(...ro.map(r => r.severity)) : 0
      }
    })
    contract.value = query.contract ?? ''
    versionCount.value = (query.versions ?? []).length
    specNote.value = query.specLimits?.note ?? ''
    activeVersionNo.value = query.limitsVersionNo ?? null
    capability.value = query.capability ?? null
    subgroupLimits.value = query.subgroupLimits ?? []
    mainLimits.value = query.limits ? toMainLimits(query.limits) : null
    varLimits.value = query.limits ? toVarLimits(query.limits) : null
    render()
  } catch (e) { log(`加载历史子组失败：${e.message}`, 'warn') }
}

onMounted(async () => {
  xbarChart = echarts.init(xbarEl.value)
  rangeChart = echarts.init(rangeEl.value)
  bindClicks(xbarChart)
  bindClicks(rangeChart)
  window.addEventListener('resize', resize)
  await loadHistory()
  timer = setInterval(render, 1000)
})

onUnmounted(() => {
  clearInterval(timer)
  window.removeEventListener('resize', resize)
  xbarChart?.dispose()
  rangeChart?.dispose()
})

function resize() { xbarChart?.resize(); rangeChart?.resize() }
watch(() => store.selectedChartId, () => {
  store.subgroups = []
  mainLimits.value = null
  varLimits.value = null
  subgroupLimits.value = []
  detail.value = null
  nextTick(loadHistory)
})
</script>

<template>
  <div v-if="!mainLimits" class="note">
    <strong>Phase I 基线尚未冻结 —— 判异被拒绝。</strong>
    按方案 5.7「Phase II 只读契约」，控制限必须来自审批发布的版本表；无生效版本时子组只聚合、不判异，
    本页不会用近期数据现算 UCL/LCL。请到「基线管理」完成预检 → 冻结 → 审批发布。
  </div>
  <div v-else class="note info">
    <strong>冻结限值版本 V{{ activeVersionNo }} 已生效，Phase II 判异运行中。</strong>
    红色虚线为 UCL/LCL、绿线为 CL、淡色带为 Zone 分区；<b>命中规则的点标红放大，点击可下钻</b>
    查看量测值、规则、限值版本与审批人。表格中每个子组单独标注它当时生效的版本；规格限不画在 Xbar 图上。
  </div>

  <div v-if="store.lastRuleOut" class="note" style="border-color:var(--red);background:#2a1414;color:#fecaca">
    最新告警 · {{ severityText(store.lastRuleOut.severity) }}：{{ store.lastRuleOut.message }}
    <span style="float:right">共 {{ store.ruleOutEvents }} 条 Rule Out</span>
  </div>

  <div class="toolbar">
    <label>控制图</label>
    <select v-model.number="store.selectedChartId" style="min-width:320px">
      <option v-for="c in store.charts" :key="c.id" :value="c.id">
        {{ c.code }} · {{ c.name }} · {{ chartTypeLabel(c.chartType) }} / n={{ c.subgroupSize }}
      </option>
    </select>
    <button class="btn ghost" @click="loadHistory">刷新历史 / 重取限</button>
    <span class="badge">{{ store.subgroups.length }} 个子组 · {{ store.ruleOutEvents }} 条判异</span>
  </div>

  <div class="grid cols-4" style="margin-bottom:16px">
    <div class="card kpi">
      <div class="label">实时子组事件</div><div class="value" style="color:var(--cyan)">{{ store.subgroupEvents }}</div>
      <div class="sub">SignalR /hubs/spc 推送</div>
    </div>
    <div class="card kpi">
      <div class="label">接入量测点</div><div class="value">{{ store.measureEvents }}</div>
      <div class="sub">PROC_TAG 校验 + 幂等去重</div>
    </div>
    <div class="card kpi">
      <div class="label">冻结限值版本</div>
      <div class="value" :style="{ color: mainLimits ? 'var(--green)' : 'var(--amber)' }">
        {{ versionCount }}<span v-if="activeVersionNo" style="font-size:14px"> · V{{ activeVersionNo }} ACTIVE</span>
      </div>
      <div class="sub">一图一 ACTIVE（唯一索引兜底）</div>
    </div>
    <div class="card kpi">
      <div class="label">Cpk（冻结 σ̂）</div>
      <div class="value" style="font-size:20px;padding-top:6px">
        {{ capability?.available && capability.indices?.cpk != null ? Number(capability.indices.cpk).toFixed(3) : '—' }}
      </div>
      <div class="sub">{{ capability?.available ? capability.indices?.grade : '过程未受控或无生效版本' }}</div>
    </div>
  </div>

  <div class="grid cols-2" style="margin-bottom:16px">
    <div class="card"><div ref="xbarEl" class="chart-box" /></div>
    <div class="card"><div ref="rangeEl" class="chart-box" /></div>
  </div>

  <div class="grid cols-3-1">
    <div class="card">
      <h3>最近子组（按 EVENT_TIME 有序 · 点击图表判异点下钻）</h3>
      <table>
        <thead><tr><th>SEQ</th><th>事件时间</th><th>X̄</th><th>R</th><th>s</th><th>MR</th><th>n</th><th>当时生效版本</th><th>判异</th></tr></thead>
        <tbody>
          <tr v-for="p in store.subgroups.slice(-15).reverse()" :key="p.id ?? p.seq">
            <td>#{{ p.seq }}</td>
            <td>{{ new Date(p.eventTime).toLocaleTimeString('zh-CN', { hour12: false }) }}</td>
            <td>{{ Number(p.xbar).toFixed(4) }}</td>
            <td>{{ Number(p.range).toFixed(4) }}</td>
            <td>{{ Number(p.stddev ?? 0).toFixed(4) }}</td>
            <td>{{ Number(p.mr ?? 0).toFixed(4) }}</td>
            <td>{{ p.sampleCount }}</td>
            <td>
              <span v-if="versionOf(p.seq)" class="pill active">V{{ versionOf(p.seq) }}</span>
              <span v-else class="pill pending">当时无生效版本</span>
            </td>
            <td>
              <span v-if="p.ruleOuts?.length" :class="'sev-' + (p.maxSeverity >= 4 ? 'critical' : p.maxSeverity === 3 ? 'major' : 'warning')">
                {{ p.ruleOuts.join(' / ') }}
              </span>
              <span v-else class="hint">受控</span>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-if="!store.subgroups.length" class="hint" style="padding:16px 0">暂无子组。到「数据源 / 演练」启动模拟器即可看到实时数据。</div>
    </div>

    <div class="card">
      <template v-if="detail">
        <div class="toolbar" style="margin-bottom:10px">
          <h3 style="margin:0">点位下钻</h3>
          <button class="btn ghost" style="margin-left:auto;padding:4px 9px" @click="detail = null">关闭</button>
        </div>
        <template v-if="detail.simple">
          <p class="hint">该点未命中判异规则（受控）。</p>
          <div class="detail-row"><span>SEQ</span><span>#{{ detail.simple.seq }}</span></div>
          <div class="detail-row"><span>X̄ / R</span><span>{{ Number(detail.simple.xbar).toFixed(4) }} / {{ Number(detail.simple.range).toFixed(4) }}</span></div>
          <div class="detail-row"><span>当时生效版本</span><span>V{{ versionOf(detail.simple.seq) ?? '—' }}</span></div>
        </template>
        <template v-else>
          <div class="detail-row"><span>规则</span><span>{{ detail.ruleCode }} · {{ detail.severityLabel }}</span></div>
          <div class="detail-row"><span>说明</span><span>{{ detail.description }}</span></div>
          <div class="detail-row"><span>z / 统计量</span><span>{{ Number(detail.ruleOut.zValue).toFixed(3) }} / {{ Number(detail.ruleOut.statValue).toFixed(4) }}</span></div>
          <div class="detail-row"><span>限值版本</span><span>V{{ detail.limitVersion?.versionNo }} · ID {{ detail.limitVersion?.id }}</span></div>
          <div class="detail-row"><span>审批人</span><span>{{ detail.limitVersion?.approvedBy }}</span></div>
          <div class="detail-row"><span>处置状态</span><span>{{ detail.ocapStatus }}</span></div>
          <div class="detail-row"><span>批次 / SN</span><span>{{ detail.ruleOut.lotId || '—' }} / {{ detail.ruleOut.sn || '—' }}</span></div>
          <div class="code-block" style="margin-top:10px">HASH {{ detail.limitVersion?.dataHash }}</div>
          <h3 style="margin-top:14px">原始量测值</h3>
          <table>
            <thead><tr><th>值</th><th>PROC_TAG</th></tr></thead>
            <tbody>
              <tr v-for="m in detail.measures" :key="m.id">
                <td>{{ Number(m.measureValue).toFixed(4) }}</td>
                <td class="hint">{{ m.procTag }}</td>
              </tr>
            </tbody>
          </table>
          <p class="hint" style="margin-top:10px">{{ detail.auditChain }}</p>
        </template>
      </template>

      <template v-else>
        <h3>取限契约 / 能力</h3>
        <p class="hint">{{ contract || 'GetLimitsForEvaluation(chartId, eventTime) 只查询版本表。' }}</p>
        <p class="hint" style="margin-top:12px">{{ specNote }}</p>
        <div v-if="capability?.available" style="margin-top:16px">
          <table>
            <tbody>
              <tr><td>Cp</td><td>{{ Number(capability.indices.cp ?? 0).toFixed(3) }}</td></tr>
              <tr><td>Cpk</td><td><b>{{ Number(capability.indices.cpk ?? 0).toFixed(3) }}</b></td></tr>
              <tr><td>Pp / Ppk</td><td>{{ Number(capability.indices.pp ?? 0).toFixed(3) }} / {{ Number(capability.indices.ppk ?? 0).toFixed(3) }}</td></tr>
              <tr><td>σ̂（冻结）</td><td>{{ Number(capability.indices.sigmaHat ?? 0).toFixed(5) }}</td></tr>
              <tr><td>等级</td><td>{{ capability.indices.grade }}</td></tr>
            </tbody>
          </table>
          <p class="hint" style="margin-top:10px">{{ capability.controlBasis }}</p>
        </div>
        <div v-if="selected" style="margin-top:16px">
          <span class="pill active">规格限 USL {{ selected.usl }} / LSL {{ selected.lsl }}</span>
          <p class="hint" style="margin-top:8px">规格限来自设计，只用于能力直方图；控制限来自过程历史数据，二者物理分表。</p>
        </div>
      </template>
    </div>
  </div>
</template>
