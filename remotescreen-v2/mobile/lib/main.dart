import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const RemoteScreenApp());
}

class RemoteScreenApp extends StatelessWidget {
  const RemoteScreenApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      title: 'Remote Screen Mobile V2',
      theme: ThemeData(
        useMaterial3: true,
        brightness: Brightness.dark,
      ),
      home: const ConnectPage(),
    );
  }
}

class ConnectPage extends StatefulWidget {
  const ConnectPage({super.key});

  @override
  State<ConnectPage> createState() => _ConnectPageState();
}

class _ConnectPageState extends State<ConnectPage> {
  final _address = TextEditingController(text: 'http://192.168.0.96:5050');
  final _token = TextEditingController();

  bool _busy = false;
  bool _finding = false;
  String? _error;
  String? _status;

  String get _base =>
      _address.text.trim().replaceAll(RegExp(r'/+$'), '');

  Future<void> _findPc() async {
    setState(() {
      _finding = true;
      _error = null;
      _status = 'Searching the local network...';
    });

    RawDatagramSocket? socket;
    StreamSubscription<RawSocketEvent>? subscription;
    try {
      socket = await RawDatagramSocket.bind(
        InternetAddress.anyIPv4,
        0,
        reuseAddress: true,
      );
      socket.broadcastEnabled = true;

      final completer = Completer<InternetAddress?>();
      subscription = socket.listen((event) {
        if (event != RawSocketEvent.read) return;
        final datagram = socket?.receive();
        if (datagram == null) return;

        final message = utf8.decode(datagram.data, allowMalformed: true);
        if (!message.startsWith('REMOTE_SCREEN_V2|')) return;

        if (!completer.isCompleted) {
          completer.complete(datagram.address);
        }
      });

      final payload = utf8.encode('REMOTE_SCREEN_DISCOVER_V2');
      socket.send(
        payload,
        InternetAddress('255.255.255.255'),
        5051,
      );

      final result = await completer.future.timeout(
        const Duration(seconds: 3),
        onTimeout: () => null,
      );

      if (!mounted) return;
      if (result == null) {
        setState(() {
          _status = null;
          _error =
              'PC was not found automatically. Make sure both devices are on the same Wi-Fi, then use the address shown on the PC.';
        });
      } else {
        setState(() {
          _address.text = 'http://${result.address}:5050';
          _status = 'PC found: ${result.address}';
          _error = null;
        });
      }
    } catch (e) {
      if (mounted) {
        setState(() {
          _status = null;
          _error = 'LAN discovery failed: $e';
        });
      }
    } finally {
      await subscription?.cancel();
      socket?.close();
      if (mounted) setState(() => _finding = false);
    }
  }

  Future<void> _connect() async {
    final base = _base;
    final token = _token.text.trim();

    if (base.isEmpty || token.length != 6) {
      setState(() {
        _error = 'Enter the PC address and the 6-digit session code.';
        _status = null;
      });
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
      _status = 'Checking the PC...';
    });

    final client = HttpClient()
      ..connectionTimeout = const Duration(seconds: 5);

