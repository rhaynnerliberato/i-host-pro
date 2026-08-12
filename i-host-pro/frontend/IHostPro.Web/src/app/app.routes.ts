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
    ],
  },
];
