<script setup>
import { ref, onMounted, computed } from 'vue'
import { SPC } from '../api'
import { store, log, chartTypeLabel } from '../store'

const emit = defineEmits(['refresh'])
const form = ref(blank())
const saving = ref(false)
const message = ref('')
const ruleSets = ref([])
const catalog = ref([])
const rsMessage = ref('')
const rsSaving = ref(false)
const bindCode = ref('')

const levels = ['CRITICAL', 'MAJOR', 'WARNING', 'INFO']
const currentChart = computed(() => store.charts.find(c => c.id === store.selectedChartId))

function blank() {
  return {
    code: '', name: '', chartType: 1, subgroupSize: 5,
    target: 10, usl: 10.10, lsl: 9.90, unit: 'mm',
    machine: 'CA6140-01', product: 'CYL-LINER-A', process: 'FINISH-TURN',
    recipe: 'RCP-STD-01', ruleSetVer: 'WECO-8-V1', isEnabled: 1
  }
}

function blankRuleSet() {
  return {
    code: '', name: '', description: '', createdBy: 'qe.li', enabled: true,
    rules: ['R1', 'R2', 'R3', 'R4'],
    severity: { R1: 'CRITICAL', R2: 'MAJOR', R3: 'MAJOR', R4: 'WARNING', R5: 'WARNING', R6: 'WARNING', R7: 'WARNING', R8: 'MAJOR' }
  }
}
const rsForm = ref(blankRuleSet())

function edit(c) {
  form.value = {
    code: c.code, name: c.name, chartType: c.chartType, subgroupSize: c.subgroupSize,
    target: c.target, usl: c.usl, lsl: c.lsl, unit: c.unit,
    machine: c.machine, product: c.product, process: c.process,
    recipe: c.recipe, ruleSetVer: c.ruleSetVer, isEnabled: c.isEnabled
  }
  message.value = `正在编辑 ${c.code}（同编码保存即更新）`
}

async function save() {
  saving.value = true
  try {
    const res = await SPC.saveChart(form.value)
    if (!res.ok) { message.value = `保存失败：${res.error}`; log(message.value, 'warn') }
    else {
      message.value = `已保存控制图 ${res.chart.code} (ID=${res.chart.id})`
      log(message.value, 'ok')
      form.value = blank()
      emit('refresh')
    }
  } catch (e) { message.value = e.message; log(e.message, 'warn') }
  finally { saving.value = false }
}

async function loadRuleSets() {
  try {
    const [sets, rules] = await Promise.all([SPC.ruleSets(), SPC.ruleCatalog()])
    ruleSets.value = sets.items ?? []
    catalog.value = rules.items ?? []
  } catch (e) { log(`读取规则集失败：${e.message}`, 'warn') }
}

function editRuleSet(v) {
  if (v.builtIn) {
    rsMessage.value = `${v.version} 是内置预设，只读。已复制其配置，可修改编码后另存为自定义规则集。`
    rsForm.value = {
      code: 'CUSTOM-' + v.version.replace(/[^A-Za-z0-9]/g, ''),
      name: v.name + ' (custom)',
      description: '基于 ' + v.version + ' 派生',
      createdBy: 'qe.li', enabled: true,
      rules: [...v.rules],
      severity: Object.fromEntries(catalog.value.map(r => [r.code, v.severity?.[r.code] ?? 'WARNING']))
    }
  } else {
    rsMessage.value = `正在编辑 ${v.version}（同编码保存即更新，变更会写审计日志）`
    rsForm.value = {
      code: v.version, name: v.name, description: v.description,
      createdBy: v.createdBy || 'qe.li', enabled: v.enabled,
      rules: [...v.rules],
      severity: Object.fromEntries(catalog.value.map(r => [r.code, v.severity?.[r.code] ?? 'WARNING']))
    }
  }
  bindCode.value = v.version
}

async function saveRuleSet() {
  rsSaving.value = true
  try {
    const selected = rsForm.value.rules
    const payload = {
      code: rsForm.value.code, name: rsForm.value.name,
      description: rsForm.value.description, createdBy: rsForm.value.createdBy,
      enabled: rsForm.value.enabled,
      rules: selected,
      severity: Object.fromEntries(selected.map(c => [c, rsForm.value.severity[c] ?? 'WARNING']))
    }
    const res = await SPC.saveRuleSet(payload)
    if (!res.ok) { rsMessage.value = `保存失败：${res.error}`; log(rsMessage.value, 'warn') }
    else {
      rsMessage.value = `规则集 ${res.item.version} 已保存（${res.item.rules.length} 条规则），可绑定到控制图`
      log(rsMessage.value, 'ok')
      bindCode.value = res.item.version
      rsForm.value = blankRuleSet()
      await loadRuleSets()
    }
  } catch (e) { rsMessage.value = e.message; log(e.message, 'warn') }
  finally { rsSaving.value = false }
}

