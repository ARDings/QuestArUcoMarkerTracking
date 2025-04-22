using UnityEngine;
using System.IO;
using System;
using Uralstech.UXR.QuestCamera;

public class StereoCameraRecorder : MonoBehaviour 
{
    [SerializeField] private bool _useBothCameras = true;
    [SerializeField] private int _targetFrameRate = 30;
    [SerializeField] private Vector2Int _recordingResolution = new Vector2Int(2048, 2048);

    private CameraInfo _leftCameraInfo;
    private CameraDevice _leftCameraDevice;
    private CaptureSessionObject<ContinuousCaptureSession> _leftCaptureSession;

    private CameraInfo _rightCameraInfo;
    private CameraDevice _rightCameraDevice; 
    private CaptureSessionObject<ContinuousCaptureSession> _rightCaptureSession;

    private string _sessionFolder;
    private bool _isRecording = false;
    private int _frameCount = 0;

    private void Start()
    {
        Application.targetFrameRate = _targetFrameRate;

        // Kamera-Berechtigungen prüfen/anfordern
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UCameraManager.HeadsetCameraPermission))
        {
            InitializeCameras();
        }
        else
        {
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => InitializeCameras();
            UnityEngine.Android.Permission.RequestUserPermission(UCameraManager.HeadsetCameraPermission, callbacks);
        }
    }

    private void InitializeCameras()
    {
        // Aufnahme-Verzeichnis erstellen
        _sessionFolder = Path.Combine(Application.persistentDataPath, "Recordings", 
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(Path.Combine(_sessionFolder, "left"));
        if (_useBothCameras)
        {
            Directory.CreateDirectory(Path.Combine(_sessionFolder, "right"));
        }

        // Kameras initialisieren
        _leftCameraInfo = UCameraManager.Instance.GetCamera(CameraInfo.CameraEye.Left);
        if (_useBothCameras)
        {
            _rightCameraInfo = UCameraManager.Instance.GetCamera(CameraInfo.CameraEye.Right);
        }

        StartCameras();
    }

    private async void StartCameras()
    {
        // Linke Kamera starten
        _leftCameraDevice = UCameraManager.Instance.OpenCamera(_leftCameraInfo);
        var state = await _leftCameraDevice.WaitForInitializationAsync();
        if (state != NativeWrapperState.Opened)
        {
            Debug.LogError("Failed to open left camera");
            return;
        }

        _leftCaptureSession = _leftCameraDevice.CreateContinuousCaptureSession(_leftCameraInfo.SupportedResolutions[^1]);

        if (_useBothCameras)
        {
            // Rechte Kamera starten
            _rightCameraDevice = UCameraManager.Instance.OpenCamera(_rightCameraInfo);
            state = await _rightCameraDevice.WaitForInitializationAsync();
            if (state != NativeWrapperState.Opened)
            {
                Debug.LogError("Failed to open right camera");
                return;
            }

            _rightCaptureSession = _rightCameraDevice.CreateContinuousCaptureSession(_rightCameraInfo.SupportedResolutions[^1]);
        }

        _isRecording = true;
        Debug.Log($"Started stereo recording to {_sessionFolder}");
    }

    private void Update()
    {
        if (!_isRecording) return;

        // Frames nur alle 1/targetFrameRate Sekunden aufnehmen
        if (Time.frameCount % (60 / _targetFrameRate) != 0) return;

        // Linkes Auge aufnehmen
        if (_leftCaptureSession?.TextureConverter?.FrameRenderTexture != null)
        {
            SaveTextureToFile(_leftCaptureSession.TextureConverter.FrameRenderTexture, 
                Path.Combine(_sessionFolder, "left", $"frame_{_frameCount:D6}.jpg"));
        }

        // Rechtes Auge aufnehmen
        if (_useBothCameras && _rightCaptureSession?.TextureConverter?.FrameRenderTexture != null)
        {
            SaveTextureToFile(_rightCaptureSession.TextureConverter.FrameRenderTexture, 
                Path.Combine(_sessionFolder, "right", $"frame_{_frameCount:D6}.jpg"));
        }

        _frameCount++;
    }

    private void SaveTextureToFile(RenderTexture rt, string filePath)
    {
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        var currentRT = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = currentRT;

        File.WriteAllBytes(filePath, tex.EncodeToJPG(95));
        Destroy(tex);
    }

    private void OnDestroy()
    {
        if (_isRecording)
        {
            // Sessions beenden
            _leftCaptureSession?.Destroy();
            _rightCaptureSession?.Destroy();

            // Geräte freigeben
            _leftCameraDevice?.Destroy();
            _rightCameraDevice?.Destroy();

            Debug.Log($"Stopped recording. {_frameCount} frames saved to {_sessionFolder}");

            // FFmpeg-Befehl zum Zusammenführen ausgeben
            Debug.Log("To create stereo video, use FFmpeg commands:");
            Debug.Log(
                $"cd {_sessionFolder}\n" +
                "ffmpeg -framerate 30 -i left/frame_%06d.jpg left.mp4\n" +
                "ffmpeg -framerate 30 -i right/frame_%06d.jpg right.mp4\n" +
                "ffmpeg -i left.mp4 -i right.mp4 " +
                "-filter_complex \"[0:v][1:v]hstack\" " +
                "-c:v h264 -metadata:s:v:0 stereo_mode=left_right " +
                "-s 4096x2048 " + // Finale Auflösung für Quest 3
                "stereo_final.mp4"
            );
        }
    }
} 