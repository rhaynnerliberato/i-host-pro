import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { TranslocoPipe } from '@jsverse/transloco';

import { PolicyDefinitionResponse } from '../../../core/api/generated/api-client';
import { PoliciesService } from '../policies.service';
import { PolicyDetailDialog, PolicyDetailDialogData } from '../policy-detail-dialog/policy-detail-dialog';

type LoadState = 'loading' | 'loaded' | 'empty' | 'error';

/** The catalog (§3) is fixed and seeded — never created/edited/removed from this UI (Fase 5, Incremento 1, Checkpoint 5: only PolicyDefinition READ is in scope). */
@Component({
  selector: 'app-policies-list',
  imports: [TranslocoPipe, MatButtonModule, MatProgressSpinnerModule, MatTableModule],
  templateUrl: './policies-list.html',
  styleUrl: './policies-list.scss',
})
export class PoliciesList {
  private readonly policiesService = inject(PoliciesService);
  private readonly dialog = inject(MatDialog);

  protected readonly displayedColumns = ['code', 'name', 'category', 'actions'];

  protected readonly state = signal<LoadState>('loading');
  protected readonly policies = signal<PolicyDefinitionResponse[]>([]);

  constructor() {
    this.loadPolicies();
  }

  protected loadPolicies(): void {
    this.state.set('loading');
    this.policiesService.list().subscribe({
      next: (items) => {
        this.policies.set(items);
        this.state.set(items.length === 0 ? 'empty' : 'loaded');
      },
      error: () => this.state.set('error'),
    });
  }

  protected openDetailDialog(policy: PolicyDefinitionResponse): void {
    this.dialog.open<PolicyDetailDialog, PolicyDetailDialogData>(PolicyDetailDialog, {
      data: { policy },
      width: '640px',
    });
  }
}
