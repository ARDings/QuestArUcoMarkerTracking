package com.uralstech.ucamera

import android.hardware.camera2.*
import android.media.ImageReader
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.util.Range
import android.view.Surface
import com.unity3d.player.UnityPlayer

class RepeatingCaptureSessionWrapper(
    unityListener: String,
    frameCallback: CameraFrameCallback,
    width: Int,
    height: Int,
    imageQueueSize: Int = 2
) : CaptureSessionWrapper(unityListener, frameCallback, width, height, imageQueueSize),
    ImageReader.OnImageAvailableListener {

    companion object {
        private const val TAG = "RepeatingCaptureSession"
    }

    private val cameraThread = HandlerThread("CameraThread").apply { start() }
    private val cameraHandler = Handler(cameraThread.looper)
    private var currentCameraPose: CameraPose? = null
    val frameDurationNs = 210_000_000L
    override fun startCaptureSession(cameraDevice: CameraDevice, captureTemplate: Int) {
        try {
            imageReader.setOnImageAvailableListener(this, cameraHandler)

            val surfaces = listOf(imageReader.surface)

            // Erstelle CaptureRequest.Builder mit angepassten Parametern
            val captureRequestBuilder = cameraDevice.createCaptureRequest(captureTemplate).apply {
                addTarget(imageReader.surface)

                // Setze die Framerate auf 5 FPS
                set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range<Int>(1, 1))
                set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_OFF)
                set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_OFF)
                set(CaptureRequest.CONTROL_AWB_MODE, CaptureRequest.CONTROL_AWB_MODE_OFF)
                set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_OFF)

                // Aktiviere Auto-Exposure


                // Optional: Setze Priorität auf Bildqualität statt Framerate
                set(CaptureRequest.CONTROL_AE_ANTIBANDING_MODE, CaptureRequest.CONTROL_AE_ANTIBANDING_MODE_50HZ)

                // Hier wird die minimale Zeit zwischen zwei Frames gesetzt
                set(CaptureRequest.SENSOR_FRAME_DURATION, frameDurationNs)

                // Optional: Belichtungszeit (kürzer als Frame Duration!)
                set(CaptureRequest.SENSOR_EXPOSURE_TIME, 100_000_000L) // z. B. 100 ms

            }

            cameraDevice.createCaptureSession(
                surfaces,
                object : CameraCaptureSession.StateCallback() {
                    override fun onConfigured(session: CameraCaptureSession) {
                        captureSession = session

                        // Setze den repeating request direkt hier
                        try {
                            session.setRepeatingRequest(
                                captureRequestBuilder.build(),
                                object : CameraCaptureSession.CaptureCallback() {
                                    override fun onCaptureCompleted(
                                        session: CameraCaptureSession,
                                        request: CaptureRequest,
                                        result: TotalCaptureResult
                                    ) {
                                        // Hole die relevanten Kamera-Sensordaten
                                        val timestamp = result.get(CaptureResult.SENSOR_TIMESTAMP)
                                        val focalLength = result.get(CaptureResult.LENS_FOCAL_LENGTH)
                                        val aperture = result.get(CaptureResult.LENS_APERTURE)

                                        // Hole die physische Orientierung und Position der Kamera
                                        val orientation = result.get(CaptureResult.LENS_POSE_ROTATION)
                                        val translation = result.get(CaptureResult.LENS_POSE_TRANSLATION)

                                        // Sende die Zeitstempel und Kamera-Metadaten
                                        val poseData = "campose:" +
                                                "${timestamp}:" +
                                                "${focalLength}:" +
                                                "${aperture}:" +
                                                "${orientation?.joinToString(",")}:" +
                                                "${translation?.joinToString(",")}"

                                        UnityPlayer.UnitySendMessage(
                                            unityListener,
                                            "_onCameraPose",
                                            poseData
                                        )
                                    }
                                },
                                cameraHandler
                            )
                            UnityPlayer.UnitySendMessage(unityListener, "_onSessionRequestSet", "")
                        } catch (e: CameraAccessException) {
                            Log.e(TAG, "Failed to set repeating request", e)
                            UnityPlayer.UnitySendMessage(unityListener, "_onSessionRequestFailed",
                                e.message ?: "Unknown error")
                        }

                        UnityPlayer.UnitySendMessage(unityListener, "_onSessionConfigured", "")
                    }

                    override fun onConfigureFailed(session: CameraCaptureSession) {
                        Log.e(TAG, "Failed to configure capture session")
                        UnityPlayer.UnitySendMessage(unityListener, "_onSessionConfigurationFailed",
                            "Configuration failed")
                    }
                },
                cameraHandler
            )
        } catch (e: CameraAccessException) {
            Log.e(TAG, "Failed to start capture session", e)
            UnityPlayer.UnitySendMessage(unityListener, "_onSessionConfigurationFailed",
                e.message ?: "Unknown error")
        }
    }

    override fun closeSession() {
        super.closeSession()
        cameraThread.quitSafely()
    }

    override fun onImageAvailable(reader: ImageReader) {
        val image = reader.acquireLatestImage() ?: return

        try {
            val planes = image.planes
            val yBuffer = planes[0].buffer
            val uBuffer = planes[1].buffer
            val vBuffer = planes[2].buffer

            val ySize = yBuffer.remaining()
            val uSize = uBuffer.remaining()
            val vSize = vBuffer.remaining()

            val yRowStride = planes[0].rowStride
            val uvRowStride = planes[1].rowStride
            val uvPixelStride = planes[1].pixelStride

            val sensorTimestamp = image.timestamp
            val systemTimestamp = System.nanoTime()
            val unixTimestamp = System.currentTimeMillis()

            // Übergebe die Daten an den frameCallback
            frameCallback.onFrameReady(
                yBuffer, uBuffer, vBuffer,
                ySize, uSize, vSize,
                yRowStride, uvRowStride, uvPixelStride,
                sensorTimestamp, systemTimestamp, unixTimestamp
            )

            // Verwende das neue einheitliche Format
            val timestampStr = "ts:$sensorTimestamp:$systemTimestamp:$unixTimestamp"

            UnityPlayer.UnitySendMessage(
                unityListener,
                "_onFrameTimestamps",
                timestampStr
            )

            // Hole die aktuelle Kamera-Pose
            currentCameraPose?.let { pose ->
                val poseStr = "pose:${pose.position.x}:${pose.position.y}:${pose.position.z}:" +
                        "${pose.rotation.x}:${pose.rotation.y}:${pose.rotation.z}:${pose.rotation.w}"
                UnityPlayer.UnitySendMessage(
                    unityListener,
                    "_onCameraPose",
                    poseStr
                )
            }

            // Optional: Sende auch die Image-Metadaten in einem separaten Event
            val metadataStr = "meta:${image.width}:${image.height}:${image.format}:$yRowStride:$uvRowStride:$uvPixelStride"

            UnityPlayer.UnitySendMessage(
                unityListener,
                "_onImageMetadata",
                metadataStr
            )

        } finally {
            image.close()
        }
    }
}