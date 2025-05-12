// Copyright 2025 URAV ADVANCED LEARNING SYSTEMS PRIVATE LIMITED
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using System.Collections.Generic;

namespace Uralstech.UXR.QuestCamera
{
    /// <summary>
    /// A wrapper for a native Camera2 CaptureSession and ImageReader.
    /// </summary>
    /// <remarks>
    /// This is different from <see cref="OnDemandCaptureSession"/> as it returns a
    /// continuous stream of images.
    /// </remarks>
    public class ContinuousCaptureSession : MonoBehaviour
    {
        /// <summary>
        /// The current assumed state of the native CaptureSession wrapper.
        /// </summary>
        public NativeWrapperState CurrentState { get; private set; }

        /// <summary>
        /// Is the native CaptureSession wrapper active and usable?
        /// </summary>
        public bool IsActiveAndUsable => _captureSession?.Get<bool>("isActiveAndUsable") ?? false;

        /// <summary>
        /// Called when the session has been configured.
        /// </summary>
        public UnityEvent OnSessionConfigured = new();

        /// <summary>
        /// Called when the session could not be configured.
        /// </summary>
        public UnityEvent<string> OnSessionConfigurationFailed = new();

        /// <summary>
        /// Called when the session request has been set.
        /// </summary>
        public UnityEvent OnSessionRequestSet = new();

        /// <summary>
        /// Called when the session request could not be set.
        /// </summary>
        public UnityEvent<string> OnSessionRequestFailed = new();

        /// <summary>
        /// The native capture session object.
        /// </summary>
        protected AndroidJavaObject _captureSession;

        // Event für Timestamps
        public event System.Action<string> OnFrameTimestamps;

        // Neue Struktur für Frame + Timestamp
        private struct FrameData
        {
            public AndroidJavaObject Image;
            public ImageData ImageMetadata;
            public long SensorTimestamp;
            public long SystemTimestamp;
            public long UnixTimestamp;
            public long TextureUpdateTime;  // Unity Zeit wenn die Textur aktualisiert wurde
        }

        private Queue<FrameData> _frameQueue = new Queue<FrameData>();
        private object _queueLock = new object();

        [Serializable]
        private class ImageData
        {
            public int width;
            public int height;
            public int format;
            public int yRowStride;
            public int uvRowStride;
            public int uvPixelStride;
        }

        [Serializable]
        private class TimestampData
        {
            public long sensorTs;
            public long systemTs;
            public long unixTs;
        }

        [Serializable]
        private class FrameMetadata
        {
            public ImageData imageData;
            public TimestampData timestamps;
        }

        // Add these fields to track time synchronization
        private bool _timeOffsetInitialized = false;
        private long _systemToUnityTimeOffsetNs = 0;

        // Timestamp-bezogene Felder
        private long _lastSensorTimestamp;
        private long _lastSystemTimestamp;
        private long _lastUnixTimestamp;

        // Bild-Metadaten Felder
        private int _imageWidth;
        private int _imageHeight;
        private int _imageFormat;

        // Events
        public event System.Action<string> OnImageMetadata;

        // Properties für externen Zugriff
        public long LastSensorTimestamp => _lastSensorTimestamp;
        public long LastSystemTimestamp => _lastSystemTimestamp;
        public long LastUnixTimestamp => _lastUnixTimestamp;
        
        public int ImageWidth => _imageWidth;
        public int ImageHeight => _imageHeight;
        public int ImageFormat => _imageFormat;

        public void _onImageAvailable(string jsonData)
        {
            Debug.Log($"[ContinuousCaptureSession] _onImageAvailable called with data: {jsonData}");
            
            try 
            {
                var metadata = JsonUtility.FromJson<FrameMetadata>(jsonData);
                
                // Store the current Unity time when the frame arrives
                long unityTimeNs = (long)(Time.realtimeSinceStartupAsDouble * 1000000000);
                
                var frameData = new FrameData
                {
                    Image = null,
                    ImageMetadata = metadata.imageData,
                    SensorTimestamp = metadata.timestamps.sensorTs,
                    SystemTimestamp = metadata.timestamps.systemTs,
                    UnixTimestamp = metadata.timestamps.unixTs,
                    TextureUpdateTime = unityTimeNs  // Use current Unity time
                };

                // Calculate reception delay properly
                long receptionDelay;
                if (_timeOffsetInitialized)
                {
                    // Get current Unity time in nanoseconds 
                    long currentUnityTimeNs = (long)(Time.realtimeSinceStartupAsDouble * 1000000000);
                    // Convert Unity time to system time base
                    long estimatedCurrentSystemTimeNs = currentUnityTimeNs + _systemToUnityTimeOffsetNs;
                    // Calculate delay from frame capture to now
                    receptionDelay = estimatedCurrentSystemTimeNs - metadata.timestamps.systemTs;
                    
                    Debug.Log($"[Frame Timing] Frame reception delay: {receptionDelay/1000000.0f}ms " +
                              $"(current system: {estimatedCurrentSystemTimeNs/1000000.0f}, frame system: {metadata.timestamps.systemTs/1000000.0f})");
                }
                else
                {
                    // If time offset isn't initialized yet, show a warning
                    receptionDelay = (long)(Time.realtimeSinceStartupAsDouble * 1000000000) - metadata.timestamps.systemTs;
                    Debug.LogWarning($"[Frame Timing] Time offset not initialized yet, delay calculation may be incorrect: {receptionDelay/1000000.0f}ms");
                }

                lock (_queueLock)
                {
                    _frameQueue.Enqueue(frameData);
                }

                OnFrameTimestamps?.Invoke(
                    $"{metadata.timestamps.sensorTs}, {metadata.timestamps.systemTs}, {metadata.timestamps.unixTs}"
                );
            }
            catch (Exception e)
            {
                Debug.LogError($"[ContinuousCaptureSession] Error processing image: {e.Message}\n{e.StackTrace}");
            }
        }

