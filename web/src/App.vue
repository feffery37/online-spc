<script setup>
import { ref, onMounted, onUnmounted, computed } from 'vue'
import Dashboard from './views/Dashboard.vue'
import Baseline from './views/Baseline.vue'
import Alerts from './views/Alerts.vue'
import Disposition from './views/Disposition.vue'
import Reports from './views/Reports.vue'
import Charts from './views/Charts.vue'
import Simulator from './views/Simulator.vue'
import { SPC } from './api'
import { connectRealtime } from './realtime'
import { store, log } from './store'

const tab = ref('dashboard')
let connection = null
const alertTotal = computed(() =>
  store.alertCounts.critical + store.alertCounts.major + store.alertCounts.warning)

async function refreshCharts() {
  try {
    const res = await SPC.charts()
    store.charts = res.items ?? []
    if (!store.selectedChartId && store.charts.length) store.selectedChartId = store.charts[0].id
  } catch (e) { log(`读取控制图失败：${e.message}`, 'warn') }
}

function addRuleOut(p) {
  store.ruleOutEvents++
  store.lastRuleOut = p
  if (p.chartId === store.selectedChartId) {
    store.ruleOuts.unshift(p)
    if (store.ruleOuts.length > 200) store.ruleOuts.pop()
  }
  const key = p.severity >= 4 ? 'critical' : p.severity === 3 ? 'major' : 'warning'
  store.alertCounts[key]++
  const sg = store.subgroups.find(s => s.id === p.subgroupId || s.seq === p.seq)
  if (sg) {
    sg.ruleOuts = [...(sg.ruleOuts ?? []), p.ruleCode]
    sg.maxSeverity = Math.max(sg.maxSeverity ?? 0, p.severity)
  }
  log(p.message ?? `Rule Out ${p.ruleCode}`, 'warn')
}

onMounted(async () => {
  try { store.meta = await SPC.meta() } catch (e) { log(`后端未就绪：${e.message}`, 'warn') }
  await refreshCharts()
  try { store.simulator = await SPC.simulatorState() } catch { /* 后端未启动 */ }

  connection = connectRealtime({
    onSubgroup(p) {
      store.subgroupEvents++
      store.lastSubgroup = p
      if (p.chartId === store.selectedChartId) {
        store.subgroups.push({
          ...p,
          ruleOuts: (p.ruleOuts ?? []).map(c => typeof c === 'number' ? 'R' + c : c)
        })
        if (store.subgroups.length > 120) store.subgroups.shift()
      }
      log(`子组 #${p.seq} 闭合 · X̄=${p.xbar.toFixed(4)} · ${p.ruleOuts?.length ? '判异 ' + p.ruleOuts.join('/') : '受控'}`)
    },
    onMeasure(p) {
      store.measureEvents += p.count ?? 0
      log(`接入 ${p.count} 个量测点（含 PROC_TAG 校验与幂等去重）`, 'ok')
    },
    onNotice(p) { log(p.message ?? JSON.stringify(p), 'ok') },
    onRuleOut: addRuleOut,
    onDisposition(p) {
      store.dispositionEvents++
      store.dispositions.unshift(p)
      if (store.dispositions.length > 100) store.dispositions.pop()
      log(p.message ?? `处置状态 → ${p.statusLabel}`, 'ok')
    },
    onReport(p) {
      store.reportEvents++
      store.reports.unshift(p)
      if (store.reports.length > 50) store.reports.pop()
      log(p.message ?? `报表快照 ${p.reportType}`, 'ok')
    }
  }, (status) => {
    store.status = status
    log(status === 'connected' ? 'SignalR 已连接 /hubs/spc' : `SignalR ${status}`, status === 'connected' ? 'ok' : 'warn')
  })
  log('Online-SPC 看板已启动，等待实时数据…', 'ok')
})

onUnmounted(() => connection?.stop())

defineExpose({ refreshCharts })
</script>

<template>
  <div class="app-shell">
    <header class="topbar">
      <div class="brand">
        <strong>Online-SPC</strong>
        <span>Process Control</span>
      </div>
      <nav class="nav">
        <button :class="{ active: tab === 'dashboard' }" @click="tab = 'dashboard'">实时监控</button>
        <button :class="{ active: tab === 'alerts' }" @click="tab = 'alerts'">
          告警中心 <span v-if="alertTotal" class="alert-count">{{ alertTotal }}</span>
        </button>
        <button :class="{ active: tab === 'disposition' }" @click="tab = 'disposition'">处置闭环</button>
        <button :class="{ active: tab === 'reports' }" @click="tab = 'reports'">报表中心</button>
        <button :class="{ active: tab === 'baseline' }" @click="tab = 'baseline'">基线管理</button>
        <button :class="{ active: tab === 'charts' }" @click="tab = 'charts'">建模配置</button>
        <button :class="{ active: tab === 'simulator' }" @click="tab = 'simulator'">数据源 / 演练</button>
      </nav>
      <div class="topbar-right">
        <span class="badge cyan">M0→M5</span>
        <span v-if="store.alertCounts.critical" class="badge alert">CRITICAL {{ store.alertCounts.critical }}</span>
        <span class="badge"><span class="dot" :class="store.status === 'connected' ? 'on' : 'off'" />{{ store.status === 'connected' ? '实时链路已连接' : '链路断开' }}</span>
      </div>
    </header>

    <main class="container">
      <Dashboard v-if="tab === 'dashboard'" />
      <Alerts v-else-if="tab === 'alerts'" />
      <Disposition v-else-if="tab === 'disposition'" />
      <Reports v-else-if="tab === 'reports'" />
      <Baseline v-else-if="tab === 'baseline'" />
      <Charts v-else-if="tab === 'charts'" @refresh="refreshCharts" />
      <Simulator v-else />
    </main>
  </div>
</template>
