import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Button } from 'primeng/button';
import { App } from '../../app';

@Component({
  selector: 'app-status-page',
  imports: [Button],
  templateUrl: './status-page.component.html',
  styleUrl: './status-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class StatusPageComponent {
  readonly shell = inject(App);
}
