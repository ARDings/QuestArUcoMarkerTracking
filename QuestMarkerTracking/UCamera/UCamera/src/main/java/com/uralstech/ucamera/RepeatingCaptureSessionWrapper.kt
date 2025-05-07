package com.uralstech.ucamera

import android.hardware.camera2.*
import android.media.ImageReader
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
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

    override fun startCaptureSession(cameraDevice: CameraDevice, captureTemplate: Int) {
        try {
            // Setze den ImageReader Listener
            imageReader.setOnImageAvailableListener(this, cameraHandler)

            val surfaces = listOf(imageReader.surface)

            cameraDevice.createCaptureSession(
                surfaces,
                object : CameraCaptureSession.StateCallback() {
                    override fun onConfigured(session: CameraCaptureSession) {
                        captureSession = session
                        setRepeatingRequest(cameraDevice, captureTemplate)
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

    private fun setRepeatingRequest(cameraDevice: CameraDevice, captureTemplate: Int) {
        try {
            val captureRequestBuilder = cameraDevice.createCaptureRequest(captureTemplate)
            captureRequestBuilder.addTarget(imageReader.surface)

            // Wichtige Kamera-Parameter setzen
            captureRequestBuilder.set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO)
            captureRequestBuilder.set(CaptureRequest.CONTROL_AF_MODE, CameraMetadata.CONTROL_AF_MODE_CONTINUOUS_PICTURE)

            captureSession?.setRepeatingRequest(
                captureRequestBuilder.build(),
                object : CameraCaptureSession.CaptureCallback() {
                    override fun onCaptureCompleted(
                        session: CameraCaptureSession,
                        request: CaptureRequest,
                        result: TotalCaptureResult
                    ) {
                        // Optional: Hier können Sie zusätzliche Metadaten verarbeiten
                    }
                },
                cameraHandler
            )

            UnityPlayer.UnitySendMessage(unityListener, "_onSessionRequestSet", "")
        } catch (e: CameraAccessException) {
            Log.e(TAG, "Failed to set repeating request", e)
            UnityPlayer.UnitySendMessage(unityListener, "_onSessionRequestFailed", e.message ?: "Unknown error")
        }
    }

    override fun closeSession() {
        super.closeSession()
        cameraThread.quitSafely()
    }

    override fun onImageAvailable(reader: ImageReader) {
        val image = reader.acquireLatestImage() ?: return
        
        try {
            // Konvertiere das Image in YUV Puffer
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

            // JSON mit Image-Daten und Timestamps erstellen
            val jsonData = """
                {
                    "imageData": {
                        "width": ${image.width},
                        "height": ${image.height},
                        "format": ${image.format},
                        "yRowStride": $yRowStride,
                        "uvRowStride": $uvRowStride,
                        "uvPixelStride": $uvPixelStride
                    },
                    "timestamps": {
                        "sensorTs": $sensorTimestamp,
                        "systemTs": $systemTimestamp,
                        "unixTs": $unixTimestamp
                    }
                }
            """.trimIndent()

            UnityPlayer.UnitySendMessage(
                unityListener,
                "_onImageAvailable",
                jsonData
            )
        } finally {
            image.close()
        }
    }
}