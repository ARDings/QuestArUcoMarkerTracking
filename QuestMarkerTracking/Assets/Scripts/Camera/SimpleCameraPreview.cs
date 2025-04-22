using UnityEngine;
using UnityEngine.UI;
using Uralstech.UXR.QuestCamera;

public class SimpleCameraPreview : MonoBehaviour
{
    [Tooltip("Use both cameras instead of just left camera")]
    [SerializeField] private bool _useBothCameras = false;

    [Tooltip("Preview to show the left camera feed")]
    [SerializeField] private RawImage _leftCameraPreview;

    [Tooltip("Preview to show the right camera feed (only used if useBothCameras is true)")]
    [SerializeField] private RawImage _rightCameraPreview;

    private CameraInfo _leftCameraInfo;
    private CameraDevice _leftCameraDevice;
    private CaptureSessionObject<ContinuousCaptureSession> _leftCaptureSession;

    private CameraInfo _rightCameraInfo;
    private CameraDevice _rightCameraDevice;
    private CaptureSessionObject<ContinuousCaptureSession> _rightCaptureSession;

    protected void Start()
    {
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

        _leftCaptureSession = _leftCameraDevice.CreateContinuousCaptureSession(_leftCameraInfo.SupportedResolutions[^1]);
        state = await _leftCaptureSession.CaptureSession.WaitForInitializationAsync();
        if (state != NativeWrapperState.Opened)
        {
            Debug.LogError("Failed to open left capture session.");
            _leftCaptureSession.Destroy();
            _leftCameraDevice.Destroy();
            (_leftCameraDevice, _leftCaptureSession) = (null, null);
            return;
        }
        _leftCameraPreview.texture = _leftCaptureSession.TextureConverter.FrameRenderTexture;
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

            _rightCaptureSession = _rightCameraDevice.CreateContinuousCaptureSession(_rightCameraInfo.SupportedResolutions[^1]);
            state = await _rightCaptureSession.CaptureSession.WaitForInitializationAsync();
            if (state != NativeWrapperState.Opened)
            {
                Debug.LogError("Failed to open right capture session.");
                _rightCaptureSession.Destroy();
                _rightCameraDevice.Destroy();
                (_rightCameraDevice, _rightCaptureSession) = (null, null);
                return;
            }
            _rightCameraPreview.texture = _rightCaptureSession.TextureConverter.FrameRenderTexture;
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