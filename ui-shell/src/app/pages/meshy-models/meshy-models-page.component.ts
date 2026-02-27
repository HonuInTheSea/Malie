import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { App } from '../../app';
import { MeshyModelViewerComponent } from '../../components/meshy-model-viewer/meshy-model-viewer.component';

@Component({
  selector: 'app-meshy-models-page',
  imports: [MeshyModelViewerComponent],
  templateUrl: './meshy-models-page.component.html',
  styleUrl: './meshy-models-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class MeshyModelsPageComponent {
  readonly shell = inject(App);
}
