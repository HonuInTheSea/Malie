import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { InputText } from 'primeng/inputtext';
import { Step, StepList, StepPanel, StepPanels, Stepper } from 'primeng/stepper';
import { MeshyModelRowEntry } from '../meshy-model-viewer/meshy-model-viewer.component';
import { OnboardingMeshyStepComponent } from '../onboarding-meshy-step/onboarding-meshy-step.component';

@Component({
  selector: 'app-onboarding-dialog',
  imports: [
    FormsModule,
    Dialog,
    Button,
    InputText,
    Stepper,
    StepList,
    Step,
    StepPanels,
    StepPanel,
    OnboardingMeshyStepComponent
  ],
  templateUrl: './onboarding-dialog.component.html',
  styleUrl: './onboarding-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class OnboardingDialogComponent {
  readonly visible = input(false);
  readonly step = input(1);
  readonly location = input('');
  readonly meshyApiKey = input('');
  readonly weatherApiKey = input('');
  readonly latLngApiKey = input('');
  readonly onboardingPois = input<string[]>([]);
  readonly meshyRows = input<MeshyModelRowEntry[]>([]);
  readonly meshyStatus = input('Ready.');
  readonly meshyQueueStatus = input('Queue idle.');
  readonly meshyLogs = input<string[]>([]);
  readonly meshyBusy = input(false);
  readonly meshyRotationMinutes = input(0);
  readonly meshyProgressPercent = input(0);
  readonly meshyProgressText = input('Queue idle.');

  readonly stepChange = output<number>();
  readonly locationChange = output<string>();
  readonly meshyApiKeyChange = output<string>();
  readonly weatherApiKeyChange = output<string>();
  readonly latLngApiKeyChange = output<string>();
  readonly useCurrentLocation = output<void>();
  readonly meshyRefreshRequested = output<void>();
  readonly onboardingPoisRefreshRequested = output<void>();
  readonly meshyQueuePoiRequested = output<string>();
  readonly meshyDownloadPoiRequested = output<string>();
  readonly meshySetActiveRequested = output<string>();
  readonly meshyExportRequested = output<string>();
  readonly meshyDeleteRequested = output<string>();
  readonly meshyRenameRequested = output<{ oldName: string; newName: string; localRelativePath: string }>();
  readonly meshyCustomPromptRequested = output<string>();
  readonly meshyRotationMinutesApplied = output<number>();
  readonly meshyImportRequested = output<{ poiName: string; fileName: string; dataUrl: string }>();
  readonly next = output<void>();
  readonly previous = output<void>();
  readonly finish = output<void>();

  onStepValueChange(value: number | undefined): void {
    if (typeof value === 'number' && Number.isFinite(value)) {
      this.stepChange.emit(value);
    }
  }
}

