using UnityEngine;
using UnityEngine.UI;
using Uralstech.UXR.QuestCamera;
using System;  // Für Exception
using System.Collections.Generic;
using System.Linq;

public class SimpleCameraPreview : MonoBehaviour
{
    [Tooltip("Use both cameras instead of just left camera")]
    [SerializeField] private bool _useBothCameras = false;

    [Tooltip("Enable preview display (set to false if you only need camera data without UI)")]
    [SerializeField] private bool _enablePreviewDisplay = true;

    [Tooltip("Preview to show the left camera feed")]
    [SerializeField] private RawImage _leftCameraPreview;

    [Tooltip("Preview to show the right camera feed (only used if useBothCameras is true)")]
    [SerializeField] private RawImage _rightCameraPreview;

    // Füge öffentliche Eigenschaften hinzu, um Zugriff auf die Texturen zu ermöglichen
    public RenderTexture LeftCameraTexture => _leftCaptureSession?.TextureConverter?.FrameRenderTexture;
    public RenderTexture RightCameraTexture => _rightCaptureSession?.TextureConverter?.FrameRenderTexture;
    
    // Füge eine Eigenschaft hinzu, um zu prüfen ob die Kameras bereit sind
    public bool AreCamerasReady => _leftCaptureSession?.TextureConverter?.FrameRenderTexture != null;

    // Füge Resolution-Property hinzu
    public Vector2Int Resolution => 
        _leftCaptureSession?.TextureConverter?.FrameRenderTexture != null 
            ? new Vector2Int(
                _leftCaptureSession.TextureConverter.FrameRenderTexture.width,
                _leftCaptureSession.TextureConverter.FrameRenderTexture.height)
            : Vector2Int.zero;

    private CameraInfo _leftCameraInfo;
    private CameraDevice _leftCameraDevice;
    private CaptureSessionObject<ContinuousCaptureSession> _leftCaptureSession;

    private CameraInfo _rightCameraInfo;
    private CameraDevice _rightCameraDevice;
    private CaptureSessionObject<ContinuousCaptureSession> _rightCaptureSession;

    private float _lastLogTime = 0;
    private int _frameCount = 0;

    // Neue Struktur für die Timestamps
    public struct FrameTimestamps
    {
        public long SensorTimestampNs;  // Camera sensor timestamp
        public long SystemTimestampNs;  // Android system timestamp
        public long UnixTimestampMs;    // Unix timestamp
    }

    private FrameTimestamps _currentFrameTimestamps;
    public FrameTimestamps CurrentFrameTimestamps => _currentFrameTimestamps;

    // Struktur für einen kompletten Frame mit allen Daten
    public struct CameraFrame
    {
        public RenderTexture Texture;
        public FrameTimestamps Timestamps;
        public bool IsValid => Texture != null;
    }

    // Neue Methode, die Textur und Timestamps zusammen zurückgibt
    public CameraFrame GetCurrentFrame()
    {
        var frame = new CameraFrame
        {
            Texture = LeftCameraTexture,
            Timestamps = CurrentFrameTimestamps
        };
        //erstelle eine vergleichbare unity Zeit, damit wir sehen, ob unsere timelines zusammenpassen
 
        Debug.Log($"[Camera Frame] GetCurrentFrame called - " +
                  $"\n  Texture: {(frame.Texture != null ? $"{frame.Texture.width}x{frame.Texture.height}" : "null")}" +
                  $"\n  Sensor Time: {frame.Timestamps.SensorTimestampNs}ns" +
                  $"\n  System Time: {frame.Timestamps.SystemTimestampNs}ns" +
                  $"\n  Unix Time: {frame.Timestamps.UnixTimestampMs}ms");
                  
        return frame;
    }

    // Add this event for timestamp notifications
    public event Action<FrameTimestamps> OnFrameTimestampsUpdated;

    // Add a field to track if we have a new frame
    private bool _hasNewFrame = false;

    // Neue Felder für Kamera-FPS Tracking
    private float _lastCameraFrameTime = 0;
    private int _cameraFrameCount = 0;
    private float _lastCameraFpsLogTime = 0;
    private Queue<float> _frameIntervals = new Queue<float>();
    private const int MAX_INTERVAL_SAMPLES = 10;

    private const float TARGET_FPS = 5f;
    private const float EXPECTED_FRAME_INTERVAL_MS = 1000f / TARGET_FPS; // 200ms bei 5 FPS

    [Serializable]
    private class CameraPoseData
    {
        public float[] rotation;
        public float[] position;
    }

