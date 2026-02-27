import { Routes } from '@angular/router';
import { MeshyModelsPageComponent } from './pages/meshy-models/meshy-models-page.component';
import { SettingsPageComponent } from './pages/settings/settings-page.component';
import { StatusPageComponent } from './pages/status/status-page.component';
import { DashboardComponent } from './pages/dashboard/dashboard.component';
import { WallpaperPageComponent } from './pages/wallpaper/wallpaper-page.component';
import { HelpPageComponent } from './pages/help/help-page.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: 'dashboard', component: DashboardComponent },
  { path: 'settings', component: SettingsPageComponent },
  { path: 'meshy-models', component: MeshyModelsPageComponent },
  { path: 'wallpaper', component: WallpaperPageComponent },
  { path: 'status', component: StatusPageComponent },
  { path: 'help', component: HelpPageComponent },
  { path: '**', redirectTo: 'dashboard' }
];