async function bind(code) {
  if (!store.selectedChartId) { rsMessage.value = '请先在上方选择控制图'; return }
  try {
    const res = await SPC.bindRuleSet(store.selectedChartId, code)
    if (!res.ok) { rsMessage.value = `绑定失败：${res.error}`; log(rsMessage.value, 'warn') }
    else {
      rsMessage.value = `已把 ${code} 绑定到 ${currentChart.value?.code ?? '当前控制图'}，后续判异立即按新规则集执行`
      log(rsMessage.value, 'ok')
      emit('refresh')
    }
  } catch (e) { rsMessage.value = e.message; log(e.message, 'warn') }
}

function severityOf(v) {
  return Object.entries(v.severity ?? {}).map(([k, s]) => `${k}:${s}`).join('  ')
}

onMounted(loadRuleSets)
</script>

<template>
  <div class="note info">
    控制项、层别（机台 / 产品 / 工序 / 配方）、图型、子组大小、规格限全部配置化，不硬编码（F-01）。
    <b>规格限只存在 SPC_CHART，控制限只存在 SPC_LIMIT_VERSION</b>，物理分离避免最常见也最昂贵的混淆（方案 4.5）。
    Phase II 规则集按图配置：Phase I 恒用 R1~R4，Phase II 才启用所选子集。
  </div>

  <div class="grid cols-3-1">
    <div class="card">
      <h3>控制图定义</h3>
      <table>
        <thead><tr><th>编码</th><th>名称</th><th>图型</th><th>n</th><th>USL / LSL</th><th>规则集</th><th>状态</th><th /></tr></thead>
        <tbody>
          <tr v-for="c in store.charts" :key="c.id">
            <td><b>{{ c.code }}</b></td>
            <td>{{ c.name }}</td>
            <td>{{ chartTypeLabel(c.chartType) }}</td>
            <td>{{ c.subgroupSize }}</td>
            <td>{{ c.usl }} / {{ c.lsl }} {{ c.unit }}</td>
            <td><span class="pill active">{{ c.ruleSetVer }}</span></td>
            <td><span class="pill" :class="c.isEnabled ? 'active' : 'pending'">{{ c.isEnabled ? '启用' : '停用' }}</span></td>
            <td><button class="btn ghost" @click="edit(c)">编辑</button></td>
          </tr>
        </tbody>
      </table>
      <div v-if="!store.charts.length" class="hint" style="padding:16px 0">暂无控制图，右侧新增一个。</div>

      <h3 style="margin-top:22px">规则目录（阈值是 WECO/Nelson 标准常量，不可修改）</h3>
      <table>
        <thead><tr><th>规则</th><th>判定条件</th><th>Phase I</th></tr></thead>
        <tbody>
          <tr v-for="r in catalog" :key="r.code">
            <td><b>{{ r.code }}</b></td>
            <td>{{ r.description }} <span class="hint">· {{ r.threshold }}</span></td>
            <td><span class="pill" :class="r.phaseI ? 'active' : 'pending'">{{ r.phaseI ? '启用' : '不启用' }}</span></td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="card">
      <h3>新增 / 修改控制图</h3>
      <div class="form-grid" style="grid-template-columns:1fr">
        <div class="field"><label>控制图编码 *</label><input v-model="form.code" type="text" placeholder="CH-DEMO-02" /></div>
        <div class="field"><label>名称</label><input v-model="form.name" type="text" placeholder="缸套外径 · 精车" /></div>
        <div class="field"><label>图型</label>
          <select v-model.number="form.chartType">
            <option :value="1">Xbar-R（n=2~10）</option>
            <option :value="2">Xbar-S（n≥10 推荐）</option>
            <option :value="3">I-MR（n=1）</option>
          </select>
        </div>
        <div class="field"><label>子组大小 n</label><input v-model.number="form.subgroupSize" type="number" min="1" max="10" /></div>
        <div class="field"><label>Phase II 规则集</label>
          <select v-model="form.ruleSetVer">
            <option v-for="s in ruleSets" :key="s.version" :value="s.version">{{ s.version }}</option>
          </select>
        </div>
        <div class="field"><label>USL / LSL（规格限）</label>
          <div style="display:flex;gap:8px">
            <input v-model.number="form.usl" type="number" step="0.01" />
            <input v-model.number="form.lsl" type="number" step="0.01" />
          </div>
        </div>
        <div class="field"><label>目标值 / 单位</label>
          <div style="display:flex;gap:8px">
            <input v-model.number="form.target" type="number" step="0.1" />
            <input v-model="form.unit" type="text" style="width:80px" />
          </div>
        </div>
        <div class="field"><label>机台 / 产品 / 工序</label>
          <input v-model="form.machine" type="text" placeholder="CA6140-01" />
          <input v-model="form.product" type="text" placeholder="CYL-LINER-A" style="margin-top:6px" />
          <input v-model="form.process" type="text" placeholder="FINISH-TURN" style="margin-top:6px" />
        </div>
      </div>
      <div style="margin-top:14px;display:flex;gap:8px">
        <button class="btn" :disabled="saving || !form.code" @click="save">保存</button>
        <button class="btn ghost" @click="form = blank()">清空</button>
      </div>
      <p class="hint" style="margin-top:12px">{{ message }}</p>
    </div>
  </div>

  <div class="card" style="margin-top:16px">
    <div class="toolbar">
      <h3 style="margin:0">Phase II 规则集配置</h3>
      <span class="hint" style="margin-left:auto">
        内置预设只读；自定义规则集可逐条启停规则、设置严重级别，并绑定到控制图
      </span>
      <button class="btn ghost" @click="loadRuleSets">刷新</button>
    </div>

    <div class="grid cols-2">
      <div>
        <table>
          <thead><tr><th>编码</th><th>规则</th><th>严重级别</th><th>来源</th><th>状态</th><th /></tr></thead>
          <tbody>
            <tr v-for="v in ruleSets" :key="v.version">
              <td><b>{{ v.version }}</b><div class="hint">{{ v.description }}</div></td>
              <td>{{ v.rules.join(' / ') }}</td>
              <td class="hint">{{ severityOf(v) }}</td>
              <td><span class="pill" :class="v.builtIn ? 'active' : 'pending'">{{ v.builtIn ? '内置' : '自定义' }}</span></td>
              <td><span class="pill" :class="v.enabled ? 'active' : 'pending'">{{ v.enabled ? '启用' : '停用' }}</span></td>
              <td>
                <button class="btn ghost" style="padding:3px 8px" @click="editRuleSet(v)">{{ v.builtIn ? '复制' : '编辑' }}</button>
                <button class="btn ghost" style="padding:3px 8px;margin-top:4px" @click="bind(v.version)">绑定</button>
              </td>
            </tr>
          </tbody>
        </table>
        <div class="toolbar" style="margin-top:12px">
          <span class="hint">绑定到控制图：</span>
          <select v-model="bindCode" style="min-width:180px">
            <option v-for="v in ruleSets" :key="v.version" :value="v.version">{{ v.version }}</option>
          </select>
          <span class="badge">{{ currentChart?.code ?? '未选控制图' }} 当前 {{ currentChart?.ruleSetVer ?? '—' }}</span>
          <button class="btn" :disabled="!bindCode" @click="bind(bindCode)">应用</button>
        </div>
      </div>

      <div>
        <h3>自定义规则集</h3>
        <div class="form-grid" style="grid-template-columns:1fr 1fr">
          <div class="field"><label>编码 *（不可用 WECO- 前缀）</label><input v-model="rsForm.code" type="text" placeholder="LINE-A-RULES-V1" /></div>
          <div class="field"><label>名称 *</label><input v-model="rsForm.name" type="text" placeholder="A 线保守规则" /></div>
        </div>
        <div class="field" style="margin-top:8px"><label>说明</label><input v-model="rsForm.description" type="text" style="width:100%" /></div>
        <div class="form-grid" style="grid-template-columns:1fr 1fr;margin-top:8px">
          <div class="field"><label>配置人（审计留痕）</label><input v-model="rsForm.createdBy" type="text" /></div>
          <div class="field"><label>启用</label>
            <select v-model="rsForm.enabled"><option :value="true">启用</option><option :value="false">停用</option></select>
          </div>
        </div>

        <h3 style="margin-top:14px">逐条规则启停与严重级别</h3>
        <div v-for="r in catalog" :key="r.code"
             style="display:grid;grid-template-columns:74px 1fr 128px;gap:8px;align-items:center;padding:3px 0;border-bottom:1px solid #1e2a40">
          <label style="display:flex;gap:6px;align-items:center">
            <input type="checkbox" :value="r.code" v-model="rsForm.rules" /> <b>{{ r.code }}</b>
          </label>
          <span class="hint" :title="r.description">{{ r.threshold }}</span>
          <select v-model="rsForm.severity[r.code]" :disabled="!rsForm.rules.includes(r.code)">
            <option v-for="l in levels" :key="l" :value="l">{{ l }}</option>
          </select>
        </div>
        <div style="margin-top:12px;display:flex;gap:8px">
          <button class="btn" :disabled="rsSaving || !rsForm.code" @click="saveRuleSet">保存规则集</button>
          <button class="btn ghost" @click="rsForm = blankRuleSet()">清空</button>
        </div>
        <p class="hint" style="margin-top:10px">
          至少启用一条规则。变更会写 <code>SPC_AUDIT_LOG</code>；绑定控制图后立即对新子组生效，
          历史 Rule Out 仍保留当时命中的规则与严重级别。Phase I 建限恒用 R1~R4，不受此配置影响。
        </p>
        <p v-if="rsMessage" class="hint" style="margin-top:8px;color:var(--green)">{{ rsMessage }}</p>
      </div>
    </div>
  </div>
</template>
