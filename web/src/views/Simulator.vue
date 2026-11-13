<script setup>
import { ref, computed } from 'vue'
import { SPC } from '../api'
import { store, log } from '../store'

const scenario = ref('stable')
const intervalMs = ref(400)
const burst = ref(1)
const chartCode = ref('CH-DEMO-01')
const busy = ref(false)
const replayCount = ref(60)

const scenarios = [
  { id: 'stable', name: '稳定过程', desc: 'μ=10.000，σ=0.030，用于 Phase I 基线候选数据' },
  { id: 'drift', name: '均值突变（换料/换刀）', desc: '第 40 点起 +0.16，温度同步 +3.5℃，验证 R2 检出' },
  { id: 'trend', name: '趋势性漂移（刀具磨损）', desc: '线性 +0.00035/点，主轴转速同步上升，验证 R3 检出' },
  { id: 'oscillation', name: '系统性振荡', desc: '逐点 ±0.14 交替，验证 R4 检出' },
  { id: 'spike', name: '突发大偏移', desc: '每 47 点注入 3σ 外单点，压力同步异常，验证 R1 检出' }
]

const state = computed(() => store.simulator ?? { running: false, generated: 0 })

async function refresh() {
  try { store.simulator = await SPC.simulatorState() } catch (e) { log(e.message, 'warn') }
}

async function start() {
  busy.value = true
  try {
    const res = await SPC.simulatorStart({
      chartCode: chartCode.value, scenario: scenario.value,
      intervalMs: Number(intervalMs.value), burst: Number(burst.value)
    })
    store.simulator = res.state
    log(`模拟器已启动：${chartCode.value} / ${scenario.value} / ${intervalMs.value}ms / burst=${burst.value}`, 'ok')
  } catch (e) { log(`启动失败：${e.message}`, 'warn') }
  finally { busy.value = false }
}

async function stop() {
  try {
    const res = await SPC.simulatorStop()
    store.simulator = res.state
    log('模拟器已停止', 'ok')
  } catch (e) { log(e.message, 'warn') }
}

async function replay() {
  busy.value = true
  try {
    const res = await SPC.replay(chartCode.value, Number(replayCount.value))
    log(`乱序重放：接受 ${res.result?.accepted ?? 0}、去重 ${res.result?.duplicated ?? 0}、拒绝 ${res.result?.rejected ?? 0}`, 'ok')
    await refresh()
  } catch (e) { log(`重放失败：${e.message}`, 'warn') }
  finally { busy.value = false }
}
</script>

<template>
  <div class="note info">
    现场数据源为 OPC UA / MQTT / SECS-GEM / 量具串口 / CSV / MES EDC；此处用模拟器产生等价的标准量测记录，
    <b>每个点强制携带 LOT_ID / SN / 工单 / 班次 / RECIPE / 机台 / PROC_TAG 工艺参数标签</b>（F-02、附录 A）。
    所有点都经 POST /api/spc/measure/ingest 走完整校验、去重与有序入队链路。
  </div>

  <div class="grid cols-2">
    <div class="card">
      <h3>采集模拟器</h3>
      <div class="grid cols-2" style="gap:12px">
        <div class="field"><label>目标控制图编码</label><input v-model="chartCode" type="text" /></div>
        <div class="field"><label>采样间隔 (ms)</label><input v-model.number="intervalMs" type="number" min="50" max="10000" /></div>
        <div class="field"><label>每周期点数 Burst</label><input v-model.number="burst" type="number" min="1" max="20" /></div>
        <div class="field"><label>场景</label>
          <select v-model="scenario"><option v-for="s in scenarios" :key="s.id" :value="s.id">{{ s.name }}</option></select>
        </div>
      </div>
      <p class="hint" style="margin:12px 0 16px">{{ scenarios.find(s => s.id === scenario)?.desc }}</p>
      <div style="display:flex;gap:10px;flex-wrap:wrap">
        <button class="btn" :disabled="busy || state.running" @click="start">启动采集</button>
        <button class="btn danger" :disabled="!state.running" @click="stop">停止</button>
        <button class="btn ghost" @click="refresh">刷新状态</button>
      </div>
      <div class="grid cols-4" style="margin-top:20px;gap:10px">
        <div class="kpi"><div class="label">运行状态</div><div class="value" :style="{ color: state.running ? 'var(--green)' : 'var(--muted)', fontSize: '20px' }">{{ state.running ? 'RUNNING' : 'STOPPED' }}</div></div>
        <div class="kpi"><div class="label">已生成点数</div><div class="value">{{ state.generated ?? 0 }}</div></div>
        <div class="kpi"><div class="label">场景</div><div class="value" style="font-size:18px;padding-top:6px">{{ state.scenario || '-' }}</div></div>
        <div class="kpi"><div class="label">间隔</div><div class="value" style="font-size:18px;padding-top:6px">{{ state.intervalMs || '-' }} ms</div></div>
      </div>
    </div>

    <div class="card">
      <h3>断网续传 / 乱序重放演练（N-02）</h3>
      <p class="hint">
        生成一批事件时间落在过去 3 分钟窗口内的量测点，<b>故意打乱到达顺序</b>提交。
        接入层按 EVENT_TIME 排序入队，聚合层按 EVENT_TIME 从库重取未分组点，
        因此连续类判异规则依赖的先后顺序不受网络抖动影响。
      </p>
      <div class="toolbar" style="margin-top:16px">
        <label>点数</label>
        <input v-model.number="replayCount" type="number" min="5" max="500" style="width:100px" />
        <button class="btn" :disabled="busy" @click="replay">执行乱序重放</button>
      </div>
      <div class="note" style="margin:16px 0 0">
        判异规则（R2/R3/R4/R5…）依赖点的先后顺序；<b>按到达时间回放会造成误判</b>，这是在线 SPC 最容易被忽略的坑。
      </div>
      <h3 style="margin-top:22px">采集校验规则</h3>
      <table>
        <thead><tr><th>校验项</th><th>行为</th></tr></thead>
        <tbody>
          <tr><td>PROC_TAG 工艺参数标签</td><td>缺失即拒绝，避免判异后无法根因分析</td></tr>
          <tr><td>LOT_ID / SN / 工单 / 班次 / RECIPE / 机台</td><td>缺失即拒绝（F-02）</td></tr>
          <tr><td>DedupKey = SHA256(图+事件时间+SN+值+来源)</td><td>重复点直接丢弃，边缘重放幂等</td></tr>
          <tr><td>事件时间归一 UTC</td><td>统一时间基准，按事件时间有序</td></tr>
          <tr><td>数值 / 控制图可用性</td><td>非有限值、未知编码、停用图均拒绝</td></tr>
        </tbody>
      </table>
    </div>
  </div>

  <div class="card" style="margin-top:16px">
    <h3>事件日志</h3>
    <div class="log">
      <div v-for="(l, i) in store.logs" :key="i" :class="l.kind">
        [{{ l.time }}] {{ l.message }}
      </div>
      <div v-if="!store.logs.length" class="hint">暂无事件。</div>
    </div>
  </div>
</template>