    try {
      final uri = Uri.parse(
        '$base/health?token=${Uri.encodeQueryComponent(token)}',
      );

      final request = await client.getUrl(uri);
      final response = await request.close();

      if (response.statusCode == 401) {
        await response.drain();
        throw const RemoteScreenException('Wrong session code.');
      }

      if (response.statusCode != 200) {
        await response.drain();
        throw RemoteScreenException(
          'PC returned HTTP ${response.statusCode}.',
        );
      }

      final body = await utf8.decoder.bind(response).join();
      if (!body.contains('"ok":true')) {
        throw const RemoteScreenException(
          'The PC responded but the Remote Screen service is not ready.',
        );
      }

      if (!mounted) return;

      Navigator.of(context).push(
        MaterialPageRoute(
          builder: (_) => ViewerPage(
            baseAddress: base,
            token: token,
          ),
        ),
      );
    } on SocketException {
      if (mounted) {
        setState(() {
          _error =
              'PC is not reachable on port 5050. Tap Find PC, and make sure Windows Firewall allows Remote Screen on Private networks.';
        });
      }
    } on TimeoutException {
      if (mounted) {
        setState(() {
          _error =
              'Connection timed out. Tap Find PC and verify the PC address shown in the desktop app.';
        });
      }
    } on RemoteScreenException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } catch (e) {
      if (mounted) setState(() => _error = 'Could not connect: $e');
    } finally {
      client.close(force: true);
      if (mounted) {
        setState(() {
          _busy = false;
          _status = null;
        });
      }
    }
  }

  @override
  void dispose() {
    _address.dispose();
    _token.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Remote Screen Mobile V2'),
      ),
      body: SafeArea(
        child: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 560),
            child: ListView(
              padding: const EdgeInsets.all(24),
              children: [
                const Icon(Icons.desktop_windows_rounded, size: 70),
                const SizedBox(height: 18),
                const Text(
                  'Windows desktop + system audio',
                  textAlign: TextAlign.center,
                  style: TextStyle(
                    fontSize: 25,
                    fontWeight: FontWeight.w600,
                  ),
                ),
                const SizedBox(height: 8),
                const Text(
                  'Keep the phone and PC on the same Wi-Fi/LAN.',
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 28),
                TextField(
                  controller: _address,
                  keyboardType: TextInputType.url,
                  autocorrect: false,
                  decoration: const InputDecoration(
                    labelText: 'PC address',
                    hintText: 'http://192.168.0.96:5050',
                    border: OutlineInputBorder(),
                  ),
                ),
                const SizedBox(height: 10),
                OutlinedButton.icon(
                  onPressed: _finding ? null : _findPc,
                  icon: _finding
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.wifi_find_rounded),
                  label: const Text('Find PC'),
                ),
                const SizedBox(height: 16),
                TextField(
                  controller: _token,
                  keyboardType: TextInputType.number,
                  maxLength: 6,
                  obscureText: true,
                  inputFormatters: [
                    FilteringTextInputFormatter.digitsOnly,
                  ],
                  decoration: const InputDecoration(
                    labelText: 'Session code',
                    border: OutlineInputBorder(),
                  ),
                  onSubmitted: (_) => _connect(),
                ),
                if (_status != null) ...[
                  const SizedBox(height: 4),
                  Text(
                    _status!,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.primary,
                    ),
                  ),
                ],
                if (_error != null) ...[
                  const SizedBox(height: 8),
                  Text(
                    _error!,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                ],
                const SizedBox(height: 14),
                FilledButton.icon(
                  onPressed: _busy ? null : _connect,
                  icon: _busy
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.play_arrow_rounded),
                  label: const Padding(
                    padding: EdgeInsets.symmetric(vertical: 14),
                    child: Text('Connect'),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class ViewerPage extends StatefulWidget {
  final String baseAddress;
  final String token;

  const ViewerPage({
    super.key,
    required this.baseAddress,
    required this.token,
  });

  @override
  State<ViewerPage> createState() => _ViewerPageState();
}

class _ViewerPageState extends State<ViewerPage> {
  static const _audioChannel = MethodChannel('remote_screen/audio');

  Uint8List? _frame;
  final List<int> _buffer = <int>[];

  bool _stopping = false;
  bool _connected = false;
  bool _audioEnabled = true;
  bool _fit = true;

  int _frames = 0;
  int _fps = 0;
  Timer? _fpsTimer;
  HttpClient? _videoClient;
  String? _message;

  String get _tokenEncoded => Uri.encodeQueryComponent(widget.token);

  @override
  void initState() {
    super.initState();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);

    _fpsTimer = Timer.periodic(const Duration(seconds: 1), (_) {
      if (!mounted) return;
      setState(() {
        _fps = _frames;
        _frames = 0;
      });
    });

    unawaited(_videoLoop());
    unawaited(_startAudio());
  }

  Future<void> _startAudio() async {
    if (!_audioEnabled || _stopping) return;
    try {
      await _audioChannel.invokeMethod('startAudio', {
        'url':
            '${widget.baseAddress}/audio?token=$_tokenEncoded',
      });
    } catch (_) {
      if (mounted) {
        setState(() {
          _message = 'Video connected. Audio is currently unavailable.';
        });
      }
    }
  }

  Future<void> _stopAudio() async {
    try {
      await _audioChannel.invokeMethod('stopAudio');
    } catch (_) {}
  }

  Future<void> _toggleAudio() async {
    setState(() => _audioEnabled = !_audioEnabled);
    if (_audioEnabled) {
      await _startAudio();
    } else {
      await _stopAudio();
    }
  }

  Future<void> _videoLoop() async {
    while (!_stopping) {
      HttpClient? client;
      try {
        if (mounted) {
          setState(() {
            _connected = false;
            _message = _frame == null
                ? 'Connecting...'
                : 'Reconnecting...';
          });
        }

        client = HttpClient()
          ..connectionTimeout = const Duration(seconds: 5);
        _videoClient = client;

        final request = await client.getUrl(
          Uri.parse(
            '${widget.baseAddress}/stream?token=$_tokenEncoded',
          ),
        );
        final response = await request.close();

        if (response.statusCode == 401) {
          await response.drain();
          throw const RemoteScreenException('Wrong session code.');
        }
        if (response.statusCode != 200) {
          await response.drain();
          throw RemoteScreenException(
            'Video HTTP ${response.statusCode}',
          );
        }

        if (mounted) {
          setState(() {
            _connected = true;
            _message = null;
          });
        }

        await for (final chunk in response) {
          if (_stopping) break;
          _onChunk(chunk);
        }
      } catch (e) {
        if (_stopping) break;
        if (mounted) {
          setState(() {
            _connected = false;
            _message =
                e is RemoteScreenException
                    ? e.message
                    : 'Connection lost. Reconnecting...';
          });
        }
      } finally {
        client?.close(force: true);
        if (identical(_videoClient, client)) {
          _videoClient = null;
        }
      }

      if (!_stopping) {
        await Future<void>.delayed(const Duration(seconds: 1));
      }
    }
  }

  void _onChunk(List<int> chunk) {
    _buffer.addAll(chunk);
    _extractFrames();
  }

  void _extractFrames() {
    while (true) {
      final start = _findMarker(_buffer, 0xFF, 0xD8, 0);
      if (start < 0) {
        if (_buffer.length > 4 * 1024 * 1024) {
          _buffer.clear();
        }
        return;
      }

      final end = _findMarker(_buffer, 0xFF, 0xD9, start + 2);
      if (end < 0) {
        if (start > 0) {
          _buffer.removeRange(0, start);
        }
        return;
      }

      final bytes = Uint8List.fromList(
        _buffer.sublist(start, end + 2),
      );
      _buffer.removeRange(0, end + 2);

      _frames++;
      if (mounted) {
        setState(() => _frame = bytes);
      }
    }
  }

  int _findMarker(
    List<int> data,
    int first,
    int second,
    int from,
  ) {
    for (var i = from; i < data.length - 1; i++) {
      if (data[i] == first && data[i + 1] == second) {
        return i;
      }
    }
    return -1;
  }

  @override
  void dispose() {
    _stopping = true;
    _fpsTimer?.cancel();
    _videoClient?.close(force: true);
    unawaited(_stopAudio());
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black,
      body: GestureDetector(
        onDoubleTap: () => setState(() => _fit = !_fit),
        child: Stack(
          fit: StackFit.expand,
          children: [
            if (_frame != null)
              InteractiveViewer(
                minScale: 0.5,
                maxScale: 5,
                child: Center(
                  child: Image.memory(
                    _frame!,
                    gaplessPlayback: true,
                    fit: _fit ? BoxFit.contain : BoxFit.cover,
                    width: double.infinity,
                    height: double.infinity,
                    filterQuality: FilterQuality.medium,
                  ),
                ),
              )
            else
              const Center(child: CircularProgressIndicator()),
            if (_message != null)
              Center(
                child: Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 18,
                    vertical: 12,
                  ),
                  decoration: BoxDecoration(
                    color: Colors.black87,
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Text(
                    _message!,
                    textAlign: TextAlign.center,
                  ),
                ),
              ),
            Positioned(
              top: 10,
              left: 10,
              right: 10,
              child: Row(
                children: [
                  IconButton.filledTonal(
                    onPressed: () => Navigator.of(context).pop(),
                    icon: const Icon(Icons.close),
                  ),
                  const SizedBox(width: 8),
                  IconButton.filledTonal(
                    onPressed: _toggleAudio,
                    icon: Icon(
                      _audioEnabled
                          ? Icons.volume_up_rounded
                          : Icons.volume_off_rounded,
                    ),
                  ),
                  const Spacer(),
                  Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 10,
                      vertical: 6,
                    ),
                    decoration: BoxDecoration(
                      color: Colors.black54,
                      borderRadius: BorderRadius.circular(20),
                    ),
                    child: Text(
                      '${_connected ? "LIVE" : "WAIT"} · $_fps fps',
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class RemoteScreenException implements Exception {
  final String message;
  const RemoteScreenException(this.message);

  @override
  String toString() => message;
}
