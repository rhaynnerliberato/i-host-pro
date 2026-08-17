import { Routes } from '@angular/router';

import { authGuard } from './core/auth/auth.guard';
import { permissionGuard } from './core/auth/permission.guard';

export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login/login').then((m) => m.Login),
  },
  {
    path: 'forbidden',
    loadComponent: () => import('./features/forbidden/forbidden').then((m) => m.Forbidden),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/admin-layout/admin-layout').then((m) => m.AdminLayout),
    children: [
      {
        path: '',
        loadComponent: () => import('./features/home/home').then((m) => m.Home),
      },
      {
        path: 'users',
        canActivate: [permissionGuard],
        data: { titleKey: 'layout.nav.users', permissions: ['USERS:MANAGE'] },
        loadComponent: () => import('./features/users/users-list/users-list').then((m) => m.UsersList),
      },
      {
        path: 'condominiums',
        canActivate: [permissionGuard],
        data: { titleKey: 'layout.nav.condominiums', permissions: ['PROPERTIES:MANAGE'] },
        loadComponent: () =>
          import('./features/property-management/condominiums/condominiums-list/condominiums-list').then((m) => m.CondominiumsList),
      },
      {
        path: 'properties',
        canActivate: [permissionGuard],
        data: { titleKey: 'layout.nav.properties', permissions: ['PROPERTIES:MANAGE'] },
        loadComponent: () => import('./features/property-management/properties/properties-list/properties-list').then((m) => m.PropertiesList),
      },
      {
        path: 'reservations',
        canActivate: [permissionGuard],
        data: { titleKey: 'layout.nav.reservations', permissions: ['RESERVATIONS:MANAGE'] },
        loadComponent: () => import('./features/reservations/reservations-list/reservations-list').then((m) => m.ReservationsList),
      },
      {
        path: 'policies',
        canActivate: [permissionGuard],
        data: { titleKey: 'layout.nav.policies', permissions: ['POLICIES:READ', 'POLICIES:MANAGE'] },
        loadComponent: () => import('./features/policies/policies-list/policies-list').then((m) => m.PoliciesList),
      },
      {
        path: 'housekeeping',
        canActivate: [permissionGuard],
        data: { titleKey: 'layout.nav.housekeeping', permissions: ['CLEANINGS:MANAGE'] },
        loadComponent: () => import('./features/housekeeping/cleanings-list/cleanings-list').then((m) => m.CleaningsList),
      },
      {
        path: 'schedule',
        canActivate: [permissionGuard],
        data: { titleKey: 'layout.nav.schedule', permissions: ['SCHEDULE:MANAGE', 'SCHEDULE:READ'] },
        loadComponent: () => import('./features/schedule/schedule-calendar/schedule-calendar').then((m) => m.ScheduleCalendar),
      },
      {
        path: 'dashboard',
        canActivate: [permissionGuard],
        data: { titleKey: 'layout.nav.dashboard', permissions: ['DASHBOARD:MANAGE', 'DASHBOARD:READ'] },
        loadComponent: () => import('./features/dashboard/dashboard-overview/dashboard-overview').then((m) => m.DashboardOverview),
      },
    ],
  },
  {
    // Portal da Faxineira (Fase 6, Incremento 2A) — deliberately a SEPARATE
    // route tree from the admin layout above, its own dedicated mobile-first
    // shell, never nested under AdminLayout (approval §5-6).
    path: 'my-cleanings',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/portal-shell/portal-shell').then((m) => m.PortalShell),
    children: [
      {
        path: '',
        canActivate: [permissionGuard],
        data: { permissions: ['CLEANINGS:MANAGE:OWN_CLEANING'] },
        loadComponent: () => import('./features/portal/my-cleanings-list/my-cleanings-list').then((m) => m.MyCleaningsList),
      },
      {
        path: ':cleaningId',
        canActivate: [permissionGuard],
        data: { permissions: ['CLEANINGS:MANAGE:OWN_CLEANING'] },
        loadComponent: () => import('./features/portal/my-cleaning-detail/my-cleaning-detail').then((m) => m.MyCleaningDetail),
      },
    ],
  },
];
