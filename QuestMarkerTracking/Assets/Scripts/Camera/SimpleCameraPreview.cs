using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;  // Für GraphicsDeviceType
using Uralstech.UXR.QuestCamera;

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

    // Texture Properties anpassen
    public Texture2D LeftCameraTexture => _leftCaptureSession?.Texture;
    public Texture2D RightCameraTexture => _rightCaptureSession?.Texture;
    
    public bool AreCamerasReady => _leftCaptureSession?.Texture != null;

    public Vector2Int Resolution => 
        _leftCaptureSession?.Texture != null 
            ? new Vector2Int(
                _leftCaptureSession.Texture.width,
                _leftCaptureSession.Texture.height)
            : Vector2Int.zero;

    private CameraInfo _leftCameraInfo;
    private CameraDevice _leftCameraDevice;
    private SurfaceTextureCaptureSession _leftCaptureSession;

    private CameraInfo _rightCameraInfo;
    private CameraDevice _rightCameraDevice;
    private SurfaceTextureCaptureSession _rightCaptureSession;

    // RenderTexture für ArUcoTrackingAppCoordinator
    private RenderTexture _leftRenderTexture;
    private RenderTexture _rightRenderTexture;

    // Public Properties für RenderTextures
    public RenderTexture LeftRenderTexture => _leftRenderTexture;
    public RenderTexture RightRenderTexture => _rightRenderTexture;

    private float _lastLogTime = 0;
    private int _frameCount = 0;

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

    private void InitializeCameras()
    {
        // Get the left eye camera
        _leftCameraInfo = UCameraManager.Instance.GetCamera(CameraInfo.CameraEye.Left);
        Debug.Log($"Got left camera info: {_leftCameraInfo}");
        
        if (_useBothCameras)
        {
            // Get the right eye camera
            _rightCameraInfo = UCameraManager.Instance.GetCamera(CameraInfo.CameraEye.Right);
            Debug.Log($"Got right camera info: {_rightCameraInfo}");
        }

        // Start cameras automatically
        StartCameras();
    }

    private async void StartCameras()
    {
        // Check Graphics API
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES3)
        {
            Debug.LogError("SurfaceTextureCaptureSession requires OpenGL ES 3.0 or higher!");
            enabled = false;
            return;
        }

        // Linke Kamera Setup
        if (_leftCameraInfo == null)
        {
            Debug.LogError("No left camera info available");
            return;
        }

        _leftCameraDevice = UCameraManager.Instance.OpenCamera(_leftCameraInfo);
        NativeWrapperState state = await _leftCameraDevice.WaitForInitializationAsync();
        if (state != NativeWrapperState.Opened)
        {
            Debug.LogError("Failed to open left camera.");
            _leftCameraDevice.Destroy();
            _leftCameraDevice = null;
            return;
        }
        Debug.Log("Left camera opened.");

        var supportedResolutions = _leftCameraInfo.SupportedResolutions;
        var selectedResolution = supportedResolutions.Length > 2 ? 
                                supportedResolutions[supportedResolutions.Length - 3] : 
                                supportedResolutions[0];
        
        Debug.Log($"Camera2: Selected camera resolution: {selectedResolution.width}x{selectedResolution.height}");

        // SurfaceTextureCaptureSession erstellen
        var captureSessionObject = _leftCameraDevice.CreateSurfaceTextureCaptureSession(selectedResolution);
        _leftCaptureSession = captureSessionObject as SurfaceTextureCaptureSession;
        
        state = await _leftCaptureSession.WaitForInitializationAsync();
        if (state != NativeWrapperState.Opened)
        {
            Debug.LogError("Failed to open left capture session.");
            _leftCaptureSession.Release();
            _leftCameraDevice.Destroy();
            (_leftCameraDevice, _leftCaptureSession) = (null, null);
            return;
        }

        // RenderTexture für ArUco erstellen
        _leftRenderTexture = new RenderTexture(selectedResolution.width, selectedResolution.height, 0);
        _leftRenderTexture.Create();

        // Preview Setup
        if (_enablePreviewDisplay && _leftCameraPreview != null)
        {
            _leftCameraPreview.texture = _leftRenderTexture;
        }
        
        Debug.Log("Left capture session opened.");

        // Rechte Kamera (falls aktiviert)
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
            
            var rightCaptureSessionObject = _rightCameraDevice.CreateSurfaceTextureCaptureSession(selectedResolution);
            _rightCaptureSession = rightCaptureSessionObject as SurfaceTextureCaptureSession;
            
            state = await _rightCaptureSession.WaitForInitializationAsync();
            if (state != NativeWrapperState.Opened)
            {
                Debug.LogError("Failed to open right capture session.");
                _rightCaptureSession.Release();
                _rightCameraDevice.Destroy();
                (_rightCameraDevice, _rightCaptureSession) = (null, null);
                return;
            }

            // RenderTexture für rechte Kamera
            _rightRenderTexture = new RenderTexture(selectedResolution.width, selectedResolution.height, 0);
            _rightRenderTexture.Create();

            if (_enablePreviewDisplay && _rightCameraPreview != null)
            {
                _rightCameraPreview.texture = _rightRenderTexture;
            }
            
            Debug.Log("Right capture session opened.");
        }
    }

    protected void OnDestroy()
    {
        if (_leftRenderTexture != null)
        {
            _leftRenderTexture.Release();
            Destroy(_leftRenderTexture);
        }
        if (_rightRenderTexture != null)
        {
            _rightRenderTexture.Release();
            Destroy(_rightRenderTexture);
        }

        // Cleanup left camera
        if (_leftCaptureSession != null)
        {
            _leftCaptureSession.Release();
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
            _rightCaptureSession.Release();
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

        // Texture2D in RenderTexture kopieren
        if (_leftCaptureSession?.Texture != null)
        {
            Graphics.Blit(_leftCaptureSession.Texture, _leftRenderTexture);
        }
        if (_rightCaptureSession?.Texture != null && _rightRenderTexture != null)
        {
            Graphics.Blit(_rightCaptureSession.Texture, _rightRenderTexture);
        }
        
        _frameCount++;
        
        // Log FPS alle 5 Sekunden
        if (Time.time - _lastLogTime > 5f)
        {
            float fps = _frameCount / (Time.time - _lastLogTime);
            Debug.Log($"Camera2: Current FPS: {fps:F1}");
            
            if (_leftCaptureSession?.Texture != null)
            {
                Debug.Log($"Camera2: Current texture resolution: {_leftCaptureSession.Texture.width}x{_leftCaptureSession.Texture.height}");
            }
            
            _frameCount = 0;
            _lastLogTime = Time.time;
        }
    }
} 