        private void Update()
        {
            FrameData? frameData = null;
            lock(_queueLock)
            {
                if(_frameQueue.Count > 0)
                    frameData = _frameQueue.Dequeue();
            }

            if(frameData.HasValue)
            {
                var frame = frameData.Value;
                
                // Update Texture
                if (frame.Image != null)
                {
                    UpdateTexture(frame.Image);
                    frame.Image.Dispose();
                }

                // Log frame processing delay
                long currentTime = (long)(Time.realtimeSinceStartup * 1000000000);
                long delay = currentTime - frame.TextureUpdateTime;
                Debug.Log($"[ContinuousCaptureSession] Frame processed - Delay: {delay/1000000.0f}ms");
            }
        }

        protected virtual void OnDestroy()
        {
            Release();
        }

        /// <summary>
        /// Sets the native CaptureSession wrapper.
        /// </summary>
        internal void SetCaptureSession(AndroidJavaObject nativeObject)
        {
            _captureSession = nativeObject;
        }

        /// <summary>
        /// Waits until the CaptureSession is open or erred out.
        /// </summary>
        public IEnumerator WaitForInitialization()
        {
            yield return new WaitUntil(() => CurrentState != NativeWrapperState.Initializing);
        }

#if UNITY_6000_0_OR_NEWER
        /// <summary>
        /// Waits until the CaptureSession is open or erred out.
        /// </summary>
        /// <remarks>
        /// Requires Unity 6.0 or higher.
        /// </remarks>
        /// <returns>The current state of the CaptureSession.</returns>
        public async Awaitable<NativeWrapperState> WaitForInitializationAsync()
        {
            if (CurrentState != NativeWrapperState.Initializing)
                return CurrentState;

            await Awaitable.MainThreadAsync();
            while (CurrentState == NativeWrapperState.Initializing)
                await Awaitable.NextFrameAsync();

            return CurrentState;
        }
#endif

        /// <summary>
        /// Releases the CaptureSession's native resources, and makes it unusable.
        /// </summary>
        public void Release()
        {
            _captureSession?.Call("close");
            _captureSession?.Dispose();
            _captureSession = null;
        }

        protected virtual void UpdateTexture(AndroidJavaObject image)
        {
            Debug.Log("[ContinuousCaptureSession] UpdateTexture called");
            try 
            {
                // Rufe die native Methode zum Aktualisieren der Textur auf
                _captureSession?.Call("updateTexture", image);
                Debug.Log("[ContinuousCaptureSession] Texture update successful");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ContinuousCaptureSession] Failed to update texture: {e.Message}\n{e.StackTrace}");
            }
        }

        #region Native Callbacks
#pragma warning disable IDE1006 // Naming Styles
        public void _onSessionConfigured(string _)
        {
            Debug.Log("[ContinuousCaptureSession] Session configured");
            OnSessionConfigured?.Invoke();
        }

        public void _onSessionConfigurationFailed(string reason)
        {
            Debug.LogError($"[ContinuousCaptureSession] Session configuration failed: {reason}");
            CurrentState = NativeWrapperState.Closed;
            OnSessionConfigurationFailed?.Invoke(reason);
        }

        public void _onSessionRequestSet(string _)
        {
            Debug.Log("[ContinuousCaptureSession] Session request set");
            CurrentState = NativeWrapperState.Opened;
            OnSessionRequestSet?.Invoke();
        }

        public void _onSessionRequestFailed(string reason)
        {
            Debug.LogError($"[ContinuousCaptureSession] Session request failed: {reason}");
            CurrentState = NativeWrapperState.Closed;
            OnSessionRequestFailed?.Invoke(reason);
        }

