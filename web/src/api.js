export const BASE = 'http://localhost:5080'

export async function api(path, options = {}) {
  const res = await fetch(BASE + path, {
    headers: { 'Content-Type': 'application/json' },
    ...options
  })
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}: ${await res.text()}`)
  return res.json()
}

export const post = (path, body) => api(path, { method: 'POST', body: JSON.stringify(body ?? {}) })

export const SPC = {
  meta: () => api('/api/meta'),
  charts: () => api('/api/spc/charts'),
  saveChart: (chart) => post('/api/spc/charts', chart),
  overview: (id) => api(`/api/spc/charts/${id}/overview`),
  chartQuery: (id, take = 60) => api(`/api/spc/chart/query?chartId=${id}&take=${take}`),
  subgroups: (id, take = 60) => api(`/api/spc/subgroups?chartId=${id}&take=${take}`),
  measures: (id, take = 30) => api(`/api/spc/measure?chartId=${id}&take=${take}`),
  ingest: (items) => post('/api/spc/measure/ingest', { items }),

  // M2：Phase I 冻结基线 / 审批发布 / 回放 / 能力
  baselinePrecheck: (req) => post('/api/spc/baseline/precheck', req),
  baselineFreeze: (req) => post('/api/spc/baseline/freeze', req),
  baselinePublish: (req) => post('/api/spc/baseline/publish', req),
  baselineVersions: (id) => api(`/api/spc/baseline/versions?chartId=${id}`),
  baselineResolve: (id, eventTime) =>
    api(`/api/spc/baseline/resolve?chartId=${id}&eventTime=${encodeURIComponent(eventTime)}`),
  verifyHash: (versionId) => post(`/api/spc/baseline/verify-hash?versionId=${versionId}`),
  capability: (id) => api(`/api/spc/baseline/capability?chartId=${id}`),
  baselineData: (code, subgroups) =>
    post(`/api/spc/simulator/baseline-data?chartCode=${encodeURIComponent(code)}&subgroups=${subgroups}`),

  // M3：Phase II 判异 / Rule Out / 告警
  evaluate: (req) => post('/api/spc/evaluate', req),
  ruleOuts: (id, take = 100) => api(`/api/spc/ruleout?chartId=${id}&take=${take}`),
  ruleOutDetail: (id) => api(`/api/spc/ruleout/${id}`),
  alerts: (id) => api(`/api/spc/alerts?chartId=${id}`),
  ruleSets: () => api('/api/spc/rulesets'),

  // M4：OCAP + MES Hold 处置闭环
  ocaps: (id) => api(`/api/spc/ocap?chartId=${id}`),
  ocapDetail: (id) => api(`/api/spc/ocap/${id}`),
  simulateCallback: (ruleOutId) => post(`/api/spc/mes/simulate-callback?ruleOutId=${ruleOutId}`),
  reviewOcap: (req) => post('/api/spc/ocap/review', req),
  verifyOcap: (req) => post('/api/spc/ocap/verify', req),
  mesActions: (id) => api(`/api/spc/mes/actions?chartId=${id}`),
  mesState: () => api('/api/spc/mes/state'),
  auditTrace: (ruleOutId) => api(`/api/spc/audit/trace?ruleOutId=${ruleOutId}`),

  // M5：报表中心 / 合规取证 / 定时推送
  reportCapability: (id, bins = 20) => api(`/api/spc/reports/capability?chartId=${id}&bins=${bins}`),
  reportPeriods: (id, granularity = 'day', count = 7) =>
    api(`/api/spc/reports/periods?chartId=${id}&granularity=${granularity}&count=${count}`),
  reportCompliance: (id) => api(`/api/spc/reports/compliance?chartId=${id}`),
  reportAudit: (id) => api(`/api/spc/reports/audit?chartId=${id}`),
  reportSnapshots: (id) => api(`/api/spc/reports/snapshots?chartId=${id}`),
  reportGenerate: (id, type = 'DAILY') => post(`/api/spc/reports/generate?chartId=${id}&type=${type}`),
  reportSchedule: () => api('/api/spc/reports/schedule'),
  reportRunNow: () => post('/api/spc/reports/schedule/run'),
  reportExportUrl: (id, kind, granularity = 'day', count = 7) =>
    `${BASE}/api/spc/reports/export?chartId=${id}&kind=${kind}&granularity=${granularity}&count=${count}`,

  simulatorState: () => api('/api/spc/simulator/state'),
  simulatorStart: (req) => post('/api/spc/simulator/start', req),
  simulatorStop: () => post('/api/spc/simulator/stop'),
  replay: (chartCode, count) =>
    post(`/api/spc/simulator/replay?chartCode=${encodeURIComponent(chartCode)}&count=${count}`)
}
