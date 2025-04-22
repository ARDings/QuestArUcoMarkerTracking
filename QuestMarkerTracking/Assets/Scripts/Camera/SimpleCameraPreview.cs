using UnityEngine;
using UnityEngine.UI;
using Uralstech.UXR.QuestCamera;

public class SimpleCameraPreview : MonoBehaviour
{
    [Tooltip("Preview to show the camera feed")]
    [SerializeField] private RawImage _cameraPreview;

    private CameraInfo _cameraInfo;
    private CameraDevice _cameraDevice;
    private CaptureSessionObject<ContinuousCaptureSession> _captureSession;

    protected void Start()
    {
        // Check camera permission
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UCameraManager.HeadsetCameraPermission))
        {
            InitializeCamera();
        }
        else
        {
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => InitializeCamera();
            UnityEngine.Android.Permission.RequestUserPermission(UCameraManager.HeadsetCameraPermission, callbacks);
            Debug.Log("Camera permission requested.");
        }
    }

    private void InitializeCamera()
    {
        // Get the left eye camera
        _cameraInfo = UCameraManager.Instance.GetCamera(CameraInfo.CameraEye.Left);
        Debug.Log($"Got camera info: {_cameraInfo}");
        
        // Start camera automatically
        StartCamera();
    }

    private async void StartCamera()
    {
        if (_cameraInfo == null)
        {
            Debug.LogError("No camera info available");
            return;
        }

        // Open the camera
        _cameraDevice = UCameraManager.Instance.OpenCamera(_cameraInfo);

        // Wait for initialization and check its state
        NativeWrapperState state = await _cameraDevice.WaitForInitializationAsync();
        if (state != NativeWrapperState.Opened)
        {
            Debug.LogError("Failed to open camera.");
            _cameraDevice.Destroy();
            _cameraDevice = null;
            return;
        }

        Debug.Log("Camera opened.");

        // Open the capture session with highest resolution
        _captureSession = _cameraDevice.CreateContinuousCaptureSession(_cameraInfo.SupportedResolutions[^1]);

        // Wait for initialization and check its state
        state = await _captureSession.CaptureSession.WaitForInitializationAsync();
        if (state != NativeWrapperState.Opened)
        {
            Debug.LogError("Failed to open capture session.");
            _captureSession.Destroy();
            _cameraDevice.Destroy();
            (_cameraDevice, _captureSession) = (null, null);
            return;
        }

        // Set preview texture
        _cameraPreview.texture = _captureSession.TextureConverter.FrameRenderTexture;
        Debug.Log("Capture session opened.");
    }

    protected void OnDestroy()
    {
        if (_captureSession != null)
        {
            _captureSession.Destroy();
            _captureSession = null;
        }

        if (_cameraDevice != null)
        {
            _cameraDevice.Destroy();
            _cameraDevice = null;
        }
    }
} 