(function () {
    if (!window.THREE) {
        document.body.innerHTML = "<div style='padding:16px;color:white;'>Renderer bootstrap failed: three.min.js not loaded.</div>";
        return;
    }

    const timeLabel = document.getElementById("timeText");
    const locationLabel = document.getElementById("location");
    const weatherIcon = document.getElementById("weatherIcon");
    const dateLabel = document.getElementById("dateText");
    const temperatureLabel = document.getElementById("temperatureText");
    const summaryLabel = document.getElementById("summary");
    const poiLabel = document.getElementById("poiText");
    const alertsLabel = document.getElementById("alerts");
    const debugLabel = document.createElement("div");
    debugLabel.id = "renderDebug";
    document.body.appendChild(debugLabel);

    const DEFAULT_CITY_MODEL_CANDIDATES = [];
    const WEATHER_ICON_BASE_URL = "./assets/weather-icons";

    const scene = new THREE.Scene();
    const renderer = new THREE.WebGLRenderer({
        antialias: true,
        alpha: false,
        powerPreference: "high-performance"
    });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2.2));
    renderer.setSize(window.innerWidth, window.innerHeight);
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.06;
    if ("outputColorSpace" in renderer && THREE.SRGBColorSpace) {
        renderer.outputColorSpace = THREE.SRGBColorSpace;
    } else if ("outputEncoding" in renderer && THREE.sRGBEncoding) {
        renderer.outputEncoding = THREE.sRGBEncoding;
    }
    if ("physicallyCorrectLights" in renderer) {
        renderer.physicallyCorrectLights = true;
    }
    document.body.appendChild(renderer.domElement);

    const camera = new THREE.OrthographicCamera(-12, 12, 12, -12, 0.1, 250);
    camera.position.set(20, 20, 20);
    camera.lookAt(0, 1.2, 0);

    const worldRoot = new THREE.Group();
    const defaultCityGroup = new THREE.Group();
    const baseGroup = new THREE.Group();
    const cityGroup = new THREE.Group();
    const cloudGroup = new THREE.Group();
    const precipitationGroup = new THREE.Group();
    const celestialGroup = new THREE.Group();
    worldRoot.add(defaultCityGroup);
    worldRoot.add(baseGroup);
    worldRoot.add(cityGroup);
    worldRoot.add(cloudGroup);
    worldRoot.add(precipitationGroup);
    worldRoot.add(celestialGroup);
    scene.add(worldRoot);

    const hemiLight = new THREE.HemisphereLight(0xddeeff, 0x7788aa, 0.72);
    const sunLight = new THREE.DirectionalLight(0xffffff, 1.24);
    const moonLight = new THREE.DirectionalLight(0xc8deff, 0.18);
    const fillLight = new THREE.DirectionalLight(0x8fb6ff, 0.22);
    sunLight.position.set(15, 26, 14);
    fillLight.position.set(-14, 12, -8);
    scene.add(hemiLight, sunLight, moonLight, fillLight);
    scene.add(sunLight.target, moonLight.target);

    sunLight.castShadow = true;
    sunLight.shadow.mapSize.width = 2048;
    sunLight.shadow.mapSize.height = 2048;
    sunLight.shadow.camera.near = 0.5;
    sunLight.shadow.camera.far = 80;
    sunLight.shadow.camera.left = -18;
    sunLight.shadow.camera.right = 18;
    sunLight.shadow.camera.top = 18;
    sunLight.shadow.camera.bottom = -18;
    sunLight.shadow.bias = -0.00022;

    const sunDisc = new THREE.Mesh(
        new THREE.SphereGeometry(0.64, 16, 16),
        new THREE.MeshBasicMaterial({ color: 0xfff2b2 })
    );
    const moonPhaseCanvas = document.createElement("canvas");
    moonPhaseCanvas.width = 192;
    moonPhaseCanvas.height = 192;
    const moonPhaseContext = moonPhaseCanvas.getContext("2d", { willReadFrequently: true });
    const moonPhaseTexture = new THREE.CanvasTexture(moonPhaseCanvas);
    setTextureColorSpace(moonPhaseTexture);
    if (renderer.capabilities && renderer.capabilities.getMaxAnisotropy) {
        moonPhaseTexture.anisotropy = renderer.capabilities.getMaxAnisotropy();
    }
    const moonDisc = new THREE.Mesh(
        new THREE.PlaneGeometry(1.16, 1.16),
        new THREE.MeshBasicMaterial({
            color: 0xffffff,
            map: moonPhaseTexture,
            transparent: true,
            depthWrite: false
        })
    );
    moonDisc.renderOrder = 4;
    celestialGroup.add(sunDisc, moonDisc);

    const swayTargets = [];
    const weatherParticles = [];
    const billboardPanels = [];
    const dynamicTextures = [];
    const waterSurfaces = [];
    const wetRoadSurfaces = [];
    const vehicleAgents = [];
    const toonGradientMap = createToonGradientMap();
    dynamicTextures.push(toonGradientMap);
    let activePayload = null;
    let random = createSeededRandom(Date.now());
    let lightningFlashUntil = 0;
    let nextLightningAt = 0;
    let frameCount = 0;
    let lastFpsTick = performance.now();
    let fps = 0;
    let lastDebugPush = 0;
    let lastCelestialUpdate = 0;
    let lastFrameTimestamp = 0;
    let lastClockRefresh = 0;
    let activeClockTimeZone = null;
    let cameraPulseOffset = Math.random() * Math.PI * 2;
    let lastMoonPhaseFraction = -1;
    let moonDiscSpin = 0;
    let moonPhaseName = "New Moon";
    let moonIlluminationPercent = 0;
    let activeCelestialLabel = "Sun";
    let activeGlbOrientation = {
        rotationXDegrees: 0,
        rotationYDegrees: 0,
        rotationZDegrees: 0,
        scale: 1,
        offsetX: 0,
        offsetY: 0,
        offsetZ: 0
    };
    let defaultCityModelLoaded = false;
    let defaultCityModelFailed = false;
    let defaultCityLoadPromise = null;
    let defaultCitySceneSource = "none";
    let meshyCityLoadPromise = null;
    let activeMeshyCityModelUrl = "";
    let wallpaperModelEntries = [];
    let wallpaperModelRotationMinutes = 0;
    let wallpaperModelRotationIntervalMs = 0;
    let wallpaperModelNextRotateAtMs = 0;
    let wallpaperModelRelativePath = "";
    let wallpaperModelRotationInFlight = false;
    let wallpaperBackgroundImageTexture = null;
    let wallpaperBackgroundImageUrl = "";
    let wallpaperBackgroundImageLoadId = 0;
    let wallpaperBackgroundMediaType = "none";
    let wallpaperBackgroundVideoElement = null;
    let wallpaperBackgroundMediaAspect = 0;
    let wallpaperBackgroundDisplayMode = "fill";
    let wallpaperBackgroundMediaScene = null;
    let wallpaperBackgroundMediaCamera = null;
    let wallpaperBackgroundMediaQuad = null;
    let wallpaperBackgroundMediaMaterial = null;
    let wallpaperBackgroundMediaEnabled = false;
    let wallpaperBackgroundFallbackColor = new THREE.Color("#7aa7d8");
    let animatedAiBackgroundEnabled = false;
    let showWallpaperStatsOverlay = true;
    let animatedAiBackgroundScene = null;
    let animatedAiBackgroundCamera = null;
    let animatedAiBackgroundUniforms = null;
    let animatedAiBackgroundLastFrameAtMs = 0;
    const animatedAiPointerUv = new THREE.Vector2(0.5, 0.5);
    const animatedAiPointerVelocity = new THREE.Vector2(0, 0);
    let animatedAiPointerStrength = 0;
    let animatedAiPointerAge = 10;
    let animatedAiLastPointerTs = 0;

    function postHostDebug(message) {
        if (!message) {
            return;
        }

        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: "renderer-debug", message: String(message) });
            }
        } catch (error) {
            // Ignore host post failures.
        }
    }

    function setDebugLine(line) {
        debugLabel.textContent = line;
        postHostDebug(line);
    }

    function setStatsOverlayVisibility(visible) {
        const shouldShow = visible !== false;
        showWallpaperStatsOverlay = shouldShow;
        debugLabel.style.display = shouldShow ? "block" : "none";
    }

    function createSeededRandom(seed) {
        let value = seed >>> 0;
        return function () {
            value = (value * 1664525 + 1013904223) >>> 0;
            return value / 0x100000000;
        };
    }

    function hashString(value) {
        let hash = 2166136261;
        const text = String(value || "");
        for (let i = 0; i < text.length; i += 1) {
            hash ^= text.charCodeAt(i);
            hash += (hash << 1) + (hash << 4) + (hash << 7) + (hash << 8) + (hash << 24);
        }
        return hash >>> 0;
    }

    function clamp01(value, fallback) {
        const number = Number(value);
        if (Number.isFinite(number)) {
            return Math.max(0, Math.min(1, number));
        }
        return fallback;
    }

    function asHexColor(value, fallback) {
        if (!value || typeof value !== "string") {
            return fallback;
        }
        const trimmed = value.trim();
        if (/^#([0-9a-f]{3}|[0-9a-f]{6})$/i.test(trimmed)) {
            return trimmed;
        }
        if (/^([0-9a-f]{3}|[0-9a-f]{6})$/i.test(trimmed)) {
            return "#" + trimmed;
        }
        return fallback;
    }

    function toFiniteNumber(value, fallback) {
        const parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    }

    function clampNumber(value, min, max) {
        return Math.max(min, Math.min(max, value));
    }

    function normalizeGlbOrientation(rawOrientation) {
        const source = rawOrientation || {};
        return {
            rotationXDegrees: clampNumber(toFiniteNumber(source.rotationXDegrees, 0), -180, 180),
            rotationYDegrees: clampNumber(toFiniteNumber(source.rotationYDegrees, 0), -180, 180),
            rotationZDegrees: clampNumber(toFiniteNumber(source.rotationZDegrees, 0), -180, 180),
            scale: clampNumber(toFiniteNumber(source.scale, 1), 0.05, 8),
            offsetX: clampNumber(toFiniteNumber(source.offsetX, 0), -30, 30),
            offsetY: clampNumber(toFiniteNumber(source.offsetY, 0), -30, 30),
            offsetZ: clampNumber(toFiniteNumber(source.offsetZ, 0), -30, 30)
        };
    }

    function applyDefaultGlbOrientation(orientation) {
        const normalized = normalizeGlbOrientation(orientation);
        activeGlbOrientation = normalized;
        const degToRadFactor = Math.PI / 180;
        defaultCityGroup.rotation.set(
            normalized.rotationXDegrees * degToRadFactor,
            normalized.rotationYDegrees * degToRadFactor,
            normalized.rotationZDegrees * degToRadFactor
        );
        defaultCityGroup.scale.setScalar(normalized.scale);
        defaultCityGroup.position.set(normalized.offsetX, normalized.offsetY, normalized.offsetZ);
    }

    function setTextureColorSpace(texture) {
        if (!texture) {
            return;
        }

        if ("colorSpace" in texture && THREE.SRGBColorSpace) {
            texture.colorSpace = THREE.SRGBColorSpace;
        } else if ("encoding" in texture && THREE.sRGBEncoding) {
            texture.encoding = THREE.sRGBEncoding;
        }
    }

    function normalizeWallpaperBackgroundDisplayMode(mode) {
        const normalized = String(mode || "").trim().toLowerCase();
        if (normalized === "original") {
            return "original";
        }

        if (normalized === "stretch") {
            return "stretch";
        }

        return "fill";
    }

    function inferWallpaperBackgroundMediaType(sourceUrl) {
        const normalizedUrl = String(sourceUrl || "").trim().toLowerCase();
        const baseUrl = normalizedUrl.split("?")[0];
        return /\.(mp4|webm|ogg|mov|m4v|mkv)$/i.test(baseUrl) ? "video" : "image";
    }

    function enforceSilentVideo(videoElement) {
        if (!videoElement) {
            return;
        }

        videoElement.muted = true;
        videoElement.defaultMuted = true;
        videoElement.volume = 0;
        videoElement.setAttribute("muted", "");

        if (videoElement.audioTracks && videoElement.audioTracks.length > 0) {
            for (let index = 0; index < videoElement.audioTracks.length; index += 1) {
                videoElement.audioTracks[index].enabled = false;
            }
        }
    }

    function ensureWallpaperBackgroundMediaResources() {
        if (wallpaperBackgroundMediaScene && wallpaperBackgroundMediaCamera && wallpaperBackgroundMediaQuad && wallpaperBackgroundMediaMaterial) {
            return;
        }

        wallpaperBackgroundMediaMaterial = new THREE.MeshBasicMaterial({
            map: null,
            transparent: true,
            depthTest: false,
            depthWrite: false,
            toneMapped: false
        });
        wallpaperBackgroundMediaQuad = new THREE.Mesh(new THREE.PlaneGeometry(2, 2), wallpaperBackgroundMediaMaterial);
        wallpaperBackgroundMediaScene = new THREE.Scene();
        wallpaperBackgroundMediaScene.add(wallpaperBackgroundMediaQuad);
        wallpaperBackgroundMediaCamera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
    }

    function disposeWallpaperBackgroundVideoElement(videoElement) {
        if (!videoElement) {
            return;
        }

        try {
            videoElement.pause();
            videoElement.removeAttribute("src");
            videoElement.load();
        } catch (error) {
            // Ignore best-effort cleanup errors.
        }
    }

    function clearWallpaperBackgroundVideoElement() {
        if (!wallpaperBackgroundVideoElement) {
            return;
        }

        disposeWallpaperBackgroundVideoElement(wallpaperBackgroundVideoElement);
        wallpaperBackgroundVideoElement = null;
    }

    function updateWallpaperBackgroundMediaTransform() {
        if (!wallpaperBackgroundMediaQuad || !wallpaperBackgroundMediaEnabled) {
            return;
        }

        const viewportAspect = Math.max(0.001, window.innerWidth / Math.max(1, window.innerHeight));
        const mediaAspect = wallpaperBackgroundMediaAspect > 0 ? wallpaperBackgroundMediaAspect : viewportAspect;
        const mode = normalizeWallpaperBackgroundDisplayMode(wallpaperBackgroundDisplayMode);
        let scaleX = 1;
        let scaleY = 1;

        if (mode === "stretch") {
            scaleX = 1;
            scaleY = 1;
        } else if (mode === "fill") {
            if (mediaAspect > viewportAspect) {
                scaleX = mediaAspect / viewportAspect;
                scaleY = 1;
            } else {
                scaleX = 1;
                scaleY = viewportAspect / mediaAspect;
            }
        } else {
            if (mediaAspect > viewportAspect) {
                scaleX = 1;
                scaleY = viewportAspect / mediaAspect;
            } else {
                scaleX = mediaAspect / viewportAspect;
                scaleY = 1;
            }
        }

        wallpaperBackgroundMediaQuad.scale.set(
            Number.isFinite(scaleX) ? Math.max(0.001, scaleX) : 1,
            Number.isFinite(scaleY) ? Math.max(0.001, scaleY) : 1,
            1
        );
    }

    function disposeWallpaperBackgroundImageTexture() {
        if (wallpaperBackgroundImageTexture && wallpaperBackgroundImageTexture.dispose) {
            wallpaperBackgroundImageTexture.dispose();
        }

        wallpaperBackgroundImageTexture = null;
        wallpaperBackgroundImageUrl = "";
        wallpaperBackgroundMediaType = "none";
        wallpaperBackgroundMediaAspect = 0;
        wallpaperBackgroundMediaEnabled = false;
        clearWallpaperBackgroundVideoElement();
        if (wallpaperBackgroundMediaMaterial) {
            wallpaperBackgroundMediaMaterial.map = null;
            wallpaperBackgroundMediaMaterial.needsUpdate = true;
        }
    }

    function setWallpaperBackgroundMediaTexture(texture, sourceUrl, mediaType, aspectRatio, displayMode, videoElement) {
        ensureWallpaperBackgroundMediaResources();

        if (wallpaperBackgroundImageTexture && wallpaperBackgroundImageTexture !== texture && wallpaperBackgroundImageTexture.dispose) {
            wallpaperBackgroundImageTexture.dispose();
        }

        if (wallpaperBackgroundVideoElement && wallpaperBackgroundVideoElement !== videoElement) {
            disposeWallpaperBackgroundVideoElement(wallpaperBackgroundVideoElement);
        }

        wallpaperBackgroundImageTexture = texture;
        wallpaperBackgroundImageUrl = sourceUrl;
        wallpaperBackgroundMediaType = mediaType;
        wallpaperBackgroundMediaAspect = Number.isFinite(aspectRatio) && aspectRatio > 0 ? aspectRatio : 1;
        wallpaperBackgroundDisplayMode = normalizeWallpaperBackgroundDisplayMode(displayMode);
        wallpaperBackgroundVideoElement = mediaType === "video" ? (videoElement || null) : null;
        wallpaperBackgroundMediaMaterial.map = texture;
        wallpaperBackgroundMediaMaterial.needsUpdate = true;
        wallpaperBackgroundMediaEnabled = true;
        updateWallpaperBackgroundMediaTransform();
    }

    function ensureWallpaperBackgroundMedia(sourceUrl, displayMode, fallbackBackground) {
        const normalizedUrl = String(sourceUrl || "").trim();
        wallpaperBackgroundDisplayMode = normalizeWallpaperBackgroundDisplayMode(displayMode);
        if (!normalizedUrl) {
            wallpaperBackgroundImageLoadId += 1;
            disposeWallpaperBackgroundImageTexture();
            scene.background = fallbackBackground;
            return false;
        }

        if (wallpaperBackgroundImageTexture && normalizedUrl === wallpaperBackgroundImageUrl) {
            wallpaperBackgroundDisplayMode = normalizeWallpaperBackgroundDisplayMode(displayMode);
            wallpaperBackgroundMediaEnabled = true;
            updateWallpaperBackgroundMediaTransform();
            scene.background = null;
            return true;
        }

        const currentLoadId = ++wallpaperBackgroundImageLoadId;
        const mediaType = inferWallpaperBackgroundMediaType(normalizedUrl);

        if (mediaType === "video") {
            const video = document.createElement("video");
            video.src = normalizedUrl;
            video.crossOrigin = "anonymous";
            video.loop = true;
            video.muted = true;
            video.defaultMuted = true;
            video.volume = 0;
            video.playsInline = true;
            video.preload = "auto";
            video.setAttribute("playsinline", "");
            video.setAttribute("webkit-playsinline", "");
            enforceSilentVideo(video);
            video.addEventListener("volumechange", () => enforceSilentVideo(video));
            video.addEventListener("play", () => enforceSilentVideo(video));
            wallpaperBackgroundVideoElement = video;

            const tryPlay = () => {
                const playPromise = video.play();
                if (playPromise && typeof playPromise.catch === "function") {
                    playPromise.catch(() => {
                        // Autoplay may be blocked in some WebView states.
                    });
                }
            };

            video.addEventListener("loadedmetadata", () => {
                if (currentLoadId !== wallpaperBackgroundImageLoadId) {
                    return;
                }

                enforceSilentVideo(video);

                const videoTexture = new THREE.VideoTexture(video);
                setTextureColorSpace(videoTexture);
                videoTexture.minFilter = THREE.LinearFilter;
                videoTexture.magFilter = THREE.LinearFilter;
                videoTexture.generateMipmaps = false;
                const mediaAspect = video.videoWidth > 0 && video.videoHeight > 0
                    ? video.videoWidth / video.videoHeight
                    : (window.innerWidth / Math.max(1, window.innerHeight));
                setWallpaperBackgroundMediaTexture(videoTexture, normalizedUrl, "video", mediaAspect, displayMode, video);
                scene.background = null;
                tryPlay();
                postHostDebug(`Wallpaper background video loaded: ${normalizedUrl}`);
            }, { once: true });

            video.addEventListener("canplay", tryPlay, { once: true });
            video.addEventListener("error", (error) => {
                if (currentLoadId !== wallpaperBackgroundImageLoadId) {
                    return;
                }

                disposeWallpaperBackgroundImageTexture();
                scene.background = fallbackBackground;
                postHostDebug(`Wallpaper background video failed to load (${normalizedUrl}): ${String(error)}`);
            }, { once: true });

            video.load();
            scene.background = fallbackBackground;
            return false;
        }

        const loader = new THREE.TextureLoader();
        loader.load(
            normalizedUrl,
            (texture) => {
                if (currentLoadId !== wallpaperBackgroundImageLoadId) {
                    if (texture && texture.dispose) {
                        texture.dispose();
                    }
                    return;
                }

                setTextureColorSpace(texture);
                texture.wrapS = THREE.ClampToEdgeWrapping;
                texture.wrapT = THREE.ClampToEdgeWrapping;
                texture.needsUpdate = true;
                const image = texture.image || {};
                const mediaAspect = image.width > 0 && image.height > 0
                    ? image.width / image.height
                    : (window.innerWidth / Math.max(1, window.innerHeight));
                setWallpaperBackgroundMediaTexture(texture, normalizedUrl, "image", mediaAspect, displayMode, null);
                scene.background = null;
                postHostDebug(`Wallpaper background image loaded: ${normalizedUrl}`);
            },
            undefined,
            (error) => {
                if (currentLoadId !== wallpaperBackgroundImageLoadId) {
                    return;
                }

                disposeWallpaperBackgroundImageTexture();
                scene.background = fallbackBackground;
                postHostDebug(`Wallpaper background image failed to load (${normalizedUrl}): ${String(error)}`);
            });

        scene.background = fallbackBackground;
        return false;
    }

    function ensureAnimatedAiBackgroundResources() {
        if (animatedAiBackgroundScene && animatedAiBackgroundCamera && animatedAiBackgroundUniforms) {
            return;
        }

        animatedAiBackgroundUniforms = {
            uTime: { value: 0 },
            uBaseColor: { value: new THREE.Color("#1f2a3a") },
            uTintA: { value: new THREE.Color("#5ea6ff") },
            uTintB: { value: new THREE.Color("#87c0ff") },
            uTintC: { value: new THREE.Color("#cde4ff") },
            uNightFactor: { value: 0 },
            uMotionBoost: { value: 1.0 },
            uPointer: { value: new THREE.Vector2(0.5, 0.5) },
            uPointerVelocity: { value: new THREE.Vector2(0, 0) },
            uPointerStrength: { value: 0 },
            uPointerAge: { value: 10 }
        };

        const vertexShader = `
            varying vec2 vUv;
            void main() {
                vUv = uv;
                gl_Position = vec4(position.xy, 0.0, 1.0);
            }
        `;

        const fragmentShader = `
            precision highp float;
            varying vec2 vUv;
            uniform float uTime;
            uniform vec3 uBaseColor;
            uniform vec3 uTintA;
            uniform vec3 uTintB;
            uniform vec3 uTintC;
            uniform float uNightFactor;
            uniform float uMotionBoost;
            uniform vec2 uPointer;
            uniform vec2 uPointerVelocity;
            uniform float uPointerStrength;
            uniform float uPointerAge;

            float blob(vec2 uv, vec2 center, float radius) {
                float dist = length(uv - center);
                return smoothstep(radius, radius * 0.16, dist);
            }

            void main() {
                vec2 uv = vUv;
                float t = uTime;
                float motion = 1.0 + (uMotionBoost * 0.5);

                vec2 centerA = vec2(0.22 + sin(t * 0.20) * (0.16 * motion), 0.80 + cos(t * 0.18) * (0.12 * motion));
                vec2 centerB = vec2(0.76 + sin(t * 0.14 + 1.2) * (0.18 * motion), 0.24 + cos(t * 0.13 + 0.7) * (0.13 * motion));
                vec2 centerC = vec2(0.58 + sin(t * 0.16 + 3.0) * (0.13 * motion), 0.64 + cos(t * 0.19 + 2.0) * (0.14 * motion));
                vec2 centerD = vec2(0.35 + sin(t * 0.11 + 2.1) * (0.20 * motion), 0.40 + cos(t * 0.15 + 3.1) * (0.13 * motion));
                vec2 centerE = vec2(0.52 + sin(t * 0.09 + 4.1) * (0.22 * motion), 0.30 + cos(t * 0.12 + 1.7) * (0.11 * motion));

                float layerA = blob(uv, centerA, 0.72);
                float layerB = blob(uv, centerB, 0.64);
                float layerC = blob(uv, centerC, 0.58);
                float layerD = blob(uv, centerD, 0.70);
                float layerE = blob(uv, centerE, 0.74);

                vec3 color = uBaseColor * (0.54 - (uNightFactor * 0.12));
                color += uTintA * (layerA * 0.88);
                color += uTintB * (layerB * 0.82);
                color += uTintC * (layerC * 0.68);
                color += mix(uTintA, uTintB, 0.5) * (layerD * 0.58);
                color += mix(uTintB, uTintC, 0.4) * (layerE * 0.34);

                vec2 pointerDir = normalize(uPointerVelocity + vec2(0.0001, 0.0001));
                float pointerAgeFade = exp(-uPointerAge * 2.4);
                float pointerCore = blob(uv, uPointer, 0.26) * pointerAgeFade * uPointerStrength;
                float pointerTrail = blob(uv, uPointer - pointerDir * 0.14, 0.42) * pointerAgeFade * uPointerStrength * 0.75;
                float pointerWake = blob(uv, uPointer - pointerDir * 0.24, 0.58) * pointerAgeFade * uPointerStrength * 0.42;
                vec3 pointerColor = mix(uTintC, vec3(1.0), 0.42);
                color += pointerColor * (pointerCore * 0.62 + pointerTrail * 0.46 + pointerWake * 0.28);

                float vignette = smoothstep(1.18, 0.16, length((uv - vec2(0.5)) * vec2(1.18, 1.0)));
                color = mix(color * 0.72, color, vignette);
                color = pow(max(color, vec3(0.0)), vec3(0.9));
                color = min(color * 1.08, vec3(1.0));

                gl_FragColor = vec4(color, 1.0);
            }
        `;

        const material = new THREE.ShaderMaterial({
            uniforms: animatedAiBackgroundUniforms,
            vertexShader: vertexShader,
            fragmentShader: fragmentShader,
            depthTest: false,
            depthWrite: false
        });

        const quad = new THREE.Mesh(new THREE.PlaneGeometry(2, 2), material);
        animatedAiBackgroundScene = new THREE.Scene();
        animatedAiBackgroundScene.add(quad);
        animatedAiBackgroundCamera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
    }

    function setAnimatedAiBackgroundEnabled(enabled, baseColor, timeOfDay, sunStrength) {
        animatedAiBackgroundEnabled = !!enabled;
        if (!animatedAiBackgroundEnabled) {
            animatedAiBackgroundLastFrameAtMs = 0;
            return;
        }

        ensureAnimatedAiBackgroundResources();

        const base = (baseColor && baseColor.isColor ? baseColor : new THREE.Color("#7aa7d8")).clone();
        const normalizedTimeOfDay = String(timeOfDay || "day").toLowerCase();
        const nightFactor = normalizedTimeOfDay === "night"
            ? 1
            : (normalizedTimeOfDay === "dusk" || normalizedTimeOfDay === "dawn" ? 0.5 : 0);
        const sunFactor = clamp01(sunStrength, 0.5);
        const motionBoost = normalizedTimeOfDay === "night" ? 0.78 : (0.92 + (sunFactor * 0.4));

        animatedAiBackgroundUniforms.uBaseColor.value.copy(
            base.clone().multiplyScalar(0.2 + (sunFactor * 0.26))
        );
        animatedAiBackgroundUniforms.uTintA.value.copy(
            base.clone().lerp(new THREE.Color("#4c9aff"), 0.66)
        );
        animatedAiBackgroundUniforms.uTintB.value.copy(
            base.clone().lerp(new THREE.Color("#87c7ff"), 0.76)
        );
        animatedAiBackgroundUniforms.uTintC.value.copy(
            base.clone().lerp(new THREE.Color("#f2f8ff"), 0.84)
        );
        animatedAiBackgroundUniforms.uNightFactor.value = nightFactor;
        animatedAiBackgroundUniforms.uMotionBoost.value = motionBoost;
    }

    function renderAnimatedAiBackground(timestampMs) {
        if (!animatedAiBackgroundEnabled || !animatedAiBackgroundUniforms) {
            return;
        }

        const dtSeconds = animatedAiBackgroundLastFrameAtMs > 0
            ? Math.max(0.001, Math.min(0.05, (timestampMs - animatedAiBackgroundLastFrameAtMs) * 0.001))
            : 0.016;
        animatedAiBackgroundLastFrameAtMs = timestampMs;
        animatedAiPointerStrength = Math.max(0, animatedAiPointerStrength - (dtSeconds * 0.38));
        animatedAiPointerAge = Math.min(12, animatedAiPointerAge + dtSeconds);
        animatedAiPointerVelocity.multiplyScalar(Math.pow(0.05, dtSeconds * 2.5));

        animatedAiBackgroundUniforms.uTime.value = timestampMs * 0.001;
        animatedAiBackgroundUniforms.uPointer.value.copy(animatedAiPointerUv);
        animatedAiBackgroundUniforms.uPointerVelocity.value.copy(animatedAiPointerVelocity);
        animatedAiBackgroundUniforms.uPointerStrength.value = animatedAiPointerStrength;
        animatedAiBackgroundUniforms.uPointerAge.value = animatedAiPointerAge;
    }

    function updateAnimatedAiPointerFromClient(clientX, clientY) {
        const width = Math.max(1, window.innerWidth);
        const height = Math.max(1, window.innerHeight);
        const normalizedX = clampNumber(clientX / width, 0, 1);
        const normalizedY = clampNumber(1 - (clientY / height), 0, 1);
        const now = performance.now();
        const deltaMs = Math.max(1, now - animatedAiLastPointerTs);

        const velocityX = (normalizedX - animatedAiPointerUv.x) / deltaMs;
        const velocityY = (normalizedY - animatedAiPointerUv.y) / deltaMs;
        animatedAiPointerVelocity.set(
            clampNumber(velocityX * 1200, -2, 2),
            clampNumber(velocityY * 1200, -2, 2)
        );
        animatedAiPointerUv.set(normalizedX, normalizedY);
        animatedAiPointerStrength = Math.min(1, Math.max(animatedAiPointerStrength, 0.45) + 0.28);
        animatedAiPointerAge = 0;
        animatedAiLastPointerTs = now;
    }

    function clearGroup(group) {
        for (let index = group.children.length - 1; index >= 0; index -= 1) {
            const child = group.children[index];
            group.remove(child);
            child.traverse((node) => {
                if (node.geometry) {
                    node.geometry.dispose();
                }
                if (node.material) {
                    if (Array.isArray(node.material)) {
                        node.material.forEach((material) => {
                            if (material.map) {
                                material.map.dispose();
                            }
                            material.dispose();
                        });
                    } else {
                        if (node.material.map) {
                            node.material.map.dispose();
                        }
                        node.material.dispose();
                    }
                }
            });
        }
    }

    function clearDynamicTextures() {
        while (dynamicTextures.length > 0) {
            const texture = dynamicTextures.pop();
            if (texture && texture.dispose) {
                texture.dispose();
            }
        }
    }

    function resizeCamera() {
        const aspect = Math.max(0.001, window.innerWidth / Math.max(1, window.innerHeight));
        const frustum = 13;
        camera.left = -frustum * aspect;
        camera.right = frustum * aspect;
        camera.top = frustum;
        camera.bottom = -frustum;
        camera.updateProjectionMatrix();
        renderer.setSize(window.innerWidth, window.innerHeight);
        updateWallpaperBackgroundMediaTransform();
        if (animatedAiBackgroundEnabled) {
            ensureAnimatedAiBackgroundResources();
            renderAnimatedAiBackground(performance.now());
        }
        setDebugLine(`Renderer ${window.innerWidth}x${window.innerHeight} @ DPR ${Math.round((window.devicePixelRatio || 1) * 100) / 100}`);
    }

    function createToonGradientMap() {
        const canvas = document.createElement("canvas");
        canvas.width = 4;
        canvas.height = 1;
        const context = canvas.getContext("2d");
        context.fillStyle = "#2a3342";
        context.fillRect(0, 0, 1, 1);
        context.fillStyle = "#5f7188";
        context.fillRect(1, 0, 1, 1);
        context.fillStyle = "#9fb4cd";
        context.fillRect(2, 0, 1, 1);
        context.fillStyle = "#edf6ff";
        context.fillRect(3, 0, 1, 1);

        const texture = new THREE.CanvasTexture(canvas);
        texture.minFilter = THREE.NearestFilter;
        texture.magFilter = THREE.NearestFilter;
        texture.generateMipmaps = false;
        setTextureColorSpace(texture);
        return texture;
    }

    function createFacadeTexture(colorHex) {
        const canvas = document.createElement("canvas");
        canvas.width = 128;
        canvas.height = 128;
        const context = canvas.getContext("2d");

        const baseColor = new THREE.Color(colorHex);
        const deepTone = baseColor.clone().multiplyScalar(0.62);
        const lightTone = baseColor.clone().lerp(new THREE.Color(0xffffff), 0.24);
        const midTone = baseColor.clone().lerp(new THREE.Color("#c9d6e6"), 0.2);

        const gradient = context.createLinearGradient(0, 0, 0, 128);
        gradient.addColorStop(0, "#" + lightTone.getHexString());
        gradient.addColorStop(0.45, "#" + midTone.getHexString());
        gradient.addColorStop(1, "#" + deepTone.getHexString());
        context.fillStyle = gradient;
        context.fillRect(0, 0, 128, 128);

        context.fillStyle = "rgba(255,255,255,0.06)";
        for (let index = 0; index < 450; index += 1) {
            const x = Math.random() * 128;
            const y = Math.random() * 128;
            const size = 0.5 + Math.random() * 1.6;
            context.fillRect(x, y, size, size);
        }

        const horizontalBands = 2 + Math.floor(Math.random() * 3);
        for (let band = 0; band < horizontalBands; band += 1) {
            const y = 18 + band * (26 + Math.random() * 8);
            context.fillStyle = `rgba(255,255,255,${0.05 + Math.random() * 0.06})`;
            context.fillRect(0, y, 128, 5 + Math.random() * 5);
        }

        const gridX = 8 + Math.floor(Math.random() * 4);
        const gridY = 8 + Math.floor(Math.random() * 4);
        const cellWidth = 128 / gridX;
        const cellHeight = 128 / gridY;
        for (let gy = 0; gy < gridY; gy += 1) {
            for (let gx = 0; gx < gridX; gx += 1) {
                if (Math.random() > 0.78) {
                    continue;
                }

                const padX = 1 + Math.random() * 2.5;
                const padY = 1 + Math.random() * 2;
                const windowWidth = Math.max(2.5, cellWidth - (padX * 2));
                const windowHeight = Math.max(3, cellHeight - (padY * 2));
                const opacity = 0.07 + Math.random() * 0.22;
                context.fillStyle = `rgba(255,255,255,${opacity.toFixed(3)})`;
                context.fillRect((gx * cellWidth) + padX, (gy * cellHeight) + padY, windowWidth, windowHeight);

                if (Math.random() > 0.74) {
                    context.fillStyle = `rgba(255,233,176,${(0.08 + Math.random() * 0.12).toFixed(3)})`;
                    context.fillRect((gx * cellWidth) + padX + 1, (gy * cellHeight) + padY + 1, Math.max(1, windowWidth - 2), Math.max(1, windowHeight - 2));
                }
            }
        }

        context.strokeStyle = "rgba(0,0,0,0.12)";
        context.lineWidth = 1;
        const seamCount = 2 + Math.floor(Math.random() * 3);
        for (let seam = 0; seam < seamCount; seam += 1) {
            const x = 14 + (seam * 28) + Math.random() * 14;
            context.beginPath();
            context.moveTo(x, 0);
            context.lineTo(x + (Math.random() - 0.5) * 3, 128);
            context.stroke();
        }

        const texture = new THREE.CanvasTexture(canvas);
        texture.wrapS = THREE.RepeatWrapping;
        texture.wrapT = THREE.RepeatWrapping;
        texture.repeat.set(1, 1.5);
        if (renderer.capabilities && renderer.capabilities.getMaxAnisotropy) {
            texture.anisotropy = renderer.capabilities.getMaxAnisotropy();
        }
        setTextureColorSpace(texture);
        dynamicTextures.push(texture);
        return texture;
    }

    function buildDioramaBase(sceneData, paletteA, paletteB, paletteC, pointsOfInterest) {
        const baseSize = 16.5;
        const slab = new THREE.Mesh(
            new THREE.BoxGeometry(baseSize, 1.0, baseSize),
            new THREE.MeshStandardMaterial({ color: new THREE.Color(paletteB).multiplyScalar(0.67), roughness: 0.82 })
        );
        slab.position.y = -0.62;
        slab.receiveShadow = true;
        baseGroup.add(slab);

        const topTexture = createFacadeTexture(paletteA);
        topTexture.repeat.set(2.2, 2.2);
        const topPlate = new THREE.Mesh(
            new THREE.BoxGeometry(baseSize - 0.3, 0.2, baseSize - 0.3),
            new THREE.MeshStandardMaterial({
                color: new THREE.Color(paletteA).multiplyScalar(0.94),
                map: topTexture,
                roughness: 0.62
            })
        );
        topPlate.position.y = -0.02;
        topPlate.receiveShadow = true;
        baseGroup.add(topPlate);

        const roadMaterial = new THREE.MeshStandardMaterial({ color: 0x5c6674, roughness: 0.9 });
        const centerRoadA = new THREE.Mesh(new THREE.BoxGeometry(13.5, 0.06, 1.4), roadMaterial);
        const centerRoadB = new THREE.Mesh(new THREE.BoxGeometry(1.4, 0.06, 13.5), roadMaterial);
        centerRoadA.position.y = 0.1;
        centerRoadB.position.y = 0.1;
        centerRoadA.receiveShadow = true;
        centerRoadB.receiveShadow = true;
        baseGroup.add(centerRoadA, centerRoadB);

        const lanePaintMaterial = new THREE.MeshStandardMaterial({
            color: 0xece9cf,
            roughness: 0.62,
            metalness: 0.04
        });
        for (let stripe = -5; stripe <= 5; stripe += 1) {
            if (stripe === 0) {
                continue;
            }

            const stripeLong = new THREE.Mesh(new THREE.PlaneGeometry(0.24, 0.64), lanePaintMaterial);
            stripeLong.rotation.x = -Math.PI / 2;
            stripeLong.position.set(stripe * 1.08, 0.138, 0);
            baseGroup.add(stripeLong);

            const stripeWide = new THREE.Mesh(new THREE.PlaneGeometry(0.64, 0.24), lanePaintMaterial);
            stripeWide.rotation.x = -Math.PI / 2;
            stripeWide.position.set(0, 0.138, stripe * 1.08);
            baseGroup.add(stripeWide);
        }

        const pointText = pointsOfInterest.join(" ").toLowerCase();
        const needsWater = pointText.includes("harbor") || pointText.includes("bay") || pointText.includes("beach") || pointText.includes("pier");
        const needsVolcanicLandmark = pointText.includes("diamond head")
            || pointText.includes("crater")
            || pointText.includes("volcano")
            || pointText.includes("pali")
            || pointText.includes("mount");

        if (needsVolcanicLandmark) {
            const terrainTexture = createFacadeTexture("#6a9366");
            terrainTexture.repeat.set(2.2, 2.4);
            const ridge = new THREE.Mesh(
                new THREE.ConeGeometry(3.2, 2.35, 28),
                new THREE.MeshStandardMaterial({
                    color: 0x6f8f61,
                    map: terrainTexture,
                    roughness: 0.84
                })
            );
            ridge.position.set(-5.05, 1.15, 0.45);
            ridge.castShadow = true;
            ridge.receiveShadow = true;
            baseGroup.add(ridge);

            const craterRing = new THREE.Mesh(
                new THREE.TorusGeometry(1.12, 0.2, 12, 32),
                new THREE.MeshStandardMaterial({ color: 0x66805d, roughness: 0.88 })
            );
            craterRing.position.set(-5.05, 2.18, 0.45);
            craterRing.rotation.x = Math.PI / 2;
            craterRing.castShadow = true;
            craterRing.receiveShadow = true;
            baseGroup.add(craterRing);

            const craterFloor = new THREE.Mesh(
                new THREE.CylinderGeometry(0.95, 1.05, 0.22, 28),
                new THREE.MeshStandardMaterial({ color: 0x5a7d4e, roughness: 0.9 })
            );
            craterFloor.position.set(-5.05, 1.86, 0.45);
            craterFloor.castShadow = true;
            craterFloor.receiveShadow = true;
            baseGroup.add(craterFloor);
        }

        if (needsWater || (sceneData.props || []).join(" ").toLowerCase().includes("water")) {
            const waterTexture = createFacadeTexture("#74c5ff");
            waterTexture.repeat.set(2.1, 2.0);
            const water = new THREE.Mesh(
                new THREE.BoxGeometry(7.8, 0.05, 5.8),
                new THREE.MeshStandardMaterial({
                    color: 0x68b9ff,
                    map: waterTexture,
                    roughness: 0.24,
                    metalness: 0.18
                })
            );
            water.position.set(4.05, 0.11, 4.55);
            water.receiveShadow = true;
            baseGroup.add(water);
            waterSurfaces.push({
                mesh: water,
                texture: waterTexture,
                phase: random() * Math.PI * 2,
                baseY: water.position.y
            });

            const shorelineFoam = new THREE.Mesh(
                new THREE.PlaneGeometry(8.0, 0.42),
                new THREE.MeshStandardMaterial({
                    color: 0xeef9ff,
                    transparent: true,
                    opacity: 0.42,
                    roughness: 0.38,
                    metalness: 0.04
                })
            );
            shorelineFoam.rotation.x = -Math.PI / 2;
            shorelineFoam.position.set(4.05, 0.145, 1.8);
            baseGroup.add(shorelineFoam);
            waterSurfaces.push({
                mesh: shorelineFoam,
                texture: null,
                phase: random() * Math.PI * 2,
                baseY: shorelineFoam.position.y
            });

            const pier = new THREE.Mesh(
                new THREE.BoxGeometry(3.2, 0.16, 0.42),
                new THREE.MeshStandardMaterial({ color: new THREE.Color(paletteC).multiplyScalar(0.88), roughness: 0.76 })
            );
            pier.position.set(4.8, 0.15, 2.1);
            pier.castShadow = true;
            pier.receiveShadow = true;
            baseGroup.add(pier);
        }
    }

    function createMiniatureBuildingMaterial(baseHex, style, height) {
        const textureColor = style === "glass"
            ? new THREE.Color(baseHex).lerp(new THREE.Color("#b9d3ef"), 0.42)
            : new THREE.Color(baseHex).lerp(new THREE.Color("#d7dde4"), 0.26);
        const facadeTexture = createFacadeTexture("#" + textureColor.getHexString());
        facadeTexture.repeat.set(
            style === "glass" ? 1.25 : 1,
            Math.max(1.5, height / (style === "glass" ? 1.5 : 2.1))
        );

        if (style === "glass") {
            return new THREE.MeshPhysicalMaterial({
                color: new THREE.Color(baseHex).lerp(new THREE.Color("#d4e6fa"), 0.36),
                map: facadeTexture,
                roughness: 0.22,
                metalness: 0.08,
                clearcoat: 0.55,
                clearcoatRoughness: 0.14,
                transmission: 0.02,
                thickness: 0.22
            });
        }

        return new THREE.MeshToonMaterial({
            color: new THREE.Color(baseHex).lerp(new THREE.Color("#ebf1f7"), 0.12),
            map: facadeTexture,
            gradientMap: toonGradientMap
        });
    }

    function createRoundedRectShape(width, depth, radius) {
        const halfWidth = Math.max(0.06, width / 2);
        const halfDepth = Math.max(0.06, depth / 2);
        const corner = Math.max(0.02, Math.min(radius, halfWidth * 0.96, halfDepth * 0.96));

        const shape = new THREE.Shape();
        shape.moveTo(-halfWidth + corner, -halfDepth);
        shape.lineTo(halfWidth - corner, -halfDepth);
        shape.quadraticCurveTo(halfWidth, -halfDepth, halfWidth, -halfDepth + corner);
        shape.lineTo(halfWidth, halfDepth - corner);
        shape.quadraticCurveTo(halfWidth, halfDepth, halfWidth - corner, halfDepth);
        shape.lineTo(-halfWidth + corner, halfDepth);
        shape.quadraticCurveTo(-halfWidth, halfDepth, -halfWidth, halfDepth - corner);
        shape.lineTo(-halfWidth, -halfDepth + corner);
        shape.quadraticCurveTo(-halfWidth, -halfDepth, -halfWidth + corner, -halfDepth);
        return shape;
    }

    function createRoundedExtrudedMesh(width, depth, height, material, options) {
        const safeWidth = Math.max(0.12, width);
        const safeDepth = Math.max(0.12, depth);
        const safeHeight = Math.max(0.08, height);
        const requestedCorner = options && typeof options.cornerRadius === "number"
            ? options.cornerRadius
            : Math.min(safeWidth, safeDepth) * 0.2;
        const cornerRadius = Math.max(0.02, Math.min(requestedCorner, safeWidth * 0.46, safeDepth * 0.46));
        const bevelSize = Math.max(0.01, Math.min(
            options && typeof options.bevelSize === "number" ? options.bevelSize : cornerRadius * 0.56,
            cornerRadius * 0.9));
        const bevelThickness = Math.max(0.01, Math.min(
            options && typeof options.bevelThickness === "number" ? options.bevelThickness : Math.min(0.18, safeHeight * 0.11),
            safeHeight * 0.35));

        const shape = createRoundedRectShape(safeWidth, safeDepth, cornerRadius);
        const geometry = new THREE.ExtrudeGeometry(shape, {
            depth: safeHeight,
            steps: 1,
            curveSegments: 16,
            bevelEnabled: true,
            bevelSegments: 3,
            bevelSize,
            bevelThickness
        });
        geometry.rotateX(-Math.PI / 2);
        geometry.translate(0, -(safeHeight / 2), 0);

        const mesh = new THREE.Mesh(geometry, material);
        mesh.castShadow = true;
        mesh.receiveShadow = true;
        return mesh;
    }

    function addShrubScatter(parent, x, y, z, spreadX, spreadZ, count, tintHex) {
        const baseColor = new THREE.Color(tintHex || "#5c9b6e");
        for (let index = 0; index < count; index += 1) {
            const shrub = new THREE.Mesh(
                new THREE.SphereGeometry(0.06 + random() * 0.05, 10, 8),
                new THREE.MeshStandardMaterial({
                    color: baseColor.clone().offsetHSL((random() - 0.5) * 0.08, 0.04, (random() - 0.5) * 0.08),
                    roughness: 0.82,
                    metalness: 0.02
                })
            );
            shrub.position.set(
                x + (random() - 0.5) * spreadX,
                y + (random() - 0.5) * 0.04,
                z + (random() - 0.5) * spreadZ
            );
            shrub.castShadow = true;
            shrub.receiveShadow = true;
            parent.add(shrub);
        }
    }

    function addCartoonOutline(root, thickness, colorHex, opacity, maxMeshes) {
        if (!root) {
            return;
        }

        const meshes = [];
        root.traverse((node) => {
            if (!node || !node.isMesh || !node.geometry || node.userData.isCartoonOutline || node.userData.hasCartoonOutline) {
                return;
            }

            meshes.push(node);
        });

        const count = Math.min(maxMeshes || meshes.length, meshes.length);
        for (let index = 0; index < count; index += 1) {
            const mesh = meshes[index];
            const outline = new THREE.Mesh(
                mesh.geometry.clone(),
                new THREE.MeshBasicMaterial({
                    color: colorHex || 0x34445a,
                    side: THREE.BackSide,
                    transparent: true,
                    opacity: typeof opacity === "number" ? opacity : 0.18
                })
            );
            outline.scale.set(
                1 + (thickness || 0.03),
                1 + (thickness || 0.03),
                1 + (thickness || 0.03)
            );
            outline.userData.isCartoonOutline = true;
            outline.renderOrder = 1;
            mesh.userData.hasCartoonOutline = true;
            mesh.add(outline);
        }
    }

    function addPalmCluster(x, z, count) {
        const trunkMaterial = new THREE.MeshStandardMaterial({ color: 0x7a5a3a, roughness: 0.86 });
        const leafMaterial = new THREE.MeshStandardMaterial({ color: 0x3d9363, roughness: 0.72 });
        for (let index = 0; index < count; index += 1) {
            const offsetX = (random() - 0.5) * 0.62;
            const offsetZ = (random() - 0.5) * 0.62;
            const trunkHeight = 0.32 + random() * 0.22;
            const trunk = new THREE.Mesh(new THREE.CylinderGeometry(0.03, 0.05, trunkHeight, 7), trunkMaterial);
            trunk.position.y = trunkHeight / 2 + 0.1;

            const leafA = new THREE.Mesh(new THREE.SphereGeometry(0.14 + random() * 0.06, 9, 9), leafMaterial);
            leafA.position.y = trunkHeight + 0.2;
            leafA.scale.set(1.26, 0.82, 1.08);

            const leafB = new THREE.Mesh(new THREE.SphereGeometry(0.12 + random() * 0.05, 9, 9), leafMaterial);
            leafB.position.set(0.08, trunkHeight + 0.25, -0.03);
            leafB.scale.set(1.1, 0.78, 1);

            trunk.castShadow = true;
            leafA.castShadow = true;
            leafB.castShadow = true;
            trunk.receiveShadow = true;
            leafA.receiveShadow = true;
            leafB.receiveShadow = true;

            const palm = new THREE.Group();
            palm.add(trunk, leafA, leafB);
            palm.position.set(x + offsetX, 0, z + offsetZ);
            cityGroup.add(palm);
        }
    }

    function buildCityBuildings(paletteA, paletteB, paletteC) {
        const neutralPalette = [
            "#d4dde6",
            "#bdcad8",
            "#aac0d8",
            "#dfe5eb",
            "#9fb1c8",
            "#8ea9c8",
            paletteA,
            paletteB
        ];

        const xSlots = [-5.8, -4.2, -2.6, -1.2, 1.2, 2.6, 4.2, 5.8];
        const zSlots = [-5.8, -4.2, -2.6, -1.2, 1.2, 2.6, 4.2, 5.8];
        const lots = [];
        for (let xi = 0; xi < xSlots.length; xi += 1) {
            for (let zi = 0; zi < zSlots.length; zi += 1) {
                const x = xSlots[xi];
                const z = zSlots[zi];
                if (Math.abs(x) < 1.7 || Math.abs(z) < 1.7) {
                    continue;
                }
                if (Math.abs(x) > 5.3 && Math.abs(z) > 5.3) {
                    continue;
                }
                if (random() > 0.64) {
                    continue;
                }

                lots.push({
                    x: x + ((random() - 0.5) * 0.36),
                    z: z + ((random() - 0.5) * 0.36),
                    yaw: (random() - 0.5) * 0.34
                });
            }
        }

        for (let index = 0; index < lots.length; index += 1) {
            const lot = lots[index];
            const width = 0.9 + random() * 0.7;
            const depth = 0.9 + random() * 0.7;
            const podiumHeight = 0.25 + random() * 0.22;
            const towerHeight = 2.1 + random() * 4.8;
            const style = random() > 0.34 ? "glass" : "concrete";
            const colorHex = neutralPalette[Math.floor(random() * neutralPalette.length)];
            const lotYaw = typeof lot.yaw === "number" ? lot.yaw : 0;

            const podium = createRoundedExtrudedMesh(
                width * 1.18,
                depth * 1.18,
                podiumHeight,
                new THREE.MeshStandardMaterial({
                    color: new THREE.Color(colorHex).multiplyScalar(0.84),
                    roughness: 0.72,
                    metalness: 0.05
                }),
                {
                    cornerRadius: Math.min(width, depth) * 0.26,
                    bevelSize: 0.09
                }
            );
            podium.position.set(lot.x, podiumHeight / 2 + 0.1, lot.z);
            podium.rotation.y = lotYaw;
            cityGroup.add(podium);

            const variant = index % 5;
            const baseY = podiumHeight + 0.1;
            if (variant === 0) {
                const lowerHeight = towerHeight * (0.5 + random() * 0.12);
                const upperHeight = towerHeight * (0.28 + random() * 0.1);
                const lowerTower = createRoundedExtrudedMesh(
                    width * 0.78,
                    depth * 0.74,
                    lowerHeight,
                    createMiniatureBuildingMaterial(colorHex, style, lowerHeight),
                    {
                        cornerRadius: Math.min(width, depth) * 0.24
                    }
                );
                lowerTower.position.set(lot.x, baseY + lowerHeight / 2, lot.z);
                lowerTower.rotation.y = lotYaw;
                cityGroup.add(lowerTower);

                const upperTower = createRoundedExtrudedMesh(
                    width * 0.58,
                    depth * 0.54,
                    upperHeight,
                    createMiniatureBuildingMaterial(colorHex, "glass", upperHeight),
                    {
                        cornerRadius: Math.min(width, depth) * 0.2
                    }
                );
                upperTower.position.set(lot.x + (random() - 0.5) * 0.08, baseY + lowerHeight + upperHeight / 2 + 0.05, lot.z + (random() - 0.5) * 0.08);
                upperTower.rotation.y = lotYaw;
                cityGroup.add(upperTower);

                const roofCap = new THREE.Mesh(
                    new THREE.SphereGeometry(Math.max(0.16, Math.min(width, depth) * 0.22), 16, 12),
                    new THREE.MeshStandardMaterial({
                        color: new THREE.Color(colorHex).lerp(new THREE.Color("#eef5ff"), 0.46),
                        roughness: 0.46,
                        metalness: 0.06
                    })
                );
                roofCap.scale.set(1.08, 0.5, 1.02);
                roofCap.position.set(lot.x, baseY + lowerHeight + upperHeight + 0.08, lot.z);
                roofCap.rotation.y = lotYaw;
                roofCap.castShadow = true;
                roofCap.receiveShadow = true;
                cityGroup.add(roofCap);
            } else if (variant === 1) {
                const split = (width * 0.42) + 0.08;
                for (let side = -1; side <= 1; side += 2) {
                    const twinHeight = towerHeight * (side > 0 ? 1 : 0.85);
                    const twin = createRoundedExtrudedMesh(
                        width * 0.42,
                        depth * 0.5,
                        twinHeight,
                        createMiniatureBuildingMaterial(colorHex, style, twinHeight),
                        {
                            cornerRadius: Math.min(width, depth) * 0.23
                        }
                    );
                    twin.position.set(lot.x + (side * split * 0.42), baseY + twinHeight / 2, lot.z);
                    twin.rotation.y = lotYaw;
                    cityGroup.add(twin);

                    const twinCrown = new THREE.Mesh(
                        new THREE.SphereGeometry(Math.max(0.12, width * 0.12), 12, 10),
                        new THREE.MeshStandardMaterial({
                            color: new THREE.Color(colorHex).lerp(new THREE.Color("#dbe7f6"), 0.44),
                            roughness: 0.42,
                            metalness: 0.08
                        })
                    );
                    twinCrown.scale.set(1.1, 0.58, 1.1);
                    twinCrown.position.set(lot.x + (side * split * 0.42), baseY + twinHeight + 0.05, lot.z);
                    twinCrown.rotation.y = lotYaw;
                    twinCrown.castShadow = true;
                    twinCrown.receiveShadow = true;
                    cityGroup.add(twinCrown);
                }

                const connector = createRoundedExtrudedMesh(
                    width * 0.44,
                    depth * 0.26,
                    0.2,
                    new THREE.MeshStandardMaterial({
                        color: new THREE.Color("#d7e3f0").lerp(new THREE.Color(colorHex), 0.18),
                        roughness: 0.48,
                        metalness: 0.07
                    }),
                    {
                        cornerRadius: 0.08
                    }
                );
                connector.position.set(lot.x, baseY + Math.min(2.6, towerHeight * 0.42), lot.z);
                connector.rotation.y = lotYaw;
                cityGroup.add(connector);
            } else if (variant === 2) {
                const radius = Math.min(width, depth) * 0.34;
                const roundTower = new THREE.Mesh(
                    new THREE.CylinderGeometry(radius * 0.95, radius * 1.08, towerHeight, 26),
                    createMiniatureBuildingMaterial(colorHex, "glass", towerHeight)
                );
                roundTower.position.set(lot.x, baseY + towerHeight / 2, lot.z);
                roundTower.rotation.y = lotYaw;
                roundTower.castShadow = true;
                roundTower.receiveShadow = true;
                cityGroup.add(roundTower);

                const crownRing = new THREE.Mesh(
                    new THREE.TorusGeometry(radius * 0.92, Math.max(0.04, radius * 0.12), 10, 26),
                    new THREE.MeshStandardMaterial({
                        color: new THREE.Color("#e2edf8").lerp(new THREE.Color(colorHex), 0.18),
                        roughness: 0.36,
                        metalness: 0.1
                    })
                );
                crownRing.rotation.x = Math.PI / 2;
                crownRing.position.set(lot.x, baseY + towerHeight + 0.03, lot.z);
                crownRing.rotation.z = lotYaw;
                crownRing.castShadow = true;
                crownRing.receiveShadow = true;
                cityGroup.add(crownRing);

                const annex = createRoundedExtrudedMesh(
                    width * 0.5,
                    depth * 0.36,
                    towerHeight * 0.26,
                    createMiniatureBuildingMaterial(colorHex, "concrete", towerHeight * 0.26),
                    {
                        cornerRadius: Math.min(width, depth) * 0.16
                    }
                );
                annex.position.set(lot.x + radius * 1.15, baseY + (towerHeight * 0.26) / 2, lot.z - radius * 0.4);
                annex.rotation.y = lotYaw;
                cityGroup.add(annex);
            } else if (variant === 3) {
                const petalCount = 3 + Math.floor(random() * 2);
                const coreHeight = towerHeight * 0.62;
                const coreTower = createRoundedExtrudedMesh(
                    width * 0.46,
                    depth * 0.46,
                    coreHeight,
                    createMiniatureBuildingMaterial(colorHex, style, coreHeight),
                    {
                        cornerRadius: Math.min(width, depth) * 0.22
                    }
                );
                coreTower.position.set(lot.x, baseY + coreHeight / 2, lot.z);
                coreTower.rotation.y = lotYaw;
                cityGroup.add(coreTower);

                for (let petal = 0; petal < petalCount; petal += 1) {
                    const angle = (Math.PI * 2 * petal) / petalCount;
                    const distance = 0.24 + random() * 0.2;
                    const petalHeight = towerHeight * (0.34 + random() * 0.2);
                    const petalTower = createRoundedExtrudedMesh(
                        width * 0.3,
                        depth * 0.28,
                        petalHeight,
                        createMiniatureBuildingMaterial(colorHex, petal % 2 === 0 ? "glass" : "concrete", petalHeight),
                        {
                            cornerRadius: Math.min(width, depth) * 0.18
                        }
                    );
                    petalTower.position.set(
                        lot.x + Math.cos(angle) * distance,
                        baseY + petalHeight / 2,
                        lot.z + Math.sin(angle) * distance
                    );
                    petalTower.rotation.y = lotYaw + (angle * 0.25);
                    cityGroup.add(petalTower);
                }

                const roofBloom = new THREE.Mesh(
                    new THREE.SphereGeometry(Math.max(0.18, Math.min(width, depth) * 0.2), 16, 12),
                    new THREE.MeshStandardMaterial({
                        color: new THREE.Color("#e8f2ff").lerp(new THREE.Color(colorHex), 0.14),
                        roughness: 0.4,
                        metalness: 0.08
                    })
                );
                roofBloom.scale.set(1.15, 0.46, 1.1);
                roofBloom.position.set(lot.x, baseY + coreHeight + 0.08, lot.z);
                roofBloom.rotation.y = lotYaw;
                roofBloom.castShadow = true;
                roofBloom.receiveShadow = true;
                cityGroup.add(roofBloom);
            } else {
                const terraceAHeight = towerHeight * 0.5;
                const terraceBHeight = towerHeight * 0.3;
                const terraceA = createRoundedExtrudedMesh(
                    width * 0.74,
                    depth * 0.52,
                    terraceAHeight,
                    createMiniatureBuildingMaterial(colorHex, "concrete", terraceAHeight),
                    {
                        cornerRadius: Math.min(width, depth) * 0.2
                    }
                );
                terraceA.position.set(lot.x - 0.12, baseY + terraceAHeight / 2, lot.z - 0.1);
                terraceA.rotation.y = lotYaw;
                cityGroup.add(terraceA);

                const terraceB = createRoundedExtrudedMesh(
                    width * 0.56,
                    depth * 0.44,
                    terraceBHeight,
                    createMiniatureBuildingMaterial(colorHex, "glass", terraceBHeight),
                    {
                        cornerRadius: Math.min(width, depth) * 0.18
                    }
                );
                terraceB.position.set(lot.x + 0.18, baseY + terraceAHeight + terraceBHeight / 2 + 0.02, lot.z + 0.12);
                terraceB.rotation.y = lotYaw;
                cityGroup.add(terraceB);
            }

            if (index % 3 === 0 || towerHeight < 3.2 || random() > 0.66) {
                addPalmCluster(lot.x + (random() - 0.5) * 0.55, lot.z + (random() - 0.5) * 0.55, 2 + Math.floor(random() * 2));
            }

            addShrubScatter(cityGroup, lot.x, podiumHeight + 0.14, lot.z, width * 0.96, depth * 0.96, 3 + Math.floor(random() * 4), "#67a676");
        }

        const coastalComplex = createRoundedExtrudedMesh(
            4.0,
            2.3,
            0.75,
            new THREE.MeshStandardMaterial({
                color: new THREE.Color("#c9d4de").lerp(new THREE.Color(paletteC), 0.12),
                roughness: 0.68,
                metalness: 0.06
            }),
            {
                cornerRadius: 0.34,
                bevelSize: 0.12
            }
        );
        coastalComplex.position.set(1.4, 0.48, 5.0);
        cityGroup.add(coastalComplex);

        const coastalRoof = createRoundedExtrudedMesh(
            1.6,
            1.0,
            0.24,
            new THREE.MeshStandardMaterial({ color: 0x8fa3b8, roughness: 0.5, metalness: 0.1 }),
            {
                cornerRadius: 0.12,
                bevelSize: 0.05
            }
        );
        coastalRoof.position.set(1.4, 0.95, 5.1);
        cityGroup.add(coastalRoof);

        addShrubScatter(cityGroup, 1.4, 0.98, 5.1, 1.4, 0.8, 8, "#4f9568");
        addPalmCluster(3.3, 4.9, 6);
        addPalmCluster(-0.5, 4.8, 5);
    }

    function applyCartoonPassToCity() {
        const candidateMeshes = [];
        cityGroup.traverse((node) => {
            if (!node || !node.isMesh || !node.geometry || node.userData.isCartoonOutline || node.userData.hasCartoonOutline) {
                return;
            }

            candidateMeshes.push(node);
        });

        const maxOutlined = Math.min(160, candidateMeshes.length);
        for (let index = 0; index < maxOutlined; index += 1) {
            const node = candidateMeshes[index];
            if (index % 2 !== 0 && random() > 0.72) {
                continue;
            }

            addCartoonOutline(node, 0.024, 0x32475f, 0.14, 1);
        }
    }

    function buildMicroVehicles() {
        const laneSpecs = [
            { axis: "x", fixed: -0.36, min: -6.4, max: 6.4, heading: 1, tint: 0xd3473b },
            { axis: "x", fixed: 0.36, min: -6.4, max: 6.4, heading: -1, tint: 0x4e83d9 },
            { axis: "z", fixed: -0.36, min: -6.4, max: 6.4, heading: 1, tint: 0xf4c44f },
            { axis: "z", fixed: 0.36, min: -6.4, max: 6.4, heading: -1, tint: 0x9ad16a }
        ];

        for (let laneIndex = 0; laneIndex < laneSpecs.length; laneIndex += 1) {
            const lane = laneSpecs[laneIndex];
            for (let carIndex = 0; carIndex < 6; carIndex += 1) {
                const carBody = new THREE.Mesh(
                    new THREE.BoxGeometry(0.34, 0.14, 0.18),
                    new THREE.MeshStandardMaterial({
                        color: new THREE.Color(lane.tint).offsetHSL((random() - 0.5) * 0.12, 0.04, (random() - 0.5) * 0.06),
                        roughness: 0.48,
                        metalness: 0.26
                    })
                );
                const cabin = new THREE.Mesh(
                    new THREE.BoxGeometry(0.17, 0.09, 0.16),
                    new THREE.MeshStandardMaterial({ color: 0xdbe8f9, roughness: 0.18, metalness: 0.08 })
                );
                cabin.position.y = 0.1;

                const car = new THREE.Group();
                car.add(carBody, cabin);
                car.position.y = 0.2;
                car.castShadow = true;
                car.receiveShadow = true;

                const initial = lane.min + ((lane.max - lane.min) * random());
                if (lane.axis === "x") {
                    car.position.set(initial, 0.2, lane.fixed);
                    car.rotation.y = Math.PI / 2;
                } else {
                    car.position.set(lane.fixed, 0.2, initial);
                    car.rotation.y = 0;
                }

                cityGroup.add(car);
                vehicleAgents.push({
                    mesh: car,
                    axis: lane.axis,
                    min: lane.min,
                    max: lane.max,
                    fixed: lane.fixed,
                    speed: (0.58 + random() * 0.9) * lane.heading
                });
            }
        }
    }

    function chooseLandmarkType(name, index) {
        const lower = name.toLowerCase();
        if (lower.includes("queen emma")) {
            return "palace";
        }
        if (lower.includes("diamond head") || lower.includes("pali") || lower.includes("crater")) {
            return "crater";
        }
        if (lower.includes("tower") || lower.includes("needle") || lower.includes("spire") || lower.includes("skytree")) {
            return "tower";
        }
        if (lower.includes("palace") || lower.includes("capitol") || lower.includes("hall") || lower.includes("judiciary")) {
            return "dome";
        }
        if (lower.includes("bridge") || lower.includes("arch")) {
            return "arch";
        }
        if (lower.includes("statue") || lower.includes("monument") || lower.includes("kamehameha")) {
            return "statue";
        }
        if (lower.includes("lookout") || lower.includes("mount") || lower.includes("head") || lower.includes("pali")) {
            return "mountain";
        }

        const cycle = ["tower", "dome", "arch", "statue", "crater"];
        return cycle[index % cycle.length];
    }

    function makeLandmarkMaterial(colorHex) {
        const texture = createFacadeTexture(colorHex);
        texture.repeat.set(1.2, 1.4);
        return new THREE.MeshToonMaterial({
            color: new THREE.Color(colorHex).lerp(new THREE.Color("#f6fbff"), 0.05),
            map: texture,
            gradientMap: toonGradientMap
        });
    }

    function createTowerLandmark(colorHex, seed) {
        const group = new THREE.Group();
        const bodyHeight = 3.8 + (seed % 4) * 0.7;

        const base = new THREE.Mesh(
            new THREE.CylinderGeometry(0.46, 0.62, 0.55, 8),
            makeLandmarkMaterial(colorHex)
        );
        base.position.y = 0.28;

        const shaft = new THREE.Mesh(
            new THREE.CylinderGeometry(0.12, 0.22, bodyHeight, 10),
            makeLandmarkMaterial("#d8e3ef")
        );
        shaft.position.y = bodyHeight / 2 + 0.55;

        const tip = new THREE.Mesh(
            new THREE.ConeGeometry(0.24, 0.9, 10),
            makeLandmarkMaterial(colorHex)
        );
        tip.position.y = bodyHeight + 1.08;

        base.castShadow = true;
        shaft.castShadow = true;
        tip.castShadow = true;
        base.receiveShadow = true;
        shaft.receiveShadow = true;
        tip.receiveShadow = true;
        group.add(base, shaft, tip);
        return group;
    }

    function createDomeLandmark(colorHex) {
        const group = new THREE.Group();
        const podium = createRoundedExtrudedMesh(
            2.0,
            2.0,
            0.45,
            makeLandmarkMaterial("#dbe3ef"),
            {
                cornerRadius: 0.32,
                bevelSize: 0.1
            }
        );
        podium.position.y = 0.23;

        const body = createRoundedExtrudedMesh(
            1.55,
            1.55,
            1.12,
            makeLandmarkMaterial(colorHex),
            {
                cornerRadius: 0.26
            }
        );
        body.position.y = 1.0;

        const dome = new THREE.Mesh(
            new THREE.SphereGeometry(0.65, 16, 12, 0, Math.PI * 2, 0, Math.PI / 2),
            makeLandmarkMaterial("#e6edf5")
        );
        dome.position.y = 1.56;

        podium.castShadow = true;
        body.castShadow = true;
        dome.castShadow = true;
        podium.receiveShadow = true;
        body.receiveShadow = true;
        dome.receiveShadow = true;
        group.add(podium, body, dome);
        return group;
    }

    function createPalaceLandmark() {
        const group = new THREE.Group();
        const pink = "#d89aa3";
        const pinkSoft = "#e3b0b8";
        const roof = "#92a4b3";

        const mainBlock = createRoundedExtrudedMesh(
            2.05,
            1.2,
            0.78,
            makeLandmarkMaterial(pink),
            {
                cornerRadius: 0.22
            }
        );
        mainBlock.position.y = 0.5;

        const wingA = createRoundedExtrudedMesh(
            0.92,
            0.74,
            0.62,
            makeLandmarkMaterial(pinkSoft),
            {
                cornerRadius: 0.14
            }
        );
        wingA.position.set(-1.28, 0.42, 0.05);

        const wingB = createRoundedExtrudedMesh(
            0.92,
            0.74,
            0.62,
            makeLandmarkMaterial(pinkSoft),
            {
                cornerRadius: 0.14
            }
        );
        wingB.position.set(1.28, 0.42, 0.05);

        const roofMain = new THREE.Mesh(
            new THREE.ConeGeometry(0.86, 0.46, 4),
            makeLandmarkMaterial(roof)
        );
        roofMain.rotation.y = Math.PI * 0.25;
        roofMain.position.y = 1.14;

        const tower = new THREE.Mesh(
            new THREE.CylinderGeometry(0.18, 0.2, 0.58, 8),
            makeLandmarkMaterial("#f1d3d8")
        );
        tower.position.set(0, 1.45, 0);

        const towerCap = new THREE.Mesh(
            new THREE.ConeGeometry(0.22, 0.34, 8),
            makeLandmarkMaterial(roof)
        );
        towerCap.position.set(0, 1.88, 0);

        const frontArcade = createRoundedExtrudedMesh(
            1.6,
            0.42,
            0.26,
            makeLandmarkMaterial("#f0c5cc"),
            {
                cornerRadius: 0.08,
                bevelSize: 0.04
            }
        );
        frontArcade.position.set(0, 0.31, 0.68);

        for (const part of [mainBlock, wingA, wingB, roofMain, tower, towerCap, frontArcade]) {
            part.castShadow = true;
            part.receiveShadow = true;
            group.add(part);
        }

        return group;
    }

    function createArchLandmark(colorHex) {
        const group = new THREE.Group();
        const supports = createRoundedExtrudedMesh(
            2.8,
            0.68,
            0.95,
            makeLandmarkMaterial(colorHex),
            {
                cornerRadius: 0.16
            }
        );
        supports.position.y = 0.48;

        const arch = new THREE.Mesh(
            new THREE.TorusGeometry(1.22, 0.16, 10, 24, Math.PI),
            makeLandmarkMaterial("#e2ebf5")
        );
        arch.rotation.z = Math.PI;
        arch.position.y = 1.42;

        supports.castShadow = true;
        arch.castShadow = true;
        supports.receiveShadow = true;
        arch.receiveShadow = true;
        group.add(supports, arch);
        return group;
    }

    function createStatueLandmark(colorHex) {
        const group = new THREE.Group();
        const pedestal = createRoundedExtrudedMesh(
            1.2,
            1.2,
            0.84,
            makeLandmarkMaterial(colorHex),
            {
                cornerRadius: 0.2
            }
        );
        pedestal.position.y = 0.42;

        const statueBody = new THREE.Mesh(
            new THREE.CylinderGeometry(0.18, 0.24, 1.0, 8),
            makeLandmarkMaterial("#cad8e8")
        );
        statueBody.position.y = 1.22;

        const head = new THREE.Mesh(
            new THREE.SphereGeometry(0.16, 10, 10),
            makeLandmarkMaterial("#cad8e8")
        );
        head.position.y = 1.78;

        pedestal.castShadow = true;
        statueBody.castShadow = true;
        head.castShadow = true;
        pedestal.receiveShadow = true;
        statueBody.receiveShadow = true;
        head.receiveShadow = true;
        group.add(pedestal, statueBody, head);
        return group;
    }

    function createCraterLandmark() {
        const group = new THREE.Group();
        const mountain = new THREE.Mesh(
            new THREE.ConeGeometry(1.75, 2.05, 28),
            makeLandmarkMaterial("#739266")
        );
        mountain.position.y = 1.04;

        const craterRing = new THREE.Mesh(
            new THREE.TorusGeometry(0.78, 0.18, 12, 28),
            makeLandmarkMaterial("#95b087")
        );
        craterRing.position.y = 2.05;
        craterRing.rotation.x = Math.PI / 2;

        const craterBase = new THREE.Mesh(
            new THREE.CylinderGeometry(0.66, 0.74, 0.2, 18),
            makeLandmarkMaterial("#5f7f56")
        );
        craterBase.position.y = 1.85;

        mountain.castShadow = true;
        craterRing.castShadow = true;
        craterBase.castShadow = true;
        mountain.receiveShadow = true;
        craterRing.receiveShadow = true;
        craterBase.receiveShadow = true;
        group.add(mountain, craterRing, craterBase);
        return group;
    }

    function createMountainLandmark(colorHex) {
        const group = new THREE.Group();
        const mountain = new THREE.Mesh(
            new THREE.ConeGeometry(1.2, 2.2, 22),
            makeLandmarkMaterial(colorHex)
        );
        mountain.position.y = 1.1;
        mountain.scale.set(1, 1, 0.95);

        const snowCap = new THREE.Mesh(
            new THREE.ConeGeometry(0.56, 0.52, 18),
            makeLandmarkMaterial("#f3f8ff")
        );
        snowCap.position.y = 2.05;

        const ridge = new THREE.Mesh(
            new THREE.SphereGeometry(0.92, 16, 12),
            makeLandmarkMaterial("#" + new THREE.Color(colorHex).multiplyScalar(0.84).getHexString())
        );
        ridge.position.set(-0.18, 0.94, 0.1);
        ridge.scale.set(1.0, 0.55, 0.82);

        mountain.castShadow = true;
        snowCap.castShadow = true;
        ridge.castShadow = true;
        mountain.receiveShadow = true;
        snowCap.receiveShadow = true;
        ridge.receiveShadow = true;
        group.add(mountain, snowCap, ridge);
        return group;
    }

    function buildPoiImageMap(pointOfInterestImages) {
        const map = new Map();
        if (!Array.isArray(pointOfInterestImages)) {
            return map;
        }

        for (let index = 0; index < pointOfInterestImages.length; index += 1) {
            const item = pointOfInterestImages[index];
            if (!item || !item.name || !item.dataUri) {
                continue;
            }

            const key = String(item.name).toLowerCase();
            if (!map.has(key)) {
                map.set(key, []);
            }

            const views = map.get(key);
            views.push(String(item.dataUri));
        }

        return map;
    }

    function buildPoiMeshMap(pointOfInterestMeshes) {
        const map = new Map();
        if (!Array.isArray(pointOfInterestMeshes)) {
            return map;
        }

        for (let index = 0; index < pointOfInterestMeshes.length; index += 1) {
            const item = pointOfInterestMeshes[index];
            if (!item || !item.name || !item.localWebUrl) {
                continue;
            }

            const key = String(item.name).toLowerCase();
            if (!map.has(key)) {
                map.set(key, []);
            }

            map.get(key).push(item);
        }

        return map;
    }

    function normalizeRelativePath(value) {
        if (!value || typeof value !== "string") {
            return "";
        }

        return value
            .trim()
            .replace(/\\/g, "/")
            .replace(/^\/+/, "")
            .toLowerCase();
    }

    function parseMeshyViewerSettings(rawSettings) {
        const settings = rawSettings || {};
        const activeModelRelativePath = normalizeRelativePath(settings.activeModelRelativePath || "");
        const minutes = Number(settings.rotationMinutes);
        const rotationMinutes = Number.isFinite(minutes)
            ? Math.max(0, Math.min(1440, minutes))
            : 0;

        return {
            activeModelRelativePath: activeModelRelativePath,
            rotationMinutes: rotationMinutes
        };
    }

    function buildWallpaperModelEntries(pointOfInterestMeshes) {
        const entries = [];
        const seenKeys = new Set();

        const addEntry = (entry) => {
            if (!entry || !entry.url) {
                return;
            }

            const relativePath = normalizeRelativePath(entry.relativePath || "");
            const dedupeKey = relativePath || String(entry.url).trim().toLowerCase();
            if (!dedupeKey || seenKeys.has(dedupeKey)) {
                return;
            }

            seenKeys.add(dedupeKey);
            entries.push({
                label: entry.label || "Model",
                name: entry.name || entry.label || "Model",
                url: String(entry.url).trim(),
                relativePath: relativePath,
                source: entry.source || "unknown"
            });
        };

        if (Array.isArray(pointOfInterestMeshes)) {
            for (let index = 0; index < pointOfInterestMeshes.length; index += 1) {
                const item = pointOfInterestMeshes[index];
                if (!item || !item.localWebUrl) {
                    continue;
                }

                const fileName = String(item.localRelativePath || "")
                    .split("/")
                    .pop() || "model.glb";
                const itemName = String(item.name || "Meshy Model").trim();
                addEntry({
                    label: `${itemName} | ${fileName}`,
                    name: itemName,
                    url: item.localWebUrl,
                    relativePath: item.localRelativePath || "",
                    source: "meshy-cache"
                });
            }
        }

        for (let index = 0; index < DEFAULT_CITY_MODEL_CANDIDATES.length; index += 1) {
            const candidate = DEFAULT_CITY_MODEL_CANDIDATES[index];
            addEntry({
                label: candidate.label,
                name: candidate.label,
                url: candidate.url,
                relativePath: candidate.relativePath || "",
                source: candidate.source || "default"
            });
        }

        return entries;
    }

    function findWallpaperModelEntry(entries, relativePath) {
        const key = normalizeRelativePath(relativePath || "");
        if (!key || !Array.isArray(entries) || entries.length === 0) {
            return null;
        }

        for (let index = 0; index < entries.length; index += 1) {
            if (normalizeRelativePath(entries[index].relativePath) === key) {
                return entries[index];
            }
        }

        return null;
    }

    function getMeshLoader() {
        if (!window.THREE || !THREE.GLTFLoader) {
            return null;
        }

        if (!window.__meshyGltfLoader) {
            window.__meshyGltfLoader = new THREE.GLTFLoader();
        }

        return window.__meshyGltfLoader;
    }

    function prepareModelForScene(modelRoot) {
        modelRoot.traverse((node) => {
            if (!node || !node.isMesh) {
                return;
            }

            node.castShadow = true;
            node.receiveShadow = true;
            if (node.material && !Array.isArray(node.material)) {
                if ("flatShading" in node.material) {
                    node.material.flatShading = false;
                }
                node.material.needsUpdate = true;
            }
        });
    }

    function normalizeModelForDiorama(modelRoot, targetFootprint) {
        const bounds = new THREE.Box3().setFromObject(modelRoot);
        if (bounds.isEmpty()) {
            return false;
        }

        const size = new THREE.Vector3();
        const center = new THREE.Vector3();
        bounds.getSize(size);
        bounds.getCenter(center);
        modelRoot.position.sub(center);

        const footprint = Math.max(size.x, size.z, 0.01);
        const scale = (targetFootprint || 14.8) / footprint;
        modelRoot.scale.setScalar(scale);

        const normalizedBounds = new THREE.Box3().setFromObject(modelRoot);
        modelRoot.position.y -= normalizedBounds.min.y;
        modelRoot.position.y += 0.03;
        return true;
    }

    function ensureDefaultCitySceneLoaded() {
        if (defaultCityModelLoaded &&
            (defaultCitySceneSource === "default-glb" || defaultCitySceneSource === "meshy-default")) {
            applyDefaultGlbOrientation(activeGlbOrientation);
            defaultCityGroup.visible = true;
            postHostDebug(`Default city model already active from source '${defaultCitySceneSource}'.`);
            return Promise.resolve(true);
        }

        if (defaultCityModelFailed) {
            return Promise.resolve(false);
        }

        if (defaultCityLoadPromise) {
            return defaultCityLoadPromise;
        }

        const loader = getMeshLoader();
        if (!loader) {
            defaultCityModelFailed = true;
            postHostDebug("Default city model failed: GLTFLoader unavailable.");
            return Promise.resolve(false);
        }

        const probeModelUrl = (candidate) => {
            if (!window.fetch || !candidate || !candidate.url) {
                return;
            }

            fetch(candidate.url, { method: "HEAD", cache: "no-store" })
                .then((response) => {
                    const contentType = response.headers.get("content-type") || "unknown";
                    const contentLength = response.headers.get("content-length") || "unknown";
                    postHostDebug(
                        `Default city model probe (${candidate.label}): status=${response.status} ` +
                        `ok=${response.ok} type=${contentType} length=${contentLength}`);
                })
                .catch((error) => {
                    postHostDebug(
                        `Default city model probe failed (${candidate.label}): ${String(error)}`);
                });
        };

        const tryLoadCandidateAt = (index, resolve) => {
            if (index >= DEFAULT_CITY_MODEL_CANDIDATES.length) {
                defaultCityModelFailed = true;
                postHostDebug("Default city model failed for all candidates. Rendering procedural fallback.");
                resolve(false);
                return;
            }

            const candidate = DEFAULT_CITY_MODEL_CANDIDATES[index];
            probeModelUrl(candidate);
            postHostDebug(
                `Default city model load requested [${index + 1}/${DEFAULT_CITY_MODEL_CANDIDATES.length}]: ` +
                `${candidate.label} | ${candidate.url}`);
            let lastProgressBucket = -1;
            loader.load(candidate.url, (gltf) => {
                const modelRoot = gltf && (gltf.scene || (Array.isArray(gltf.scenes) ? gltf.scenes[0] : null));
                if (!modelRoot) {
                    postHostDebug(`Default city model failed (${candidate.label}): no scene root.`);
                    tryLoadCandidateAt(index + 1, resolve);
                    return;
                }

                prepareModelForScene(modelRoot);
                if (!normalizeModelForDiorama(modelRoot, 14.8)) {
                    postHostDebug(`Default city model failed (${candidate.label}): empty bounds.`);
                    tryLoadCandidateAt(index + 1, resolve);
                    return;
                }

                clearGroup(defaultCityGroup);
                defaultCityGroup.add(modelRoot);
                applyDefaultGlbOrientation(activeGlbOrientation);
                defaultCityGroup.visible = true;
                defaultCityModelLoaded = true;
                defaultCityModelFailed = false;
                defaultCitySceneSource = candidate.source;
                activeMeshyCityModelUrl = candidate.source === "meshy-default" ? candidate.url : "";
                wallpaperModelRelativePath = normalizeRelativePath(candidate.relativePath || "");
                postHostDebug(`Default city model loaded and applied: ${candidate.label} (source=${candidate.source}).`);
                resolve(true);
            }, (progressEvent) => {
                if (!progressEvent || !progressEvent.total || progressEvent.total <= 0) {
                    return;
                }

                const percent = Math.max(0, Math.min(100, Math.round((progressEvent.loaded / progressEvent.total) * 100)));
                const bucket = Math.floor(percent / 25);
                if (bucket === lastProgressBucket || percent < 1) {
                    return;
                }

                lastProgressBucket = bucket;
                postHostDebug(`Default city model loading (${candidate.label}): ${percent}%`);
            }, (error) => {
                postHostDebug(`Default city model load failed (${candidate.label}): ${String(error)}`);
                tryLoadCandidateAt(index + 1, resolve);
            });
        };

        defaultCityLoadPromise = new Promise((resolve) => {
            tryLoadCandidateAt(0, resolve);
        }).finally(() => {
            defaultCityLoadPromise = null;
        });

        return defaultCityLoadPromise;
    }

    function resolveMeshyModelEntry(payload, pointsOfInterest, pointOfInterestMeshes) {
        if (!Array.isArray(pointOfInterestMeshes) || pointOfInterestMeshes.length === 0) {
            return null;
        }

        const validMeshes = pointOfInterestMeshes.filter((entry) =>
            entry && entry.localWebUrl && entry.name);
        if (validMeshes.length === 0) {
            return null;
        }

        const poiSet = new Set(
            (Array.isArray(pointsOfInterest) ? pointsOfInterest : [])
                .map((name) => String(name || "").toLowerCase().trim())
                .filter(Boolean)
        );

        const cityCandidates = [
            payload && payload.weather ? payload.weather.locationName : "",
            payload && payload.coordinates ? payload.coordinates.displayName : "",
            payload ? payload.locationQuery : ""
        ]
            .map((text) => String(text || "").toLowerCase().split(",")[0].trim())
            .filter(Boolean);

        for (let index = 0; index < validMeshes.length; index += 1) {
            const meshName = String(validMeshes[index].name || "").toLowerCase().trim();
            if (!meshName) {
                continue;
            }

            if (cityCandidates.some((candidate) => meshName.includes(candidate) || candidate.includes(meshName))) {
                return validMeshes[index];
            }
        }

        const cityMeshFallback = validMeshes.find((entry) => {
            const meshName = String(entry.name || "").toLowerCase().trim();
            return meshName && !poiSet.has(meshName);
        });
        if (cityMeshFallback) {
            return cityMeshFallback;
        }

        return validMeshes[0];
    }

    function ensureMeshyCitySceneLoaded(meshEntry, expectedSeed, sourceTag, modelRelativePath) {
        const loader = getMeshLoader();
        const modelUrl = meshEntry && (meshEntry.localWebUrl || meshEntry.url)
            ? String(meshEntry.localWebUrl || meshEntry.url).trim()
            : "";
        const resolvedSourceTag = sourceTag || "meshy-city";
        const normalizedRelativePath = normalizeRelativePath(
            modelRelativePath ||
            (meshEntry ? meshEntry.localRelativePath : "") ||
            (meshEntry ? meshEntry.relativePath : ""));
        const modelLabel = meshEntry && (meshEntry.displayLabel || meshEntry.label || meshEntry.name)
            ? String(meshEntry.displayLabel || meshEntry.label || meshEntry.name)
            : modelUrl;

        if (!loader || !meshEntry || !modelUrl) {
            postHostDebug("Meshy city model load skipped: loader unavailable or mesh entry missing URL.");
            return Promise.resolve(false);
        }

        if (defaultCitySceneSource === resolvedSourceTag &&
            activeMeshyCityModelUrl === modelUrl &&
            defaultCityGroup.children.length > 0) {
            applyDefaultGlbOrientation(activeGlbOrientation);
            defaultCityGroup.visible = true;
            return Promise.resolve(true);
        }

        if (meshyCityLoadPromise) {
            return meshyCityLoadPromise;
        }

        postHostDebug(`Meshy city model load requested: ${modelUrl} (${modelLabel})`);
        meshyCityLoadPromise = new Promise((resolve) => {
            let lastProgressBucket = -1;
            loader.load(modelUrl, (gltf) => {
                if (!activePayload || !activePayload.scene || activePayload.scene.seed !== expectedSeed) {
                    postHostDebug("Meshy city model load ignored: stale payload seed.");
                    resolve(false);
                    return;
                }

                const modelRoot = gltf && (gltf.scene || (Array.isArray(gltf.scenes) ? gltf.scenes[0] : null));
                if (!modelRoot) {
                    postHostDebug(`Meshy city model failed: no scene root (${modelUrl}).`);
                    resolve(false);
                    return;
                }

                prepareModelForScene(modelRoot);
                if (!normalizeModelForDiorama(modelRoot, 14.8)) {
                    postHostDebug(`Meshy city model failed: empty bounds (${modelUrl}).`);
                    resolve(false);
                    return;
                }

                clearGroup(defaultCityGroup);
                defaultCityGroup.add(modelRoot);
                applyDefaultGlbOrientation(activeGlbOrientation);
                defaultCityGroup.visible = true;
                defaultCitySceneSource = resolvedSourceTag;
                activeMeshyCityModelUrl = modelUrl;
                wallpaperModelRelativePath = normalizedRelativePath;
                defaultCityModelLoaded = false;
                defaultCityModelFailed = false;
                postHostDebug(`Meshy city model loaded and applied from ${modelUrl}.`);
                resolve(true);
            }, (progressEvent) => {
                if (!progressEvent || !progressEvent.total || progressEvent.total <= 0) {
                    return;
                }

                const percent = Math.max(0, Math.min(100, Math.round((progressEvent.loaded / progressEvent.total) * 100)));
                const bucket = Math.floor(percent / 25);
                if (bucket === lastProgressBucket || percent < 1) {
                    return;
                }

                lastProgressBucket = bucket;
                postHostDebug(`Meshy city model loading: ${percent}% (${modelUrl})`);
            }, (error) => {
                postHostDebug(`Meshy city model load failed (${modelUrl}): ${String(error)}`);
                resolve(false);
            });
        }).finally(() => {
            meshyCityLoadPromise = null;
        });

        return meshyCityLoadPromise;
    }

    function applyMeshyModelToLandmark(landmark, meshEntry, expectedSeed, poiName) {
        const loader = getMeshLoader();
        if (!loader || !meshEntry || !meshEntry.localWebUrl) {
            return;
        }

        const modelUrl = String(meshEntry.localWebUrl);
        loader.load(modelUrl, (gltf) => {
            if (!activePayload || !activePayload.scene || activePayload.scene.seed !== expectedSeed) {
                return;
            }

            const modelRoot = gltf && (gltf.scene || (Array.isArray(gltf.scenes) ? gltf.scenes[0] : null));
            if (!modelRoot) {
                postHostDebug(`Meshy model for '${poiName}' has no scene root.`);
                return;
            }

            prepareModelForScene(modelRoot);

            const bounds = new THREE.Box3().setFromObject(modelRoot);
            if (bounds.isEmpty()) {
                postHostDebug(`Meshy model bounds empty for '${poiName}'.`);
                return;
            }

            const size = new THREE.Vector3();
            const center = new THREE.Vector3();
            bounds.getSize(size);
            bounds.getCenter(center);
            modelRoot.position.sub(center);

            const maxSize = Math.max(size.x, size.y, size.z, 0.01);
            const targetSize = 1.95;
            const uniformScale = targetSize / maxSize;
            modelRoot.scale.setScalar(uniformScale);
            modelRoot.position.y += Math.max(0.24, (size.y * uniformScale) * 0.52);

            clearGroup(landmark);
            landmark.add(modelRoot);
            addCartoonOutline(landmark, 0.025, 0x243549, 0.12, 12);
            postHostDebug(`Meshy model loaded for '${poiName}' from ${modelUrl}`);
        }, undefined, (error) => {
            postHostDebug(`Meshy model load failed for '${poiName}': ${String(error)}`);
        });
    }

    function createCartoonStyleFromSeed(seed, fallbackHex) {
        const seeded = createSeededRandom(seed >>> 0);
        const base = new THREE.Color(fallbackHex || "#f2c879");
        base.offsetHSL((seeded() - 0.5) * 0.08, 0.06, 0.03);
        const accent = base.clone().offsetHSL(0.03 + seeded() * 0.04, 0.1, 0.08);
        const shadow = base.clone().multiplyScalar(0.68);
        return {
            baseColor: base,
            accentColor: accent,
            shadowColor: shadow
        };
    }

    function blendCartoonStyles(styles, fallbackStyle) {
        if (!Array.isArray(styles) || styles.length === 0) {
            return fallbackStyle;
        }

        const base = new THREE.Color(0x000000);
        const accent = new THREE.Color(0x000000);
        const shadow = new THREE.Color(0x000000);

        for (let index = 0; index < styles.length; index += 1) {
            base.add(styles[index].baseColor);
            accent.add(styles[index].accentColor);
            shadow.add(styles[index].shadowColor);
        }

        base.multiplyScalar(1 / styles.length);
        accent.multiplyScalar(1 / styles.length);
        shadow.multiplyScalar(1 / styles.length);

        return {
            baseColor: base.lerp(fallbackStyle.baseColor, 0.2),
            accentColor: accent.lerp(fallbackStyle.accentColor, 0.15),
            shadowColor: shadow.lerp(fallbackStyle.shadowColor, 0.25)
        };
    }

    function extractCartoonStyleFromImage(dataUri, fallbackStyle, onReady) {
        if (!dataUri) {
            onReady(null);
            return;
        }

        const image = new Image();
        image.onload = () => {
            try {
                const canvas = document.createElement("canvas");
                canvas.width = 28;
                canvas.height = 28;
                const context = canvas.getContext("2d", { willReadFrequently: true });
                context.drawImage(image, 0, 0, 28, 28);

                const pixels = context.getImageData(0, 0, 28, 28).data;
                let r = 0;
                let g = 0;
                let b = 0;
                let count = 0;

                for (let index = 0; index < pixels.length; index += 16) {
                    const alpha = pixels[index + 3];
                    if (alpha < 8) {
                        continue;
                    }

                    r += pixels[index];
                    g += pixels[index + 1];
                    b += pixels[index + 2];
                    count += 1;
                }

                if (count < 1) {
                    onReady(null);
                    return;
                }

                const base = new THREE.Color(r / (255 * count), g / (255 * count), b / (255 * count));
                const accent = base.clone().offsetHSL(0.06, 0.3, 0.14);
                const shadow = base.clone().multiplyScalar(0.5);
                onReady({
                    baseColor: base,
                    accentColor: accent,
                    shadowColor: shadow
                });
            } catch (error) {
                onReady(null);
            }
        };
        image.onerror = () => onReady(null);
        image.src = dataUri;
    }

    function applyCartoonStyleToLandmark(landmark, style, seed) {
        if (!landmark || !style) {
            return;
        }

        let meshCount = 0;
        landmark.traverse((node) => {
            if (!node || !node.isMesh || !node.material) {
                return;
            }

            const sourceMaterials = Array.isArray(node.material) ? node.material : [node.material];
            const materials = [];
            for (let materialIndex = 0; materialIndex < sourceMaterials.length; materialIndex += 1) {
                const material = sourceMaterials[materialIndex];
                if (!material || typeof material.color === "undefined") {
                    materials.push(material);
                    continue;
                }

                const tintMix = ((meshCount + materialIndex) % 4) / 3;
                const tint = style.baseColor.clone().lerp(style.accentColor, tintMix * 0.52);
                tint.offsetHSL((((seed + meshCount + materialIndex) % 3) - 1) * 0.006, 0.02, 0.015);

                const toonMaterial = new THREE.MeshToonMaterial({
                    color: tint,
                    map: material.map || null,
                    transparent: !!material.transparent,
                    opacity: typeof material.opacity === "number" ? material.opacity : 1,
                    gradientMap: toonGradientMap
                });
                toonMaterial.needsUpdate = true;
                materials.push(toonMaterial);
            }

            node.material = Array.isArray(node.material) ? materials : (materials[0] || node.material);

            meshCount += 1;
        });

        let accentGroup = landmark.userData.cartoonAccentGroup;
        if (!accentGroup) {
            accentGroup = new THREE.Group();
            landmark.userData.cartoonAccentGroup = accentGroup;
            landmark.add(accentGroup);
        } else {
            clearGroup(accentGroup);
        }

        const accentCount = 3 + (seed % 4);
        for (let accentIndex = 0; accentIndex < accentCount; accentIndex += 1) {
            const angle = (Math.PI * 2 * accentIndex) / accentCount;
            const radius = 0.55 + ((accentIndex % 2) * 0.16);
            const accentMesh = new THREE.Mesh(
                new THREE.SphereGeometry(0.09 + (accentIndex % 2) * 0.03, 8, 8),
                new THREE.MeshStandardMaterial({
                    color: style.accentColor.clone().offsetHSL((accentIndex % 3) * 0.02, 0.02, 0.04),
                    roughness: 0.55,
                    metalness: 0.03
                })
            );
            accentMesh.position.set(Math.cos(angle) * radius, 0.22 + (accentIndex % 3) * 0.12, Math.sin(angle) * radius);
            accentMesh.castShadow = true;
            accentMesh.receiveShadow = true;
            accentGroup.add(accentMesh);
        }
    }

    function applyImageDrivenCartoonStyle(landmark, imageDataUris, seed, fallbackHex) {
        const fallbackStyle = createCartoonStyleFromSeed(seed, fallbackHex);
        applyCartoonStyleToLandmark(landmark, fallbackStyle, seed);

        if (!Array.isArray(imageDataUris) || imageDataUris.length === 0) {
            return;
        }

        const sampleUris = imageDataUris.slice(0, 3);
        const styles = [];
        let pending = sampleUris.length;

        for (let index = 0; index < sampleUris.length; index += 1) {
            extractCartoonStyleFromImage(sampleUris[index], fallbackStyle, (styleFromImage) => {
                if (styleFromImage) {
                    styles.push(styleFromImage);
                }

                pending -= 1;
                if (pending <= 0 && styles.length > 0) {
                    const blendedStyle = blendCartoonStyles(styles, fallbackStyle);
                    applyCartoonStyleToLandmark(landmark, blendedStyle, seed);
                }
            });
        }
    }

    function buildPoiLandmarks(pointsOfInterest, pointOfInterestImages, pointOfInterestMeshes, paletteC) {
        if (!Array.isArray(pointsOfInterest) || pointsOfInterest.length === 0) {
            return;
        }

        const imageMap = buildPoiImageMap(pointOfInterestImages);
        const meshMap = buildPoiMeshMap(pointOfInterestMeshes);
        const expectedSeed = activePayload && activePayload.scene ? activePayload.scene.seed : 0;

        const anchorPositions = [
            [-4.8, -4.8],
            [4.8, -4.8],
            [-4.8, 4.8],
            [4.8, 4.8],
            [0.0, -5.8],
            [5.8, 0.0],
            [-5.8, 0.0],
            [0.0, 5.8]
        ];

        const landmarks = pointsOfInterest.slice(0, anchorPositions.length);
        for (let index = 0; index < landmarks.length; index += 1) {
            const name = landmarks[index];
            const landmarkType = chooseLandmarkType(name, index);
            const seed = hashString(name);
            const color = new THREE.Color(asHexColor(paletteC, "#f2c879"));
            color.offsetHSL(((index % 3) - 1) * 0.02, 0.03, 0.02);

            let landmark;
            if (landmarkType === "tower") {
                landmark = createTowerLandmark("#" + color.getHexString(), seed);
            } else if (landmarkType === "palace") {
                landmark = createPalaceLandmark();
            } else if (landmarkType === "dome") {
                landmark = createDomeLandmark("#" + color.getHexString());
            } else if (landmarkType === "arch") {
                landmark = createArchLandmark("#" + color.getHexString());
            } else if (landmarkType === "statue") {
                landmark = createStatueLandmark("#" + color.getHexString());
            } else if (landmarkType === "crater") {
                landmark = createCraterLandmark();
            } else {
                landmark = createMountainLandmark("#" + color.getHexString());
            }

            const position = anchorPositions[index];
            landmark.position.set(position[0], 0.06, position[1]);
            landmark.rotation.y = (random() - 0.5) * 0.22;
            cityGroup.add(landmark);

            const imageDataUris = imageMap.get(String(name).toLowerCase()) || [];
            applyImageDrivenCartoonStyle(landmark, imageDataUris, seed, "#" + color.getHexString());
            addCartoonOutline(landmark, 0.04, 0x2f4158, 0.22, 22);

            const meshCandidates = meshMap.get(String(name).toLowerCase()) || [];
            if (meshCandidates.length > 0) {
                applyMeshyModelToLandmark(landmark, meshCandidates[0], expectedSeed, name);
            }
        }
    }

    function buildClouds(sceneData) {
        const coverage = clamp01(sceneData.cloudCoverage, 0.45);
        const count = Math.floor(4 + coverage * 12);
        const material = new THREE.MeshStandardMaterial({
            color: 0xf3f7ff,
            transparent: true,
            opacity: 0.82
        });

        for (let index = 0; index < count; index += 1) {
            const cluster = new THREE.Group();
            const puffCount = 3 + Math.floor(random() * 3);
            for (let puffIndex = 0; puffIndex < puffCount; puffIndex += 1) {
                const puff = new THREE.Mesh(
                    new THREE.SphereGeometry(0.62 + random() * 0.34, 10, 10),
                    material
                );
                puff.position.set((random() - 0.5) * 1.3, (random() - 0.5) * 0.26, (random() - 0.5) * 1.3);
                cluster.add(puff);
            }
            cluster.position.set((random() - 0.5) * 20, 8 + random() * 4, (random() - 0.5) * 16);
            cluster.userData = { drift: 0.2 + random() * 0.7, span: 22 + random() * 8 };
            cloudGroup.add(cluster);
        }
    }

    function buildWetRoadOverlays(intensity) {
        const opacity = Math.max(0.2, Math.min(0.54, 0.26 + intensity * 0.3));
        const puddleMaterial = new THREE.MeshStandardMaterial({
            color: 0x9db5cb,
            transparent: true,
            opacity,
            roughness: 0.12,
            metalness: 0.42
        });

        const roadA = new THREE.Mesh(new THREE.PlaneGeometry(13.2, 1.3), puddleMaterial);
        roadA.rotation.x = -Math.PI / 2;
        roadA.position.set(0, 0.144, 0);
        baseGroup.add(roadA);
        wetRoadSurfaces.push({ mesh: roadA, baseOpacity: opacity, phase: random() * Math.PI * 2 });

        const roadB = new THREE.Mesh(new THREE.PlaneGeometry(1.3, 13.2), puddleMaterial.clone());
        roadB.rotation.x = -Math.PI / 2;
        roadB.position.set(0, 0.145, 0);
        baseGroup.add(roadB);
        wetRoadSurfaces.push({ mesh: roadB, baseOpacity: opacity * 0.95, phase: random() * Math.PI * 2 });
    }

    function resolveTemperatureCelsius(weather) {
        if (!weather || typeof weather !== "object") {
            return null;
        }

        const celsius = Number(weather.temperatureC);
        if (Number.isFinite(celsius)) {
            return celsius;
        }

        const fahrenheit = Number(weather.temperatureF);
        if (Number.isFinite(fahrenheit)) {
            return (fahrenheit - 32) * (5 / 9);
        }

        return null;
    }

    function isLikelyTropicalLocation(weather, coordinates) {
        const latitude = coordinates ? Number(coordinates.latitude) : NaN;
        if (Number.isFinite(latitude) && Math.abs(latitude) <= 23.5) {
            return true;
        }

        const locationText = String((weather && weather.locationName) || "").toLowerCase();
        return locationText.includes("honolulu") || locationText.includes("hawaii");
    }

    function allowsSnowVisuals(weather, coordinates) {
        const temperatureC = resolveTemperatureCelsius(weather);
        const tropical = isLikelyTropicalLocation(weather, coordinates);
        if (tropical) {
            return false;
        }

        if (!Number.isFinite(temperatureC)) {
            return true;
        }

        return temperatureC <= 2.0;
    }

    function getWeatherVisualSignal(sceneData, weather, coordinates) {
        const shortForecast = normalizeForecastText(weather && weather.shortForecast || "");
        const detailedForecast = normalizeForecastText(weather && weather.detailedForecast || "");
        const summary = `${shortForecast} ${detailedForecast}`.trim();
        const segments = splitForecastSegments(shortForecast || summary);
        const primarySegment = segments[0] || shortForecast || summary;
        const secondarySegment = segments[1] || "";
        const primaryChanceOnly = isChanceOnlyPrecip(primarySegment);
        const secondaryLikelyPrecip = hasAnyTerm(secondarySegment, ["likely", "numerous", "definite"]) &&
            hasPrecipitationTerms(secondarySegment);

        const iconName = chooseWeatherIconName({
            weather: weather || {},
            coordinates: coordinates || {},
            scene: { timeOfDay: sceneData && sceneData.timeOfDay ? sceneData.timeOfDay : "" }
        }).toLowerCase();
        const iconSuggestsSnow = iconName.includes("snow") || iconName.includes("sleet");
        const iconSuggestsRain = iconName.includes("rain") || iconName.includes("drizzle") || iconName.includes("hail");
        const iconSuggestsFog = iconName.includes("fog") || iconName.includes("haze") || iconName.includes("mist");

        const primaryMentionsSnow = hasAnyTerm(primarySegment, [
            "snow",
            "flurr",
            "blizzard",
            "sleet",
            "freezing rain",
            "ice pellets",
            "wintry mix"
        ]);
        const primaryMentionsRain = hasAnyTerm(primarySegment, [
            "rain",
            "showers",
            "shower",
            "drizzle",
            "sprinkle",
            "thunderstorm",
            "t-storm",
            "tstorm",
            "lightning",
            "hail"
        ]);
        const primaryMentionsFog = hasAnyTerm(primarySegment, ["fog", "mist", "haze"]);
        const skyState = detectSkyState(primarySegment) || detectSkyState(shortForecast) || detectSkyState(summary);
        const clearlyDryPrimary = !!skyState && !primaryMentionsRain && !primaryMentionsSnow;

        return {
            summary,
            primarySegment,
            primaryChanceOnly,
            secondaryLikelyPrecip,
            iconSuggestsSnow,
            iconSuggestsRain,
            iconSuggestsFog,
            primaryMentionsSnow,
            primaryMentionsRain,
            primaryMentionsFog,
            clearlyDryPrimary
        };
    }

    function buildWeather(sceneData, weather, coordinates, weatherText, enableGroundWetness) {
        const lowered = String(weatherText || "").toLowerCase();
        const accent = (sceneData.accentEffect || "").toLowerCase();
        const signal = getWeatherVisualSignal(sceneData, weather, coordinates);
        const snowAllowed = allowsSnowVisuals(weather, coordinates);

        const hasWeatherSignal = signal.summary.length > 0;
        const primaryPrecipRequested =
            (signal.primaryMentionsSnow || signal.primaryMentionsRain || signal.primaryMentionsFog) &&
            !signal.primaryChanceOnly;

        const allowAccentFallback = !hasWeatherSignal || (!primaryPrecipRequested && !signal.clearlyDryPrimary);
        const isSnow = snowAllowed &&
            (
                (signal.primaryMentionsSnow && !signal.primaryChanceOnly) ||
                signal.secondaryLikelyPrecip && hasAnyTerm(signal.summary, ["snow", "flurr", "sleet", "ice pellets", "wintry mix"]) ||
                signal.iconSuggestsSnow ||
                (allowAccentFallback && accent === "snow")
            );
        const isRain = !isSnow &&
            (
                (signal.primaryMentionsRain && !signal.primaryChanceOnly) ||
                signal.secondaryLikelyPrecip && hasAnyTerm(signal.summary, ["rain", "showers", "drizzle", "thunderstorm", "hail"]) ||
                signal.iconSuggestsRain ||
                (allowAccentFallback && accent === "rain")
            );
        const isFog = !isSnow && !isRain &&
            (
                signal.primaryMentionsFog ||
                signal.iconSuggestsFog ||
                (allowAccentFallback && accent === "fog") ||
                lowered.includes("fog") ||
                lowered.includes("haze")
            );

        const intensity = clamp01(sceneData.precipitationIntensity, 0.35);
        const count = Math.floor(45 + intensity * 200);

        if (isSnow) {
            const geometry = new THREE.SphereGeometry(0.055, 8, 8);
            const material = new THREE.MeshStandardMaterial({ color: 0xf6fbff, roughness: 0.4 });
            for (let index = 0; index < count; index += 1) {
                const particle = new THREE.Mesh(geometry, material);
                particle.position.set((random() - 0.5) * 16, 1.2 + random() * 12, (random() - 0.5) * 16);
                precipitationGroup.add(particle);
                weatherParticles.push({
                    mesh: particle,
                    speed: 1.0 + random() * 1.6,
                    wobble: random() * Math.PI * 2,
                    type: "snow"
                });
            }
            scene.fog = new THREE.FogExp2(0xdde9f7, 0.008 + intensity * 0.012);
            return;
        }

        if (isRain) {
            const geometry = new THREE.BoxGeometry(0.03, 0.36, 0.03);
            const material = new THREE.MeshStandardMaterial({ color: 0x84b8ff, roughness: 0.16, metalness: 0.22 });
            for (let index = 0; index < count; index += 1) {
                const particle = new THREE.Mesh(geometry, material);
                particle.position.set((random() - 0.5) * 16, 1.2 + random() * 12, (random() - 0.5) * 16);
                precipitationGroup.add(particle);
                weatherParticles.push({
                    mesh: particle,
                    speed: 5.2 + random() * 7.0,
                    wobble: random() * Math.PI * 2,
                    type: "rain"
                });
            }
            if (enableGroundWetness !== false) {
                buildWetRoadOverlays(intensity);
            }
            scene.fog = new THREE.FogExp2(0x8ca6c4, 0.006 + intensity * 0.012);
            return;
        }

        if (isFog) {
            scene.fog = new THREE.FogExp2(0xd8e3ed, 0.010);
            return;
        }

        scene.fog = null;
    }

    function normalizeAngleDegrees(degrees) {
        let normalized = degrees % 360;
        if (normalized < 0) {
            normalized += 360;
        }
        return normalized;
    }

    function toJulianDay(date) {
        return (date.getTime() / 86400000) + 2440587.5;
    }

    function degToRad(degrees) {
        return degrees * (Math.PI / 180);
    }

    function radToDeg(radians) {
        return radians * (180 / Math.PI);
    }

    function gmstDegrees(julianDay) {
        const t = (julianDay - 2451545.0) / 36525.0;
        const gmst = 280.46061837
            + 360.98564736629 * (julianDay - 2451545.0)
            + (0.000387933 * t * t)
            - ((t * t * t) / 38710000.0);
        return normalizeAngleDegrees(gmst);
    }

    function equatorialToHorizontal(raDeg, decDeg, latitudeDeg, longitudeDeg, julianDay) {
        const lstDeg = normalizeAngleDegrees(gmstDegrees(julianDay) + longitudeDeg);
        const haDeg = normalizeAngleDegrees(lstDeg - raDeg);

        const ha = degToRad(haDeg);
        const dec = degToRad(decDeg);
        const lat = degToRad(latitudeDeg);

        const sinAlt = Math.sin(dec) * Math.sin(lat) + Math.cos(dec) * Math.cos(lat) * Math.cos(ha);
        const altitude = Math.asin(Math.max(-1, Math.min(1, sinAlt)));

        const azimuth = Math.atan2(
            Math.sin(ha),
            (Math.cos(ha) * Math.sin(lat)) - (Math.tan(dec) * Math.cos(lat))
        );

        return {
            azimuthDeg: normalizeAngleDegrees(radToDeg(azimuth) + 180),
            altitudeDeg: radToDeg(altitude)
        };
    }

    function computeSunHorizontal(latitudeDeg, longitudeDeg, date) {
        const julianDay = toJulianDay(date);
        const n = julianDay - 2451545.0;

        const meanLongitude = normalizeAngleDegrees(280.460 + (0.9856474 * n));
        const meanAnomaly = normalizeAngleDegrees(357.528 + (0.9856003 * n));
        const eclipticLongitude = meanLongitude
            + (1.915 * Math.sin(degToRad(meanAnomaly)))
            + (0.020 * Math.sin(degToRad(2 * meanAnomaly)));
        const obliquity = 23.439 - (0.0000004 * n);

        const ra = radToDeg(Math.atan2(
            Math.cos(degToRad(obliquity)) * Math.sin(degToRad(eclipticLongitude)),
            Math.cos(degToRad(eclipticLongitude))
        ));
        const dec = radToDeg(Math.asin(
            Math.sin(degToRad(obliquity)) * Math.sin(degToRad(eclipticLongitude))
        ));

        return equatorialToHorizontal(normalizeAngleDegrees(ra), dec, latitudeDeg, longitudeDeg, julianDay);
    }

    function computeMoonHorizontal(latitudeDeg, longitudeDeg, date) {
        const julianDay = toJulianDay(date);
        const n = julianDay - 2451545.0;

        const l0 = normalizeAngleDegrees(218.316 + (13.176396 * n));
        const mMoon = normalizeAngleDegrees(134.963 + (13.064993 * n));
        const mSun = normalizeAngleDegrees(357.529 + (0.98560028 * n));
        const d = normalizeAngleDegrees(297.850 + (12.190749 * n));
        const f = normalizeAngleDegrees(93.272 + (13.229350 * n));

        const moonLon = l0
            + (6.289 * Math.sin(degToRad(mMoon)))
            + (1.274 * Math.sin(degToRad((2 * d) - mMoon)))
            + (0.658 * Math.sin(degToRad(2 * d)))
            + (0.214 * Math.sin(degToRad(2 * mMoon)))
            - (0.186 * Math.sin(degToRad(mSun)));

        const moonLat = (5.128 * Math.sin(degToRad(f)))
            + (0.280 * Math.sin(degToRad(mMoon + f)))
            + (0.277 * Math.sin(degToRad(mMoon - f)))
            + (0.173 * Math.sin(degToRad((2 * d) - f)));

        const obliquity = 23.439 - (0.0000004 * n);
        const moonRa = radToDeg(Math.atan2(
            Math.sin(degToRad(moonLon)) * Math.cos(degToRad(obliquity))
                - Math.tan(degToRad(moonLat)) * Math.sin(degToRad(obliquity)),
            Math.cos(degToRad(moonLon))
        ));
        const moonDec = radToDeg(Math.asin(
            Math.sin(degToRad(moonLat)) * Math.cos(degToRad(obliquity))
                + Math.cos(degToRad(moonLat)) * Math.sin(degToRad(obliquity)) * Math.sin(degToRad(moonLon))
        ));

        return equatorialToHorizontal(normalizeAngleDegrees(moonRa), moonDec, latitudeDeg, longitudeDeg, julianDay);
    }

    function describeMoonPhase(phaseFraction) {
        const phase = ((phaseFraction % 1) + 1) % 1;
        if (phase < 0.03 || phase >= 0.97) {
            return "New Moon";
        }
        if (phase < 0.22) {
            return "Waxing Crescent";
        }
        if (phase < 0.28) {
            return "First Quarter";
        }
        if (phase < 0.47) {
            return "Waxing Gibbous";
        }
        if (phase < 0.53) {
            return "Full Moon";
        }
        if (phase < 0.72) {
            return "Waning Gibbous";
        }
        if (phase < 0.78) {
            return "Last Quarter";
        }
        return "Waning Crescent";
    }

    function computeMoonPhaseData(date) {
        const synodicMonthDays = 29.530588853;
        const knownNewMoonEpochUtc = Date.UTC(2000, 0, 6, 18, 14, 0);
        const elapsedDays = (date.getTime() - knownNewMoonEpochUtc) / 86400000;
        let ageDays = elapsedDays % synodicMonthDays;
        if (ageDays < 0) {
            ageDays += synodicMonthDays;
        }

        const phaseFraction = ageDays / synodicMonthDays;
        const illumination = 0.5 * (1 - Math.cos(phaseFraction * Math.PI * 2));
        return {
            ageDays,
            phaseFraction,
            illumination,
            name: describeMoonPhase(phaseFraction)
        };
    }

    function renderMoonPhaseTexture(phaseFraction) {
        if (!moonPhaseContext || !moonPhaseTexture) {
            return;
        }

        const size = moonPhaseCanvas.width;
        const center = size * 0.5;
        const radius = size * 0.44;
        const edgeSoftness = 0.04;
        const phaseAngle = phaseFraction * Math.PI * 2;
        const lightX = Math.sin(phaseAngle);
        const lightZ = -Math.cos(phaseAngle);

        const imageData = moonPhaseContext.createImageData(size, size);
        const pixels = imageData.data;
        for (let py = 0; py < size; py += 1) {
            const ny = (py + 0.5 - center) / radius;
            for (let px = 0; px < size; px += 1) {
                const nx = (px + 0.5 - center) / radius;
                const pixelIndex = ((py * size) + px) * 4;
                const radialSquared = (nx * nx) + (ny * ny);

                if (radialSquared > 1) {
                    pixels[pixelIndex + 3] = 0;
                    continue;
                }

                const nz = Math.sqrt(Math.max(0, 1 - radialSquared));
                const lightDot = (nx * lightX) + (nz * lightZ);
                const litAmount = Math.max(0, lightDot);
                const rim = Math.pow(Math.max(0, 1 - radialSquared), 0.55);
                const ambient = 0.12 + (0.18 * rim);
                const brightness = ambient + (litAmount * 0.8);
                const shadowScale = lightDot < 0 ? 0.36 : 1;

                const r = Math.max(8, Math.min(255, Math.round((188 + (52 * litAmount)) * brightness * shadowScale)));
                const g = Math.max(10, Math.min(255, Math.round((202 + (42 * litAmount)) * brightness * shadowScale)));
                const b = Math.max(14, Math.min(255, Math.round((224 + (30 * litAmount)) * brightness * shadowScale)));
                const edgeAlpha = radialSquared > (1 - edgeSoftness)
                    ? Math.max(0, (1 - radialSquared) / edgeSoftness)
                    : 1;

                pixels[pixelIndex] = r;
                pixels[pixelIndex + 1] = g;
                pixels[pixelIndex + 2] = b;
                pixels[pixelIndex + 3] = Math.round(255 * edgeAlpha);
            }
        }

        moonPhaseContext.putImageData(imageData, 0, 0);
        moonPhaseContext.globalCompositeOperation = "source-over";
        moonPhaseContext.fillStyle = "rgba(93,109,144,0.2)";
        moonPhaseContext.beginPath();
        moonPhaseContext.arc(center - (radius * 0.22), center - (radius * 0.08), radius * 0.17, 0, Math.PI * 2);
        moonPhaseContext.fill();
        moonPhaseContext.beginPath();
        moonPhaseContext.arc(center + (radius * 0.16), center + (radius * 0.15), radius * 0.12, 0, Math.PI * 2);
        moonPhaseContext.fill();
        moonPhaseContext.beginPath();
        moonPhaseContext.arc(center + (radius * 0.02), center - (radius * 0.23), radius * 0.09, 0, Math.PI * 2);
        moonPhaseContext.fill();

        moonPhaseTexture.needsUpdate = true;
    }

    function azimuthAltitudeToVector(azimuthDeg, altitudeDeg) {
        const azimuth = degToRad(azimuthDeg);
        const altitude = degToRad(altitudeDeg);

        const x = Math.sin(azimuth) * Math.cos(altitude);
        const y = Math.sin(altitude);
        const z = Math.cos(azimuth) * Math.cos(altitude);
        return new THREE.Vector3(x, y, z).normalize();
    }

    function applySkyFromTimeOfDay(
        sceneData,
        sunStrength,
        wallpaperBackgroundImageUrlValue,
        wallpaperBackgroundColorValue,
        wallpaperBackgroundDisplayModeValue,
        useAnimatedAiBackgroundValue) {
        const timeOfDay = String(sceneData.timeOfDay || "").toLowerCase();
        const wallpaperBase = asHexColor(
            wallpaperBackgroundColorValue,
            asHexColor(sceneData.paletteA, "#b9dfff"));
        let color = new THREE.Color(wallpaperBase);

        if (timeOfDay === "night") {
            color = color.clone().multiplyScalar(0.38);
        } else if (timeOfDay === "dusk") {
            color = color.clone().lerp(new THREE.Color("#385e9a"), 0.34);
        } else if (timeOfDay === "dawn") {
            color = color.clone().lerp(new THREE.Color("#f3bd90"), 0.24);
        } else {
            color = color.clone().lerp(new THREE.Color("#eef7ff"), 0.08);
        }

        color.multiplyScalar(0.72 + sunStrength * 0.34);
        wallpaperBackgroundFallbackColor.copy(color);
        const usingBackgroundMedia = ensureWallpaperBackgroundMedia(
            wallpaperBackgroundImageUrlValue,
            wallpaperBackgroundDisplayModeValue,
            color);
        const usingAnimatedAiBackground = !usingBackgroundMedia && !!useAnimatedAiBackgroundValue;
        setAnimatedAiBackgroundEnabled(usingAnimatedAiBackground, color, timeOfDay, sunStrength);
        if (usingAnimatedAiBackground) {
            scene.background = null;
            renderAnimatedAiBackground(performance.now());
        } else if (usingBackgroundMedia) {
            scene.background = null;
        } else {
            scene.background = color;
        }

        const center = color.clone().lerp(new THREE.Color("#ffffff"), 0.34);
        const edge = color.clone().multiplyScalar(timeOfDay === "night" ? 0.64 : 0.83);
        document.body.style.background = usingBackgroundMedia
            ? `radial-gradient(circle at 50% 34%, rgba(${center.r * 255}, ${center.g * 255}, ${center.b * 255}, 0.26) 0%, rgba(${color.r * 255}, ${color.g * 255}, ${color.b * 255}, 0.18) 58%, rgba(${edge.r * 255}, ${edge.g * 255}, ${edge.b * 255}, 0.28) 100%)`
            : (usingAnimatedAiBackground
                ? `radial-gradient(circle at 44% 26%, rgba(255,255,255,0.14) 0%, rgba(${center.r * 255}, ${center.g * 255}, ${center.b * 255}, 0.08) 38%, rgba(${edge.r * 255}, ${edge.g * 255}, ${edge.b * 255}, 0.22) 100%)`
                : `radial-gradient(circle at 50% 34%, #${center.getHexString()} 0%, #${color.getHexString()} 58%, #${edge.getHexString()} 100%)`);
    }

    function updateLighting(sceneData, payload) {
        const latitude = payload && payload.coordinates && typeof payload.coordinates.latitude === "number" ? payload.coordinates.latitude : 0;
        const longitude = payload && payload.coordinates && typeof payload.coordinates.longitude === "number" ? payload.coordinates.longitude : 0;
        const timestamp = payload && (payload.weather.capturedAt || payload.generatedAt) ? new Date(payload.weather.capturedAt || payload.generatedAt) : new Date();

        const sun = computeSunHorizontal(latitude, longitude, timestamp);
        const moon = computeMoonHorizontal(latitude, longitude, timestamp);
        const moonPhase = computeMoonPhaseData(timestamp);
        const sunVector = azimuthAltitudeToVector(sun.azimuthDeg, sun.altitudeDeg);
        const moonVector = azimuthAltitudeToVector(moon.azimuthDeg, moon.altitudeDeg);

        sunLight.position.copy(sunVector.clone().multiplyScalar(34));
        sunLight.target.position.set(0, 0.4, 0);
        moonLight.position.copy(moonVector.clone().multiplyScalar(30));
        moonLight.target.position.set(0, 0.4, 0);

        const sunStrength = Math.max(0, Math.min(1, (sun.altitudeDeg + 6) / 45));
        const moonStrength = Math.max(0, Math.min(1, (moon.altitudeDeg + 12) / 40)) * (1 - sunStrength);
        const isDayTime = sun.altitudeDeg >= 0;

        sunLight.intensity = 0.35 + (1.55 * sunStrength);
        moonLight.intensity = 0.06 + (0.62 * moonStrength);
        hemiLight.intensity = 0.44 + (0.46 * Math.max(sunStrength, 0.18));
        fillLight.intensity = 0.18 + (0.24 * (1 - sunStrength));

        sunDisc.visible = isDayTime;
        moonDisc.visible = !isDayTime && moon.altitudeDeg > -14;
        sunDisc.position.copy(sunVector.clone().multiplyScalar(24));
        moonDisc.position.copy(moonVector.clone().multiplyScalar(22));

        if (Math.abs(moonPhase.phaseFraction - lastMoonPhaseFraction) > 0.00035) {
            renderMoonPhaseTexture(moonPhase.phaseFraction);
            lastMoonPhaseFraction = moonPhase.phaseFraction;
        }

        moonIlluminationPercent = Math.round(moonPhase.illumination * 100);
        moonPhaseName = moonPhase.name;

        const moonToSun = sunVector.clone().sub(moonVector).normalize();
        const cameraRight = new THREE.Vector3(1, 0, 0).applyQuaternion(camera.quaternion).normalize();
        const cameraUp = new THREE.Vector3(0, 1, 0).applyQuaternion(camera.quaternion).normalize();
        moonDiscSpin = Math.atan2(moonToSun.dot(cameraUp), moonToSun.dot(cameraRight));
        activeCelestialLabel = isDayTime
            ? `Sun alt ${sun.altitudeDeg.toFixed(1)}`
            : `Moon ${moonPhaseName} ${moonIlluminationPercent}%`;

        applySkyFromTimeOfDay(
            sceneData,
            sunStrength,
            payload && payload.wallpaperBackgroundImageUrl,
            payload && payload.wallpaperBackgroundColor,
            payload && payload.wallpaperBackgroundDisplayMode,
            payload && payload.useAnimatedAiBackground);
        postHostDebug(`Sun az ${sun.azimuthDeg.toFixed(1)} alt ${sun.altitudeDeg.toFixed(1)} | Moon az ${moon.azimuthDeg.toFixed(1)} alt ${moon.altitudeDeg.toFixed(1)} | Active ${activeCelestialLabel}`);
    }

    function isNightTimeForIcon(payload) {
        const timeOfDay = String(payload && payload.scene && payload.scene.timeOfDay || "").toLowerCase();
        return timeOfDay === "night" || timeOfDay === "dusk";
    }

    function normalizeForecastText(value) {
        return String(value || "")
            .toLowerCase()
            .replace(/[(),.;:!?]/g, " ")
            .replace(/\s+/g, " ")
            .trim();
    }

    function splitForecastSegments(forecastText) {
        const normalized = normalizeForecastText(forecastText);
        if (!normalized) {
            return [];
        }

        return normalized
            .split(/\b(?:then|becoming|later)\b/g)
            .map((segment) => segment.trim())
            .filter(Boolean);
    }

    function hasAnyTerm(text, terms) {
        const source = String(text || "");
        return terms.some((term) => source.includes(term));
    }

    function detectSkyState(text) {
        const source = normalizeForecastText(text);
        if (!source) {
            return null;
        }

        if (hasAnyTerm(source, ["mostly cloudy", "overcast", "cloudy"])) {
            return "overcast";
        }

        if (hasAnyTerm(source, ["partly cloudy", "partly sunny", "mostly sunny", "mostly clear", "few clouds", "partly clear"])) {
            return "partly";
        }

        if (hasAnyTerm(source, ["sunny", "clear", "fair"])) {
            return "clear";
        }

        return null;
    }

    function chooseSkyIconByState(skyState, isNight) {
        if (skyState === "overcast") {
            return isNight ? "overcast-night.svg" : "overcast-day.svg";
        }

        if (skyState === "partly") {
            return isNight ? "partly-cloudy-night.svg" : "partly-cloudy-day.svg";
        }

        return isNight ? "clear-night.svg" : "clear-day.svg";
    }

    function chooseCloudPrecipIcon(precipType, skyState, isNight) {
        const prefix = skyState === "overcast" ? "overcast" : "partly-cloudy";
        const dayPart = isNight ? "night" : "day";
        return `${prefix}-${dayPart}-${precipType}.svg`;
    }

    function chooseAtmosphericIcon(kind, skyState, isNight) {
        if (kind === "dust") {
            return isNight ? "dust-night.svg" : "dust-day.svg";
        }

        if (kind === "smoke") {
            return chooseCloudPrecipIcon("smoke", skyState, isNight);
        }

        if (kind === "fog") {
            if (skyState === "overcast") {
                return isNight ? "overcast-night-fog.svg" : "overcast-day-fog.svg";
            }

            if (skyState === "partly") {
                return isNight ? "partly-cloudy-night-fog.svg" : "partly-cloudy-day-fog.svg";
            }

            return isNight ? "fog-night.svg" : "fog-day.svg";
        }

        if (kind === "haze") {
            if (skyState === "overcast") {
                return isNight ? "overcast-night-haze.svg" : "overcast-day-haze.svg";
            }

            if (skyState === "partly") {
                return isNight ? "partly-cloudy-night-haze.svg" : "partly-cloudy-day-haze.svg";
            }

            return isNight ? "haze-night.svg" : "haze-day.svg";
        }

        return isNight ? "clear-night.svg" : "clear-day.svg";
    }

    function hasPrecipitationTerms(text) {
        return hasAnyTerm(text, [
            "thunderstorm",
            "t-storm",
            "tstorm",
            "lightning",
            "rain",
            "showers",
            "shower",
            "drizzle",
            "sprinkles",
            "sprinkle",
            "snow",
            "flurr",
            "sleet",
            "freezing rain",
            "ice pellets",
            "wintry mix",
            "hail"
        ]);
    }

    function isChanceOnlyPrecip(text) {
        const source = normalizeForecastText(text);
        if (!source || !hasPrecipitationTerms(source)) {
            return false;
        }

        const hasChance = hasAnyTerm(source, ["slight chance", "chance", "isolated", "scattered", "patchy"]);
        const hasLikely = hasAnyTerm(source, ["likely", "numerous", "definite"]);
        return hasChance && !hasLikely;
    }

    function chooseWeatherIconName(payload) {
        const weather = payload && payload.weather ? payload.weather : {};
        const coordinates = payload && payload.coordinates ? payload.coordinates : {};
        const shortForecast = normalizeForecastText(weather.shortForecast || "");
        const detailedForecast = normalizeForecastText(weather.detailedForecast || "");
        const summary = `${shortForecast} ${detailedForecast}`.trim();
        const isNight = isNightTimeForIcon(payload);
        const snowAllowed = allowsSnowVisuals(weather, coordinates);

        if (!summary) {
            return isNight ? "clear-night.svg" : "clear-day.svg";
        }

        const segments = splitForecastSegments(shortForecast || summary);
        const primarySegment = segments[0] || summary;
        const secondarySegment = segments[1] || "";
        const primarySkyState = detectSkyState(primarySegment) || detectSkyState(summary);
        const primaryHasPrecip = hasPrecipitationTerms(primarySegment);
        const secondaryIsChancePrecip = isChanceOnlyPrecip(secondarySegment);

        if (!primaryHasPrecip && secondaryIsChancePrecip && primarySkyState) {
            return chooseSkyIconByState(primarySkyState, isNight);
        }

        if (/chance showers? and thunderstorms?/.test(summary)) {
            return isNight ? "thunderstorms-night-rain.svg" : "thunderstorms-day-rain.svg";
        }

        if (/mostly cloudy\s+then\s+slight chance\s+(?:of\s+)?(?:rain|showers?)/.test(shortForecast)) {
            return isNight ? "overcast-night.svg" : "overcast-day.svg";
        }

        if (hasAnyTerm(summary, ["tornado", "waterspout", "funnel cloud"])) {
            return "tornado.svg";
        }

        if (hasAnyTerm(summary, ["hurricane", "typhoon", "tropical storm", "cyclone"])) {
            return "hurricane.svg";
        }

        if (hasAnyTerm(summary, ["thunder", "lightning", "t-storm", "tstorm", "thunderstorm"])) {
            if (hasAnyTerm(summary, ["snow", "flurr"])) {
                return snowAllowed
                    ? (isNight ? "thunderstorms-night-snow.svg" : "thunderstorms-day-snow.svg")
                    : (isNight ? "thunderstorms-night-rain.svg" : "thunderstorms-day-rain.svg");
            }

            if (hasAnyTerm(summary, ["rain", "showers", "drizzle"])) {
                return isNight ? "thunderstorms-night-rain.svg" : "thunderstorms-day-rain.svg";
            }

            return isNight ? "thunderstorms-night.svg" : "thunderstorms-day.svg";
        }

        if (hasAnyTerm(summary, ["freezing rain", "sleet", "ice pellets", "wintry mix"])) {
            return snowAllowed
                ? chooseCloudPrecipIcon("sleet", primarySkyState, isNight)
                : chooseCloudPrecipIcon("rain", primarySkyState, isNight);
        }

        if (hasAnyTerm(summary, ["hail"])) {
            return chooseCloudPrecipIcon("hail", primarySkyState, isNight);
        }

        if (hasAnyTerm(summary, ["snow", "flurries", "flurr", "blizzard"])) {
            return snowAllowed
                ? chooseCloudPrecipIcon("snow", primarySkyState, isNight)
                : (primarySkyState
                    ? chooseSkyIconByState(primarySkyState, isNight)
                    : (isNight ? "clear-night.svg" : "clear-day.svg"));
        }

        if (hasAnyTerm(summary, ["drizzle", "sprinkles", "sprinkle"])) {
            return chooseCloudPrecipIcon("drizzle", primarySkyState, isNight);
        }

        if (hasAnyTerm(summary, ["rain", "showers", "shower"])) {
            return chooseCloudPrecipIcon("rain", primarySkyState, isNight);
        }

        if (hasAnyTerm(summary, ["fog", "mist"])) {
            return chooseAtmosphericIcon("fog", primarySkyState, isNight);
        }

        if (hasAnyTerm(summary, ["haze"])) {
            return chooseAtmosphericIcon("haze", primarySkyState, isNight);
        }

        if (hasAnyTerm(summary, ["smoke"])) {
            return chooseAtmosphericIcon("smoke", primarySkyState, isNight);
        }

        if (hasAnyTerm(summary, ["dust", "sand", "ash"])) {
            return chooseAtmosphericIcon("dust", primarySkyState, isNight);
        }

        if (hasAnyTerm(summary, ["wind", "breezy", "gust", "blustery"])) {
            return "wind.svg";
        }

        if (primarySkyState === "overcast") {
            return chooseSkyIconByState("overcast", isNight);
        }

        if (primarySkyState === "partly") {
            return chooseSkyIconByState("partly", isNight);
        }

        return chooseSkyIconByState("clear", isNight);
    }

    function updateWeatherIcon(payload) {
        const iconName = chooseWeatherIconName(payload);
        const iconPath = `${WEATHER_ICON_BASE_URL}/${iconName}`;
        const fallbackName = isNightTimeForIcon(payload) ? "clear-night.svg" : "clear-day.svg";
        const fallbackPath = `${WEATHER_ICON_BASE_URL}/${fallbackName}`;

        weatherIcon.dataset.fallbackApplied = "0";
        weatherIcon.onerror = () => {
            if (weatherIcon.dataset.fallbackApplied !== "1" && iconPath !== fallbackPath) {
                weatherIcon.dataset.fallbackApplied = "1";
                weatherIcon.src = fallbackPath;
                return;
            }

            weatherIcon.style.display = "none";
        };
        weatherIcon.src = iconPath;
        weatherIcon.alt = iconName.replace(".svg", "");
        weatherIcon.style.display = "block";
    }

    function normalizeTemperatureUnit(unitToken) {
        const token = String(unitToken || "").trim().toLowerCase();
        if (token === "c" || token === "celsius" || token === "centigrade") {
            return "C";
        }

        return "F";
    }

    function resolveTemperatureForHud(payload) {
        const requestedUnit = normalizeTemperatureUnit(payload && payload.temperatureUnit);
        const weather = payload && payload.weather ? payload.weather : {};
        const hasF = typeof weather.temperatureF === "number" && Number.isFinite(weather.temperatureF);
        const hasC = typeof weather.temperatureC === "number" && Number.isFinite(weather.temperatureC);
        const valueF = hasF ? weather.temperatureF : null;
        const valueC = hasC ? weather.temperatureC : null;

        if (requestedUnit === "C") {
            const value = valueC !== null ? valueC : (valueF !== null ? ((valueF - 32) * 5 / 9) : null);
            return { value, unit: "C" };
        }

        const value = valueF !== null ? valueF : (valueC !== null ? ((valueC * 9 / 5) + 32) : null);
        return { value, unit: "F" };
    }

    function parseHexColorToRgb(hex, fallbackHex) {
        const input = String(hex || fallbackHex || "#7aa7d8").trim();
        const normalized = input.startsWith("#") ? input : `#${input}`;
        const shortMatch = /^#([0-9a-f]{3})$/i.exec(normalized);
        if (shortMatch) {
            const chars = shortMatch[1];
            return {
                r: parseInt(chars[0] + chars[0], 16),
                g: parseInt(chars[1] + chars[1], 16),
                b: parseInt(chars[2] + chars[2], 16)
            };
        }

        const longMatch = /^#([0-9a-f]{6})$/i.exec(normalized);
        if (longMatch) {
            const value = longMatch[1];
            return {
                r: parseInt(value.slice(0, 2), 16),
                g: parseInt(value.slice(2, 4), 16),
                b: parseInt(value.slice(4, 6), 16)
            };
        }

        return parseHexColorToRgb(fallbackHex || "#7aa7d8", "#7aa7d8");
    }

    function rgbToHexColor(rgb) {
        const toChannel = (value) => {
            const normalized = Math.max(0, Math.min(255, Math.round(Number(value) || 0)));
            return normalized.toString(16).padStart(2, "0");
        };

        return `#${toChannel(rgb.r)}${toChannel(rgb.g)}${toChannel(rgb.b)}`;
    }

    function blendHexColor(baseHex, mixHex, mixRatio) {
        const ratio = clamp01(mixRatio, 0);
        const base = parseHexColorToRgb(baseHex, "#7aa7d8");
        const mix = parseHexColorToRgb(mixHex, "#ffffff");
        return rgbToHexColor({
            r: (base.r * (1 - ratio)) + (mix.r * ratio),
            g: (base.g * (1 - ratio)) + (mix.g * ratio),
            b: (base.b * (1 - ratio)) + (mix.b * ratio)
        });
    }

    function srgbChannelToLinear(value) {
        const channel = Math.max(0, Math.min(255, value)) / 255;
        return channel <= 0.04045
            ? channel / 12.92
            : Math.pow((channel + 0.055) / 1.055, 2.4);
    }

    function getRelativeLuminance(rgb) {
        const r = srgbChannelToLinear(rgb.r);
        const g = srgbChannelToLinear(rgb.g);
        const b = srgbChannelToLinear(rgb.b);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    function applyHudContrastTheme(paletteA, paletteB, paletteC) {
        const rgbA = parseHexColorToRgb(paletteA, "#b9dfff");
        const rgbB = parseHexColorToRgb(paletteB, "#91c9ef");
        const rgbC = parseHexColorToRgb(paletteC, "#f2c879");
        const averaged = {
            r: Math.round((rgbA.r + rgbB.r + rgbC.r) / 3),
            g: Math.round((rgbA.g + rgbB.g + rgbC.g) / 3),
            b: Math.round((rgbA.b + rgbB.b + rgbC.b) / 3)
        };

        const luminance = getRelativeLuminance(averaged);
        const prefersDarkText = luminance > 0.48;
        const root = document.documentElement;

        root.style.setProperty("--hud-fg", prefersDarkText ? "#101826" : "#f4f9ff");
        root.style.setProperty("--hud-soft", prefersDarkText ? "#20344b" : "#d9ebff");
        root.style.setProperty("--hud-alert", prefersDarkText ? "#7a1f1f" : "#ffe1e1");
        root.style.setProperty(
            "--hud-shadow",
            prefersDarkText
                ? "0 1px 1px rgba(255, 255, 255, 0.20), 0 2px 8px rgba(255, 255, 255, 0.12)"
                : "0 1px 1px rgba(0, 0, 0, 0.28), 0 2px 9px rgba(0, 0, 0, 0.18)");
    }

    function normalizeHudFontFamily(value) {
        const source = String(value || "").trim();
        if (!source) {
            return "\"Segoe UI\", sans-serif";
        }

        const sanitized = source.replace(/[^A-Za-z0-9 _.,'"()-]/g, "").trim();
        if (!sanitized) {
            return "\"Segoe UI\", sans-serif";
        }

        return sanitized.includes(",") ? sanitized : `"${sanitized}"`;
    }

    function normalizeHudFontSize(value, fallback) {
        const numeric = Number(value);
        if (!Number.isFinite(numeric)) {
            return fallback;
        }

        return Math.max(8, Math.min(144, numeric));
    }

    function applyHudTypography(textStyle) {
        const style = textStyle || {};
        const legacyFontFamily = style.fontFamily;
        const root = document.documentElement;
        root.style.setProperty("--hud-time-font-family", normalizeHudFontFamily(style.timeFontFamily || legacyFontFamily));
        root.style.setProperty("--hud-location-font-family", normalizeHudFontFamily(style.locationFontFamily || legacyFontFamily));
        root.style.setProperty("--hud-date-font-family", normalizeHudFontFamily(style.dateFontFamily || legacyFontFamily));
        root.style.setProperty("--hud-temperature-font-family", normalizeHudFontFamily(style.temperatureFontFamily || legacyFontFamily));
        root.style.setProperty("--hud-summary-font-family", normalizeHudFontFamily(style.summaryFontFamily || legacyFontFamily));
        root.style.setProperty("--hud-poi-font-family", normalizeHudFontFamily(style.poiFontFamily || legacyFontFamily));
        root.style.setProperty("--hud-alert-font-family", normalizeHudFontFamily(style.alertsFontFamily || legacyFontFamily));
        root.style.setProperty("--hud-time-size", `${normalizeHudFontSize(style.timeFontSize, 58)}px`);
        root.style.setProperty("--hud-location-size", `${normalizeHudFontSize(style.locationFontSize, 54)}px`);
        root.style.setProperty("--hud-date-size", `${normalizeHudFontSize(style.dateFontSize, 14)}px`);
        root.style.setProperty("--hud-temperature-size", `${normalizeHudFontSize(style.temperatureFontSize, 34)}px`);
        root.style.setProperty("--hud-summary-size", `${normalizeHudFontSize(style.summaryFontSize, 14)}px`);
        root.style.setProperty("--hud-poi-size", `${normalizeHudFontSize(style.poiFontSize, 14)}px`);
        root.style.setProperty("--hud-alert-size", `${normalizeHudFontSize(style.alertsFontSize, 14)}px`);
    }

    function resolveClockTimeZone(payload) {
        const candidate = payload && payload.weather && payload.weather.timeZoneId
            ? String(payload.weather.timeZoneId).trim()
            : (payload && payload.coordinates && payload.coordinates.timeZoneId
                ? String(payload.coordinates.timeZoneId).trim()
                : "");
        if (!candidate) {
            return null;
        }

        try {
            new Intl.DateTimeFormat(undefined, { timeZone: candidate }).format(new Date());
            return candidate;
        } catch (error) {
            return null;
        }
    }

    function formatDateWithWeekday(dateValue, timeZone) {
        try {
            const options = {
                weekday: "long",
                year: "numeric",
                month: "long",
                day: "numeric"
            };
            if (timeZone) {
                options.timeZone = timeZone;
            }

            return new Intl.DateTimeFormat(undefined, options).format(dateValue);
        } catch (error) {
            return dateValue.toLocaleDateString();
        }
    }

    function formatTime(dateValue, timeZone) {
        try {
            const options = {
                hour: "numeric",
                minute: "2-digit",
                second: "2-digit"
            };
            if (timeZone) {
                options.timeZone = timeZone;
            }

            return new Intl.DateTimeFormat(undefined, options).format(dateValue);
        } catch (error) {
            return dateValue.toLocaleTimeString();
        }
    }

    function refreshLiveClock(now) {
        const value = now || new Date();
        if (timeLabel) {
            timeLabel.textContent = formatTime(value, activeClockTimeZone);
        }

        if (dateLabel) {
            dateLabel.textContent = formatDateWithWeekday(value, activeClockTimeZone);
        }
    }

    function updateHud(payload) {
        const cityName = payload.locationQuery || payload.weather.locationName || payload.coordinates.displayName || "Unknown City";
        const cityDisplay = String(cityName).split(",")[0].trim();
        locationLabel.textContent = cityDisplay.toUpperCase();

        activeClockTimeZone = resolveClockTimeZone(payload);
        updateWeatherIcon(payload);
        refreshLiveClock(new Date());

        const resolvedTemperature = resolveTemperatureForHud(payload);
        const temperatureText = typeof resolvedTemperature.value === "number" && Number.isFinite(resolvedTemperature.value)
            ? `${Math.round(resolvedTemperature.value)}°${resolvedTemperature.unit}`
            : `--°${resolvedTemperature.unit}`;
        temperatureLabel.textContent = temperatureText;
        summaryLabel.textContent = payload.weather.shortForecast || payload.weather.detailedForecast || "Weather unavailable";

        poiLabel.textContent = "";

        const alerts = Array.isArray(payload.weather.alerts) ? payload.weather.alerts : [];
        const severeAlert = alerts.find((alert) =>
            alert && alert.severity && String(alert.severity).toLowerCase() !== "minor");
        alertsLabel.textContent = severeAlert
            ? `${severeAlert.eventName} (${severeAlert.severity})`
            : "";
    }

    async function applyPayload(payload) {
        if (!payload || !payload.scene || !payload.weather) {
            return;
        }

        activePayload = payload;
        random = createSeededRandom((payload.scene.seed || Date.now()) >>> 0);
        swayTargets.length = 0;
        weatherParticles.length = 0;
        billboardPanels.length = 0;
        waterSurfaces.length = 0;
        wetRoadSurfaces.length = 0;
        vehicleAgents.length = 0;
        clearDynamicTextures();
        clearGroup(baseGroup);
        clearGroup(cityGroup);
        clearGroup(cloudGroup);
        clearGroup(precipitationGroup);
        applyDefaultGlbOrientation(payload.glbOrientation);

        const paletteA = asHexColor(payload.scene.paletteA, "#b9dfff");
        const paletteB = asHexColor(payload.scene.paletteB, "#91c9ef");
        const paletteC = asHexColor(payload.scene.paletteC, "#f2c879");
        const wallpaperBackgroundColor = asHexColor(
            payload.wallpaperBackgroundColor,
            "#7aa7d8");
        const backgroundA = blendHexColor(wallpaperBackgroundColor, paletteA, 0.16);
        const backgroundB = blendHexColor(wallpaperBackgroundColor, paletteB, 0.26);
        const backgroundC = blendHexColor(wallpaperBackgroundColor, "#ffffff", 0.12);

        document.documentElement.style.setProperty("--bg-a", backgroundA);
        document.documentElement.style.setProperty("--bg-b", backgroundB);
        document.documentElement.style.setProperty("--bg-c", backgroundC);
        applyHudContrastTheme(backgroundA, backgroundB, backgroundC);
        applyHudTypography(payload.wallpaperTextStyle);
        setStatsOverlayVisibility(payload.showWallpaperStatsOverlay);

        const points = Array.isArray(payload.pointsOfInterest) ? payload.pointsOfInterest : [];
        const pointOfInterestImages = Array.isArray(payload.pointOfInterestImages) ? payload.pointOfInterestImages : [];
        const pointOfInterestMeshes = Array.isArray(payload.pointOfInterestMeshes) ? payload.pointOfInterestMeshes : [];
        const poiMeshMap = buildPoiMeshMap(pointOfInterestMeshes);
        const viewerSettings = parseMeshyViewerSettings(payload.meshyViewer);
        wallpaperModelEntries = buildWallpaperModelEntries(pointOfInterestMeshes);
        const previousRotationIntervalMs = wallpaperModelRotationIntervalMs;
        const previousRotationMinutes = wallpaperModelRotationMinutes;
        wallpaperModelRotationMinutes = viewerSettings.rotationMinutes;
        wallpaperModelRotationIntervalMs =
            wallpaperModelRotationMinutes > 0
                ? Math.max(1000, Math.round(wallpaperModelRotationMinutes * 60 * 1000))
                : 0;
        const rotationEnabled = wallpaperModelRotationIntervalMs > 0 && wallpaperModelEntries.length > 1;
        if (!rotationEnabled) {
            wallpaperModelNextRotateAtMs = 0;
        } else {
            const nowMs = performance.now();
            if (wallpaperModelNextRotateAtMs <= 0 || previousRotationIntervalMs !== wallpaperModelRotationIntervalMs) {
                wallpaperModelNextRotateAtMs = nowMs + wallpaperModelRotationIntervalMs;
            } else if (wallpaperModelNextRotateAtMs < nowMs) {
                wallpaperModelNextRotateAtMs = nowMs + wallpaperModelRotationIntervalMs;
            }
        }

        const expectedSeed = payload && payload.scene ? payload.scene.seed : 0;
        let selectedModelEntry = null;
        if (rotationEnabled && wallpaperModelRelativePath) {
            selectedModelEntry = findWallpaperModelEntry(
                wallpaperModelEntries,
                wallpaperModelRelativePath);
        }

        if (!selectedModelEntry) {
            selectedModelEntry = findWallpaperModelEntry(
                wallpaperModelEntries,
                viewerSettings.activeModelRelativePath || wallpaperModelRelativePath);
        }

        let viewerSelectedModelReady = false;
        if (selectedModelEntry) {
            viewerSelectedModelReady = await ensureMeshyCitySceneLoaded({
                localWebUrl: selectedModelEntry.url,
                localRelativePath: selectedModelEntry.relativePath,
                name: selectedModelEntry.name,
                displayLabel: selectedModelEntry.label
            }, expectedSeed, "viewer-selected", selectedModelEntry.relativePath);
            if (viewerSelectedModelReady) {
                wallpaperModelRelativePath = normalizeRelativePath(selectedModelEntry.relativePath);
            } else {
                postHostDebug(
                    `Viewer-selected wallpaper model failed to load. Falling back to automatic source selection: ${selectedModelEntry.label}`);
            }
        }

        const meshySceneEntry = resolveMeshyModelEntry(payload, points, pointOfInterestMeshes);
        const meshySceneReady = viewerSelectedModelReady
            ? true
            : (meshySceneEntry
                ? await ensureMeshyCitySceneLoaded(meshySceneEntry, expectedSeed, "meshy-city", meshySceneEntry.localRelativePath)
                : false);

        const defaultSceneReady = meshySceneReady ? false : await ensureDefaultCitySceneLoaded();
        const hasLocationSpecificMeshHints =
            !!meshySceneEntry ||
            points.some((point) => poiMeshMap.has(String(point || "").toLowerCase()));
        const shouldUseProceduralScene = !meshySceneReady && (!defaultSceneReady || hasLocationSpecificMeshHints);
        defaultCityGroup.visible = meshySceneReady || (!shouldUseProceduralScene && defaultSceneReady);

        postHostDebug(
            `Scene source decision: meshyReady=${meshySceneReady} defaultReady=${defaultSceneReady} ` +
            `defaultSource=${defaultCitySceneSource || "none"} meshHints=${hasLocationSpecificMeshHints} ` +
            `procedural=${shouldUseProceduralScene} selectedModel='${viewerSettings.activeModelRelativePath || "auto"}' ` +
            `rotateMinutes=${wallpaperModelRotationMinutes}`);
        if (rotationEnabled && previousRotationMinutes !== wallpaperModelRotationMinutes) {
            postHostDebug(
                `Wallpaper model rotation configured: every ${wallpaperModelRotationMinutes.toFixed(3)} minute(s).`);
        }

        if (shouldUseProceduralScene) {
            buildDioramaBase(payload.scene, paletteA, paletteB, paletteC, points);
            buildCityBuildings(paletteA, paletteB, paletteC);
            buildMicroVehicles();
            buildPoiLandmarks(points, pointOfInterestImages, pointOfInterestMeshes, paletteC);
            applyCartoonPassToCity();
        }

        buildClouds(payload.scene);

        const weatherText = `${payload.weather.shortForecast || ""} ${payload.weather.detailedForecast || ""}`;
        buildWeather(payload.scene, payload.weather, payload.coordinates, weatherText, shouldUseProceduralScene);
        updateLighting(payload.scene, payload);
        updateHud(payload);
        const sceneBaseLabel = meshySceneReady
            ? (defaultCitySceneSource || "meshy-city")
            : (shouldUseProceduralScene
                ? (hasLocationSpecificMeshHints ? "procedural-meshy" : "procedural-fallback")
                : (defaultCitySceneSource || "honolulu-glb"));
        setDebugLine(
            `Scene applied | ${payload.weather.locationName || payload.coordinates.displayName || "Unknown"} | ` +
            `base=${sceneBaseLabel} | accent=${payload.scene.accentEffect || "clear"} | ` +
            `poiMeshes=${pointOfInterestMeshes.length} | objects=${scene.children.length}`);
    }

    function rotateWallpaperModelIfDue(timestamp) {
        if (!activePayload || wallpaperModelRotationInFlight) {
            return;
        }

        if (wallpaperModelRotationIntervalMs <= 0 ||
            wallpaperModelEntries.length <= 1 ||
            wallpaperModelNextRotateAtMs <= 0 ||
            timestamp < wallpaperModelNextRotateAtMs) {
            return;
        }

        wallpaperModelNextRotateAtMs = timestamp + wallpaperModelRotationIntervalMs;

        const activeKey = normalizeRelativePath(wallpaperModelRelativePath);
        let currentIndex = -1;
        for (let index = 0; index < wallpaperModelEntries.length; index += 1) {
            if (normalizeRelativePath(wallpaperModelEntries[index].relativePath) === activeKey) {
                currentIndex = index;
                break;
            }
        }

        if (currentIndex < 0) {
            currentIndex = 0;
        }

        const nextEntry = wallpaperModelEntries[(currentIndex + 1) % wallpaperModelEntries.length];
        if (!nextEntry || !nextEntry.url) {
            return;
        }

        wallpaperModelRotationInFlight = true;
        const expectedSeed = activePayload && activePayload.scene ? activePayload.scene.seed : 0;
        ensureMeshyCitySceneLoaded({
            localWebUrl: nextEntry.url,
            localRelativePath: nextEntry.relativePath,
            name: nextEntry.name,
            displayLabel: nextEntry.label
        }, expectedSeed, "viewer-rotate", nextEntry.relativePath)
            .then((loaded) => {
                if (!loaded) {
                    postHostDebug(`Wallpaper model rotation skipped (load failed): ${nextEntry.label}`);
                    return;
                }

                wallpaperModelRelativePath = normalizeRelativePath(nextEntry.relativePath);
                postHostDebug(`Wallpaper model rotated to '${nextEntry.label}'.`);
            })
            .catch((error) => {
                postHostDebug(`Wallpaper model rotation error (${nextEntry.label}): ${String(error)}`);
            })
            .finally(() => {
                wallpaperModelRotationInFlight = false;
            });
    }

    function animate(timestamp) {
        requestAnimationFrame(animate);
        if (!lastFrameTimestamp) {
            lastFrameTimestamp = timestamp;
        }
        const deltaSeconds = Math.max(0.001, Math.min(0.05, (timestamp - lastFrameTimestamp) / 1000));
        lastFrameTimestamp = timestamp;

        const seconds = timestamp * 0.001;
        const windFactor = activePayload ? clamp01(activePayload.scene.windFactor, 0.35) : 0.35;
        frameCount += 1;

        if (activePayload && (timestamp - lastCelestialUpdate > 15000)) {
            updateLighting(activePayload.scene, activePayload);
            lastCelestialUpdate = timestamp;
        }

        rotateWallpaperModelIfDue(timestamp);

        if (timestamp - lastFpsTick >= 1000) {
            fps = Math.round((frameCount * 1000) / Math.max(1, (timestamp - lastFpsTick)));
            frameCount = 0;
            lastFpsTick = timestamp;
        }

        if (timestamp - lastClockRefresh >= 1000) {
            refreshLiveClock(new Date());
            lastClockRefresh = timestamp;
        }

        const cameraLift = Math.sin(seconds * 0.2 + cameraPulseOffset) * 0.45;
        camera.position.y = 20 + cameraLift;
        camera.lookAt(0, 1.4, 0);
        moonDisc.quaternion.copy(camera.quaternion);
        moonDisc.rotateZ(moonDiscSpin);

        worldRoot.rotation.y = Math.sin(seconds * 0.18) * 0.015;

        for (let index = 0; index < billboardPanels.length; index += 1) {
            billboardPanels[index].lookAt(camera.position);
        }

        for (let index = 0; index < cloudGroup.children.length; index += 1) {
            const cloud = cloudGroup.children[index];
            const drift = cloud.userData.drift || 0.4;
            const span = cloud.userData.span || 24;
            cloud.position.x += (0.0015 + windFactor * 0.004) * drift;
            if (cloud.position.x > span) {
                cloud.position.x = -span;
            }
        }

        for (let index = 0; index < waterSurfaces.length; index += 1) {
            const item = waterSurfaces[index];
            if (!item || !item.mesh) {
                continue;
            }

            item.mesh.position.y = item.baseY + Math.sin(seconds * 1.35 + item.phase) * 0.02;
            if (item.texture && item.texture.offset) {
                item.texture.offset.x = (seconds * 0.02) % 1;
                item.texture.offset.y = (seconds * 0.015) % 1;
            }
        }

        for (let index = 0; index < wetRoadSurfaces.length; index += 1) {
            const item = wetRoadSurfaces[index];
            if (!item || !item.mesh || !item.mesh.material) {
                continue;
            }

            const opacity = item.baseOpacity + Math.sin(seconds * 1.45 + item.phase) * 0.04;
            item.mesh.material.opacity = Math.max(0.14, Math.min(0.62, opacity));
            item.mesh.material.needsUpdate = true;
        }

        for (let index = 0; index < vehicleAgents.length; index += 1) {
            const vehicle = vehicleAgents[index];
            if (!vehicle || !vehicle.mesh) {
                continue;
            }

            if (vehicle.axis === "x") {
                vehicle.mesh.position.x += vehicle.speed * deltaSeconds;
                if (vehicle.mesh.position.x > vehicle.max) {
                    vehicle.mesh.position.x = vehicle.min;
                } else if (vehicle.mesh.position.x < vehicle.min) {
                    vehicle.mesh.position.x = vehicle.max;
                }
            } else {
                vehicle.mesh.position.z += vehicle.speed * deltaSeconds;
                if (vehicle.mesh.position.z > vehicle.max) {
                    vehicle.mesh.position.z = vehicle.min;
                } else if (vehicle.mesh.position.z < vehicle.min) {
                    vehicle.mesh.position.z = vehicle.max;
                }
            }
        }

        for (let index = 0; index < weatherParticles.length; index += 1) {
            const particle = weatherParticles[index];
            const speed = particle.speed * (0.010 + windFactor * 0.007);
            particle.mesh.position.y -= speed;
            particle.mesh.position.x += (windFactor - 0.5) * 0.011;

            if (particle.type === "snow") {
                particle.mesh.position.x += Math.sin(seconds * 1.8 + particle.wobble) * 0.006;
                particle.mesh.position.z += Math.cos(seconds * 1.2 + particle.wobble) * 0.006;
            }

            if (particle.mesh.position.y < -0.5) {
                particle.mesh.position.y = 8 + random() * 7;
                particle.mesh.position.x = (random() - 0.5) * 16;
                particle.mesh.position.z = (random() - 0.5) * 16;
            }
        }

        for (let index = 0; index < swayTargets.length; index += 1) {
            const target = swayTargets[index];
            target.mesh.rotation.z = Math.sin(seconds * 0.85 + target.phase) * target.amount * windFactor;
        }

        if (activePayload && activePayload.scene.accentEffect === "lightning") {
            if (seconds > nextLightningAt) {
                lightningFlashUntil = seconds + 0.10;
                nextLightningAt = seconds + 2.2 + random() * 4.8;
            }
        } else {
            lightningFlashUntil = 0;
            nextLightningAt = seconds + 9999;
        }

        if (seconds < lightningFlashUntil) {
            sunLight.intensity += 1.0;
            hemiLight.intensity += 0.4;
        }

        if (animatedAiBackgroundEnabled || wallpaperBackgroundMediaEnabled) {
            renderer.autoClear = false;
            renderer.setClearColor(wallpaperBackgroundFallbackColor, 1);
            renderer.clear();
            if (wallpaperBackgroundMediaEnabled && wallpaperBackgroundMediaScene && wallpaperBackgroundMediaCamera) {
                renderer.render(wallpaperBackgroundMediaScene, wallpaperBackgroundMediaCamera);
            }
            if (animatedAiBackgroundEnabled) {
                renderAnimatedAiBackground(timestamp);
                renderer.render(animatedAiBackgroundScene, animatedAiBackgroundCamera);
            }
            renderer.clearDepth();
            renderer.render(scene, camera);
            renderer.autoClear = true;
        } else {
            renderer.autoClear = true;
            renderer.render(scene, camera);
        }

        if (timestamp - lastDebugPush > 4000) {
            const activeWeather = activePayload && activePayload.weather
                ? activePayload.weather.shortForecast || activePayload.weather.detailedForecast || "n/a"
                : "n/a";
            const line = `Frames ${fps} fps | weatherParticles=${weatherParticles.length} | clouds=${cloudGroup.children.length} | vehicles=${vehicleAgents.length} | celestial='${activeCelestialLabel}' | weather='${activeWeather}'`;
            debugLabel.textContent = line;
            postHostDebug(line);
            lastDebugPush = timestamp;
        }
    }

    window.renderFromHost = function (payload) {
        try {
            const normalized = typeof payload === "string" ? JSON.parse(payload) : payload;
            Promise.resolve(applyPayload(normalized)).catch((error) => {
                summaryLabel.textContent = "Failed to apply renderer payload.";
                poiLabel.textContent = "";
                alertsLabel.textContent = String(error);
                setDebugLine(`Payload error: ${String(error)}`);
            });
        } catch (error) {
            summaryLabel.textContent = "Failed to apply renderer payload.";
            poiLabel.textContent = "";
            alertsLabel.textContent = String(error);
            setDebugLine(`Payload error: ${String(error)}`);
        }
    };

    resizeCamera();
    window.addEventListener("resize", resizeCamera);
    window.addEventListener("pointermove", (event) => {
        if (!animatedAiBackgroundEnabled || !event) {
            return;
        }

        updateAnimatedAiPointerFromClient(event.clientX, event.clientY);
    }, { passive: true });
    renderer.domElement.addEventListener("pointermove", (event) => {
        if (!animatedAiBackgroundEnabled || !event) {
            return;
        }

        updateAnimatedAiPointerFromClient(event.clientX, event.clientY);
    }, { passive: true });
    window.addEventListener("touchmove", (event) => {
        if (!animatedAiBackgroundEnabled || !event || !event.touches || event.touches.length === 0) {
            return;
        }

        const touch = event.touches[0];
        updateAnimatedAiPointerFromClient(touch.clientX, touch.clientY);
    }, { passive: true });
    window.addEventListener("error", (event) => {
        const detail = event && event.message ? event.message : "Unknown renderer error";
        setDebugLine(`Window error: ${detail}`);
    });
    window.addEventListener("unhandledrejection", (event) => {
        const reason = event && event.reason ? String(event.reason) : "Unknown promise rejection";
        setDebugLine(`Unhandled rejection: ${reason}`);
    });
    if (locationLabel) {
        locationLabel.textContent = "Application is initializing...";
    }
    if (summaryLabel) {
        summaryLabel.textContent = "Loading configured location and live weather...";
    }
    if (dateLabel) {
        dateLabel.textContent = "Initializing date...";
    }
    if (temperatureLabel) {
        temperatureLabel.textContent = "--";
    }
    if (poiLabel) {
        poiLabel.textContent = "";
    }
    if (alertsLabel) {
        alertsLabel.textContent = "";
    }
    setDebugLine("Renderer initialized. Waiting for first host payload.");
    setStatsOverlayVisibility(showWallpaperStatsOverlay);
    refreshLiveClock(new Date());
    requestAnimationFrame(animate);
})();

