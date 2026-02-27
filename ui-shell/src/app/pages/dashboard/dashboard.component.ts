import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Button } from 'primeng/button';
import { App } from '../../app';
import { OnboardingDialogComponent } from '../../components/onboarding-dialog/onboarding-dialog.component';

@Component({
  selector: 'app-dashboard-page',
  imports: [RouterLink, Button, OnboardingDialogComponent],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DashboardComponent {
  readonly shell = inject(App);
  readonly cachedMeshyModelCount = computed(
    () => this.shell.meshyManagerRows().filter((row) => row.isCachedModel).length
  );
}
