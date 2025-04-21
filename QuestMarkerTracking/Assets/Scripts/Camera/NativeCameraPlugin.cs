using UnityEngine;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using PassthroughCameraSamples;  // Für PassthroughCameraIntrinsics
using PassthroughCameraEye = PassthroughCameraSamples.PassthroughCameraEye;  // Für die Enum-Definition

namespace TryAR.Camera
{
    public class NativeCameraPlugin
    {
        // Plugin instance
        private static AndroidJavaObject _camera2Helper;
        private static AndroidJavaObject _activity;
        private static ImageAvailableCallback _imageCallback;
        private static bool _isInitialized;
        private static bool _isCameraRunning;

        // Callback delegate for image data
        public delegate void ImageAvailableCallback(byte[] data, int width, int height);

        // Add camera configuration struct
        public class CameraConfig
        {
            public string id;
            public int width;
            public int height;
            public bool isLeftCamera;
            public PassthroughCameraIntrinsics intrinsics;
            // Neue Felder für Meta-spezifische Daten
            public Vector3 lensTranslation;
            public Quaternion lensRotation;
            public bool isPassthroughCamera;
        }

        private static List<CameraConfig> _cameraConfigs;

        private class ImageCallbackProxy : AndroidJavaProxy
        {
            private ImageAvailableCallback _callback;

            public ImageCallbackProxy(ImageAvailableCallback callback) : base("com.tryar.camera2.Camera2Helper$ImageCallback")
            {
                _callback = callback;
            }

            public void onImageAvailable(byte[] data, int width, int height)
            {
                MainThreadDispatcher.RunOnMainThread(() => 
                {
                    _callback?.Invoke(data, width, height);
                });
            }
        }

        // Initialisiere die Kamera mit der angegebenen Auflösung und Kamera-ID
        public static void Initialize(int width, int height, PassthroughCameraEye eye = PassthroughCameraEye.Left)
        {
            if (_isInitialized)
            {
                Debug.Log("[Camera2Helper] Already initialized, reinitializing...");
                Release();
            }

            try
            {
                Debug.Log($"[Camera2Helper] Initializing camera plugin (width: {width}, height: {height}, eye: {eye})");
                
                // Hole die Activity
                if (_activity == null)
                {
                    using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    {
                        _activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    }
                }

                // Erstelle Camera2Helper
                _camera2Helper = new AndroidJavaObject("com.tryar.camera2.Camera2Helper", _activity);
                
                // Hole die Kamera-ID basierend auf dem gewählten Auge
                string cameraId = null;
                
                // Verwende PassthroughCameraUtils wie WebCamTextureManager
                if (PassthroughCameraUtils.EnsureInitialized() && 
                    PassthroughCameraUtils.CameraEyeToCameraIdMap.TryGetValue(eye, out var cameraData))
                {
                    // Verwende die Kamera-ID direkt
                    cameraId = cameraData.id;
                    Debug.Log($"[Camera2Helper] Found camera for {eye} eye: ID={cameraId}, Index={cameraData.index}");
                }
                else
                {
                    // Fallback: Hole alle Kamera-Konfigurationen und wähle die erste passende
                    string configs = _camera2Helper.Call<string>("getCameraConfigurations");
                    _cameraConfigs = ParseCameraConfigs(configs);
                    
                    var config = _cameraConfigs.Find(c => 
                        (eye == PassthroughCameraEye.Left && c.isLeftCamera) || 
                        (eye == PassthroughCameraEye.Right && !c.isLeftCamera));
                    
                    if (config != null)
                    {
                        cameraId = config.id;
                        Debug.Log($"[Camera2Helper] Selected camera ID: {cameraId} for {eye} eye");
                    }
                    else
                    {
                        Debug.LogWarning($"[Camera2Helper] No camera found for {eye} eye, using first available");
                        if (_cameraConfigs.Count > 0)
                        {
                            cameraId = _cameraConfigs[0].id;
                        }
                    }
                }
                
                if (string.IsNullOrEmpty(cameraId))
                {
                    Debug.LogError("[Camera2Helper] No camera ID found, cannot initialize");
                    return;
                }
                
                // Initialisiere mit der gewählten Kamera
                _camera2Helper.Call("initialize", width, height, cameraId);
                _isInitialized = true;
                Debug.Log($"[Camera2Helper] Initialization complete with camera ID: {cameraId}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Failed to initialize: {e.Message}\n{e.StackTrace}");
            }
        }

