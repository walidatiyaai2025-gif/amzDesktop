package com.walid.remotescreen.remote_screen_mobile_v24

import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import java.io.BufferedInputStream
import java.net.HttpURLConnection
import java.net.URL

class MainActivity : FlutterActivity() {
    private val channelName = "com.walid.remotescreen/audio"

    @Volatile
    private var audioRunning = false

    @Volatile
    private var muted = false

    private var audioThread: Thread? = null
    private var connection: HttpURLConnection? = null
    private var audioTrack: AudioTrack? = null

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)

        MethodChannel(
            flutterEngine.dartExecutor.binaryMessenger,
            channelName
        ).setMethodCallHandler { call, result ->
            when (call.method) {
                "startAudio" -> {
                    val url = call.argument<String>("url")
                    if (url.isNullOrBlank()) {
                        result.error("BAD_URL", "Audio URL is missing.", null)
                    } else {
                        startAudio(url, result)
                    }
                }

                "stopAudio" -> {
                    stopAudioInternal()
                    result.success(true)
                }

                "setMuted" -> {
                    muted = call.argument<Boolean>("muted") ?: false
                    try {
                        audioTrack?.setVolume(if (muted) 0f else 1f)
                    } catch (_: Exception) {
                    }
                    result.success(true)
                }

                else -> result.notImplemented()
            }
        }
    }

    private fun startAudio(
        url: String,
        result: MethodChannel.Result
    ) {
        stopAudioInternal()

        audioRunning = true

        val thread = Thread {
            var replied = false
            var localConnection: HttpURLConnection? = null
            var localTrack: AudioTrack? = null

            try {
                val conn = URL(url).openConnection() as HttpURLConnection
                localConnection = conn
                connection = conn

                conn.requestMethod = "GET"
                conn.connectTimeout = 6000
                conn.readTimeout = 0
                conn.useCaches = false
                conn.setRequestProperty("Connection", "close")
                conn.connect()

                val code = conn.responseCode
                if (code != 200) {
                    val message = try {
                        conn.errorStream?.bufferedReader()?.readText()
                    } catch (_: Exception) {
                        null
                    }

                    throw IllegalStateException(
                        if (!message.isNullOrBlank()) message
                        else "Audio endpoint returned HTTP $code"
                    )
                }

                val sampleRate =
                    conn.getHeaderField("X-Sample-Rate")?.toIntOrNull()
                        ?: 48000

                val channels =
                    conn.getHeaderField("X-Channels")?.toIntOrNull()
                        ?.coerceIn(1, 2)
                        ?: 2

                val channelConfig =
                    if (channels == 1)
                        AudioFormat.CHANNEL_OUT_MONO
                    else
                        AudioFormat.CHANNEL_OUT_STEREO

                val minBuffer = AudioTrack.getMinBufferSize(
                    sampleRate,
                    channelConfig,
                    AudioFormat.ENCODING_PCM_16BIT
                )

                if (minBuffer <= 0) {
                    throw IllegalStateException(
                        "Android could not allocate an audio buffer."
                    )
                }

                val bufferSize = maxOf(minBuffer * 4, 32768)

                @Suppress("DEPRECATION")
                val track = AudioTrack(
                    AudioManager.STREAM_MUSIC,
                    sampleRate,
                    channelConfig,
                    AudioFormat.ENCODING_PCM_16BIT,
                    bufferSize,
                    AudioTrack.MODE_STREAM
                )

                if (track.state != AudioTrack.STATE_INITIALIZED) {
                    track.release()
                    throw IllegalStateException(
                        "Android AudioTrack initialization failed."
                    )
                }

                localTrack = track
                audioTrack = track

                track.setVolume(if (muted) 0f else 1f)
                track.play()

                runOnUiThread {
                    if (!replied) {
                        replied = true
                        result.success(
                            mapOf(
                                "sampleRate" to sampleRate,
                                "channels" to channels
                            )
                        )
                    }
                }

                BufferedInputStream(conn.inputStream, 65536).use { input ->
                    val buffer = ByteArray(16384)

                    while (audioRunning) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        if (read == 0) continue

                        var offset = 0

                        while (offset < read && audioRunning) {
                            val written = track.write(
                                buffer,
                                offset,
                                read - offset
                            )

                            if (written < 0) {
                                throw IllegalStateException(
                                    "AudioTrack write failed: $written"
                                )
                            }

                            offset += written
                        }
                    }
                }
            } catch (e: Exception) {
                if (!replied) {
                    runOnUiThread {
                        if (!replied) {
                            replied = true
                            result.error(
                                "AUDIO_START_FAILED",
                                e.message ?: "Audio playback failed.",
                                null
                            )
                        }
                    }
                }
            } finally {
                try {
                    localTrack?.pause()
                } catch (_: Exception) {
                }

                try {
                    localTrack?.flush()
                } catch (_: Exception) {
                }

                try {
                    localTrack?.release()
                } catch (_: Exception) {
                }

                if (audioTrack === localTrack) {
                    audioTrack = null
                }

                try {
                    localConnection?.disconnect()
                } catch (_: Exception) {
                }

                if (connection === localConnection) {
                    connection = null
                }
            }
        }

        audioThread = thread
        thread.name = "RemoteScreenPcmAudio"
        thread.isDaemon = true
        thread.start()
    }

    private fun stopAudioInternal() {
        audioRunning = false

        try {
            connection?.disconnect()
        } catch (_: Exception) {
        }

        try {
            audioTrack?.pause()
        } catch (_: Exception) {
        }

        try {
            audioTrack?.flush()
        } catch (_: Exception) {
        }

        try {
            audioTrack?.release()
        } catch (_: Exception) {
        }

        connection = null
        audioTrack = null

        try {
            audioThread?.interrupt()
        } catch (_: Exception) {
        }

        audioThread = null
    }

    override fun onDestroy() {
        stopAudioInternal()
        super.onDestroy()
    }
}
