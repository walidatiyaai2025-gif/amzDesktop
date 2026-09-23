import 'dart:async';
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
      title: 'Remote Screen Mobile V2.2',
      theme: ThemeData(useMaterial3: true, brightness: Brightness.dark),
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
  final _address = TextEditingController();
  final _token = TextEditingController();
  bool _busy = false;
  String? _error;

  Future<void> _connect() async {
    final base = _address.text.trim().replaceAll(RegExp(r'/+$'), '');
    final token = _token.text.trim();

    if (base.isEmpty || token.length != 6) {
      setState(() => _error =
          'Enter the exact PC address shown by V2.2 and the 6-digit session code.');
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    final encoded = Uri.encodeQueryComponent(token);
    final streamUrl = '$base/stream?token=$encoded';
    final audioUrl = '$base/audio.pcm?token=$encoded';

    try {
      final client = HttpClient()
        ..connectionTimeout = const Duration(seconds: 6);

      final request = await client.getUrl(Uri.parse(streamUrl));
      final response = await request.close();

      if (response.statusCode != 200) {
        await response.drain();
        client.close(force: true);
        throw Exception(
          response.statusCode == 401
              ? 'Wrong session code.'
              : 'PC returned HTTP ${response.statusCode}.',
        );
      }

      if (!mounted) {
        client.close(force: true);
        return;
      }

      await Navigator.of(context).push(
        MaterialPageRoute(
          builder: (_) => ViewerPage(
            response: response,
            client: client,
            audioUrl: audioUrl,
          ),
        ),
      );
    } on SocketException {
      setState(() => _error =
          'Cannot reach the PC. Use the exact IP and port displayed by Remote Screen Desktop V2.2.');
    } catch (e) {
      setState(() => _error = 'Could not connect: $e');
    } finally {
      if (mounted) setState(() => _busy = false);
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
      appBar: AppBar(title: const Text('Remote Screen Mobile V2.2')),
      body: SafeArea(
        child: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 520),
            child: ListView(
              padding: const EdgeInsets.all(24),
              children: [
                const Icon(Icons.desktop_windows_rounded, size: 72),
                const SizedBox(height: 20),
                const Text(
                  'Windows screen + system audio',
                  textAlign: TextAlign.center,
                  style: TextStyle(fontSize: 24, fontWeight: FontWeight.w600),
                ),
                const SizedBox(height: 10),
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
                const SizedBox(height: 16),
                TextField(
                  controller: _token,
                  keyboardType: TextInputType.number,
                  maxLength: 6,
                  obscureText: true,
                  inputFormatters: [FilteringTextInputFormatter.digitsOnly],
                  decoration: const InputDecoration(
                    labelText: 'Session code',
                    border: OutlineInputBorder(),
                  ),
                  onSubmitted: (_) => _connect(),
                ),
                if (_error != null) ...[
                  const SizedBox(height: 8),
                  Text(
                    _error!,
                    style: TextStyle(color: Theme.of(context).colorScheme.error),
                  ),
                ],
                const SizedBox(height: 12),
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
  final HttpClientResponse response;
  final HttpClient client;
  final String audioUrl;

  const ViewerPage({
    super.key,
    required this.response,
    required this.client,
    required this.audioUrl,
  });

  @override
  State<ViewerPage> createState() => _ViewerPageState();
}

class _ViewerPageState extends State<ViewerPage> {
  static const _audioChannel =
      MethodChannel('com.walid.remotescreen/audio');

  Uint8List? _frame;
  StreamSubscription<List<int>>? _subscription;
  final List<int> _buffer = <int>[];

  bool _muted = false;
  bool _audioConnected = false;
  String? _audioStatus = 'Connecting audio...';
  String? _error;

  @override
  void initState() {
    super.initState();

    SystemChrome.setPreferredOrientations([
      DeviceOrientation.landscapeLeft,
      DeviceOrientation.landscapeRight,
    ]);
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);

    _subscription = widget.response.listen(
      _onChunk,
      onError: (e) {
        if (mounted) setState(() => _error = '$e');
      },
      onDone: () {
        if (mounted) setState(() => _error ??= 'Desktop disconnected.');
      },
      cancelOnError: false,
    );

    _startAudio();
  }

  Future<void> _startAudio() async {
    if (mounted) {
      setState(() {
        _audioConnected = false;
        _audioStatus = 'Connecting audio...';
      });
    }

    try {
      final info = await _audioChannel.invokeMethod<dynamic>(
        'startAudio',
        {'url': widget.audioUrl},
      );

      if (!mounted) return;

      var label = 'Audio connected';
      if (info is Map) {
        final rate = info['sampleRate'];
        final channels = info['channels'];
        if (rate != null && channels != null) {
          label = 'Audio: $rate Hz / $channels ch';
        }
      }

      setState(() {
        _audioConnected = true;
        _audioStatus = label;
      });
    } on PlatformException catch (e) {
      if (!mounted) return;
      setState(() {
        _audioConnected = false;
        _audioStatus = e.message ?? 'Audio unavailable';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _audioConnected = false;
        _audioStatus = 'Audio unavailable: $e';
      });
    }
  }

  void _onChunk(List<int> chunk) {
    _buffer.addAll(chunk);
    _extractFrames();
  }

  void _extractFrames() {
    while (true) {
      final start = _findMarker(_buffer, 0xff, 0xd8, 0);
      if (start < 0) {
        if (_buffer.length > 4 * 1024 * 1024) _buffer.clear();
        return;
      }

      final end = _findMarker(_buffer, 0xff, 0xd9, start + 2);
      if (end < 0) {
        if (start > 0) _buffer.removeRange(0, start);
        return;
      }

      final bytes = Uint8List.fromList(_buffer.sublist(start, end + 2));
      _buffer.removeRange(0, end + 2);

      if (mounted) setState(() => _frame = bytes);
    }
  }

  int _findMarker(List<int> data, int first, int second, int from) {
    for (var i = from; i < data.length - 1; i++) {
      if (data[i] == first && data[i + 1] == second) return i;
    }
    return -1;
  }

  Future<void> _toggleMute() async {
    _muted = !_muted;
    try {
      await _audioChannel.invokeMethod(
        'setMuted',
        {'muted': _muted},
      );
    } catch (_) {}
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    _subscription?.cancel();
    _audioChannel.invokeMethod('stopAudio').catchError((_) {});
    widget.client.close(force: true);

    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    SystemChrome.setPreferredOrientations(DeviceOrientation.values);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black,
      body: Stack(
        fit: StackFit.expand,
        children: [
          if (_frame == null && _error == null)
            const Center(child: CircularProgressIndicator())
          else if (_frame != null)
            InteractiveViewer(
              minScale: 0.5,
              maxScale: 5,
              child: Center(
                child: Image.memory(
                  _frame!,
                  gaplessPlayback: true,
                  fit: BoxFit.contain,
                  width: double.infinity,
                  height: double.infinity,
                  filterQuality: FilterQuality.medium,
                ),
              ),
            ),
          if (_error != null)
            Center(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Text(_error!, textAlign: TextAlign.center),
              ),
            ),
          Positioned(
            top: 10,
            left: 10,
            right: 10,
            child: Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                IconButton.filledTonal(
                  onPressed: () => Navigator.of(context).pop(),
                  icon: const Icon(Icons.close),
                ),
                Row(
                  children: [
                    if (_audioStatus != null)
                      Container(
                        constraints: const BoxConstraints(maxWidth: 280),
                        padding: const EdgeInsets.symmetric(
                          horizontal: 12,
                          vertical: 8,
                        ),
                        decoration: BoxDecoration(
                          color: Colors.black54,
                          borderRadius: BorderRadius.circular(18),
                        ),
                        child: Text(
                          _audioStatus!,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                    const SizedBox(width: 8),
                    IconButton.filledTonal(
                      onPressed: _audioConnected
                          ? _toggleMute
                          : _startAudio,
                      icon: Icon(
                        _muted ? Icons.volume_off : Icons.volume_up,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
