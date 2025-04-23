using UnityEngine;
using System.IO;
using System;
using Uralstech.UXR.QuestCamera;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;

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

    private float _nextFrameTime = 0f;
    private float _frameInterval;

    private float _recordingStartTime;

    // Wiederverwendbare Texturen
    private Texture2D _leftReadbackTexture;
    private Texture2D _rightReadbackTexture;
    
    // Für asynchrones Speichern
    private System.Threading.Tasks.Task _saveTask;
    private byte[] _leftJpgData;
    private byte[] _rightJpgData;
    private bool _isSaving = false;

    // Frame-Buffer für parallele Verarbeitung
    private const int BUFFER_SIZE = 4;
    private struct FrameData
    {
        public byte[] leftJpgData;
        public byte[] rightJpgData;
        public int frameNumber;
    }
    private Queue<FrameData> _frameBuffer = new Queue<FrameData>();
    private object _bufferLock = new object();
    private Task _processingTask;
    private CancellationTokenSource _cancellationSource = new CancellationTokenSource();

    private void Start()
    {
        _frameInterval = 1f / _targetFrameRate;
        Application.targetFrameRate = 90; // Noch höhere Framerate für mehr Headroom
        QualitySettings.vSyncCount = 0; // VSync ausschalten für bessere Performance
        
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

        // Native Auflösung beibehalten
        var nativeRes = _leftCameraInfo.SupportedResolutions[^1];
        _recordingResolution = new Vector2Int(nativeRes.width, nativeRes.height);

        // Texturen mit optimierten Settings
        _leftReadbackTexture = new Texture2D(_recordingResolution.x, _recordingResolution.y, TextureFormat.RGB24, false, true);
        _leftReadbackTexture.wrapMode = TextureWrapMode.Clamp;
        _leftReadbackTexture.filterMode = FilterMode.Point;

        if (_useBothCameras)
        {
            _rightReadbackTexture = new Texture2D(_recordingResolution.x, _recordingResolution.y, TextureFormat.RGB24, false, true);
            _rightReadbackTexture.wrapMode = TextureWrapMode.Clamp;
            _rightReadbackTexture.filterMode = FilterMode.Point;
        }

        // Start background processing
        _processingTask = Task.Run(ProcessFrameBuffer, _cancellationSource.Token);

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
        _recordingStartTime = Time.time;
        Debug.Log($"Started stereo recording to {_sessionFolder}");
    }

    private void Update()
    {
        if (!_isRecording || _isSaving) return;

        if (Time.time < _nextFrameTime) return;
        _nextFrameTime = Time.time + _frameInterval;

        // Capture frames
        if (_leftCaptureSession?.TextureConverter?.FrameRenderTexture != null)
        {
            CaptureFrames();
        }
    }

    private async void CaptureFrames()
    {
        if (_frameBuffer.Count >= BUFFER_SIZE) return; // Skip frame if buffer is full

        _isSaving = true;

        var frameData = new FrameData { frameNumber = _frameCount };

        // Capture left eye
        RenderTexture.active = _leftCaptureSession.TextureConverter.FrameRenderTexture;
        _leftReadbackTexture.ReadPixels(new Rect(0, 0, _recordingResolution.x, _recordingResolution.y), 0, 0);
        frameData.leftJpgData = _leftReadbackTexture.EncodeToJPG(95);

        if (_useBothCameras && _rightCaptureSession?.TextureConverter?.FrameRenderTexture != null)
        {
            RenderTexture.active = _rightCaptureSession.TextureConverter.FrameRenderTexture;
            _rightReadbackTexture.ReadPixels(new Rect(0, 0, _recordingResolution.x, _recordingResolution.y), 0, 0);
            frameData.rightJpgData = _rightReadbackTexture.EncodeToJPG(95);
        }

        RenderTexture.active = null;

        // Add to buffer
        lock (_bufferLock)
        {
            _frameBuffer.Enqueue(frameData);
        }

        _frameCount++;
        _isSaving = false;
    }

    private async Task ProcessFrameBuffer()
    {
        while (!_cancellationSource.Token.IsCancellationRequested)
        {
            FrameData? frameData = null;
            lock (_bufferLock)
            {
                if (_frameBuffer.Count > 0)
                {
                    frameData = _frameBuffer.Dequeue();
                }
            }

            if (frameData.HasValue)
            {
                try
                {
                    await File.WriteAllBytesAsync(
                        Path.Combine(_sessionFolder, "left", $"frame_{frameData.Value.frameNumber:D6}.jpg"),
                        frameData.Value.leftJpgData
                    );

                    if (_useBothCameras && frameData.Value.rightJpgData != null)
                    {
                        await File.WriteAllBytesAsync(
                            Path.Combine(_sessionFolder, "right", $"frame_{frameData.Value.frameNumber:D6}.jpg"),
                            frameData.Value.rightJpgData
                        );
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error saving frames: {e.Message}");
                }
            }
            else
            {
                await Task.Delay(1); // Kurze Pause wenn Buffer leer
            }
        }
    }

    private void OnDestroy()
    {
        _cancellationSource.Cancel();
        try
        {
            _processingTask?.Wait(1000); // Warte max. 1 Sekunde auf Beendigung
        }
        catch { }
        
        if (_isRecording)
        {
            float recordingDuration = Time.time - _recordingStartTime;
            float actualFrameRate = _frameCount / recordingDuration;
            
            Debug.Log($"Recording stopped. Duration: {recordingDuration:F1} seconds");
            Debug.Log($"Frames captured: {_frameCount}, Average frame rate: {actualFrameRate:F1} fps");
            
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

            // Cleanup textures
            if (_leftReadbackTexture != null) Destroy(_leftReadbackTexture);
            if (_rightReadbackTexture != null) Destroy(_rightReadbackTexture);
        }
    }
} 