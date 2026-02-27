import { ChangeDetectionStrategy, Component, computed, effect, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ToggleSwitch } from 'primeng/toggleswitch';
import { Tooltip } from 'primeng/tooltip';
import { MeshyModelRowEntry } from './components/meshy-model-viewer/meshy-model-viewer.component';

type TemperatureScale = 'Fahrenheit' | 'Celsius';
type WallpaperBackgroundDisplayMode = 'Original' | 'Fill' | 'Stretch';
type WallpaperMonitorOption = {
  deviceName: string;
  label: string;
  isPrimary: boolean;
  x: number;
  y: number;
  width: number;
  height: number;
};
type WallpaperTextFontKey =
  | 'timeFontFamily'
  | 'locationFontFamily'
  | 'dateFontFamily'
  | 'temperatureFontFamily'
  | 'summaryFontFamily'
  | 'poiFontFamily'
  | 'alertsFontFamily';
type WallpaperTextSizeKey =
  | 'timeFontSize'
  | 'locationFontSize'
  | 'dateFontSize'
  | 'temperatureFontSize'
  | 'summaryFontSize'
  | 'poiFontSize'
  | 'alertsFontSize';

export type WallpaperTextStyle = {
  timeFontFamily: string;
  locationFontFamily: string;
  dateFontFamily: string;
  temperatureFontFamily: string;
  summaryFontFamily: string;
  poiFontFamily: string;
  alertsFontFamily: string;
  timeFontSize: number;
  locationFontSize: number;
  dateFontSize: number;
  temperatureFontSize: number;
  summaryFontSize: number;
  poiFontSize: number;
  alertsFontSize: number;
};

type LogFilters = {
  showSystem: boolean;
  showWeather: boolean;
  showLatLng: boolean;  showMeshy: boolean;
  showHighDetail: boolean;
  showRendererDebug: boolean;
  showErrors: boolean;
};

type GlbOrientation = {
  rotationXDegrees: number;
  rotationYDegrees: number;
  rotationZDegrees: number;
  scale: number;
  offsetX: number;
  offsetY: number;
  offsetZ: number;
};

type MeshyManagerState = {
  status: string;
  queueStatus: string;
  isBusy: boolean;
  rotationMinutes: number;
  progressPercent: number;
  progressText: string;
  rows: MeshyModelRowEntry[];
  logs: string[];
};

type HostEnvelope = {
  type: string;
  payload?: unknown;
};

type SettingsPayload = {
  location: string;
  meshyApiKey: string;
  weatherApiKey: string;
  latLngApiKey: string;  temperatureScale: TemperatureScale;
  wallpaperMonitorDeviceName: string;
  wallpaperBackgroundColor: string;
  wallpaperBackgroundImageFileName: string;
  wallpaperBackgroundDisplayMode: WallpaperBackgroundDisplayMode;
  useAnimatedAiBackground: boolean;
  showWallpaperStatsOverlay: boolean;
  wallpaperTextStyle: WallpaperTextStyle;
  showDebugLogPane: boolean;
  logFilters: LogFilters;
  glbOrientation: GlbOrientation;
};

declare global {
  interface Window {
    __isometricLiveWeatherHost?: {
      receive: (message: HostEnvelope) => void;
    };
    chrome?: {
      webview?: {
        postMessage: (message: unknown) => void;
      };
    };
  }
}

