<script setup>
import { ref, onMounted, watch, computed } from 'vue'
import { SPC } from '../api'
import { store, log, ocapStatusText } from '../store'

const chartId = computed(() => store.selectedChartId)
const ocaps = ref([])
const actions = ref([])
const mesState = ref(null)
const selected = ref(null)
const trace = ref(null)
const busy = ref(false)
const message = ref('')
const error = ref('')
const verifyGroupId = ref(null)
const review = ref({
  cause: '刀具磨损 / 量具漂移导致均值偏移',
  countermeasure: '换刀并重新对刀，首件检验合格后复产',
  decision: '返工',
  signedBy: 'qe.li'
})

const stats = computed(() => ({
  total: ocaps.value.length,
  pendingAck: ocaps.value.filter(o => o.status === 3).length,
  pendingReview: ocaps.value.filter(o => o.status === 4 || o.status === 5).length,
  closed: ocaps.value.filter(o => o.status === 7).length
}))

const selectedActions = computed(() =>
  selected.value ? actions.value.filter(a => a.ruleOutId === selected.value.ruleOutId) : [])

const actionText = t => ({ 1: 'LOT Hold', 2: 'EQP Hold', 3: '停机', 4: '通知' })[t] ?? String(t)
const actionStatusText = s => ({ 1: 'PENDING', 2: 'SENT', 3: 'ACKED', 4: 'FAILED' })[s] ?? String(s)

async function load() {
  if (!chartId.value) return
  try {
    const [o, a, m] = await Promise.all([
      SPC.ocaps(chartId.value), SPC.mesActions(chartId.value), SPC.mesState()
    ])
    ocaps.value = o.items ?? []
    actions.value = a.items ?? []
    mesState.value = m
    if (selected.value) {
      const fresh = ocaps.value.find(x => x.id === selected.value.id)
      if (fresh) selected.value = fresh
    }
  } catch (e) { log(`读取处置数据失败：${e.message}`, 'warn') }
}

async function select(o) {
  selected.value = o
  try { trace.value = await SPC.auditTrace(o.ruleOutId) }
  catch (e) { log(`读取审计链失败：${e.message}`, 'warn') }
}

async function run(fn, successMessage) {
  busy.value = true
  error.value = ''
  try {
    const r = await fn()
    if (r?.ok === false) { error.value = r.error ?? '操作失败'; log(error.value, 'warn') }
    else { message.value = successMessage; log(successMessage, 'ok') }
    await load()
    if (selected.value) await select(selected.value)
  } catch (e) { error.value = e.message; log(e.message, 'warn') }
  finally { busy.value = false }
}

const callback = () => run(
  () => SPC.simulateCallback(selected.value.ruleOutId),
  `已模拟 MES 解冻并回写全部 Hold 回执（Rule Out #${selected.value.ruleOutId}）`)

const reviewOcap = () => run(
  () => SPC.reviewOcap({ ocapId: selected.value.id, ...review.value }),
  `OCAP ${selected.value.ocapNo} 已由 ${review.value.signedBy} 签核`)

const verifyOcap = () => run(
  () => SPC.verifyOcap({ ocapId: selected.value.id, verificationSubgroupId: Number(verifyGroupId.value) }),
  '验证子组复判完成，闭环状态已更新')

async function pickLatestSubgroup() {
  try {
    const s = await SPC.subgroups(chartId.value, 1)
    verifyGroupId.value = s.items?.[0]?.id ?? null
    message.value = verifyGroupId.value ? `已选取最新子组 ID=${verifyGroupId.value}` : '暂无可用子组'
  } catch (e) { log(e.message, 'warn') }
}

onMounted(load)
watch(chartId, () => { selected.value = null; trace.value = null; load() })
watch(() => store.dispositionEvents, () => load())
</script>

