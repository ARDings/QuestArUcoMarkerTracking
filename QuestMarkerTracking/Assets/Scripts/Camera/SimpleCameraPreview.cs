using UnityEngine;
using UnityEngine.UI;
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
        // Start left camera
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

        // Wähle eine niedrigere Auflösung (Teile durch 4)
        var supportedResolutions = _leftCameraInfo.SupportedResolutions;
        var selectedResolution = supportedResolutions.Length > 2 ? 
                                supportedResolutions[supportedResolutions.Length - 3] : // Wähle eine niedrigere Auflösung
                                supportedResolutions[0]; // Fallback zur niedrigsten Auflösung
        
        Debug.Log($"Selected camera resolution: {selectedResolution.width}x{selectedResolution.height}");

        _leftCaptureSession = _leftCameraDevice.CreateContinuousCaptureSession(selectedResolution);
        state = await _leftCaptureSession.CaptureSession.WaitForInitializationAsync();
        if (state != NativeWrapperState.Opened)
        {
            Debug.LogError("Failed to open left capture session.");
            _leftCaptureSession.Destroy();
            _leftCameraDevice.Destroy();
            (_leftCameraDevice, _leftCaptureSession) = (null, null);
            return;
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
    }

    protected void OnDestroy()
    {
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
} 