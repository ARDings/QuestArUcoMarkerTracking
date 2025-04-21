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

        public static void Initialize(int width, int height, bool useFrontCamera = false)
        {
            if (_isInitialized) 
            {
                Debug.Log("[Camera2Helper] Already initialized, reinitializing...");
                Release();
            }

            try
            {
                Debug.Log($"[Camera2Helper] Initializing camera plugin (width: {width}, height: {height}, front: {useFrontCamera})");

                // Hole die korrekte Kamera-ID von PassthroughCameraUtils
                if (!PassthroughCameraUtils.EnsureInitialized())
                {
                    Debug.LogError("[Camera2Helper] Failed to initialize PassthroughCameraUtils");
                    return;
                }

                var targetEye = useFrontCamera ? PassthroughCameraEye.Left : PassthroughCameraEye.Right;
                if (!PassthroughCameraUtils.CameraEyeToCameraIdMap.TryGetValue(targetEye, out var cameraData))
                {
                    Debug.LogError($"[Camera2Helper] Failed to get camera ID for eye: {targetEye}");
                    return;
                }

                Debug.Log($"[Camera2Helper] Found camera for {targetEye} eye: ID={cameraData.id}, Index={cameraData.index}");

                using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    _activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                }

                _camera2Helper = new AndroidJavaObject("com.tryar.camera2.Camera2Helper", _activity);
                
                if (_camera2Helper != null)
                {
                    _camera2Helper.Call("setResolution", width, height);
                    
                    // Übergebe die Kamera-ID direkt statt useFrontCamera
                    _camera2Helper.Call("selectCameraById", cameraData.id);
                    
                    _isInitialized = true;
                    Debug.Log($"[Camera2Helper] Initialization complete with camera ID: {cameraData.id}");
                }
                else
                {
                    Debug.LogError("[Camera2Helper] Failed to create Camera2Helper instance");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Failed to initialize: {e.Message}\n{e.StackTrace}");
            }
        }

        private static bool RequestCameraPermission()
        {
            if (Application.platform != RuntimePlatform.Android)
                return true;

            try
            {
                // Nutze die vorhandenen Permissions von PassthroughCameraUtils
                if (!PassthroughCameraUtils.IsSupported)
                {
                    Debug.LogError("[Camera2Helper] Passthrough Camera API is not supported!");
                    return false;
                }

                // Warte auf Permissions von PassthroughCameraPermissions
                int timeout = 3000; // 3 Sekunden timeout
                int waited = 0;
                while (PassthroughCameraPermissions.HasCameraPermission != true && waited < timeout)
                {
                    System.Threading.Thread.Sleep(100);
                    waited += 100;
                }

                if (PassthroughCameraPermissions.HasCameraPermission != true)
                {
                    Debug.LogError("[Camera2Helper] Failed to get camera permissions");
                    return false;
                }

                Debug.Log("[Camera2Helper] Camera permissions granted via PassthroughCameraPermissions");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Error requesting permissions: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        public static void StartCamera(ImageAvailableCallback callback)
        {
            if (!_isInitialized)
            {
                Debug.LogError("[Camera2Helper] Cannot start camera - not initialized!");
                return;
            }

            try
            {
                Debug.Log("[Camera2Helper] Starting camera...");
                _imageCallback = callback;
                var callbackProxy = new ImageCallbackProxy(_imageCallback);
                _camera2Helper.Call("setImageCallback", callbackProxy);
                Debug.Log("[Camera2Helper] Callback set, starting camera capture");
                _camera2Helper.Call("startCamera");
                Debug.Log("[Camera2Helper] Camera started successfully");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Failed to start camera: {e.Message}\n{e.StackTrace}");
            }
        }

        public static void StopCamera()
        {
            try
            {
                _camera2Helper?.Call("stopCamera");
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to stop camera: {e.Message}");
            }
        }

        public static void Release()
        {
            StopCamera();
            _camera2Helper?.Dispose();
            _camera2Helper = null;
            _activity?.Dispose();
            _activity = null;
            _isInitialized = false;
        }

        public static List<CameraConfig> GetCameraConfigurations()
        {
            if (_cameraConfigs != null) return _cameraConfigs;
            _cameraConfigs = new List<CameraConfig>();

            try 
            {
                using (AndroidJavaObject cameraManager = new AndroidJavaObject("android.hardware.camera2.CameraManager"))
                {
                    // Get camera IDs
                    AndroidJavaObject cameraIdList = cameraManager.Call<AndroidJavaObject>("getCameraIdList");
                    string[] cameraIds = AndroidJNIHelper.ConvertFromJNIArray<string[]>(cameraIdList.GetRawObject());

                    foreach (string id in cameraIds)
                    {
                        using (AndroidJavaObject characteristics = cameraManager.Call<AndroidJavaObject>("getCameraCharacteristics", id))
                        {
                            // Get basic info
                            var sensorSizeKey = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics").GetStatic<AndroidJavaObject>("SENSOR_INFO_PIXEL_ARRAY_SIZE");
                            var sensorSize = characteristics.Call<AndroidJavaObject>("get", sensorSizeKey);
                            
                            // Get lens pose
                            var translationKey = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics").GetStatic<AndroidJavaObject>("LENS_POSE_TRANSLATION");
                            var rotationKey = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics").GetStatic<AndroidJavaObject>("LENS_POSE_ROTATION");
                            
                            var translation = characteristics.Call<AndroidJavaObject>("get", translationKey);
                            var rotation = characteristics.Call<AndroidJavaObject>("get", rotationKey);

                            // Meta specific vendor tags - wir müssen diese als Keys erstellen
                            var vendorTags = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics$Key");
                            var cameraSourceKey = new AndroidJavaObject("android.hardware.camera2.CameraCharacteristics$Key", "com.meta.camera.source", typeof(int));
                            var cameraPositionKey = new AndroidJavaObject("android.hardware.camera2.CameraCharacteristics$Key", "com.meta.camera.position", typeof(int));
                            
                            var cameraSource = characteristics.Call<AndroidJavaObject>("get", cameraSourceKey);
                            var cameraPosition = characteristics.Call<AndroidJavaObject>("get", cameraPositionKey);

                            // Extrahiere die Werte aus den Java-Objekten
                            float[] translationArray = translation != null ? AndroidJNIHelper.ConvertFromJNIArray<float[]>(translation.GetRawObject()) : new float[3];
                            float[] rotationArray = rotation != null ? AndroidJNIHelper.ConvertFromJNIArray<float[]>(rotation.GetRawObject()) : new float[4];

                            CameraConfig config = new CameraConfig
                            {
                                id = id,
                                width = sensorSize?.Call<int>("width") ?? 0,
                                height = sensorSize?.Call<int>("height") ?? 0,
                                lensTranslation = new Vector3(
                                    translationArray.Length > 0 ? translationArray[0] : 0,
                                    translationArray.Length > 1 ? translationArray[1] : 0,
                                    translationArray.Length > 2 ? translationArray[2] : 0
                                ),
                                lensRotation = Quaternion.Euler(
                                    rotationArray.Length > 0 ? rotationArray[0] : 0,
                                    rotationArray.Length > 1 ? rotationArray[1] : 0,
                                    rotationArray.Length > 2 ? rotationArray[2] : 0
                                ),
                                isPassthroughCamera = cameraSource?.Call<int>("intValue") == 0,
                                isLeftCamera = cameraPosition?.Call<int>("intValue") == 0
                            };

                            _cameraConfigs.Add(config);
                            Debug.Log($"[Camera2Helper] Found camera {config.id}:\n" +
                                    $"Resolution: {config.width}x{config.height}\n" +
                                    $"Position: {(config.isLeftCamera ? "Left" : "Right")}\n" +
                                    $"Translation: {config.lensTranslation}\n" +
                                    $"Rotation: {config.lensRotation.eulerAngles}\n" +
                                    $"Is Passthrough: {config.isPassthroughCamera}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Failed to get camera metadata: {e.Message}\n{e.StackTrace}");
            }

            return _cameraConfigs;
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
    }
} 