    [Serializable]
    private class FrameData
    {
        public long sensorTs;
        public long systemTs;
        public long unixTs;
        public float[] rotation;
        public float[] position;
    }

    protected void Start()
    {
        // Deaktiviere UI-Elemente, wenn Preview nicht benötigt wird
        if (!_enablePreviewDisplay)
        {
            if (_leftCameraPreview != null) _leftCameraPreview.gameObject.SetActive(false);
            if (_rightCameraPreview != null) _rightCameraPreview.gameObject.SetActive(false);
        }
        else
        {
            // Prüfe, ob UI-Elemente zugewiesen sind, wenn Preview aktiviert ist
            if (_leftCameraPreview == null)
            {
                Debug.LogWarning("Left camera preview RawImage is not assigned but preview display is enabled.");
            }
            if (_useBothCameras && _rightCameraPreview == null)
            {
                Debug.LogWarning("Right camera preview RawImage is not assigned but preview display is enabled.");
            }
        }

        // Check camera permission
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UCameraManager.HeadsetCameraPermission))
        {
            InitializeCameras();
        }
        else
        {
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => InitializeCameras();
            UnityEngine.Android.Permission.RequestUserPermission(UCameraManager.HeadsetCameraPermission, callbacks);
            Debug.Log("Camera permission requested.");
        }
    }

    private async void InitializeCameras()
    {
        // Get the left eye camera
        _leftCameraInfo = UCameraManager.Instance.GetCamera(CameraInfo.CameraEye.Left);
        Debug.Log($"Got left camera info: {_leftCameraInfo}");

        if (_leftCameraInfo == null)
        {
            Debug.LogError("Failed to get left camera info.");
            return;
        }

        _leftCameraDevice = UCameraManager.Instance.OpenCamera(_leftCameraInfo);
        var state = await _leftCameraDevice.WaitForInitializationAsync();
        if (state != NativeWrapperState.Opened)
        {
            Debug.LogError("Failed to open left camera.");
            _leftCameraDevice.Destroy();
            _leftCameraDevice = null;
            return;
        }
        Debug.Log("Left camera opened.");

        // Wähle eine niedrigere Auflösung für bessere Performance
        var leftSupportedResolutions = _leftCameraInfo.SupportedResolutions;
        var leftSelectedResolution = leftSupportedResolutions.Length > 2 ? 
                                    leftSupportedResolutions[leftSupportedResolutions.Length - 3] : 
                                    leftSupportedResolutions[0];
        
        Debug.Log($"Selected left camera resolution: {leftSelectedResolution.width}x{leftSelectedResolution.height}");
        
        _leftCaptureSession = _leftCameraDevice.CreateContinuousCaptureSession(leftSelectedResolution);
        
        // Registriere den Timestamp-Handler, wenn die Session initialisiert ist
        _leftCaptureSession.CaptureSession.OnSessionRequestSet.AddListener(() => {
            Debug.Log("[SimpleCameraPreview] Session initialized, subscribing to timestamps");
            _leftCaptureSession.CaptureSession.OnFrameTimestamps += OnFrameTimestamps;
            Debug.Log("[SimpleCameraPreview] Successfully subscribed to timestamps");
        });

        state = await _leftCaptureSession.CaptureSession.WaitForInitializationAsync();
        if (state != NativeWrapperState.Opened)
        {
            Debug.LogError("Failed to open left capture session.");
            _leftCaptureSession.Destroy();
            _leftCameraDevice.Destroy();
            (_leftCameraDevice, _leftCaptureSession) = (null, null);
            return;
        }
        
        // Log die tatsächliche Auflösung der RenderTexture
        if (_leftCaptureSession?.TextureConverter?.FrameRenderTexture != null)
        {
            Debug.Log($"Camera2: Actual texture resolution: {_leftCaptureSession.TextureConverter.FrameRenderTexture.width}x{_leftCaptureSession.TextureConverter.FrameRenderTexture.height}");
            Debug.Log($"Camera2: Texture format: {_leftCaptureSession.TextureConverter.FrameRenderTexture.format}");
        }
        
        // Nur die Textur zuweisen, wenn Preview aktiviert ist und UI-Element existiert
        if (_enablePreviewDisplay && _leftCameraPreview != null)
        {
            _leftCameraPreview.texture = _leftCaptureSession.TextureConverter.FrameRenderTexture;
        }
        
        Debug.Log("Left capture session opened.");

        // Start right camera if enabled
        if (_useBothCameras && _rightCameraInfo != null)
        {
            _rightCameraDevice = UCameraManager.Instance.OpenCamera(_rightCameraInfo);
            state = await _rightCameraDevice.WaitForInitializationAsync();
            if (state != NativeWrapperState.Opened)
            {
                Debug.LogError("Failed to open right camera.");
                _rightCameraDevice.Destroy();
                _rightCameraDevice = null;
                return;
            }
            Debug.Log("Right camera opened.");

            // Verwende die gleiche niedrigere Auflösung wie für die linke Kamera
            var rightSupportedResolutions = _rightCameraInfo.SupportedResolutions;
            var rightSelectedResolution = rightSupportedResolutions.Length > 2 ? 
                                        rightSupportedResolutions[rightSupportedResolutions.Length - 3] : 
                                        rightSupportedResolutions[0];
            
            Debug.Log($"Selected right camera resolution: {rightSelectedResolution.width}x{rightSelectedResolution.height}");
            
            _rightCaptureSession = _rightCameraDevice.CreateContinuousCaptureSession(rightSelectedResolution);
            state = await _rightCaptureSession.CaptureSession.WaitForInitializationAsync();
            if (state != NativeWrapperState.Opened)
            {
                Debug.LogError("Failed to open right capture session.");
                _rightCaptureSession.Destroy();
                _rightCameraDevice.Destroy();
                (_rightCameraDevice, _rightCaptureSession) = (null, null);
                return;
            }
            
            // Nur die Textur zuweisen, wenn Preview aktiviert ist und UI-Element existiert
            if (_enablePreviewDisplay && _rightCameraPreview != null)
            {
                _rightCameraPreview.texture = _rightCaptureSession.TextureConverter.FrameRenderTexture;
            }
            
            Debug.Log("Right capture session opened.");
        }

        if (_leftCaptureSession != null)
        {
            _leftCaptureSession.CaptureSession.OnFrameTimestamps += OnFrameTimestamps;
        }

        if (_rightCaptureSession != null)
        {
            _rightCaptureSession.CaptureSession.OnFrameTimestamps += OnFrameTimestamps;
        }
    }

    protected void OnDestroy()
    {
        // Unsubscribe von Events
        if (_leftCaptureSession?.CaptureSession != null)
        {
            _leftCaptureSession.CaptureSession.OnFrameTimestamps -= OnFrameTimestamps;
        }

        // Cleanup left camera
        if (_leftCaptureSession != null)
        {
            _leftCaptureSession.Destroy();
            _leftCaptureSession = null;
        }
        if (_leftCameraDevice != null)
        {
            _leftCameraDevice.Destroy();
            _leftCameraDevice = null;
        }

        // Cleanup right camera
        if (_rightCaptureSession != null)
        {
            _rightCaptureSession.Destroy();
            _rightCaptureSession = null;
        }
        if (_rightCameraDevice != null)
        {
            _rightCameraDevice.Destroy();
            _rightCameraDevice = null;
        }
    }

    private void Update()
    {
        if (!AreCamerasReady) return;
        
        _frameCount++;
        
        // Log FPS alle 5 Sekunden
        if (Time.time - _lastLogTime > 5f)
        {
            float fps = _frameCount / (Time.time - _lastLogTime);
            Debug.Log($"Camera2: Current FPS: {fps:F1}");
            
            // Log aktuelle Auflösung erneut zur Überprüfung
            if (_leftCaptureSession?.TextureConverter?.FrameRenderTexture != null)
            {
                Debug.Log($"Camera2: Current texture resolution: {_leftCaptureSession.TextureConverter.FrameRenderTexture.width}x{_leftCaptureSession.TextureConverter.FrameRenderTexture.height}");
            }
            
            _frameCount = 0;
            _lastLogTime = Time.time;
        }
    }

    public Vector2Int GetCurrentResolution()
    {
        if (_leftCaptureSession?.TextureConverter?.FrameRenderTexture != null)
        {
            var texture = _leftCaptureSession.TextureConverter.FrameRenderTexture;
            return new Vector2Int(texture.width, texture.height);
        }
        return Vector2Int.zero;
    }

    public void OnFrameTimestamps(string data)
    {
        // Format: "ts:sensorTs:systemTs:unixTs"
        string[] parts = data.Split(':');
        if (parts.Length != 4 || parts[0] != "ts")
        {
            Debug.LogError($"Failed to parse frame data: Invalid format");
            Debug.LogError($"Data: {data}");
            return;
        }

        try
        {
            long sensorTs = long.Parse(parts[1]);
            long systemTs = long.Parse(parts[2]);
            long unixTs = long.Parse(parts[3]);

            // Verarbeite die Timestamps wie benötigt
            ProcessFrameTimestamps(sensorTs, systemTs, unixTs);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to parse frame data: {e.Message}");
            Debug.LogError($"Data: {data}");
        }
    }

    public void OnImageMetadata(string data)
    {
        // Format: "meta:width:height:format:yRowStride:uvRowStride:uvPixelStride"
        string[] parts = data.Split(':');
        if (parts.Length != 7 || parts[0] != "meta")
        {
            Debug.LogError($"Failed to parse metadata: Invalid format");
            Debug.LogError($"Data: {data}");
            return;
        }

        try
        {
            int width = int.Parse(parts[1]);
            int height = int.Parse(parts[2]);
            int format = int.Parse(parts[3]);
            int yRowStride = int.Parse(parts[4]);
            int uvRowStride = int.Parse(parts[5]);
            int uvPixelStride = int.Parse(parts[6]);

            // Verarbeite die Metadaten wie benötigt
            ProcessImageMetadata(width, height, format, yRowStride, uvRowStride, uvPixelStride);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to parse metadata: {e.Message}");
            Debug.LogError($"Data: {data}");
        }
    }

    // Method to forward time offset to capture sessions
    public void SetCaptureSessionTimeOffset(long offset)
    {
        if (_leftCaptureSession != null)
        {
            _leftCaptureSession.CaptureSession.SetTimeOffset(offset);
        }
        
        if (_rightCaptureSession != null)
        {
            _rightCaptureSession.CaptureSession.SetTimeOffset(offset);
        }
    }

    // Add a method to check for new frames
    public bool HasNewFrame()
    {
        bool result = _hasNewFrame;
        _hasNewFrame = false; // Reset after being checked
        return result;
    }

    private void ProcessFrameTimestamps(long sensorTs, long systemTs, long unixTs)
    {
        // Log für Debugging
        Debug.Log($"[Camera Frame] Timestamps:" +
                 $"\n  Sensor Time: {sensorTs}ns" +
                 $"\n  System Time: {systemTs}ns" +
                 $"\n  Unix Time: {unixTs}ms");

        // Aktualisiere die Timestamps
        _currentFrameTimestamps = new FrameTimestamps
        {
            SensorTimestampNs = sensorTs,
            SystemTimestampNs = systemTs,
            UnixTimestampMs = unixTs
        };

        // Benachrichtige Listener über neue Timestamps
        OnFrameTimestampsUpdated?.Invoke(_currentFrameTimestamps);
        _hasNewFrame = true;

        // Optional: Berechne und logge Frame-Intervalle
        float currentTime = Time.realtimeSinceStartup;
        if (_lastCameraFrameTime > 0)
        {
            float interval = (currentTime - _lastCameraFrameTime) * 1000f; // in ms
            _frameIntervals.Enqueue(interval);
            if (_frameIntervals.Count > MAX_INTERVAL_SAMPLES)
            {
                _frameIntervals.Dequeue();
            }
        }
        _lastCameraFrameTime = currentTime;
    }

    private void ProcessImageMetadata(int width, int height, int format, 
                                    int yRowStride, int uvRowStride, int uvPixelStride)
    {
        // Log für Debugging
        Debug.Log($"[Camera Frame] Image Metadata:" +
                 $"\n  Resolution: {width}x{height}" +
                 $"\n  Format: {format}" +
                 $"\n  Y Row Stride: {yRowStride}" +
                 $"\n  UV Row Stride: {uvRowStride}" +
                 $"\n  UV Pixel Stride: {uvPixelStride}");

        // Optional: Speichere die Metadaten für späteren Zugriff
        // Hier könnten wir z.B. eine ImageMetadata Struktur erstellen und füllen
    }

    public void OnCameraPose(Vector3 position, Quaternion rotation)
    {
        Debug.Log($"[Camera Frame] Camera Pose:" +
                  $"\n  Position: {position}" +
                  $"\n  Rotation: {rotation}");
                  
        _currentCameraPose = new CameraPose
        {
            Position = position,
            Rotation = rotation
        };
    }

    public struct CameraPose
    {
        public Vector3 Position;
        public Quaternion Rotation;
    }

    private CameraPose _currentCameraPose;
    public CameraPose CurrentCameraPose => _currentCameraPose;
} 