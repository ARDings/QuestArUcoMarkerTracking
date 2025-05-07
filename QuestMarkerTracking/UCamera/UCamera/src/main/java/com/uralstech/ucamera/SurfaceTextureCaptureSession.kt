package com.uralstech.ucamera

import android.graphics.SurfaceTexture
import android.hardware.camera2.*
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.opengl.GLES20
import android.opengl.GLES11Ext
import android.opengl.GLES30
import android.util.Log
import android.view.Surface
import com.unity3d.player.UnityPlayer
import java.util.concurrent.Executors

class SurfaceTextureCaptureSession(
    timeStamp: Long,
    private val unityListener: String,
    private val cameraDevice: CameraDevice,
    private val width: Int,
    private val height: Int,
    private val captureTemplate: Int
) {
    companion object {
        private const val TAG = "STCaptureSessionWrapper"

        // Unity callback method names
        private const val DESTROY_NATIVE_TEXTURE = "_destroyNativeTexture"
        private const val ON_SESSION_CONFIGURED = "_onSessionConfigured"
        private const val ON_SESSION_CONFIG_FAILED = "_onSessionConfigurationFailed"
        private const val ON_SESSION_REQUEST_SET = "_onSessionRequestSet"
        private const val ON_SESSION_REQUEST_FAILED = "_onSessionRequestFailed"
        private const val ON_CAPTURE_COMPLETED = "_onCaptureCompleted"

        init {
            System.loadLibrary("NativeTextureHelper")
        }
    }

    var isActiveAndUsable: Boolean = true

    private var captureSession: CameraCaptureSession? = null
    private val captureSessionExecutor = Executors.newSingleThreadExecutor()

    private var surface: Surface? = null
    private var surfaceTexture: SurfaceTexture? = null
    private var surfaceTextureId: Int = 0

    init {
        queueSurfaceTextureCaptureSession(timeStamp)
    }

    fun startCaptureSession(textureId: Int) {
        Log.i(TAG, "startCaptureSession was called with textureId: $textureId")
        if (captureSession != null || !isActiveAndUsable) return

        try {
            Log.i(TAG, "Starting capture session")
            surfaceTextureId = textureId

            val st = SurfaceTexture(textureId).apply {
                setDefaultBufferSize(width, height)
            }
            surfaceTexture = st
            registerSurfaceTextureForUpdates(st, textureId)

            val surf = Surface(st)
            surface = surf

            cameraDevice.createCaptureSession(
                SessionConfiguration(
                    SessionConfiguration.SESSION_REGULAR,
                    listOf(OutputConfiguration(surf)),
                    captureSessionExecutor,
                    object : CameraCaptureSession.StateCallback() {
                        override fun onConfigured(session: CameraCaptureSession) {
                            Log.i(TAG, "Session configured for camera \"${cameraDevice.id}\"")
                            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_CONFIGURED, "")
                            captureSession = session
                            setRepeatingCaptureRequest(session, captureTemplate, surf)
                        }

                        override fun onConfigureFailed(session: CameraCaptureSession) {
                            Log.e(TAG, "Session configuration failed")
                            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_CONFIG_FAILED, "")
                            close()
                        }

                        override fun onClosed(session: CameraCaptureSession) {
                            deregisterSurfaceTextureForUpdates(textureId)
                            UnityPlayer.UnitySendMessage(unityListener, DESTROY_NATIVE_TEXTURE, textureId.toString())
                            surf.release()
                            st.release()
                            captureSessionExecutor.shutdown()
                            Log.i(TAG, "Capture session executor shut down")
                        }
                    }
                )
            )
        } catch (exp: CameraAccessException) {
            Log.e(TAG, "CameraAccessException creating session", exp)
            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_CONFIG_FAILED, exp.message)
            close()
        } catch (exp: SecurityException) {
            Log.e(TAG, "SecurityException creating session", exp)
            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_CONFIG_FAILED, exp.message)
            close()
        }
    }

    private fun setRepeatingCaptureRequest(
        captureSession: CameraCaptureSession,
        captureTemplate: Int,
        surface: Surface
    ) {
        try {
            val request = captureSession.device
                .createCaptureRequest(captureTemplate)
                .apply { addTarget(surface) }
                .build()

            captureSession.setSingleRepeatingRequest(
                request,
                captureSessionExecutor,
                object : CameraCaptureSession.CaptureCallback() {
                    override fun onCaptureCompleted(
                        session: CameraCaptureSession,
                        request: CaptureRequest,
                        result: TotalCaptureResult
                    ) {
                        val sensorTs = result.get(CaptureResult.SENSOR_TIMESTAMP) ?: -1L
                        val systemTs = System.nanoTime()
                        val unixTs = System.currentTimeMillis()

                        try {
                            // Texture aktualisieren
                            surfaceTexture?.updateTexImage()

                            // Timestamps als Texture-Attribute speichern
                            GLES30.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, surfaceTextureId)

                            // Timestamps in separate Texture-Attribute speichern
                            GLES30.glTexParameterf(GLES11Ext.GL_TEXTURE_EXTERNAL_OES,
                                GLES30.GL_TEXTURE_MAX_LOD, sensorTs.toFloat())
                            GLES30.glTexParameterf(GLES11Ext.GL_TEXTURE_EXTERNAL_OES,
                                GLES30.GL_TEXTURE_MIN_LOD, systemTs.toFloat())
                            GLES30.glTexParameterf(GLES11Ext.GL_TEXTURE_EXTERNAL_OES,
                                GLES30.GL_TEXTURE_BASE_LEVEL, unixTs.toFloat())

                            Log.i(TAG, "Frame timestamps embedded - Sensor: $sensorTs, System: $systemTs, Unix: $unixTs")

                            // Unity über neuen Frame informieren
                            UnityPlayer.UnitySendMessage(unityListener, ON_CAPTURE_COMPLETED, surfaceTextureId.toString())

                        } catch (exp: RuntimeException) {
                            Log.e(TAG, "SurfaceTexture updateTexImage() failed", exp)
                            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_REQUEST_FAILED, "SurfaceTexture lost EGL context")
                            close()
                        }



                    }
                }
            )

            Log.i(TAG, "Session request set")
            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_REQUEST_SET, "")
        } catch (exp: CameraAccessException) {
            Log.e(TAG, "CameraAccessException in request set", exp)
            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_REQUEST_FAILED, exp.message)
            close()
        } catch (exp: SecurityException) {
            Log.e(TAG, "SecurityException in request set", exp)
            UnityPlayer.UnitySendMessage(unityListener, ON_SESSION_REQUEST_FAILED, exp.message)
            close()
        }
    }

    fun close() {
        if (!isActiveAndUsable) return

        Log.i(TAG, "Closing capture session wrapper")
        isActiveAndUsable = false
        captureSession?.close()
        captureSession = null
        Log.i(TAG, "Capture session wrapper closed")
    }

    private external fun queueSurfaceTextureCaptureSession(timeStamp: Long)
    private external fun registerSurfaceTextureForUpdates(surfaceTexture: SurfaceTexture, textureId: Int)
    private external fun deregisterSurfaceTextureForUpdates(textureId: Int)
}