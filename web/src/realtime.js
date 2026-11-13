import * as signalR from '@microsoft/signalr'
import { BASE } from './api'

export function connectRealtime(handlers, onStatus) {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${BASE}/hubs/spc`)
    .withAutomaticReconnect([0, 1000, 3000, 5000])
    .configureLogging(signalR.LogLevel.Warning)
    .build()

  connection.on('subgroup', (payload) => handlers.onSubgroup?.(payload))
  connection.on('measure', (payload) => handlers.onMeasure?.(payload))
  connection.on('notice', (payload) => handlers.onNotice?.(payload))
  connection.on('ruleout', (payload) => handlers.onRuleOut?.(payload))
  connection.on('disposition', (payload) => handlers.onDisposition?.(payload))
  connection.on('report', (payload) => handlers.onReport?.(payload))

  connection.onreconnecting(() => onStatus?.('reconnecting'))
  connection.onreconnected(() => onStatus?.('connected'))
  connection.onclose(() => onStatus?.('disconnected'))

  connection.start()
    .then(() => onStatus?.('connected'))
    .catch(() => onStatus?.('disconnected'))

  return connection
}
