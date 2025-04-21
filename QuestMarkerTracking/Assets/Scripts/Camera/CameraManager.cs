using UnityEngine;
using TryAR.Camera;
using System;

public class CameraManager : MonoBehaviour
{
    [SerializeField] private bool _useFrontCamera = false;
    [SerializeField] private Material _previewMaterial;
    [SerializeField] private bool _autoSwitchOnStart = false;
    [SerializeField] private float _autoSwitchInterval = 5f;

    private Texture2D _previewTexture;
    private bool _isRunning;
    private NativeCameraPlugin.CameraConfig _activeConfig;
    private Vector2Int _lastTextureDimensions;
    private NativeCameraPlugin cameraPlugin;
    private bool isAutoSwitchEnabled = false;

    private void Start()
    {
        Debug.Log("[Camera2Helper] CameraManager starting...");
        
        cameraPlugin = new NativeCameraPlugin();
        
        // Get available camera configurations
        var configs = NativeCameraPlugin.GetCameraConfigurations();
        if (configs == null || configs.Count == 0)
        {
            Debug.LogError("[Camera2Helper] No camera configurations found! Check camera permissions.");
            return;
        }

        // Select camera based on front/back preference
        _activeConfig = configs.Find(c => c.isLeftCamera == !_useFrontCamera);
        if (_activeConfig == null)
        {
            Debug.LogError("[Camera2Helper] Could not find requested camera configuration");
            return;
        }

        // Create preview texture with camera resolution
        _previewTexture = new Texture2D(_activeConfig.width, _activeConfig.height, TextureFormat.RGB24, false);
        Debug.Log($"[Camera2Helper] Created preview texture: {_previewTexture.width}x{_previewTexture.height}");
        
        if (_previewMaterial != null)
        {
            _previewMaterial.mainTexture = _previewTexture;
        }
        else
        {
            Debug.LogError("[Camera2Helper] Preview material is null! Please assign a material in the inspector.");
            return;
        }

        // Initialize and start camera
        NativeCameraPlugin.Initialize(_activeConfig.width, _activeConfig.height, _useFrontCamera);
        StartCamera();
        
        // Start auto-switching if enabled
        if (_autoSwitchOnStart)
        {
            StartAutoSwitch(_autoSwitchInterval);
        }
    }

    private void StartCamera()
    {
        NativeCameraPlugin.StartCamera((data, width, height) =>
        {
            if (!_isRunning) return;
            
            if (data != null && data.Length > 0)
            {
                // Use MainThreadDispatcher to update texture on main thread
                MainThreadDispatcher.RunOnMainThread(() => {
                    _previewTexture.LoadRawTextureData(data);
                    _previewTexture.Apply();
                });
            }
        });

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
            cameraPlugin.SwitchCamera();
            Debug.Log("[CameraManager] Manual camera switch triggered");
        }
    }

    void OnDisable()
    {
        if (isAutoSwitchEnabled)
        {
            StopAutoSwitch();
        }
    }
} 