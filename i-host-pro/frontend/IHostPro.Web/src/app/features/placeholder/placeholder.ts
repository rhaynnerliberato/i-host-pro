import { Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

/** Route-data-driven stub for a feature area not yet implemented (Fase 5+). */
@Component({
  selector: 'app-placeholder',
  imports: [TranslocoPipe],
  templateUrl: './placeholder.html',
  styleUrl: './placeholder.scss',
})
export class Placeholder {
  protected readonly titleKey = inject(ActivatedRoute).snapshot.data['titleKey'] as string;
}
