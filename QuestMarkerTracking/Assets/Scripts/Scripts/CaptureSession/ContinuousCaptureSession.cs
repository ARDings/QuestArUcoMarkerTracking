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
        public event System.Action<long, long, long> OnFrameTimestamps;

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

        public void _onImageAvailable(string jsonData)
        {
            Debug.Log($"[ContinuousCaptureSession] _onImageAvailable called with data: {jsonData}");
            
            try 
            {
                var metadata = JsonUtility.FromJson<FrameMetadata>(jsonData);
                
                var frameData = new FrameData
                {
                    Image = null,
                    ImageMetadata = metadata.imageData,
                    SensorTimestamp = metadata.timestamps.sensorTs,
                    SystemTimestamp = metadata.timestamps.systemTs,
                    UnixTimestamp = metadata.timestamps.unixTs,
                    TextureUpdateTime = (long)(Time.realtimeSinceStartupAsDouble * 1000000000)
                };

                lock (_queueLock)
                {
                    _frameQueue.Enqueue(frameData);
                }

                OnFrameTimestamps?.Invoke(
                    metadata.timestamps.sensorTs,
                    metadata.timestamps.systemTs,
                    metadata.timestamps.unixTs
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
#pragma warning restore IDE1006 // Naming Styles
        #endregion
    }
}