        // Setze den Callback für Bilddaten
        public static void SetImageCallback(ImageAvailableCallback callback)
        {
            if (!_isInitialized)
            {
                Debug.LogError("[Camera2Helper] Cannot set callback - not initialized!");
                return;
            }

            try
            {
                _imageCallback = callback;
                var callbackProxy = new ImageCallbackProxy(_imageCallback);
                _camera2Helper.Call("setImageCallback", callbackProxy);
                Debug.Log("[Camera2Helper] Image callback registered");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Failed to set callback: {e.Message}\n{e.StackTrace}");
            }
        }

        // Starte die Kamera
        public static void StartCamera(bool forceRestart = false)
        {
            if (!_isInitialized)
            {
                Debug.LogError("[Camera2Helper] Cannot start camera - not initialized!");
                return;
            }

            try
            {
                if (!_isCameraRunning || forceRestart)
                {
                    if (_isCameraRunning && forceRestart)
                    {
                        Debug.Log("[Camera2Helper] Forcing camera restart...");
                        StopCamera();
                    }
                    
                    Debug.Log("[Camera2Helper] Starting camera capture...");
                    _camera2Helper.Call("startCamera");
                    _isCameraRunning = true;
                }
                else
                {
                    Debug.Log("[Camera2Helper] Camera already running, ignoring start request");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Failed to start camera: {e.Message}\n{e.StackTrace}");
            }
        }

        // Stoppe die Kamera
        public static void StopCamera()
        {
            if (_isCameraRunning)
            {
                try
                {
                    _camera2Helper.Call("stopCamera");
                    _isCameraRunning = false;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Camera2Helper] Failed to stop camera: {e.Message}");
                }
            }
        }

        // Gib alle Ressourcen frei
        public static void Release()
        {
            StopCamera();
            if (_camera2Helper != null)
            {
                try
                {
                    _camera2Helper.Call("release");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Camera2Helper] Failed to release: {e.Message}");
                }
                _camera2Helper.Dispose();
                _camera2Helper = null;
            }
            _isInitialized = false;
            _isCameraRunning = false;
        }

        // Hilfsmethode zum Parsen der Kamera-Konfigurationen
        private static List<CameraConfig> ParseCameraConfigs(string jsonString)
        {
            var configs = new List<CameraConfig>();
            
            try
            {
                // Hier die Implementierung zum Parsen des JSON-Strings
                // und Erstellen der CameraConfig-Objekte
                
                // Beispiel:
                // var jsonArray = new JSONArray(jsonString);
                // for (int i = 0; i < jsonArray.length(); i++) {
                //     var jsonObject = jsonArray.getJSONObject(i);
                //     var config = new CameraConfig {
                //         id = jsonObject.getString("id"),
                //         width = jsonObject.getInt("width"),
                //         height = jsonObject.getInt("height"),
                //         isLeftCamera = jsonObject.getBoolean("isLeftCamera"),
                //         isPassthroughCamera = jsonObject.getBoolean("isPassthroughCamera")
                //     };
                //     configs.Add(config);
                // }
                
                Debug.Log($"[Camera2Helper] Parsed {configs.Count} camera configurations");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Failed to parse camera configs: {e.Message}");
            }
            
            return configs;
        }

        public void SwitchCamera()
        {
            if (_camera2Helper != null)
            {
                var currentConfig = _cameraConfigs.Find(c => c.id == _camera2Helper.Call<string>("getCurrentCameraId"));
                Debug.Log($"[Camera2Helper] Switching from camera:\n" +
                         $"ID: {currentConfig?.id}\n" +
                         $"Position: {(currentConfig?.isLeftCamera == true ? "Left" : "Right")}\n" +
                         $"Translation: {currentConfig?.lensTranslation}\n" +
                         $"Rotation: {currentConfig?.lensRotation.eulerAngles}\n" +
                         $"Is Passthrough: {currentConfig?.isPassthroughCamera}");

                _camera2Helper.Call("switchCamera");
            }
        }

        public void StartPeriodicCameraSwitch(long intervalMs)
        {
            if (_camera2Helper != null)
            {
                Debug.Log($"[Camera2Helper] Starting periodic camera switch every {intervalMs}ms");
                _camera2Helper.Call("startPeriodicCameraSwitch", intervalMs);
            }
        }

        public void StopPeriodicCameraSwitch()
        {
            if (_camera2Helper != null)
            {
                _camera2Helper.Call("stopPeriodicCameraSwitch");
            }
        }

        public static void RequestFrame()
        {
            if (_camera2Helper != null)
            {
                Debug.Log("[Camera2Helper] Frame wird automatisch durch kontinuierlichen Stream geliefert");
            }
        }
    }
} 