        public void _onFrameTimestamps(string data)
        {
            // Format: "ts:sensorTs:systemTs:unixTs"
            string[] parts = data.Split(':');
            if (parts.Length != 4 || parts[0] != "ts")
            {
                Debug.LogError($"[ContinuousCaptureSession] Invalid timestamp format: {data}");
                return;
            }

            try
            {
                long sensorTs = long.Parse(parts[1]);
                long systemTs = long.Parse(parts[2]);
                long unixTs = long.Parse(parts[3]);
                
                OnFrameTimestamps?.Invoke(data);
                
                // Optional: Speichere die Timestamps für spätere Verwendung
                _lastSensorTimestamp = sensorTs;
                _lastSystemTimestamp = systemTs;
                _lastUnixTimestamp = unixTs;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ContinuousCaptureSession] Error parsing timestamps: {e.Message}");
            }
        }

        public void _onImageMetadata(string data)
        {
            // Format: "meta:width:height:format:yRowStride:uvRowStride:uvPixelStride"
            string[] parts = data.Split(':');
            if (parts.Length != 7 || parts[0] != "meta")
            {
                Debug.LogError($"[ContinuousCaptureSession] Invalid metadata format: {data}");
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

                // Optional: Speichere die Metadaten
                _imageWidth = width;
                _imageHeight = height;
                _imageFormat = format;
                
                OnImageMetadata?.Invoke(data);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ContinuousCaptureSession] Error parsing metadata: {e.Message}");
            }
        }

        public void _onCameraPose(string data)
        {
            Debug.Log($"[ContinuousCaptureSession] _onCameraPose called with data: {data}");
            
            string[] parts = data.Split(':');
            Debug.Log($"[ContinuousCaptureSession] Split parts count: {parts.Length}");
            Debug.Log($"[ContinuousCaptureSession] First part: {parts[0]}");
            
            if (parts[0] != "campose") {
                Debug.LogWarning($"[ContinuousCaptureSession] Wrong format prefix: {parts[0]} (expected: campose)");
                return;
            }

            try {
                Debug.Log($"[ContinuousCaptureSession] Parsing timestamp from: {parts[1]}");
                long timestamp = long.Parse(parts[1]);
                
                Debug.Log($"[ContinuousCaptureSession] Parsing focal length from: {parts[2]}");
                float focalLength = parts[2] == "null" ? 0f : float.Parse(parts[2]);
                
                Debug.Log($"[ContinuousCaptureSession] Parsing aperture from: {parts[3]}");
                float aperture = parts[3] == "null" ? 0f : float.Parse(parts[3]);
                
                // Parse rotation quaternion
                Debug.Log($"[ContinuousCaptureSession] Parsing rotation from: {parts[4]}");
                Vector4 rotation = Vector4.zero;
                if (parts[4] != "null" && parts[4].Length > 0) {
                    string[] rotParts = parts[4].Split(',');
                    Debug.Log($"[ContinuousCaptureSession] Rotation parts count: {rotParts.Length}");
                    if (rotParts.Length == 4) {
                        rotation = new Vector4(
                            float.Parse(rotParts[0]),
                            float.Parse(rotParts[1]), 
                            float.Parse(rotParts[2]),
                            float.Parse(rotParts[3])
                        );
                    }
                }

                // Parse position vector
                Debug.Log($"[ContinuousCaptureSession] Parsing position from: {parts[5]}");
                Vector3 position = Vector3.zero;
                if (parts[5] != "null" && parts[5].Length > 0) {
                    string[] posParts = parts[5].Split(',');
                    Debug.Log($"[ContinuousCaptureSession] Position parts count: {posParts.Length}");
                    if (posParts.Length == 3) {
                        position = new Vector3(
                            float.Parse(posParts[0]),
                            float.Parse(posParts[1]),
                            float.Parse(posParts[2])
                        );
                    }
                }

                Debug.Log($"[ContinuousCaptureSession] Successfully parsed all data:" +
                          $"\n  Timestamp: {timestamp}" +
                          $"\n  Focal Length: {focalLength}mm" +
                          $"\n  Aperture: f/{aperture}" +
                          $"\n  Rotation: {rotation}" +
                          $"\n  Position: {position}");

                OnCameraPose?.Invoke(timestamp, focalLength, aperture, rotation, position);
            }
            catch (Exception e) {
                Debug.LogError($"[ContinuousCaptureSession] Error parsing camera pose: {e.Message}\nData: {data}");
            }
        }
#pragma warning restore IDE1006 // Naming Styles
        #endregion

        // Add a method to set the time offset
        public void SetTimeOffset(long offset) 
        {
            _systemToUnityTimeOffsetNs = offset;
            _timeOffsetInitialized = true;
            Debug.Log($"[ContinuousCaptureSession] Time offset set to {offset/1000000.0f}ms");
        }

        public event Action<long, float, float, Vector4, Vector3> OnCameraPose;
    }
}