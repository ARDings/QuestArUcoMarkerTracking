using UnityEngine;
using TryAR.Camera;
using System;
using System.Collections;
using PassthroughCameraSamples;
using System.Linq;

public class CameraManager : MonoBehaviour
{
    [SerializeField] private bool _useFrontCamera = false;
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

    private void Awake()
    {
        Debug.Log("[Camera2Helper] CameraManager starting...");
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

            _previewTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            Debug.Log($"[Camera2Helper] Created preview texture: {_previewTexture.width}x{_previewTexture.height}");
            
            if (_previewMaterial != null)
            {
                _previewMaterial.mainTexture = _previewTexture;
                
                // Initialisiere die Kamera mit Standardwerten
                NativeCameraPlugin.Initialize(width, height, _useFrontCamera);
                StartCamera();
                
                if (_autoSwitchOnStart)
                {
                    StartAutoSwitch(_autoSwitchInterval);
                }
            }
            else
            {
                Debug.LogError("[Camera2Helper] Preview material is missing!");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Camera2Helper] Error during initialization: {e.Message}\n{e.StackTrace}");
        }
    }

    private void StartCamera()
    {
        Debug.Log("[Camera2Helper] Starting camera capture...");
        NativeCameraPlugin.StartCamera((data, width, height) =>
        {
            if (!_isRunning) return;
            
            if (data != null && data.Length > 0)
            {
                // Speichere den Frame
                _lastFrameData = data;
                _lastFrameWidth = width;
                _lastFrameHeight = height;
                
                Debug.Log($"[Camera2Helper] Frame received: {width}x{height}, buffer size: {data.Length} bytes");
                MainThreadDispatcher.RunOnMainThread(() => {
                    try 
                    {
                        _previewTexture.LoadRawTextureData(data);
                        _previewTexture.Apply();
                        Debug.Log($"[Camera2Helper] Frame applied to texture: {_previewTexture.width}x{_previewTexture.height}, format: {_previewTexture.format}");
                        
                        // Prüfe die ersten paar Bytes des Frames
                        string dataPreview = BitConverter.ToString(data.Take(16).ToArray());
                        Debug.Log($"[Camera2Helper] Frame data preview (first 16 bytes): {dataPreview}");
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[Camera2Helper] Error applying frame to texture: {e.Message}\n{e.StackTrace}");
                        
                        // Bei einem Fehler versuche den letzten erfolgreichen Frame erneut anzuzeigen
                        if (_lastFrameData != null)
                        {
                            try
                            {
                                Debug.Log("[Camera2Helper] Attempting to display last successful frame");
                                _previewTexture.LoadRawTextureData(_lastFrameData);
                                _previewTexture.Apply();
                            }
                            catch (Exception e2)
                            {
                                Debug.LogError($"[Camera2Helper] Failed to apply last frame: {e2.Message}");
                            }
                        }
                    }
                });
            }
            else
            {
                Debug.LogWarning("[Camera2Helper] Received empty or null frame data");
                // Bei leerem Frame zeige den letzten erfolgreichen Frame
                if (_lastFrameData != null)
                {
                    MainThreadDispatcher.RunOnMainThread(() => {
                        try
                        {
                            _previewTexture.LoadRawTextureData(_lastFrameData);
                            _previewTexture.Apply();
                            Debug.Log("[Camera2Helper] Displayed last successful frame");
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[Camera2Helper] Failed to apply last frame: {e.Message}");
                        }
                    });
                }
            }
        });

        _isRunning = true;
        Debug.Log("[Camera2Helper] Camera capture started");
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
    }

    public void StartAutoSwitch(float intervalSeconds = 5f)
    {
        if (cameraPlugin != null)
        {
            isAutoSwitchEnabled = true;
            cameraPlugin.StartPeriodicCameraSwitch((long)(intervalSeconds * 1000));
            Debug.Log($"[CameraManager] Started auto camera switch every {intervalSeconds} seconds");
        }
    }

    public void StopAutoSwitch()
    {
        if (cameraPlugin != null)
        {
            isAutoSwitchEnabled = false;
            cameraPlugin.StopPeriodicCameraSwitch();
            Debug.Log("[CameraManager] Stopped auto camera switch");
        }
    }

    public void SwitchCamera()
    {
        if (cameraPlugin != null)
        {
            _useFrontCamera = !_useFrontCamera; // Toggle camera selection
            Debug.Log($"[Camera2Helper] Switching to {(_useFrontCamera ? "left" : "right")} camera");
            
            // Stop current camera
            if (_isRunning)
            {
                NativeCameraPlugin.StopCamera();
                _isRunning = false;
            }

            // Initialize with new camera
            NativeCameraPlugin.Initialize(_previewTexture.width, _previewTexture.height, _useFrontCamera);
            StartCamera();
            
            Debug.Log($"[Camera2Helper] Camera switch complete. Now using {(_useFrontCamera ? "left" : "right")} camera");
        }
    }

    private void OnDisable()
    {
        if (isAutoSwitchEnabled)
        {
            StopAutoSwitch();
        }
        if (_isRunning)
        {
            NativeCameraPlugin.StopCamera();
            _isRunning = false;
        }
    }
} 