<template>
  <div class="note info">
    <b>工业级闭环（方案 6.3 / 6.4）：</b>
    采集 → 子组聚合 → 冻结限判异 → Rule Out →
    <b>OCAP 处置单 + MES LOT/EQP Hold / 停机 / 通知</b> → MES 回执回写 → 人工签核 →
    验证子组复判 → <b>CLOSED</b>。
    状态机 <code>DETECTED → ALERTED → ACTION_SENT → MES_ACKED → IN_REVIEW → VERIFIED/CLOSED</code>，
    验证仍失控则 <code>ESCALATED</code>，绝不假装关闭。
  </div>

  <div class="grid cols-4" style="margin-bottom:16px">
    <div class="card kpi">
      <div class="label">OCAP 处置单</div><div class="value" style="color:var(--cyan)">{{ stats.total }}</div>
      <div class="sub">每条 Rule Out 自动开单</div>
    </div>
    <div class="card kpi">
      <div class="label">待 MES 回执</div><div class="value" style="color:var(--amber)">{{ stats.pendingAck }}</div>
      <div class="sub">ACTION_SENT · Hold 已下发</div>
    </div>
    <div class="card kpi">
      <div class="label">待签核 / 验证</div><div class="value" style="color:#fb923c">{{ stats.pendingReview }}</div>
      <div class="sub">MES_ACKED / IN_REVIEW</div>
    </div>
    <div class="card kpi">
      <div class="label">已闭环 CLOSED</div><div class="value" style="color:var(--green)">{{ stats.closed }}</div>
      <div class="sub">处置回执 + 签核 + 验证通过</div>
    </div>
  </div>

  <div class="grid cols-3-1">
    <div class="card">
      <div class="toolbar">
        <h3 style="margin:0">OCAP 处置台账</h3>
        <button class="btn ghost" style="margin-left:auto" @click="load">刷新</button>
      </div>
      <table>
        <thead><tr><th>单号</th><th>判异 ID</th><th>状态</th><th>责任人</th><th>处置结论</th><th>验证子组</th><th>关闭时间</th><th /></tr></thead>
        <tbody>
          <tr v-for="o in ocaps" :key="o.id" :style="selected?.id === o.id ? 'background:#172b3e' : ''">
            <td><b>{{ o.ocapNo }}</b></td>
            <td>#{{ o.ruleOutId }}</td>
            <td><span class="pill" :class="o.status === 7 ? 'active' : 'pending'">{{ ocapStatusText(o.status) }}</span></td>
            <td>{{ o.assignee }}<span v-if="o.signedBy"> / {{ o.signedBy }}</span></td>
            <td>{{ o.decision || '—' }}</td>
            <td>{{ o.verificationSubgroupId ?? '—' }}</td>
            <td>{{ o.closedAt ? new Date(o.closedAt).toLocaleTimeString('zh-CN', { hour12: false }) : '—' }}</td>
            <td><button class="btn ghost" style="padding:4px 9px" @click="select(o)">选择</button></td>
          </tr>
        </tbody>
      </table>
      <div v-if="!ocaps.length" class="hint" style="padding:16px 0">
        暂无处置单。先完成基线审批发布，再产生超限数据即可自动开单并下发 MES Hold。
      </div>
    </div>

    <div class="card">
      <h3>处置操作台</h3>
      <template v-if="!selected">
        <p class="hint">从左侧选择一张 OCAP 处置单，按状态机逐步推进闭环。</p>
      </template>
      <template v-else>
        <div class="detail-row"><span>单号</span><span>{{ selected.ocapNo }}</span></div>
        <div class="detail-row"><span>状态</span><span><span class="pill" :class="selected.status === 7 ? 'active' : 'pending'">{{ ocapStatusText(selected.status) }}</span></span></div>
        <div class="detail-row"><span>处置时限</span><span>{{ selected.dueTime ? new Date(selected.dueTime).toLocaleString('zh-CN', { hour12:false }) : '—' }}</span></div>

        <h3 style="margin-top:16px">① MES 回执</h3>
        <table v-if="selectedActions.length" style="margin-bottom:10px">
          <thead><tr><th>动作</th><th>MES 单号</th><th>状态</th></tr></thead>
          <tbody>
            <tr v-for="a in selectedActions" :key="a.id">
              <td>{{ actionText(a.actionType) }}</td>
              <td><code>{{ a.mesRef || '—' }}</code></td>
              <td><span class="pill" :class="a.status === 3 ? 'active' : 'pending'">{{ actionStatusText(a.status) }}</span></td>
            </tr>
          </tbody>
        </table>
        <button class="btn" :disabled="busy || selected.status > 3" @click="callback">模拟 MES 解冻 + 全部回执</button>
        <p class="hint" style="margin-top:6px">生产环境由真实 MES 调用 <code>POST /api/spc/ruleout/callback</code>；收齐全部回执才允许签核。</p>

        <h3 style="margin-top:18px">② 人工签核（OCAP）</h3>
        <div class="field"><label>特殊原因</label><input v-model="review.cause" type="text" style="width:100%" /></div>
        <div class="field" style="margin-top:8px"><label>纠正对策</label><input v-model="review.countermeasure" type="text" style="width:100%" /></div>
        <div class="field" style="margin-top:8px"><label>不合格品评审结论</label>
          <select v-model="review.decision" style="width:100%">
            <option>返工</option><option>返修</option><option>让步接收</option><option>报废</option>
          </select>
        </div>
        <div class="field" style="margin-top:8px"><label>签核人</label><input v-model="review.signedBy" type="text" style="width:100%" /></div>
        <button class="btn" style="margin-top:10px" :disabled="busy || selected.status !== 4" @click="reviewOcap">提交签核</button>

        <h3 style="margin-top:18px">③ 验证子组复判 → 关闭</h3>
        <p class="hint">验证子组必须晚于判异事件、且用当时生效的冻结限复判；受控才 CLOSED，仍失控则 ESCALATED。</p>
        <div class="toolbar" style="margin-top:8px">
          <input v-model.number="verifyGroupId" type="number" placeholder="验证子组 ID" style="width:140px" />
          <button class="btn ghost" @click="pickLatestSubgroup">取最新子组</button>
        </div>
        <button class="btn" style="margin-top:8px" :disabled="busy || selected.status !== 5 || !verifyGroupId" @click="verifyOcap">复判并关闭</button>
      </template>
      <p v-if="message" class="hint" style="margin-top:12px;color:var(--green)">{{ message }}</p>
      <p v-if="error" class="hint" style="margin-top:12px;color:var(--red)">{{ error }}</p>
    </div>
  </div>

  <div class="grid cols-2" style="margin-top:16px">
    <div class="card">
      <h3>MES 侧 Hold 状态（模拟器）</h3>
      <table>
        <thead><tr><th>MES 单号</th><th>动作</th><th>批次 / SN</th><th>设备</th><th>状态</th></tr></thead>
        <tbody>
          <tr v-for="h in mesState?.holds ?? []" :key="h.mesRef">
            <td><code>{{ h.mesRef }}</code></td>
            <td>{{ actionText(h.actionType) }}</td>
            <td>{{ h.lotId || '—' }} / {{ h.sn || '—' }}</td>
            <td>{{ h.equipment || '—' }}</td>
            <td><span class="pill" :class="h.status === 'RELEASED' ? 'active' : 'pending'">{{ h.status }}</span></td>
          </tr>
        </tbody>
      </table>
      <div v-if="!mesState?.holds?.length" class="hint" style="padding:12px 0">暂无 Hold。判异产生后自动下发。</div>
      <p class="hint" style="margin-top:10px">{{ mesState?.note }}</p>
      <h3 style="margin-top:16px">动作策略</h3>
      <p class="hint">
        R1 / 高等级连续规则 → LOT Hold；同一控制图 30 分钟内 ≥3 次判异 → EQP Hold；
        R1 且 CRITICAL → 停机；所有判异 → 通知 + OCAP 签核。策略与判异解耦，可配置。
      </p>
    </div>

    <div class="card">
      <h3>A-08 全链路审计追溯</h3>
      <template v-if="trace">
        <div style="margin-bottom:10px">
          <span class="pill" :class="trace.complete ? 'active' : 'pending'">
            {{ trace.complete ? '闭环完整' : '尚缺 ' + trace.missing.length + ' 环' }}
          </span>
          <span v-if="trace.missing.length" class="hint"> · {{ trace.missing.join(' / ') }}</span>
        </div>
        <div class="detail-row"><span>用哪一版限值</span><span>{{ trace.answers.whichLimitVersion }}</span></div>
        <div class="detail-row"><span>由哪批数据算出</span><span>{{ trace.answers.whichDataset }}</span></div>
        <div class="detail-row"><span>谁审批</span><span>{{ trace.answers.whoApproved }}</span></div>
        <div class="detail-row"><span>命中哪条规则</span><span>{{ trace.answers.whichRule }}</span></div>
        <div class="detail-row"><span>触发什么动作</span><span>{{ trace.answers.whatAction.join(' / ') }}</span></div>
        <div class="detail-row"><span>谁处置</span><span>{{ trace.answers.whoDisposed }}</span></div>
        <div class="detail-row"><span>结论 / 验证子组</span><span>{{ trace.answers.conclusion }} / {{ trace.answers.verification ?? '—' }}</span></div>
        <h3 style="margin-top:14px">审计事件流</h3>
        <div class="log" style="height:150px">
          <div v-for="e in trace.auditEvents" :key="e.id">
            [{{ new Date(e.eventTime).toLocaleTimeString('zh-CN', { hour12:false }) }}] {{ e.eventType }} · {{ e.entityType }}#{{ e.entityId }}
          </div>
        </div>
        <p class="hint" style="margin-top:8px">{{ trace.note }}</p>
      </template>
      <p v-else class="hint">选择一张 OCAP 处置单即可查看完整审计链。</p>
    </div>
  </div>

  <div class="card" style="margin-top:16px">
    <h3>实时处置事件 / 日志</h3>
    <div class="log">
      <div v-for="(l, i) in store.logs" :key="i" :class="l.kind">[{{ l.time }}] {{ l.message }}</div>
    </div>
  </div>
</template>
