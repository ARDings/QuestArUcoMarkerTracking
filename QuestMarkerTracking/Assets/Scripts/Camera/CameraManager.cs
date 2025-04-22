using UnityEngine;
using TryAR.Camera;
using System;
using System.Collections;
using PassthroughCameraSamples;
using System.Linq;

public class CameraManager : MonoBehaviour
{
    [SerializeField] private PassthroughCameraEye _eye = PassthroughCameraEye.Left;
    [SerializeField] private Material _previewMaterial;
    [SerializeField] private bool _autoSwitchOnStart = false;
    [SerializeField] private float _autoSwitchInterval = 5f;
    [SerializeField] private PassthroughCameraPermissions CameraPermissions;

    private Texture2D _previewTexture;
    private bool _isRunning;
    private NativeCameraPlugin.CameraConfig _activeConfig;
    private Vector2Int _lastTextureDimensions;
    private NativeCameraPlugin cameraPlugin;
    private bool isAutoSwitchEnabled = false;
    private bool m_hasPermission = false;

    // Speichere den letzten erfolgreichen Frame
    private byte[] _lastFrameData;
    private int _lastFrameWidth;
    private int _lastFrameHeight;

    private int _frameCount = 0;  // Zum Tracking der empfangenen Frames

    private static int _instanceCount = 0;
    private int _instanceId;

    private void Awake()
    {
        _instanceId = _instanceCount++;
        Debug.Log($"[Camera2Helper] CameraManager {_instanceId} starting for {_eye} eye...");
        
        // Verzögere den Start der zweiten Kamera
        if (_instanceId > 0)
        {
            StartCoroutine(DelayedStart());
        }
        else
        {
#if UNITY_ANDROID
            CameraPermissions.AskCameraPermissions();
#endif
        }
    }

    private IEnumerator DelayedStart()
    {
        // Warte bis die erste Kamera initialisiert ist
        yield return new WaitForSeconds(3f);
        
#if UNITY_ANDROID
        CameraPermissions.AskCameraPermissions();
#endif
    }

    private void OnEnable()
    {
        if (!PassthroughCameraUtils.IsSupported)
        {
            Debug.LogError("[Camera2Helper] Passthrough Camera functionality is not supported");
            enabled = false;
            return;
        }

        StartCoroutine(InitializeWhenPermissionsGranted());
    }

    private IEnumerator InitializeWhenPermissionsGranted()
    {
        while (PassthroughCameraPermissions.HasCameraPermission != true)
        {
            yield return null;
        }

        m_hasPermission = true;
        Debug.Log("[Camera2Helper] Camera permissions granted, waiting 2 seconds before initializing...");
        
        // Warte 2 Sekunden nach den Permissions
        yield return new WaitForSeconds(2f);

        // Warte nochmal kurz vor der Kamera-Initialisierung
        yield return new WaitForSeconds(0.5f);

        InitializeCamera();
    }

    private void InitializeCamera()
    {
        try 
        {
            // Standard-Auflösung für den Anfang
            int width = 1280;
            int height = 960;
            
            // Erstelle eine Textur für die Vorschau
            _previewTexture = new Texture2D(width, height, TextureFormat.RGB24, false, true);
            _previewTexture.filterMode = FilterMode.Bilinear;
            Debug.Log($"[Camera2Helper] Creating texture with format: {_previewTexture.format}");
            
            // Setze die Textur im Material
            if (_previewMaterial != null)
            {
                _previewMaterial.mainTexture = _previewTexture;
            }
            
            // Initialisiere die Kamera mit der gewünschten Auflösung und Kamera (links/rechts)
            PassthroughCameraEye eye = _eye;
            NativeCameraPlugin.Initialize(width, height, eye);
            
            // Registriere den Callback für Bilddaten
            NativeCameraPlugin.SetImageCallback(OnImageAvailable);
            
            // Starte die Kamera
            StartCamera();
            
            // Erstelle eine Instanz für Methoden wie SwitchCamera
            cameraPlugin = new NativeCameraPlugin();
            
            // Entferne den automatischen Kamerawechsel
            // if (_autoSwitchOnStart)
            // {
            //     StartAutoSwitch(_autoSwitchInterval);
            // }
            
            // Stattdessen: Liste alle verfügbaren Kameras im Log auf
            NativeCameraPlugin.LogAllCameras();
        }
        catch (Exception e)
        {
            Debug.LogError($"[Camera2Helper] Error initializing camera: {e.Message}\n{e.StackTrace}");
        }
    }

