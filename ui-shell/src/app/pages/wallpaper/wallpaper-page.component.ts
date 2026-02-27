import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Button } from 'primeng/button';
import { ColorPicker } from 'primeng/colorpicker';
import { RadioButton } from 'primeng/radiobutton';
import { ToggleSwitch } from 'primeng/toggleswitch';
import { App } from '../../app';

@Component({
  selector: 'app-wallpaper-page',
  imports: [FormsModule, Button, ColorPicker, RadioButton, ToggleSwitch],
  templateUrl: './wallpaper-page.component.html',
  styleUrl: './wallpaper-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class WallpaperPageComponent {
  readonly shell = inject(App);
}
