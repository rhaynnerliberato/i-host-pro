import { Routes } from '@angular/router';

import { authGuard } from './core/auth/auth.guard';

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
        data: { titleKey: 'layout.nav.users' },
        loadComponent: () => import('./features/placeholder/placeholder').then((m) => m.Placeholder),
      },
      {
        path: 'condominiums',
        data: { titleKey: 'layout.nav.condominiums' },
        loadComponent: () => import('./features/placeholder/placeholder').then((m) => m.Placeholder),
      },
      {
        path: 'properties',
        data: { titleKey: 'layout.nav.properties' },
        loadComponent: () => import('./features/placeholder/placeholder').then((m) => m.Placeholder),
      },
      {
        path: 'reservations',
        data: { titleKey: 'layout.nav.reservations' },
        loadComponent: () => import('./features/placeholder/placeholder').then((m) => m.Placeholder),
      },
    ],
  },
];