    private void StartCamera()
    {
        Debug.Log("[Camera2Helper] Starting camera capture...");
        NativeCameraPlugin.StartCamera();
        _isRunning = true;
    }

    private void OnDestroy()
    {
        _isRunning = false;
        NativeCameraPlugin.Release();
        
        if (_previewTexture != null)
        {
            Destroy(_previewTexture);
        }
    }

    private void Update()
    {
        if (!_isRunning || _previewTexture == null) return;

        // Wenn wir Kameradaten haben, aktualisiere die Texture
        if (_lastFrameData != null && _lastFrameData.Length > 0 && _previewTexture != null)
        {
            Debug.Log($"[Camera2Helper] Updating texture with data length: {_lastFrameData.Length}");
            
            // Prüfe RGB-Daten für Debug
            bool hasNonZeroRG = false;
            for (int i = 0; i < 100; i+=3) {
                if (_lastFrameData[i] > 10 || _lastFrameData[i+1] > 10) { // R oder G > 10
                    hasNonZeroRG = true;
                    break;
                }
            }
            Debug.Log($"[Camera2Helper] RGB-Werte: R/G Werte vorhanden: {hasNonZeroRG}");
            
            // Prüfe wie die Texture-Daten geladen werden
            // Direktes Laden der RGB-Daten in die Texture
            _previewTexture.LoadRawTextureData(_lastFrameData);
            _previewTexture.Apply();
            
            // Texture dem Material zuweisen
            if (_previewMaterial != null)
            {
                _previewMaterial.mainTexture = _previewTexture;
            }

            Debug.Log($"[Camera2Helper] Texture: Format={_previewTexture.format}, " +
                       $"Dimension={_previewTexture.width}x{_previewTexture.height}, " +
                       $"Mipmaps={_previewTexture.mipmapCount}");
            
            // Nach Apply() prüfen wir einen einzelnen Pixel
            Color color = _previewTexture.GetPixel(
                _previewTexture.width/2, 
                _previewTexture.height/2);
            Debug.Log($"[Camera2Helper] Mittlerer Pixel nach Apply: " +
                      $"R={color.r:F2}, G={color.g:F2}, B={color.b:F2}");
        }

        // Dimensions-Tracking wie zuvor
        if (_previewMaterial != null && _previewMaterial.mainTexture != null)
        {
            var texture = _previewMaterial.mainTexture as Texture2D;
            if (texture != null)
            {
                var currentDimensions = new Vector2Int(texture.width, texture.height);
                if (currentDimensions != _lastTextureDimensions)
                {
                    Debug.Log($"[Camera2Helper] Texture dimensions: {texture.width}x{texture.height}, format: {texture.format}");
                    _lastTextureDimensions = currentDimensions;
                }
            }
        }

        // Alle 5 Sekunden ein Testmuster mit Farbverläufen erzeugen
        // Kommentiere diesen Block aus, wenn du die echten Kamera-Frames sehen möchtest
        /*
        if (Time.time % 5 < 0.1f && _previewTexture != null) {
            Debug.Log("[Camera2Helper] Erzeuge UV-Testmuster");
            Color32[] testColors = new Color32[_previewTexture.width * _previewTexture.height];
            
            for (int y = 0; y < _previewTexture.height; y++) {
                for (int x = 0; x < _previewTexture.width; x++) {
                    // U variiert horizontal, V variiert vertikal
                    byte u = (byte)(128 + (x * 127 / _previewTexture.width - 64));
                    byte v = (byte)(128 + (y * 127 / _previewTexture.height - 64));
                    
                    // Y konstant bei mittlerer Helligkeit
                    byte yVal = 128;
                    
                    // BT.601 Formel für RGB
                    int r = Mathf.Clamp(yVal + (140 * (v - 128)) / 100, 0, 255);
                    int g = Mathf.Clamp(yVal - (34 * (u - 128)) / 100 - (71 * (v - 128)) / 100, 0, 255);
                    int b = Mathf.Clamp(yVal + (177 * (u - 128)) / 100, 0, 255);
                    
                    testColors[y * _previewTexture.width + x] = new Color32((byte)r, (byte)g, (byte)b, 255);
                }
            }
            
            _previewTexture.SetPixels32(testColors);
            _previewTexture.Apply();
            Debug.Log("[Camera2Helper] UV-Testmuster angewandt");
        }
        */
    }

