<script setup>
import { ref, onMounted, watch, computed } from 'vue'
import { SPC } from '../api'
import { store, log } from '../store'

const chartId = computed(() => store.selectedChartId)
const chartCode = computed(() => store.charts.find(c => c.id === store.selectedChartId)?.code ?? 'CH-DEMO-01')
const busy = ref(false)
const error = ref('')
const message = ref('')
const precheck = ref(null)
const freeze = ref(null)
const versions = ref([])
const activeVersion = ref(null)
const hashCheck = ref(null)
const resolveResult = ref(null)
const capability = ref(null)
const causes = ref([])
const baselineSubgroups = ref(25)
const resolveTime = ref(new Date(Date.now() - 3600000).toISOString().slice(0, 19))
const form = ref({
  subgroupCount: 25,
  createdBy: 'qa.zhang',
  changeReason: '首次建立基线',
  approvedBy: 'qe.li',
  effectiveFrom: ''
})

const statusText = s => ({ 1: 'DRAFT', 2: 'PENDING', 3: 'ACTIVE', 4: 'EXPIRED' })[s] ?? String(s)
const short = h => h ? h.slice(0, 12) + '…' : '—'
const fmt = t => t ? new Date(t).toLocaleString('zh-CN', { hour12: false }) : '—'

function body() {
  return {
    chartId: chartId.value,
    subgroupCount: Number(form.value.subgroupCount),
    createdBy: form.value.createdBy,
    changeReason: form.value.changeReason,
    confirmedCauses: causes.value
      .filter(c => c.reason?.trim() && c.confirmedBy?.trim())
      .map(c => ({ subgroupId: c.subgroupId, reason: c.reason, confirmedBy: c.confirmedBy }))
  }
}

async function run(fn, successMessage) {
  busy.value = true
  error.value = ''
  try {
    const result = await fn()
    if (result) message.value = successMessage ?? '操作完成'
    return result
  } catch (e) {
    error.value = e.message
    log(e.message, 'warn')
    return null
  } finally { busy.value = false }
}

async function generateData() {
  const r = await run(() => SPC.baselineData(chartCode.value, Number(baselineSubgroups.value)),
    'Phase I 候选数据已生成，等待子组聚合完成后执行预检')
  if (r) log(`生成 ${r.points} 个稳定量测点（${r.subgroupCount} 组 × n=${r.subgroupSize}）`, 'ok')
}

async function runPrecheck() {
  const r = await run(() => SPC.baselinePrecheck(body()), '试算完成')
  precheck.value = r
  if (!r) return
  causes.value = (r.hits ?? []).map(h => ({ ...h, reason: '', confirmedBy: '' }))
  log(`试算：${r.candidateCount} 个候选子组，R1~R4 命中 ${r.hits?.length ?? 0} 点`, r.hits?.length ? 'warn' : 'ok')
}

async function runFreeze() {
  const r = await run(() => SPC.baselineFreeze(body()), '冻结完成：版本已生成，状态 PENDING')
  freeze.value = r
  if (!r) return
  if (r.ok) {
    log(`Phase I 冻结成功：V${r.version.versionNo} PENDING，指纹 ${short(r.version.dataHash)}`, 'ok')
    await loadVersions()
  } else {
    log(`建限被拒绝：${r.error}`, 'warn')
  }
}

async function loadVersions() {
  if (!chartId.value) return
  try {
    const r = await SPC.baselineVersions(chartId.value)
    versions.value = r.items ?? []
    activeVersion.value = r.active ?? null
  } catch (e) { log(`读取版本失败：${e.message}`, 'warn') }
}

async function publishVersion(v) {
  const payload = {
    chartId: chartId.value,
    versionId: v.id,
    approvedBy: form.value.approvedBy,
    changeReason: '人工审批发布：' + form.value.changeReason,
    effectiveFrom: form.value.effectiveFrom ? new Date(form.value.effectiveFrom).toISOString() : null
  }
  const r = await run(() => SPC.baselinePublish(payload), `V${v.versionNo} 已审批发布为 ACTIVE`)
  if (r?.ok) {
    log(`V${v.versionNo} 由 ${payload.approvedBy} 发布；旧版本 ${r.expiredVersionId ?? '无'} 置 EXPIRED`, 'ok')
    await Promise.all([loadVersions(), loadCapability()])
  } else if (r) log(`发布失败：${r.error}`, 'warn')
}