@Component({
  selector: 'app-root',
  imports: [FormsModule, RouterOutlet, RouterLink, RouterLinkActive, ToggleSwitch, Tooltip],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class App {  private readonly defaultWallpaperTextStyle: WallpaperTextStyle = {
    timeFontFamily: 'Segoe UI',
    locationFontFamily: 'Segoe UI',
    dateFontFamily: 'Segoe UI',
    temperatureFontFamily: 'Segoe UI',
    summaryFontFamily: 'Segoe UI',
    poiFontFamily: 'Segoe UI',
    alertsFontFamily: 'Segoe UI',
    timeFontSize: 58,
    locationFontSize: 54,
    dateFontSize: 14,
    temperatureFontSize: 34,
    summaryFontSize: 14,
    poiFontSize: 14,
    alertsFontSize: 14
  };

  readonly darkMode = signal(true);
  readonly sidebarCollapsed = signal(false);
  readonly location = signal('');
  readonly meshyApiKey = signal('');
  readonly weatherApiKey = signal('');
  readonly latLngApiKey = signal('');
  readonly temperatureScale = signal<TemperatureScale>('Fahrenheit');
  readonly wallpaperMonitorDeviceName = signal('');
  readonly availableWallpaperMonitors = signal<WallpaperMonitorOption[]>([]);
  readonly wallpaperBackgroundColor = signal('#7AA7D8');
  readonly wallpaperBackgroundImageFileName = signal('');
  readonly wallpaperBackgroundDisplayMode = signal<WallpaperBackgroundDisplayMode>('Fill');
  readonly useAnimatedAiBackground = signal(false);
  readonly showWallpaperStatsOverlay = signal(true);
  readonly wallpaperTextStyle = signal<WallpaperTextStyle>(this.defaultWallpaperTextStyle);
  readonly systemFontFamilies = signal<string[]>([]);
  readonly showDebugLogPane = signal(true);
  readonly statusText = signal('Application is initializing. Loading configured location...');
  readonly lastUpdatedText = signal('Never');
  readonly debugLogs = signal<string[]>([]);
  readonly bridgeStatus = signal('Connecting to desktop host...');  readonly showHelp = signal(false);
  readonly onboardingStep = signal(1);
  readonly onboardingDismissed = signal(false);
  readonly hostStateHydrated = signal(false);
  readonly onboardingPois = signal<string[]>([]);  readonly meshyManagerRows = signal<MeshyModelRowEntry[]>([]);
  readonly meshyManagerStatus = signal('Ready.');
  readonly meshyManagerQueueStatus = signal('Queue idle.');
  readonly meshyManagerBusy = signal(false);
  readonly meshyManagerRotationMinutes = signal(0);
  readonly meshyManagerProgressPercent = signal(0);
  readonly meshyManagerProgressText = signal('Queue idle.');
  readonly meshyManagerLogs = signal<string[]>([]);
  readonly logFilters = signal<LogFilters>({
    showSystem: true,
    showWeather: true,
    showLatLng: true,    showMeshy: true,
    showHighDetail: true,
    showRendererDebug: false,
    showErrors: true
  });
  readonly glbOrientation = signal<GlbOrientation>({
    rotationXDegrees: 0,
    rotationYDegrees: 0,
    rotationZDegrees: 0,
    scale: 1,
    offsetX: 0,
    offsetY: 0,
    offsetZ: 0
  });
  readonly hasLocation = computed(() => this.location().trim().length > 0);
  readonly hasApiKeys = computed(() =>
    this.meshyApiKey().trim().length > 0 &&
    this.weatherApiKey().trim().length > 0 &&
    this.latLngApiKey().trim().length > 0
  );  readonly showOnboarding = computed(() => this.hostStateHydrated() && !this.onboardingDismissed());
  readonly availableSystemFonts = computed(() => {
    const fromHost = this.systemFontFamilies().filter((font) => font.trim().length > 0);
    if (fromHost.length > 0) {
      return fromHost;
    }

    return [
      this.defaultWallpaperTextStyle.timeFontFamily,
      'Segoe UI',
      'Arial',
      'Tahoma',
      'Verdana',
      'Calibri',
      'Times New Roman',
      'Georgia',
      'Consolas'
    ];
  });
  readonly wallpaperMonitorOptions = computed(() => {
    return [
      {
        deviceName: '',
        label: 'Automatic monitor (system default)',
        isPrimary: true,
        x: 0,
        y: 0,
        width: 0,
        height: 0
      },
      ...this.availableWallpaperMonitors()
    ];
  });
  constructor() {
    effect(() => {
      document.documentElement.classList.toggle('app-dark', this.darkMode());
    });

    try {
      this.sidebarCollapsed.set(window.localStorage.getItem('ilw.sidebarCollapsed') === '1');
    } catch {
      this.sidebarCollapsed.set(false);
    }

    effect(() => {
      try {
        window.localStorage.setItem('ilw.sidebarCollapsed', this.sidebarCollapsed() ? '1' : '0');
      } catch {
        // Ignore local storage write failures.
      }
    });

    effect(() => {
      const selectedDeviceName = this.wallpaperMonitorDeviceName();
      if (!selectedDeviceName) {
        return;
      }

      const availableMonitors = this.availableWallpaperMonitors();
      if (availableMonitors.length === 0) {
        return;
      }

      const hasSelectedMonitor = availableMonitors.some(
        (monitor) => monitor.deviceName.toLowerCase() === selectedDeviceName.toLowerCase()
      );
      if (!hasSelectedMonitor) {
        this.wallpaperMonitorDeviceName.set('');
      }
    });

    window.__isometricLiveWeatherHost = {
      receive: (message: HostEnvelope) => this.onHostMessage(message)
    };

    queueMicrotask(() => {
      this.postToHost('settings.requestState', {});
    });
  }

  applySettings(): boolean {
    const payload = this.buildSettingsPayload();
    if (!payload) {
      return false;
    }

    this.postToHost('settings.apply', payload);
    return true;
  }

  refreshNow(): void {
    const payload = this.buildSettingsPayload();
    if (!payload) {
      return;
    }

    this.postToHost('settings.refresh', payload);
  }

  useCurrentLocation(): void {
    this.postToHost('settings.useCurrentLocation', {});
  }

  resetApplication(): void {
    this.postToHost('settings.resetApplication', {});
  }

  toggleSidebarCollapsed(): void {
    this.sidebarCollapsed.update((collapsed) => !collapsed);
  }

  openMeshyModelViewer(): void {
    this.postToHost('settings.openMeshyModels', {});
  }

  clearDebugLog(): void {
    this.debugLogs.set([]);
  }

  nextOnboardingStep(): void {
    const current = this.onboardingStep();
    if (current === 1 && !this.hasLocation()) {
      this.statusText.set('Set a location before continuing onboarding.');
      return;
    }

    if (current === 2 && !this.hasApiKeys()) {
      this.statusText.set('Set all required API keys before continuing onboarding.');
      return;
    }

    const nextStep = Math.min(4, current + 1);
    this.onboardingStep.set(nextStep);
    if (nextStep === 3) {
      this.refreshOnboardingPois();
    }
  }

  previousOnboardingStep(): void {
    this.onboardingStep.set(Math.max(1, this.onboardingStep() - 1));
  }

  onOnboardingStepChange(value: number | undefined): void {
    if (typeof value === 'number' && Number.isFinite(value)) {
      this.onboardingStep.set(Math.max(1, Math.min(4, value)));
    }
  }

  finishOnboarding(): void {
    if (this.applySettings()) {
      this.refreshNow();
      this.statusText.set('Onboarding complete. Settings applied.');
      this.onboardingDismissed.set(true);
    }
  }

  dismissOnboarding(): void {
    this.onboardingDismissed.set(true);
  }
  refreshOnboardingPois(): void {
    this.postToHost('settings.onboarding.refreshPois', {});
  }

  refreshMeshyManager(): void {
    this.postToHost('settings.meshy.refresh', {});
  }

  queueMeshyPoi(poiName: string): void {
    this.postToHost('settings.meshy.queue', { poiName, prioritize: false });
  }

  downloadMeshyPoiNow(poiName: string): void {
    this.postToHost('settings.meshy.queue', { poiName, prioritize: true });
  }

  deleteMeshyCachedModel(localRelativePath: string): void {
    this.postToHost('settings.meshy.delete', { localRelativePath });
  }

  setMeshyActiveModel(localRelativePath: string): void {
    this.postToHost('settings.meshy.setActive', { localRelativePath });
  }

  exportMeshyCachedModel(localRelativePath: string): void {
    this.postToHost('settings.meshy.export', { localRelativePath });
  }

  renameMeshyPoi(request: { oldName: string; newName: string; localRelativePath: string }): void {
    this.postToHost('settings.meshy.rename', request);
  }

  submitMeshyCustomPrompt(prompt: string): void {
    this.postToHost('settings.meshy.customPrompt', { prompt });
  }

  applyMeshyRotationMinutes(minutes: number): void {
    this.postToHost('settings.meshy.rotation', { minutes });
  }

  importMeshyGlb(request: { poiName: string; fileName: string; dataUrl: string }): void {
    const poiName = (request.poiName ?? '').trim();
    const fileName = (request.fileName ?? '').trim();
    const dataUrl = (request.dataUrl ?? '').trim();
    if (!poiName || !fileName || !dataUrl) {
      this.statusText.set('Meshy import request is missing POI name, file name, or file data.');
      return;
    }

    this.postToHost('settings.meshy.import', { poiName, fileName, dataUrl });
  }

  updateLogFilter(key: keyof LogFilters, checked: boolean): void {
    this.logFilters.update((current) => ({
      ...current,
      [key]: checked
    }));

    this.postToHost('settings.logFiltersChanged', this.logFilters());
  }

  updateOrientationValue(key: keyof GlbOrientation, value: string): void {
    const numeric = Number.parseFloat(value);
    if (!Number.isFinite(numeric)) {
      return;
    }

    this.glbOrientation.update((current) => ({
      ...current,
      [key]: numeric
    }));

    this.postToHost('settings.glbOrientationChanged', this.glbOrientation());
  }

  updateWallpaperBackgroundColor(value: unknown): void {
    const normalized = this.normalizeHexColor(value, this.wallpaperBackgroundColor());
    if (normalized === this.wallpaperBackgroundColor()) {
      return;
    }

    this.wallpaperBackgroundColor.set(normalized);
    this.postToHost('settings.wallpaperBackgroundColorChanged', {
      wallpaperBackgroundColor: normalized
    });
  }

  updateWallpaperMonitorDeviceName(value: unknown): void {
    const normalized = this.normalizeWallpaperMonitorDeviceName(value, this.wallpaperMonitorDeviceName());
    if (normalized === this.wallpaperMonitorDeviceName()) {
      return;
    }

    this.wallpaperMonitorDeviceName.set(normalized);
    this.postToHost('settings.wallpaperMonitorChanged', {
      wallpaperMonitorDeviceName: normalized
    });
  }

  onWallpaperBackgroundImageSelected(event: Event): void {
    const input = event.target as HTMLInputElement | null;
    const file = input?.files?.[0];
    if (!file) {
      return;
    }

    const reader = new FileReader();
    reader.onload = () => {
      const dataUrl = typeof reader.result === 'string' ? reader.result : '';
      if (!dataUrl) {
        this.statusText.set('Failed to read selected media file.');
        return;
      }

      this.wallpaperBackgroundImageFileName.set(file.name);
      this.postToHost('settings.wallpaperBackgroundImageChanged', {
        fileName: file.name,
        dataUrl
      });
      if (input) {
        input.value = '';
      }
    };
    reader.onerror = () => {
      this.statusText.set('Failed to read selected media file.');
    };
    reader.readAsDataURL(file);
  }

  clearWallpaperBackgroundImage(): void {
    this.wallpaperBackgroundImageFileName.set('');
    this.postToHost('settings.wallpaperBackgroundImageCleared', {});
  }

  updateWallpaperAnimatedBackground(enabled: boolean): void {
    const next = !!enabled;
    if (next === this.useAnimatedAiBackground()) {
      return;
    }

    this.useAnimatedAiBackground.set(next);
    this.postToHost('settings.wallpaperAnimatedBackgroundChanged', {
      useAnimatedAiBackground: next
    });
  }

  updateWallpaperStatsOverlay(enabled: boolean): void {
    const next = !!enabled;
    if (next === this.showWallpaperStatsOverlay()) {
      return;
    }

    this.showWallpaperStatsOverlay.set(next);
    this.postToHost('settings.wallpaperStatsOverlayChanged', {
      showWallpaperStatsOverlay: next
    });
  }

  updateWallpaperBackgroundDisplayMode(value: unknown): void {
    const normalized = this.normalizeWallpaperBackgroundDisplayMode(
      value,
      this.wallpaperBackgroundDisplayMode()
    );
    if (normalized === this.wallpaperBackgroundDisplayMode()) {
      return;
    }

    this.wallpaperBackgroundDisplayMode.set(normalized);
    this.postToHost('settings.wallpaperBackgroundDisplayModeChanged', {
      wallpaperBackgroundDisplayMode: normalized
    });
  }

  updateWallpaperFontFamily(key: WallpaperTextFontKey, value: unknown): void {
    const normalized = this.normalizeFontFamily(value, this.wallpaperTextStyle()[key]);
    if (normalized === this.wallpaperTextStyle()[key]) {
      return;
    }

    this.wallpaperTextStyle.update((current) => ({
      ...current,
      [key]: normalized
    }));
    this.postToHost('settings.wallpaperTextStyleChanged', {
      wallpaperTextStyle: this.wallpaperTextStyle()
    });
  }

  updateWallpaperTextSize(key: WallpaperTextSizeKey, value: unknown): void {
    const numeric = this.normalizeFontSize(value, this.wallpaperTextStyle()[key]);
    if (numeric === this.wallpaperTextStyle()[key]) {
      return;
    }

    this.wallpaperTextStyle.update((current) => ({
      ...current,
      [key]: numeric
    }));
    this.postToHost('settings.wallpaperTextStyleChanged', {
      wallpaperTextStyle: this.wallpaperTextStyle()
    });
  }

  private buildSettingsPayload(): SettingsPayload | null {
    const location = this.location().trim();
    const meshyApiKey = this.meshyApiKey().trim();
    const weatherApiKey = this.weatherApiKey().trim();
    const latLngApiKey = this.latLngApiKey().trim();

    if (!location) {
      this.statusText.set('Location cannot be empty.');
      return null;
    }

    if (!meshyApiKey) {
      this.statusText.set('Meshy API key is required.');
      return null;
    }

    if (!weatherApiKey) {
      this.statusText.set('Weather API key is required.');
      return null;
    }

    if (!latLngApiKey) {
      this.statusText.set('LatLng API key is required.');
      return null;
    }

    return {
      location,
      meshyApiKey,
      weatherApiKey,
      latLngApiKey,      temperatureScale: this.temperatureScale(),
      wallpaperMonitorDeviceName: this.wallpaperMonitorDeviceName(),
      wallpaperBackgroundColor: this.wallpaperBackgroundColor(),
      wallpaperBackgroundImageFileName: this.wallpaperBackgroundImageFileName(),
      wallpaperBackgroundDisplayMode: this.wallpaperBackgroundDisplayMode(),
      useAnimatedAiBackground: this.useAnimatedAiBackground(),
      showWallpaperStatsOverlay: this.showWallpaperStatsOverlay(),
      wallpaperTextStyle: this.wallpaperTextStyle(),
      showDebugLogPane: this.showDebugLogPane(),
      logFilters: this.logFilters(),
      glbOrientation: this.glbOrientation()
    };
  }

  private postToHost(type: string, payload: unknown): void {
    const envelope: HostEnvelope = { type, payload };
    const webview = window.chrome?.webview;
    if (webview?.postMessage) {
      webview.postMessage(envelope);
      this.bridgeStatus.set('Connected to desktop host.');
      return;
    }

    this.bridgeStatus.set('Running in standalone browser mode.');
    if (type === 'settings.requestState') {
      this.hostStateHydrated.set(true);
    }
    console.info('Host message (no WebView host):', envelope);
  }

  private onHostMessage(message: HostEnvelope): void {
    if (!message || typeof message.type !== 'string') {
      return;
    }

    switch (message.type) {
      case 'host.state':
        this.applyHostState(message.payload);
        this.hostStateHydrated.set(true);        break;
      case 'host.status':
        this.statusText.set(this.readString(message.payload, 'text', this.statusText()));
        break;
      case 'host.lastUpdated':
        this.lastUpdatedText.set(this.readString(message.payload, 'display', this.lastUpdatedText()));
        break;
      case 'host.location':
        this.location.set(this.readString(message.payload, 'location', this.location()));
        break;
      case 'host.logReplace':
        this.debugLogs.set(this.readStringArray(message.payload, 'lines'));
        break;
      case 'host.logAppend':
        this.appendLogLine(this.readString(message.payload, 'line', ''));
        break;
      case 'host.debugPaneVisibility':
        this.showDebugLogPane.set(this.readBoolean(message.payload, 'visible', this.showDebugLogPane()));
        break;
      case 'host.glbOrientation':
        this.glbOrientation.set(this.readOrientation(message.payload, this.glbOrientation()));
        break;
      case 'host.wallpaperBackgroundColor':
        this.wallpaperBackgroundColor.set(
          this.normalizeHexColor(
            this.readString(message.payload, 'wallpaperBackgroundColor', this.wallpaperBackgroundColor()),
            this.wallpaperBackgroundColor()
          )
        );
        break;
      case 'host.wallpaperMonitor': {
        const payloadRecord = this.readRecord(message.payload);
        const parsedMonitors = this.readWallpaperMonitors(payloadRecord?.['availableMonitors']);
        if (parsedMonitors.length > 0) {
          this.availableWallpaperMonitors.set(parsedMonitors);
        }

        this.wallpaperMonitorDeviceName.set(
          this.normalizeWallpaperMonitorDeviceName(
            this.readString(payloadRecord, 'wallpaperMonitorDeviceName', this.wallpaperMonitorDeviceName()),
            this.wallpaperMonitorDeviceName()
          )
        );
        break;
      }
      case 'host.wallpaperBackgroundImage':
        this.wallpaperBackgroundImageFileName.set(
          this.readString(
            message.payload,
            'wallpaperBackgroundImageFileName',
            this.wallpaperBackgroundImageFileName()
          )
        );
        break;
      case 'host.wallpaperBackgroundDisplayMode':
        this.wallpaperBackgroundDisplayMode.set(
          this.normalizeWallpaperBackgroundDisplayMode(
            this.readString(
              message.payload,
              'wallpaperBackgroundDisplayMode',
              this.wallpaperBackgroundDisplayMode()
            ),
            this.wallpaperBackgroundDisplayMode()
          )
        );
        break;
      case 'host.wallpaperAnimatedBackground':
        this.useAnimatedAiBackground.set(
          this.readBoolean(message.payload, 'useAnimatedAiBackground', this.useAnimatedAiBackground())
        );
        break;
      case 'host.wallpaperStatsOverlay':
        this.showWallpaperStatsOverlay.set(
          this.readBoolean(message.payload, 'showWallpaperStatsOverlay', this.showWallpaperStatsOverlay())
        );
        break;
      case 'host.wallpaperTextStyle':
        this.wallpaperTextStyle.set(
          this.readWallpaperTextStyle(message.payload, this.wallpaperTextStyle())
        );
        break;
      case 'host.systemFonts':
        this.systemFontFamilies.set(this.normalizeSystemFonts(this.readStringArray(message.payload, 'fonts')));
        break;
      case 'host.openHelp':
        this.showHelp.set(true);
        break;      case 'host.onboardingPois':
        this.onboardingPois.set(this.readStringArray(message.payload, 'pois'));
        break;
      case 'host.meshyManager':
        this.applyMeshyManagerState(message.payload);
        break;
      case 'host.meshyManagerLog':
        this.appendMeshyManagerLogLine(this.readString(message.payload, 'line', ''));
        break;
      default:
        break;
    }
  }

  private applyHostState(payload: unknown): void {
    const state = this.readRecord(payload);
    if (!state) {
      return;
    }

    this.location.set(this.readString(state, 'location', this.location()));
    this.meshyApiKey.set(this.readString(state, 'meshyApiKey', this.meshyApiKey()));
    this.weatherApiKey.set(this.readString(state, 'weatherApiKey', this.weatherApiKey()));
    this.latLngApiKey.set(this.readString(state, 'latLngApiKey', this.latLngApiKey()));
    this.temperatureScale.set(
      this.readString(state, 'temperatureScale', this.temperatureScale()) === 'Celsius'
        ? 'Celsius'
        : 'Fahrenheit'
    );
    this.availableWallpaperMonitors.set(this.readWallpaperMonitors(state['availableMonitors']));
    this.wallpaperMonitorDeviceName.set(
      this.normalizeWallpaperMonitorDeviceName(
        this.readString(state, 'wallpaperMonitorDeviceName', this.wallpaperMonitorDeviceName()),
        this.wallpaperMonitorDeviceName()
      )
    );
    this.wallpaperBackgroundColor.set(
      this.normalizeHexColor(
        this.readString(state, 'wallpaperBackgroundColor', this.wallpaperBackgroundColor()),
        this.wallpaperBackgroundColor()
      )
    );
    this.wallpaperBackgroundImageFileName.set(
      this.readString(
        state,
        'wallpaperBackgroundImageFileName',
        this.wallpaperBackgroundImageFileName()
      )
    );
    this.wallpaperBackgroundDisplayMode.set(
      this.normalizeWallpaperBackgroundDisplayMode(
        this.readString(
          state,
          'wallpaperBackgroundDisplayMode',
          this.wallpaperBackgroundDisplayMode()
        ),
        this.wallpaperBackgroundDisplayMode()
      )
    );
    this.useAnimatedAiBackground.set(
      this.readBoolean(state, 'useAnimatedAiBackground', this.useAnimatedAiBackground())
    );
    this.showWallpaperStatsOverlay.set(
      this.readBoolean(state, 'showWallpaperStatsOverlay', this.showWallpaperStatsOverlay())
    );
    this.wallpaperTextStyle.set(this.readWallpaperTextStyle(state['wallpaperTextStyle'], this.wallpaperTextStyle()));
    this.systemFontFamilies.set(this.normalizeSystemFonts(this.readStringArray(state, 'systemFonts')));
    this.showDebugLogPane.set(this.readBoolean(state, 'showDebugLogPane', this.showDebugLogPane()));
    this.statusText.set(this.readString(state, 'status', this.statusText()));
    this.lastUpdatedText.set(this.readString(state, 'lastUpdatedDisplay', this.lastUpdatedText()));
    this.debugLogs.set(this.readStringArray(state, 'logs'));
    this.logFilters.set(this.readLogFilters(state['logFilters'], this.logFilters()));
    this.glbOrientation.set(this.readOrientation(state['glbOrientation'], this.glbOrientation()));
    this.onboardingPois.set(this.readStringArray(state, 'onboardingPois'));
    this.onboardingDismissed.set(
      this.readBoolean(
        state,
        'onboardingCompleted',
        this.hasLocation() && this.hasApiKeys()
      )
    );
    this.applyMeshyManagerState(state['meshyManager']);
  }

  private appendLogLine(line: string): void {
    if (!line) {
      return;
    }

    this.debugLogs.update((current) => {
      const updated = [...current, line];
      return updated.slice(Math.max(0, updated.length - 500));
    });
  }
  private appendMeshyManagerLogLine(line: string): void {
    if (!line) {
      return;
    }

    this.meshyManagerLogs.update((current) => {
      const updated = [...current, line];
      return updated.slice(Math.max(0, updated.length - 2000));
    });
  }

  private applyMeshyManagerState(payload: unknown): void {
    const record = this.readRecord(payload);
    if (!record) {
      return;
    }

    const state: MeshyManagerState = {
      status: this.readString(record, 'status', this.meshyManagerStatus()),
      queueStatus: this.readString(record, 'queueStatus', this.meshyManagerQueueStatus()),
      isBusy: this.readBoolean(record, 'isBusy', this.meshyManagerBusy()),
      rotationMinutes: this.readNumber(record, 'rotationMinutes', this.meshyManagerRotationMinutes()),
      progressPercent: this.readNumber(record, 'progressPercent', this.meshyManagerProgressPercent()),
      progressText: this.readString(record, 'progressText', this.meshyManagerProgressText()),
      rows: this.parseMeshyRows(record['rows']),
      logs: this.readStringArray(record, 'logs')
    };

    this.meshyManagerStatus.set(state.status);
    this.meshyManagerQueueStatus.set(state.queueStatus);
    this.meshyManagerBusy.set(state.isBusy);
    this.meshyManagerRotationMinutes.set(state.rotationMinutes);
    this.meshyManagerProgressPercent.set(state.progressPercent);
    this.meshyManagerProgressText.set(state.progressText);
    this.meshyManagerRows.set(state.rows);
    this.meshyManagerLogs.set(state.logs);
  }

  private parseMeshyRows(source: unknown): MeshyModelRowEntry[] {
    if (!Array.isArray(source)) {
      return [];
    }

    return source
      .map((entry) => this.readRecord(entry))
      .filter((entry): entry is Record<string, unknown> => !!entry)
      .map((entry) => ({
        poiKey: this.readString(entry, 'poiKey', ''),
        poiName: this.readString(entry, 'poiName', ''),
        modelFileName: this.readString(entry, 'modelFileName', ''),
        statusText: this.readString(entry, 'statusText', 'Unknown'),
        statusKind: this.parseMeshyStatusKind(this.readString(entry, 'statusKind', 'info')),
        isCachedModel: this.readBoolean(entry, 'isCachedModel', false),
        isActiveModel: this.readBoolean(entry, 'isActiveModel', false),
        canQueue: this.readBoolean(entry, 'canQueue', false),
        canDownloadNow: this.readBoolean(entry, 'canDownloadNow', false),
        canDelete: this.readBoolean(entry, 'canDelete', false),
        localRelativePath: this.readString(entry, 'localRelativePath', '')
      }))
      .filter((row) => row.poiName.length > 0);
  }

  private parseMeshyStatusKind(statusKind: string): MeshyModelRowEntry['statusKind'] {
    switch (statusKind) {
      case 'cached':
      case 'queued':
      case 'downloading':
      case 'missing':
      case 'error':
      case 'info':
        return statusKind;
      default:
        return 'info';
    }
  }

  private readRecord(value: unknown): Record<string, unknown> | null {
    return value && typeof value === 'object' ? (value as Record<string, unknown>) : null;
  }

  private readString(source: unknown, key: string, fallback: string): string {
    const record = this.readRecord(source);
    const value = record?.[key];
    return typeof value === 'string' ? value : fallback;
  }

  private readBoolean(source: unknown, key: string, fallback: boolean): boolean {
    const record = this.readRecord(source);
    const value = record?.[key];
    return typeof value === 'boolean' ? value : fallback;
  }

  private readNumber(source: unknown, key: string, fallback: number): number {
    const record = this.readRecord(source);
    const value = record?.[key];
    return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
  }

  private readStringArray(source: unknown, key: string): string[] {
    const record = this.readRecord(source);
    const value = record?.[key];
    if (!Array.isArray(value)) {
      return [];
    }

    return value.filter((entry): entry is string => typeof entry === 'string');
  }

  private readLogFilters(source: unknown, fallback: LogFilters): LogFilters {
    const record = this.readRecord(source);
    if (!record) {
      return fallback;
    }

    return {
      showSystem: this.readBoolean(record, 'showSystem', fallback.showSystem),
      showWeather: this.readBoolean(record, 'showWeather', fallback.showWeather),
      showLatLng: this.readBoolean(record, 'showLatLng', fallback.showLatLng),      showMeshy: this.readBoolean(record, 'showMeshy', fallback.showMeshy),
      showHighDetail: this.readBoolean(record, 'showHighDetail', fallback.showHighDetail),
      showRendererDebug: this.readBoolean(record, 'showRendererDebug', fallback.showRendererDebug),
      showErrors: this.readBoolean(record, 'showErrors', fallback.showErrors)
    };
  }

  private readOrientation(source: unknown, fallback: GlbOrientation): GlbOrientation {
    const record = this.readRecord(source);
    if (!record) {
      return fallback;
    }

    return {
      rotationXDegrees: this.readNumber(record, 'rotationXDegrees', fallback.rotationXDegrees),
      rotationYDegrees: this.readNumber(record, 'rotationYDegrees', fallback.rotationYDegrees),
      rotationZDegrees: this.readNumber(record, 'rotationZDegrees', fallback.rotationZDegrees),
      scale: this.readNumber(record, 'scale', fallback.scale),
      offsetX: this.readNumber(record, 'offsetX', fallback.offsetX),
      offsetY: this.readNumber(record, 'offsetY', fallback.offsetY),
      offsetZ: this.readNumber(record, 'offsetZ', fallback.offsetZ)
    };
  }

  private readWallpaperMonitors(source: unknown): WallpaperMonitorOption[] {
    if (!Array.isArray(source)) {
      return [];
    }

    const parsed = source
      .map((entry) => this.readRecord(entry))
      .filter((entry): entry is Record<string, unknown> => !!entry)
      .map((entry) => ({
        deviceName: this.readString(entry, 'deviceName', ''),
        label: this.readString(entry, 'label', ''),
        isPrimary: this.readBoolean(entry, 'isPrimary', false),
        x: this.readNumber(entry, 'x', 0),
        y: this.readNumber(entry, 'y', 0),
        width: this.readNumber(entry, 'width', 0),
        height: this.readNumber(entry, 'height', 0)
      }))
      .filter((entry) => entry.deviceName.trim().length > 0);

    const deduped = new Map<string, WallpaperMonitorOption>();
    for (const monitor of parsed) {
      const deviceName = monitor.deviceName.trim();
      if (!deviceName) {
        continue;
      }

      const key = deviceName.toLowerCase();
      const current = deduped.get(key);
      if (!current) {
        deduped.set(key, {
          ...monitor,
          deviceName
        });
        continue;
      }

      if (!current.isPrimary && monitor.isPrimary) {
        deduped.set(key, {
          ...monitor,
          deviceName
        });
      }
    }

    const primaryCount = [...deduped.values()].filter((monitor) => monitor.isPrimary).length;
    if (primaryCount > 1) {
      let primaryConsumed = false;
      for (const [key, monitor] of deduped.entries()) {
        if (!monitor.isPrimary) {
          continue;
        }

        if (!primaryConsumed) {
          primaryConsumed = true;
          continue;
        }

        deduped.set(key, {
          ...monitor,
          isPrimary: false
        });
      }
    }

    return [...deduped.values()];
  }

  private normalizeWallpaperMonitorDeviceName(value: unknown, fallback: string): string {
    const source = typeof value === 'string' ? value.trim() : '';
    if (!source) {
      return '';
    }

    const available = this.availableWallpaperMonitors();
    const matched = available.find((monitor) => monitor.deviceName.toLowerCase() === source.toLowerCase());
    if (matched) {
      return matched.deviceName;
    }

    return fallback && available.some((monitor) => monitor.deviceName.toLowerCase() === fallback.toLowerCase())
      ? fallback
      : '';
  }

  private normalizeHexColor(value: unknown, fallback: string): string {
    const source = typeof value === 'string' ? value.trim() : '';
    const effectiveFallback = /^#([0-9a-f]{6})$/i.test(fallback) ? fallback.toUpperCase() : '#7AA7D8';
    if (!source) {
      return effectiveFallback;
    }

    const withPrefix = source.startsWith('#') ? source : `#${source}`;
    const shortMatch = /^#([0-9a-f]{3})$/i.exec(withPrefix);
    if (shortMatch) {
      const [r, g, b] = shortMatch[1].split('');
      return `#${r}${r}${g}${g}${b}${b}`.toUpperCase();
    }

    if (/^#([0-9a-f]{6})$/i.test(withPrefix)) {
      return withPrefix.toUpperCase();
    }

    return effectiveFallback;
  }

  private normalizeWallpaperBackgroundDisplayMode(
    value: unknown,
    fallback: WallpaperBackgroundDisplayMode
  ): WallpaperBackgroundDisplayMode {
    const normalized = typeof value === 'string' ? value.trim().toLowerCase() : '';
    switch (normalized) {
      case 'original':
        return 'Original';
      case 'fill':
        return 'Fill';
      case 'stretch':
        return 'Stretch';
      default:
        return fallback;
    }
  }

  private readWallpaperTextStyle(source: unknown, fallback: WallpaperTextStyle): WallpaperTextStyle {
    const record = this.readRecord(source);
    if (!record) {
      return fallback;
    }
    const legacyFontFamily = this.normalizeFontFamily(record['fontFamily'], fallback.timeFontFamily);

    return {
      timeFontFamily: this.normalizeFontFamily(record['timeFontFamily'], legacyFontFamily),
      locationFontFamily: this.normalizeFontFamily(record['locationFontFamily'], legacyFontFamily),
      dateFontFamily: this.normalizeFontFamily(record['dateFontFamily'], legacyFontFamily),
      temperatureFontFamily: this.normalizeFontFamily(record['temperatureFontFamily'], legacyFontFamily),
      summaryFontFamily: this.normalizeFontFamily(record['summaryFontFamily'], legacyFontFamily),
      poiFontFamily: this.normalizeFontFamily(record['poiFontFamily'], legacyFontFamily),
      alertsFontFamily: this.normalizeFontFamily(record['alertsFontFamily'], legacyFontFamily),
      timeFontSize: this.normalizeFontSize(record['timeFontSize'], fallback.timeFontSize),
      locationFontSize: this.normalizeFontSize(record['locationFontSize'], fallback.locationFontSize),
      dateFontSize: this.normalizeFontSize(record['dateFontSize'], fallback.dateFontSize),
      temperatureFontSize: this.normalizeFontSize(record['temperatureFontSize'], fallback.temperatureFontSize),
      summaryFontSize: this.normalizeFontSize(record['summaryFontSize'], fallback.summaryFontSize),
      poiFontSize: this.normalizeFontSize(record['poiFontSize'], fallback.poiFontSize),
      alertsFontSize: this.normalizeFontSize(record['alertsFontSize'], fallback.alertsFontSize)
    };
  }

  private normalizeSystemFonts(fonts: string[]): string[] {
    const set = new Set<string>();
    for (const font of fonts) {
      const normalized = this.normalizeFontFamily(font, '');
      if (normalized) {
        set.add(normalized);
      }
    }

    return [...set].sort((left, right) => left.localeCompare(right));
  }

  private normalizeFontFamily(value: unknown, fallback: string): string {
    const source = typeof value === 'string' ? value.trim() : '';
    const effectiveFallback = fallback.trim() || this.defaultWallpaperTextStyle.timeFontFamily;
    if (!source) {
      return effectiveFallback;
    }

    const sanitized = source.replace(/[^A-Za-z0-9 _.,'"()-]/g, '').trim();
    return sanitized || effectiveFallback;
  }

  private normalizeFontSize(value: unknown, fallback: number): number {
    const parsed = Number(value);
    if (!Number.isFinite(parsed)) {
      return fallback;
    }

    return Math.max(8, Math.min(144, parsed));
  }
}











