import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { ref } from 'vue'
import { useAuthStore } from '../stores/auth'

/** Live connection state — pages switch to polling when this is false. */
export const realtimeConnected = ref(false)

let connection: HubConnection | null = null
let joinedProject: string | null = null

async function ensureConnection(): Promise<HubConnection> {
  if (connection && connection.state !== HubConnectionState.Disconnected) return connection

  const auth = useAuthStore()
  connection = new HubConnectionBuilder()
    .withUrl('/hubs/progress', { accessTokenFactory: () => auth.accessToken ?? '' })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build()

  connection.onreconnected(async () => {
    realtimeConnected.value = true
    if (joinedProject) await connection!.invoke('JoinProject', joinedProject).catch(() => {})
  })
  connection.onreconnecting(() => (realtimeConnected.value = false))
  connection.onclose(() => (realtimeConnected.value = false))

  try {
    await connection.start()
    realtimeConnected.value = true
  } catch {
    realtimeConnected.value = false
  }
  return connection
}

/** Joins a project group and registers a handler; returns a cleanup function. */
export async function subscribeToProject(
  projectId: string,
  handlers: Record<string, (payload: any) => void>,
): Promise<() => void> {
  const conn = await ensureConnection()
  for (const [event, handler] of Object.entries(handlers)) conn.on(event, handler)

  if (conn.state === HubConnectionState.Connected) {
    joinedProject = projectId
    await conn.invoke('JoinProject', projectId).catch(() => {})
  }

  return () => {
    for (const [event, handler] of Object.entries(handlers)) conn.off(event, handler)
    if (joinedProject === projectId) {
      joinedProject = null
      if (conn.state === HubConnectionState.Connected) conn.invoke('LeaveProject', projectId).catch(() => {})
    }
  }
}
