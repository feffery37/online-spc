<script setup>
import { ref, onMounted } from 'vue'
import { SPC } from '../api'
import { store, log, chartTypeLabel } from '../store'

const emit = defineEmits(['refresh'])
const form = ref(blank())
const saving = ref(false)
const message = ref('')
const ruleSets = ref([])

function blank() {
  return {
    code: '', name: '', chartType: 1, subgroupSize: 5,
    target: 10, usl: 10.10, lsl: 9.90, unit: 'mm',
    machine: 'CA6140-01', product: 'CYL-LINER-A', process: 'FINISH-TURN',
    recipe: 'RCP-STD-01', ruleSetVer: 'WECO-8-V1', isEnabled: 1
  }
}

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

onMounted(async () => {
  try { ruleSets.value = (await SPC.ruleSets()).items ?? [] } catch { /* 后端未就绪 */ }
})
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

      <h3 style="margin-top:22px">可用规则集（Phase II）</h3>
      <table>
        <thead><tr><th>版本</th><th>规则</th><th>说明</th></tr></thead>
        <tbody>
          <tr v-for="s in ruleSets" :key="s.version">
            <td><b>{{ s.version }}</b></td>
            <td>{{ s.rules.join(' / ') }}</td>
            <td class="hint">{{ s.description }}</td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="card">
      <h3>新增 / 修改</h3>
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
</template>
