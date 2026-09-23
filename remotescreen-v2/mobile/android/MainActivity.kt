package com.walid.remotescreen.remote_screen_mobile

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import java.io.BufferedInputStream
import java.net.HttpURLConnection
import java.net.URL
import kotlin.concurrent.thread
import kotlin.math.max

class MainActivity : FlutterActivity() {
    private val channelName = "remote_screen/audio"

    @Volatile
    private var audioRunning = false

    @Volatile
    private var audioThread: Thread? = null

    @Volatile
    private var currentConnection: HttpURLConnection? = null

    @Volatile
    private var currentTrack: AudioTrack? = null

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
                        result.error("bad_url", "Audio URL is missing.", null)
                    } else {
                        startAudio(url)
                        result.success(true)
                    }
                }

                "stopAudio" -> {
                    stopAudio()
                    result.success(true)
                }

                else -> result.notImplemented()
            }
        }
    }

    private fun startAudio(url: String) {
        stopAudio()
        audioRunning = true

        audioThread = thread(
            start = true,
            isDaemon = true,
            name = "RemoteScreenAudio"
        ) {
            while (audioRunning) {
                var connection: HttpURLConnection? = null
                var track: AudioTrack? = null

                try {
                    connection = (URL(url).openConnection() as HttpURLConnection).apply {
                        connectTimeout = 5000
                        readTimeout = 15000
                        requestMethod = "GET"
                        useCaches = false
                        setRequestProperty("Connection", "keep-alive")
                        connect()
                    }
                    currentConnection = connection

                    if (connection.responseCode != HttpURLConnection.HTTP_OK) {
                        throw IllegalStateException(
                            "Audio HTTP ${connection.responseCode}"
                        )
                    }

                    val sampleRate = connection
                        .getHeaderField("X-Audio-Sample-Rate")
                        ?.toIntOrNull()
                        ?: 48000

                    val channels = connection
                        .getHeaderField("X-Audio-Channels")
                        ?.toIntOrNull()
                        ?: 2

                    val channelMask =
                        if (channels <= 1) AudioFormat.CHANNEL_OUT_MONO
                        else AudioFormat.CHANNEL_OUT_STEREO

                    val minBuffer = AudioTrack.getMinBufferSize(
                        sampleRate,
                        channelMask,
                        AudioFormat.ENCODING_PCM_16BIT
                    )

                    val bufferSize = max(
                        if (minBuffer > 0) minBuffer * 2 else 32768,
                        32768
                    )

                    track = AudioTrack(
                        AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                            .build(),
                        AudioFormat.Builder()
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setSampleRate(sampleRate)
                            .setChannelMask(channelMask)
                            .build(),
                        bufferSize,
                        AudioTrack.MODE_STREAM,
                        AudioManager.AUDIO_SESSION_ID_GENERATE
                    )
                    currentTrack = track

                    track.play()

                    BufferedInputStream(connection.inputStream, bufferSize).use { input ->
                        val buffer = ByteArray(bufferSize)

                        while (audioRunning) {
                            val count = input.read(buffer)
                            if (count < 0) break
                            if (count == 0) continue

                            var offset = 0
                            while (offset < count && audioRunning) {
                                val written = track.write(
                                    buffer,
                                    offset,
                                    count - offset,
                                    AudioTrack.WRITE_BLOCKING
                                )
                                if (written <= 0) break
                                offset += written
                            }
                        }
                    }
                } catch (_: Exception) {
                    // The outer loop reconnects automatically.
                } finally {
                    if (currentTrack === track) currentTrack = null
                    if (currentConnection === connection) currentConnection = null

                    try {
                        track?.pause()
                        track?.flush()
                        track?.stop()
                    } catch (_: Exception) {
                    }

                    try {
                        track?.release()
                    } catch (_: Exception) {
                    }

                    try {
                        connection?.disconnect()
                    } catch (_: Exception) {
                    }
                }

                if (audioRunning) {
                    try {
                        Thread.sleep(1000)
                    } catch (_: InterruptedException) {
                    }
                }
            }
        }
    }

    private fun stopAudio() {
        audioRunning = false

        try {
            currentConnection?.disconnect()
        } catch (_: Exception) {
        }

        try {
            currentTrack?.pause()
            currentTrack?.flush()
            currentTrack?.stop()
        } catch (_: Exception) {
        }

        audioThread?.interrupt()
        audioThread = null
        currentConnection = null
        currentTrack = null
    }

    override fun onDestroy() {
        stopAudio()
        super.onDestroy()
    }
}
