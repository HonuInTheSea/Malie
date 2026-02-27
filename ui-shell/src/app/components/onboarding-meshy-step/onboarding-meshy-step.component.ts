import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { Button } from 'primeng/button';
import { ProgressBar } from 'primeng/progressbar';
import { Tooltip } from 'primeng/tooltip';
import { MeshyModelRowEntry } from '../meshy-model-viewer/meshy-model-viewer.component';

type OnboardingPoiRow = {
  poiName: string;
  statusText: string;
  statusKind: MeshyModelRowEntry['statusKind'];
  canQueue: boolean;
  canDownloadNow: boolean;
  isCachedModel: boolean;
};

@Component({
  selector: 'app-onboarding-meshy-step',
  imports: [DecimalPipe, Button, ProgressBar, Tooltip],
  templateUrl: './onboarding-meshy-step.component.html',
  styleUrl: './onboarding-meshy-step.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class OnboardingMeshyStepComponent {
  readonly location = input('');
  readonly onboardingPois = input<string[]>([]);
  readonly rows = input<MeshyModelRowEntry[]>([]);
  readonly isBusy = input(false);
  readonly status = input('Ready.');
  readonly queueStatus = input('Queue idle.');
  readonly progressPercent = input(0);
  readonly progressText = input('Queue idle.');
  readonly logs = input<string[]>([]);

  readonly refreshPoisRequested = output<void>();
  readonly queuePoiRequested = output<string>();
  readonly downloadPoiRequested = output<string>();

  readonly normalizedProgressPercent = computed(() =>
    Math.max(0, Math.min(100, Number.isFinite(this.progressPercent()) ? this.progressPercent() : 0))
  );
  readonly recentLogs = computed(() => {
    const lines = this.logs();
    return lines.slice(Math.max(0, lines.length - 8));
  });
  readonly visiblePois = computed<OnboardingPoiRow[]>(() => {
    const rowMap = new Map<string, MeshyModelRowEntry>();
    for (const row of this.rows()) {
      const key = this.normalizeKey(row.poiName);
      if (!key || rowMap.has(key)) {
        continue;
      }

      rowMap.set(key, row);
    }

    const merged = new Map<string, OnboardingPoiRow>();
    for (const poiName of this.onboardingPois()) {
      const normalizedPoiName = this.normalizePoiName(poiName);
      if (!normalizedPoiName) {
        continue;
      }

      const key = this.normalizeKey(normalizedPoiName);
      const row = rowMap.get(key);
      merged.set(key, this.toOnboardingPoiRow(normalizedPoiName, row));
    }

    for (const row of this.rows()) {
      const normalizedPoiName = this.normalizePoiName(row.poiName);
      if (!normalizedPoiName) {
        continue;
      }

      const key = this.normalizeKey(normalizedPoiName);
      if (merged.has(key)) {
        continue;
      }

      merged.set(key, this.toOnboardingPoiRow(normalizedPoiName, row));
    }

    return [...merged.values()].sort((left, right) => {
      if (left.isCachedModel !== right.isCachedModel) {
        return left.isCachedModel ? 1 : -1;
      }

      return left.poiName.localeCompare(right.poiName);
    });
  });

  statusClass(statusKind: MeshyModelRowEntry['statusKind']): string {
    return `status-pill status-${statusKind}`;
  }

  logLineClass(line: string): string {
    const lowered = (line ?? '').toLowerCase();
    if (!lowered) {
      return 'log-line';
    }

    if (
      lowered.includes('failed') ||
      lowered.includes('error') ||
      lowered.includes('skipped') ||
      lowered.includes('canceled')
    ) {
      return 'log-line log-line-error';
    }

    if (
      lowered.includes('successfully') ||
      lowered.includes('cached') ||
      lowered.includes('manifest written')
    ) {
      return 'log-line log-line-success';
    }

    if (
      lowered.includes('status: in_progress') ||
      lowered.includes('status: pending') ||
      lowered.includes('status: succeeded') ||
      lowered.includes('download image') ||
      lowered.includes('download model') ||
      lowered.includes('requested')
    ) {
      return 'log-line log-line-progress';
    }

    return 'log-line log-line-info';
  }

  private toOnboardingPoiRow(poiName: string, row?: MeshyModelRowEntry): OnboardingPoiRow {
    if (!row) {
      return {
        poiName,
        statusText: 'Not queued',
        statusKind: 'missing',
        canQueue: true,
        canDownloadNow: true,
        isCachedModel: false
      };
    }

    return {
      poiName,
      statusText: row.statusText,
      statusKind: row.statusKind,
      canQueue: row.canQueue,
      canDownloadNow: row.canDownloadNow,
      isCachedModel: row.isCachedModel
    };
  }

  private normalizePoiName(value: string): string {
    return value
      .trim()
      .split(/\s+/)
      .join(' ');
  }

  private normalizeKey(value: string): string {
    return this.normalizePoiName(value).toLowerCase();
  }
}