async function verify(v) {
  const r = await run(() => SPC.verifyHash(v.id), '指纹复核完成')
  hashCheck.value = r
  if (r) log(`A-02 指纹复核 V${v.versionNo}：${r.reproducible ? '一致，控制限可复现' : '不一致'}`, r.reproducible ? 'ok' : 'warn')
}

async function resolve() {
  const r = await run(() => SPC.baselineResolve(chartId.value, new Date(resolveTime.value).toISOString()), '版本定位完成')
  resolveResult.value = r
}

async function loadCapability() {
  if (!chartId.value) return
  try { capability.value = await SPC.capability(chartId.value) } catch { capability.value = null }
}

onMounted(async () => { await Promise.all([loadVersions(), loadCapability()]) })
watch(chartId, () => {
  precheck.value = null; freeze.value = null; hashCheck.value = null; resolveResult.value = null
  loadVersions(); loadCapability()
})
</script>

<template>
  <div class="note info">
    <b>Phase I 六步流程：</b>圈定候选子组（≥20 组）→ 试算试验控制限 → 仅 R1~R4 稳定性迭代 →
    冻结数据快照 + SHA256 指纹 → 生成 <b>PENDING</b> 版本 → 质量工程师<b>人工审批</b>发布为 ACTIVE。
    控制限是版本化数据，Phase II 只读不重算；换版只新增版本，旧版置 EXPIRED 且永不删除、计算字段永不修改。
  </div>

  <div class="grid cols-2">
    <div class="card">
      <h3>建限工作台 · {{ chartCode }}</h3>
      <div class="note" style="margin-bottom:14px">
        没有数据？先生成稳定过程候选数据（确定性无趋势噪声，可保证 Phase I 第 1 轮收敛）。
      </div>
      <div class="toolbar">
        <label>子组数</label>
        <input v-model.number="baselineSubgroups" type="number" min="20" max="200" style="width:90px" />
        <button class="btn ghost" :disabled="busy" @click="generateData">生成 Phase I 候选数据</button>
      </div>

      <div class="grid cols-2" style="gap:12px">
        <div class="field"><label>参与子组数（取最新 N 组）</label><input v-model.number="form.subgroupCount" type="number" min="20" max="500" /></div>
        <div class="field"><label>创建人 *</label><input v-model="form.createdBy" type="text" /></div>
        <div class="field"><label>换版原因</label><input v-model="form.changeReason" type="text" /></div>
        <div class="field"><label>审批人 *</label><input v-model="form.approvedBy" type="text" /></div>
      </div>

      <div style="display:flex;gap:10px;margin-top:16px;flex-wrap:wrap">
        <button class="btn ghost" :disabled="busy" @click="runPrecheck">① 试算预检（R1~R4）</button>
        <button class="btn" :disabled="busy" @click="runFreeze">② 执行冻结 → PENDING</button>
        <button class="btn ghost" :disabled="busy" @click="loadVersions">刷新版本</button>
      </div>
      <p v-if="message" class="hint" style="margin-top:10px;color:var(--green)">{{ message }}</p>
      <p v-if="error" class="hint" style="margin-top:10px;color:var(--red)">{{ error }}</p>

      <template v-if="precheck?.ok">
        <h3 style="margin-top:22px">试算试验控制限</h3>
        <div class="grid cols-4" style="gap:10px">
          <div class="kpi"><div class="label">CL</div><div class="value" style="font-size:18px">{{ Number(precheck.trialLimits.cl).toFixed(4) }}</div></div>
          <div class="kpi"><div class="label">UCL</div><div class="value" style="font-size:18px">{{ Number(precheck.trialLimits.ucl).toFixed(4) }}</div></div>
          <div class="kpi"><div class="label">LCL</div><div class="value" style="font-size:18px">{{ Number(precheck.trialLimits.lcl).toFixed(4) }}</div></div>
          <div class="kpi"><div class="label">候选 / 命中</div><div class="value" style="font-size:18px">{{ precheck.candidateCount }} / {{ precheck.hits.length }}</div></div>
        </div>
        <p class="hint" style="margin:10px 0">{{ precheck.note }}</p>
        <table v-if="precheck.hits.length">
          <thead><tr><th>SEQ</th><th>子组 ID</th><th>X̄</th><th>命中规则</th><th>已确认特殊原因（必填才可剔除）</th><th>确认人</th></tr></thead>
          <tbody>
            <tr v-for="c in causes" :key="c.subgroupId">
              <td>#{{ c.seq }}</td><td>{{ c.subgroupId }}</td><td>{{ Number(c.xbar).toFixed(4) }}</td>
              <td><span class="pill pending">{{ c.rules.join(' / ') }}</span></td>
              <td><input v-model="c.reason" type="text" placeholder="如：量具校准偏移 CAL-2026-1113-07" style="width:100%" /></td>
              <td><input v-model="c.confirmedBy" type="text" placeholder="qa.zhang" style="width:110px" /></td>
            </tr>
          </tbody>
        </table>
        <p class="hint" style="margin-top:8px">未登记原因的命中点将导致建限被<b>直接拒绝</b>——禁止为让图好看而删点（方案 5.3 / 附录 A）。</p>
      </template>

      <template v-if="freeze">
        <h3 style="margin-top:22px">冻结结果</h3>
        <div v-if="freeze.ok">
          <div class="grid cols-4" style="gap:10px">
            <div class="kpi"><div class="label">版本</div><div class="value" style="font-size:18px">V{{ freeze.version.versionNo }}</div><div class="sub"><span class="pill pending">PENDING</span></div></div>
            <div class="kpi"><div class="label">CL / UCL / LCL</div><div class="value" style="font-size:14px;padding-top:8px">{{ Number(freeze.limits.cl).toFixed(4) }} / {{ Number(freeze.limits.ucl).toFixed(4) }} / {{ Number(freeze.limits.lcl).toFixed(4) }}</div></div>
            <div class="kpi"><div class="label">σ̂ / 有效子组</div><div class="value" style="font-size:18px">{{ Number(freeze.limits.sigmaHat).toFixed(5) }} / {{ freeze.includedCount }}</div></div>
            <div class="kpi"><div class="label">迭代轮次</div><div class="value" style="font-size:18px">{{ freeze.version.iterations }}</div></div>
          </div>
          <p class="hint" style="margin-top:12px">SHA256 数据集指纹：<code>{{ freeze.version.dataHash }}</code></p>
          <p class="hint">{{ freeze.note }}</p>
          <div v-if="freeze.exclusions?.length" style="margin-top:12px">
            <h3>剔除台账</h3>
            <table>
              <thead><tr><th>轮次</th><th>SEQ</th><th>规则</th><th>已确认特殊原因</th><th>确认人</th></tr></thead>
              <tbody>
                <tr v-for="(e, i) in freeze.exclusions" :key="i">
                  <td>{{ e.iteration }}</td><td>#{{ e.seq }}</td><td>R{{ e.rule }}</td>
                  <td>{{ e.reason }}</td><td>{{ e.confirmedBy }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
        <div v-else class="note" style="margin-top:10px"><b>建限被拒绝：</b>{{ freeze.error }}</div>
      </template>
    </div>

    <div class="card">
      <h3>版本时间轴 · 审批发布</h3>
      <table>
        <thead><tr><th>V#</th><th>状态</th><th>指纹</th><th>CL / UCL / LCL</th><th>审批人</th><th>生效 / 失效</th><th>操作</th></tr></thead>
        <tbody>
          <tr v-for="v in versions" :key="v.id">
            <td><b>V{{ v.versionNo }}</b></td>
            <td><span class="pill" :class="v.status === 3 ? 'active' : 'pending'">{{ statusText(v.status) }}</span></td>
            <td><code>{{ short(v.dataHash) }}</code></td>
            <td>{{ Number(v.cl).toFixed(3) }} / {{ Number(v.ucl).toFixed(3) }} / {{ Number(v.lcl).toFixed(3) }}</td>
            <td>{{ v.approvedBy || '—' }}</td>
            <td>{{ fmt(v.effectiveFrom) }}<br /><span class="hint">{{ fmt(v.expiredTime) }}</span></td>
            <td>
              <button v-if="v.status === 2" class="btn" style="padding:5px 10px" :disabled="busy" @click="publishVersion(v)">审批发布</button>
              <button v-else class="btn ghost" style="padding:5px 10px" :disabled="busy" @click="verify(v)">校验指纹</button>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-if="!versions.length" class="hint" style="padding:14px 0">暂无版本。先生成候选数据并执行冻结。</div>

      <div class="field" style="margin-top:14px">
        <label>生效时刻（留空 = 立即生效；换版演练可指定过去/未来时刻）</label>
        <input v-model="form.effectiveFrom" type="datetime-local" step="1" style="width:100%" />
      </div>
      <p class="hint" style="margin-top:10px">
        发布在同一事务内完成：旧 ACTIVE → EXPIRED + 失效时刻，新版本 → ACTIVE + 审批人 + 生效时刻；
        数据库唯一索引保证并发下仍只有一个 ACTIVE。
      </p>

      <template v-if="hashCheck">
        <h3 style="margin-top:20px">A-02 数据指纹复核 · V{{ hashCheck.versionNo }}</h3>
        <p class="hint">存储指纹：<code>{{ hashCheck.storedHash }}</code></p>
        <p class="hint">重算指纹：<code>{{ hashCheck.recomputedHash }}</code></p>
        <p :style="{ color: hashCheck.reproducible ? 'var(--green)' : 'var(--red)' }">
          {{ hashCheck.reproducible ? '一致：同一数据集与参数重算得到完全相同的控制限与哈希。' : '不一致：数据或参数已被改动。' }}
        </p>
      </template>

      <h3 style="margin-top:20px">历史版本回放（按事件时间）</h3>
      <div class="toolbar" style="margin-bottom:10px">
        <input v-model="resolveTime" type="datetime-local" step="1" />
        <button class="btn ghost" :disabled="busy" @click="resolve">解析当时版本</button>
      </div>
      <p v-if="resolveResult" class="hint">
        {{ resolveResult.resolved ? `命中 V${resolveResult.version.versionNo}（状态 ${statusText(resolveResult.version.status)}，哈希 ${short(resolveResult.version.dataHash)}）` : '该时刻无生效版本' }}
        · {{ resolveResult.note }}
      </p>

      <h3 style="margin-top:20px">能力指数（冻结 σ̂）</h3>
      <div v-if="capability?.available">
        <div class="grid cols-4" style="gap:10px">
          <div class="kpi"><div class="label">Cp</div><div class="value" style="font-size:19px">{{ Number(capability.indices.cp ?? 0).toFixed(3) }}</div></div>
          <div class="kpi"><div class="label">Cpk</div><div class="value" style="font-size:19px;color:var(--cyan)">{{ Number(capability.indices.cpk ?? 0).toFixed(3) }}</div></div>
          <div class="kpi"><div class="label">Ppk</div><div class="value" style="font-size:19px">{{ Number(capability.indices.ppk ?? 0).toFixed(3) }}</div></div>
          <div class="kpi"><div class="label">等级</div><div class="value" style="font-size:14px;padding-top:8px">{{ capability.indices.grade }}</div></div>
        </div>
        <p class="hint" style="margin-top:10px">{{ capability.controlBasis }}</p>
      </div>
      <p v-else class="hint">{{ capability?.reason ?? '暂无生效版本：禁止现算控制限，也禁止计算 Cpk。' }}</p>
    </div>
  </div>

  <div class="card" style="margin-top:16px">
    <h3>事件日志</h3>
    <div class="log">
      <div v-for="(l, i) in store.logs" :key="i" :class="l.kind">[{{ l.time }}] {{ l.message }}</div>
    </div>
  </div>
</template>
