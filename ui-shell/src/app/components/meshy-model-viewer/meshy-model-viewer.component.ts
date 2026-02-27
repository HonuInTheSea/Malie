import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { InputNumber } from 'primeng/inputnumber';
import { InputText } from 'primeng/inputtext';
import { ProgressBar } from 'primeng/progressbar';
import { Tooltip } from 'primeng/tooltip';

export type MeshyModelRowEntry = {
  poiKey: string;
  poiName: string;
  modelFileName: string;
  statusText: string;
  statusKind: 'cached' | 'queued' | 'downloading' | 'missing' | 'error' | 'info';
  isCachedModel: boolean;
  isActiveModel: boolean;
  canQueue: boolean;
  canDownloadNow: boolean;
  canDelete: boolean;
  localRelativePath: string;
};

@Component({
  selector: 'app-meshy-model-viewer',
  imports: [FormsModule, DecimalPipe, Button, Dialog, InputNumber, InputText, ProgressBar, Tooltip],
  templateUrl: './meshy-model-viewer.component.html',
  styleUrl: './meshy-model-viewer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class MeshyModelViewerComponent {
  readonly location = input('');
  readonly rows = input<MeshyModelRowEntry[]>([]);
  readonly status = input('Ready.');
  readonly queueStatus = input('Queue idle.');
  readonly logs = input<string[]>([]);
  readonly rotationMinutes = input(0);
  readonly isBusy = input(false);
  readonly progressPercent = input(0);
  readonly progressText = input('Queue idle.');
  readonly customPromptMaxLength = input(800);

  readonly refreshRequested = output<void>();
  readonly refreshPoisRequested = output<void>();
  readonly queuePoiRequested = output<string>();
  readonly downloadPoiRequested = output<string>();
  readonly setActiveModelRequested = output<string>();
  readonly exportModelRequested = output<string>();
  readonly deleteModelRequested = output<string>();
  readonly renamePoiRequested = output<{ oldName: string; newName: string; localRelativePath: string }>();
  readonly customPromptRequested = output<string>();
  readonly rotationMinutesApplied = output<number>();
  readonly importGlbRequested = output<{ poiName: string; fileName: string; dataUrl: string }>();

  readonly customPrompt = signal('');
  readonly rotationInput = signal(0);
  readonly importDialogVisible = signal(false);
  readonly importPoiName = signal('');
  readonly importErrorText = signal('');
  readonly importInProgress = signal(false);
  readonly pendingImportFile = signal<File | null>(null);
  readonly busyOrImporting = computed(() => this.isBusy() || this.importInProgress());
  readonly normalizedProgressPercent = computed(() =>
    Math.max(0, Math.min(100, Number.isFinite(this.progressPercent()) ? this.progressPercent() : 0))
  );

  constructor() {
    effect(() => {
      const minutes = this.rotationMinutes();
      const normalized = Number.isFinite(minutes) ? Math.max(0, Math.min(1440, minutes)) : 0;
      this.rotationInput.set(normalized);
    });
  }

  onRotationInputChange(nextValue: number | null | undefined): void {
    if (typeof nextValue !== 'number' || !Number.isFinite(nextValue)) {
      return;
    }

    this.rotationInput.set(Math.max(0, Math.min(1440, nextValue)));
  }

  onApplyRotation(): void {
    const parsed = this.rotationInput();
    if (!Number.isFinite(parsed)) {
      return;
    }

    this.rotationMinutesApplied.emit(Math.max(0, Math.min(1440, parsed)));
  }

  onSubmitPrompt(): void {
    const trimmed = this.customPrompt().trim();
    if (!trimmed) {
      return;
    }

    this.customPromptRequested.emit(trimmed);
  }

  onRenamePoi(row: MeshyModelRowEntry, nextPoiName: string): void {
    const normalized = nextPoiName.trim();
    if (!normalized || normalized.localeCompare(row.poiName, undefined, { sensitivity: 'accent' }) === 0) {
      return;
    }

    this.renamePoiRequested.emit({
      oldName: row.poiName,
      newName: normalized,
      localRelativePath: row.localRelativePath
    });
  }

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
      lowered.includes('text preview submit') ||
      lowered.includes('text refine submit') ||
      lowered.includes('requested')
    ) {
      return 'log-line log-line-progress';
    }

    return 'log-line log-line-info';
  }

  openImportPicker(fileInput: HTMLInputElement): void {
    if (this.busyOrImporting()) {
      return;
    }

    fileInput.value = '';
    fileInput.click();
  }

  onImportFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement | null;
    const file = input?.files?.[0] ?? null;
    if (!file) {
      return;
    }

    if (!this.isGlbFile(file)) {
      this.pendingImportFile.set(null);
      this.importDialogVisible.set(false);
      this.importErrorText.set('Import failed: only .glb files are supported.');
      return;
    }

    this.pendingImportFile.set(file);
    this.importPoiName.set(this.defaultPoiNameFromFile(file.name));
    this.importErrorText.set('');
    this.importDialogVisible.set(true);
  }

  onImportPoiNameChange(value: string): void {
    this.importPoiName.set(value);
    if (this.importErrorText()) {
      this.importErrorText.set('');
    }
  }

  closeImportDialog(force = false): void {
    if (!force && this.importInProgress()) {
      return;
    }

    this.importDialogVisible.set(false);
    this.pendingImportFile.set(null);
    this.importPoiName.set('');
    this.importErrorText.set('');
  }

  async confirmImport(): Promise<void> {
    if (this.importInProgress()) {
      return;
    }

    const file = this.pendingImportFile();
    if (!file) {
      this.importErrorText.set('Select a GLB file to import.');
      return;
    }

    const poiName = this.normalizePoiName(this.importPoiName());
    if (!poiName) {
      this.importErrorText.set('POI name is required.');
      return;
    }

    if (this.hasDuplicatePoiName(poiName)) {
      this.importErrorText.set(`POI name '${poiName}' already exists. Choose a unique POI name.`);
      return;
    }

    this.importInProgress.set(true);
    this.importErrorText.set('');
    try {
      const dataUrl = await this.readFileAsDataUrl(file);
      this.importGlbRequested.emit({
        poiName,
        fileName: file.name,
        dataUrl
      });
      this.closeImportDialog(true);
    } catch {
      this.importErrorText.set('Import failed while reading the GLB file.');
    } finally {
      this.importInProgress.set(false);
    }
  }

  private hasDuplicatePoiName(candidate: string): boolean {
    const candidateKey = this.normalizePoiKey(candidate);
    if (!candidateKey) {
      return false;
    }

    return this.rows().some((row) => this.normalizePoiKey(row.poiName) === candidateKey);
  }

  private defaultPoiNameFromFile(fileName: string): string {
    const withoutExt = (fileName ?? '').replace(/\.glb$/i, '');
    const normalized = this.normalizePoiName(withoutExt);
    return normalized || 'Imported POI';
  }

  private normalizePoiName(value: string): string {
    return value
      .trim()
      .split(/\s+/)
      .join(' ');
  }

  private normalizePoiKey(value: string): string {
    return this.normalizePoiName(value).toLowerCase();
  }

  private isGlbFile(file: File): boolean {
    return /\.glb$/i.test(file.name ?? '');
  }

  private readFileAsDataUrl(file: File): Promise<string> {
    return new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => {
        const value = typeof reader.result === 'string' ? reader.result : '';
        if (!value) {
          reject(new Error('No file data.'));
          return;
        }

        resolve(value);
      };
      reader.onerror = () => reject(reader.error ?? new Error('Failed to read file.'));
      reader.readAsDataURL(file);
    });
  }
}
