import { useAuthStore } from '../stores/auth'

export interface ProblemDetails {
  status: number
  code: string
  detail?: string
  errors?: Record<string, string[]>
}

export class ApiError extends Error {
  status: number
  code: string
  detail?: string
  errors?: Record<string, string[]>

  constructor(problem: ProblemDetails) {
    super(problem.detail ?? problem.code)
    this.status = problem.status
    this.code = problem.code
    this.detail = problem.detail
    this.errors = problem.errors
  }

  /** First field-level message, if the server returned validation errors. */
  fieldError(field: string): string | undefined {
    return this.errors?.[field]?.[0]
  }
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  body?: unknown
  /** Set false for anonymous endpoints (login/refresh). */
  auth?: boolean
}

async function parseError(response: Response): Promise<ApiError> {
  try {
    const problem = (await response.json()) as ProblemDetails
    return new ApiError({ ...problem, status: problem.status ?? response.status })
  } catch {
    return new ApiError({ status: response.status, code: `http_${response.status}` })
  }
}

export async function api<T = void>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, auth = true } = options
  const store = useAuthStore()

  const doFetch = () =>
    fetch(path, {
      method,
      headers: {
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        ...(auth && store.accessToken ? { Authorization: `Bearer ${store.accessToken}` } : {}),
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })

  let response = await doFetch()

  // Access token expired: try one silent refresh, then retry the request once.
  if (response.status === 401 && auth && store.refreshToken) {
    const refreshed = await store.tryRefresh()
    if (refreshed) response = await doFetch()
  }

  if (response.status === 401 && auth) {
    store.clearSession()
    throw await parseError(response)
  }

  if (!response.ok) throw await parseError(response)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}