    private void RequestNewFrame()
    {
        Debug.Log("[Camera2Helper] Requesting new frame...");
        
        // NICHT die Kamera neu starten oder den Callback neu registrieren!
        // Die Kamera läuft bereits kontinuierlich und sendet Frames
        
        // Stattdessen nur prüfen, ob die Kamera läuft
        if (!_isRunning)
        {
            // Nur wenn die Kamera nicht läuft, starten wir sie neu
            NativeCameraPlugin.SetImageCallback(OnImageAvailable);
            NativeCameraPlugin.StartCamera();
            _isRunning = true;
        }
        else
        {
            Debug.Log("[Camera2Helper] Kamera läuft bereits, keine Neustart notwendig");
        }
    }

    public void SwitchCamera()
    {
        if (!_isRunning) return;
        
        // Stoppe die aktuelle Kamera
        NativeCameraPlugin.StopCamera();
        
        // Wechsle die Kamera-Einstellung
        _eye = _eye == PassthroughCameraEye.Left ? PassthroughCameraEye.Right : PassthroughCameraEye.Left;
        
        // Initialize with new camera - Konvertiere bool zu PassthroughCameraEye
        PassthroughCameraEye eye = _eye;
        NativeCameraPlugin.Initialize(_previewTexture.width, _previewTexture.height, eye);
        StartCamera();
        
        Debug.Log($"[Camera2Helper] Camera switch complete. Now using {_eye} camera");
    }

    private void OnDisable()
    {
        // Entferne den Aufruf von StopAutoSwitch
        // if (isAutoSwitchEnabled)
        // {
        //     StopAutoSwitch();
        // }
        
        if (_isRunning)
        {
            NativeCameraPlugin.StopCamera();
            _isRunning = false;
        }
    }

    private void OnImageAvailable(byte[] data, int width, int height)
    {
        if (!_isRunning) return;
        
        if (data != null && data.Length > 0)
        {
            _frameCount++;
            if (_frameCount % 30 == 0) // Log every 30 frames
            {
                Debug.Log($"[Camera2Helper] {_eye}: Frame {_frameCount}, size: {width}x{height}");
            }
            
            // Speichere den letzten erfolgreichen Frame
            _lastFrameData = data;
            _lastFrameWidth = width;
            _lastFrameHeight = height;
            
            // Aktualisiere die Textur
            try 
            {
                if (_previewTexture == null || _previewTexture.width != width || _previewTexture.height != height)
                {
                    CreateTexture(width, height);
                }
                
                _previewTexture.LoadRawTextureData(data);
                _previewTexture.Apply();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Error applying frame: {e.Message}");
            }
        }
        else
        {
            Debug.LogWarning("[Camera2Helper] Received empty frame");
        }
    }

    private void CreateTexture(int width, int height)
    {
        if (_previewTexture == null || _previewTexture.width != width || _previewTexture.height != height)
        {
            // Wichtig: Linear und kein sRGB für YUV Daten
            _previewTexture = new Texture2D(width, height, TextureFormat.RGB24, false, true);
            _previewTexture.filterMode = FilterMode.Bilinear;
            _previewMaterial.mainTexture = _previewTexture;
            Debug.Log($"[Camera2Helper] Creating texture with format: {_previewTexture.format}");
        }
    }
} 