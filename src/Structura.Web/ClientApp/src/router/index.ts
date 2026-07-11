import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('../pages/LoginPage.vue'), meta: { public: true } },
    {
      path: '/change-password',
      name: 'change-password',
      component: () => import('../pages/ChangePasswordPage.vue'),
      meta: { allowsForcedChange: true },
    },
    {
      path: '/admin',
      component: () => import('../components/AppShell.vue'),
      children: [
        { path: '', redirect: { name: 'projects' } },
        { path: 'projects', name: 'projects', component: () => import('../pages/admin/ProjectsPage.vue') },
        {
          path: 'projects/:id',
          component: () => import('../pages/admin/project/ProjectLayout.vue'),
          children: [
            { path: '', redirect: { name: 'project-records' } },
            {
              path: 'records',
              name: 'project-records',
              component: () => import('../pages/admin/project/RecordsPage.vue'),
            },
            {
              path: 'schema',
              name: 'project-schema',
              component: () => import('../pages/admin/project/SchemaBuilderPage.vue'),
            },
            {
              path: 'ai',
              name: 'project-ai',
              component: () => import('../pages/admin/project/AiSettingsPage.vue'),
            },
            {
              path: 'import',
              name: 'project-import',
              component: () => import('../pages/admin/project/ImportPage.vue'),
            },
            {
              path: 'settings',
              name: 'project-settings',
              component: () => import('../pages/admin/project/ProjectSettingsPage.vue'),
            },
          ],
        },
        {
          path: 'users',
          name: 'users',
          component: () => import('../pages/admin/UsersPage.vue'),
          meta: { adminOnly: true },
        },
      ],
    },
    {
      path: '/review',
      component: () => import('../components/AppShell.vue'),
      children: [
        { path: '', name: 'review-home', component: () => import('../pages/review/ReviewHomePage.vue') },
      ],
    },
    { path: '/', redirect: () => ({ name: 'projects' }) },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  await auth.initialize()

  if (to.meta.public) {
    return auth.isAuthenticated ? { name: auth.homeRoute() } : true
  }

  if (!auth.isAuthenticated) {
    return { name: 'login', query: to.fullPath !== '/' ? { redirect: to.fullPath } : undefined }
  }

  if (auth.mustChangePassword && !to.meta.allowsForcedChange) {
    return { name: 'change-password' }
  }

  if (to.meta.adminOnly && !auth.isAdministrator) {
    return { name: auth.homeRoute() }
  }

  // Reviewers have no admin surface; everyone else has no reviewer surface yet.
  if (to.path.startsWith('/admin') && auth.isReviewer) {
    return { name: 'review-home' }
  }
  if (to.path.startsWith('/review') && !auth.isReviewer) {
    return { name: 'projects' }
  }

  return true
})

export default router
