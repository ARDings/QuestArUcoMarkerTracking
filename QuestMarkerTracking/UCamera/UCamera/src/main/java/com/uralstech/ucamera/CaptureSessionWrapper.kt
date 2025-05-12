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

package com.uralstech.ucamera

import android.graphics.ImageFormat
import android.hardware.camera2.CameraAccessException
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.ImageReader
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.view.Surface
import com.unity3d.player.UnityPlayer
import java.util.concurrent.Executors
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.CaptureResult
import android.hardware.camera2.TotalCaptureResult

/**
 * Wrapper class for [CameraCaptureSession].
 */
abstract class CaptureSessionWrapper(
    protected val unityListener: String,
    protected val frameCallback: CameraFrameCallback,
    width: Int, height: Int, imageQueueSize: Int
) {
    companion object {
        private const val TAG = "CaptureSessionWrapper"
        private const val ON_SESSION_CONFIGURED = "_onSessionConfigured"
        private const val ON_SESSION_CONFIG_FAILED = "_onSessionConfigurationFailed"
        private const val ON_SESSION_REQUEST_SET = "_onSessionRequestSet"
        private const val ON_SESSION_REQUEST_FAILED = "_onSessionRequestFailed"
    }

    var isActiveAndUsable: Boolean = true
    protected val imageReader = ImageReader.newInstance(width, height, ImageFormat.YUV_420_888, imageQueueSize)
    private val imageReaderThread = HandlerThread("ImageReaderThread").apply { start() }
    protected val imageReaderHandler = Handler(imageReaderThread.looper)
    protected var captureSession: CameraCaptureSession? = null
    private val captureSessionExecutor = Executors.newSingleThreadExecutor()
    private var frameCount = 0

    init {
        imageReader.setOnImageAvailableListener({ reader ->
            val image = reader.acquireLatestImage() ?: return@setOnImageAvailableListener

            val sensorTs = image.timestamp
            val systemTs = System.nanoTime()
            val unixTs = System.currentTimeMillis()

            val yPlane = image.planes[0]
            val uPlane = image.planes[1]
            val vPlane = image.planes[2]

            // Timestamps als einfachen String senden
            val timestampStr = "ts:$sensorTs:$systemTs:$unixTs"

            UnityPlayer.UnitySendMessage(
                unityListener,
                "_onFrameTimestamps",
                timestampStr
            )

            frameCallback.onFrameReady(
                yPlane.buffer,
                uPlane.buffer,
                vPlane.buffer,
                yPlane.buffer.remaining(),
                uPlane.buffer.remaining(),
                vPlane.buffer.remaining(),
                yPlane.rowStride,
                uPlane.rowStride,
                uPlane.pixelStride,
                sensorTs,
                systemTs,
                unixTs
            )

            image.close()
        }, imageReaderHandler)
    }

    internal abstract fun startCaptureSession(camera: CameraDevice, captureTemplate: Int)

    protected fun startRepeatingCaptureSession(
        camera: CameraDevice,
        captureTemplate: Int,
        outputs: List<OutputConfiguration>,
        surface: Surface
    ) {
        try {
            val sessionCallback = object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(session: CameraCaptureSession) {
                    Log.i(TAG, "New capture session configured for camera with ID \"${camera.id}\".")
                    UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_CONFIGURED, "")
                    captureSession = session
                    setRepeatingCaptureRequest(session, captureTemplate, surface)
                }

                override fun onConfigureFailed(session: CameraCaptureSession) {
                    Log.e(TAG, "Could not configure capture session for camera with ID \"${camera.id}\".")
                    UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_CONFIG_FAILED, "")
                    closeSession()
                }

                override fun onClosed(session: CameraCaptureSession) {
                    captureSessionExecutor.shutdown()
                    Log.i(TAG, "Capture session executor shut down.")
                }
            }

            camera.createCaptureSession(
                SessionConfiguration(
                    SessionConfiguration.SESSION_REGULAR,
                    outputs,
                    captureSessionExecutor,
                    sessionCallback
                )
            )
        } catch (exp: CameraAccessException) {
            Log.e(TAG, "Camera access exception while creating capture session", exp)
            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_CONFIG_FAILED, exp.message ?: "")
            closeSession()
        } catch (exp: SecurityException) {
            Log.e(TAG, "Security exception while creating capture session", exp)
            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_CONFIG_FAILED, exp.message ?: "")
            closeSession()
        }
    }

    protected fun setRepeatingCaptureRequest(
        captureSession: CameraCaptureSession,
        captureTemplate: Int,
        surface: Surface
    ) {
        try {
            val captureRequest = captureSession.device.createCaptureRequest(captureTemplate).apply {
                addTarget(surface)
            }.build()

            val captureCallback = object : CameraCaptureSession.CaptureCallback() {
                override fun onCaptureCompleted(
                    session: CameraCaptureSession,
                    request: CaptureRequest,
                    result: TotalCaptureResult
                ) {
                    super.onCaptureCompleted(session, request, result)

                    val sensorTs = result.get(CaptureResult.SENSOR_TIMESTAMP)
                    val systemTs = System.nanoTime()
                    val unixTs = System.currentTimeMillis()

                    // Timestamps als einfachen String senden
                    val timestampStr = "ts:$sensorTs:$systemTs:$unixTs"

                    UnityPlayer.UnitySendMessage(
                        unityListener,
                        "_onFrameTimestamps",
                        timestampStr
                    )

                    // Log nur alle 30 Frames
                    if (frameCount++ % 30 == 0) {
                        // Debug: Alle verfügbaren Keys ausgeben
                        Log.d(TAG, "=== CaptureResult Debug Info ===")
                        result.keys.forEach { key ->
                            val value = result.get(key)
                            Log.d(TAG, "${key.name}: $value")
                        }

                        try {
                            val rotation = result.get(CaptureResult.LENS_POSE_ROTATION) as? FloatArray
                            val position = result.get(CaptureResult.LENS_POSE_TRANSLATION) as? FloatArray
                            
                            Log.i(TAG, "Android Camera Pose at ${sensorTs?.div(1000000.0f)}ms:" +
                                "\n  Position: ${position?.joinToString(",") ?: "null"}" + 
                                "\n  Rotation (Quaternion): ${rotation?.joinToString(",") ?: "null"}")

                        } catch (e: Exception) {
                            Log.e(TAG, "Failed to get camera pose:", e)
                        }
                    }

                    // Unity wird die Pose später mit diesem Timestamp synchronisieren
                    val poseStr = "pose:$sensorTs"
                    
                    UnityPlayer.UnitySendMessage(
                        unityListener,
                        "_onRequestCameraPose", 
                        poseStr
                    )
                }
            }

            captureSession.setRepeatingRequest(
                captureRequest,
                captureCallback,
                imageReaderHandler
            )

            Log.i(TAG, "Repeating request set for camera session")
            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_REQUEST_SET, "")
        } catch (exp: CameraAccessException) {
            Log.e(TAG, "Camera access exception while setting repeating request", exp)
            closeSession()
        }
    }

    // In CaptureSessionWrapper.kt
    protected open fun closeSession() {  // 'open' hinzugefügt
        if (!isActiveAndUsable) return

        Log.i(TAG, "Closing camera capture session")
        isActiveAndUsable = false

        captureSession?.close()
        captureSession = null

        imageReader.setOnImageAvailableListener(null, null)
        imageReaderThread.quitSafely()

        Log.i(TAG, "Camera capture session closed")
    }

}