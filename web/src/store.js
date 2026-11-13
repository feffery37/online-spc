import { reactive } from 'vue'

export const store = reactive({
  meta: null,
  charts: [],
  selectedChartId: null,
  subgroups: [],
  lastSubgroup: null,
  subgroupEvents: 0,
  measureEvents: 0,
  status: 'disconnected',
  simulator: null,
  ruleOuts: [],
  ruleOutEvents: 0,
  lastRuleOut: null,
  alertCounts: { critical: 0, major: 0, warning: 0 },
  dispositions: [],
  dispositionEvents: 0,
  reports: [],
  reportEvents: 0,
  logs: []
})

export function log(message, kind = '') {
  store.logs.unshift({
    time: new Date().toLocaleTimeString('zh-CN', { hour12: false }),
    message,
    kind
  })
  if (store.logs.length > 200) store.logs.pop()
}

export function chartTypeLabel(t) {
  return ({ 1: 'Xbar-R', 2: 'Xbar-S', 3: 'I-MR', 4: 'p', 5: 'np', 6: 'c', 7: 'u' })[t] ?? String(t)
}

export function severityText(s) {
  return ({ 4: 'CRITICAL', 3: 'MAJOR', 2: 'WARNING', 1: 'INFO' })[s] ?? String(s)
}

export function ocapStatusText(s) {
  return ({
    1: 'DETECTED', 2: 'ALERTED', 3: 'ACTION_SENT', 4: 'MES_ACKED',
    5: 'IN_REVIEW', 6: 'VERIFIED', 7: 'CLOSED', 8: 'ESCALATED'
  })[s] ?? String(s)
}
