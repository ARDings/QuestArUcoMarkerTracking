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
            if (_isInitialized) return;

            try
            {
                Debug.Log("[Camera2Helper] Initializing camera plugin...");
                
                // Check if PCA is supported
                if (!PassthroughCameraUtils.IsSupported)
                {
                    Debug.LogError("[Camera2Helper] Passthrough Camera API is not supported on this device!");
                    return;
                }

                using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    _activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    Debug.Log("[Camera2Helper] Got activity reference");
                }

                Debug.Log("[Camera2Helper] Creating Camera2Helper instance...");
                _camera2Helper = new AndroidJavaObject("com.tryar.camera2.Camera2Helper", _activity);
                Debug.Log("[Camera2Helper] Camera2Helper instance created successfully");
                
                Debug.Log($"[Camera2Helper] Setting resolution to {width}x{height}");
                _camera2Helper.Call("setResolution", width, height);
                
                Debug.Log($"[Camera2Helper] Selecting camera (front: {useFrontCamera})");
                _camera2Helper.Call("selectCamera", useFrontCamera);

                _isInitialized = true;
                Debug.Log("[Camera2Helper] Initialization complete");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Failed to initialize: {e.Message}\n{e.StackTrace}");
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
                if (PassthroughCameraUtils.EnsureInitialized())
                {
                    foreach (PassthroughCameraEye eye in Enum.GetValues(typeof(PassthroughCameraEye)))
                    {
                        var intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(eye);
                        var outputSizes = PassthroughCameraUtils.GetOutputSizes(eye);
                        
                        if (PassthroughCameraUtils.CameraEyeToCameraIdMap.TryGetValue(eye, out var cameraData))
                        {
                            CameraConfig config = new CameraConfig
                            {
                                id = cameraData.id,
                                width = intrinsics.Resolution.x,
                                height = intrinsics.Resolution.y,
                                isLeftCamera = eye == PassthroughCameraEye.Left,
                                intrinsics = intrinsics
                            };
                            
                            _cameraConfigs.Add(config);
                            Debug.Log($"[Camera2Helper] Found camera {config.id}: {config.width}x{config.height}, isLeft: {config.isLeftCamera}");
                        }
                    }
                }
                else
                {
                    Debug.LogError("[Camera2Helper] Failed to initialize PassthroughCameraUtils");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Camera2Helper] Failed to get camera configurations: {e.Message}\n{e.StackTrace}");
            }

            return _cameraConfigs;
        }

        public void SwitchCamera()
        {
            if (_camera2Helper != null)
            {
                _camera2Helper.Call("switchCamera");
            }
        }

        public void StartPeriodicCameraSwitch(long intervalMs)
        {
            if (_camera2Helper != null)
            {
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