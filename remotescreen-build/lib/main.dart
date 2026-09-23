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
      title: 'Remote Screen Mobile',
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
  final _address = TextEditingController(text: 'http://192.168.1.10:5050');
  final _token = TextEditingController();
  bool _busy = false;
  String? _error;

  Future<void> _connect() async {
    final base = _address.text.trim().replaceAll(RegExp(r'/+$'), '');
    final token = _token.text.trim();
    if (base.isEmpty || token.length != 6) {
      setState(() => _error = 'Enter the PC address and 6-digit session code.');
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    final streamUrl = '$base/stream?token=${Uri.encodeQueryComponent(token)}';
    try {
      final client = HttpClient()..connectionTimeout = const Duration(seconds: 5);
      final request = await client.getUrl(Uri.parse(streamUrl));
      final response = await request.close();
      if (response.statusCode != 200) {
        await response.drain();
        client.close(force: true);
        throw Exception(response.statusCode == 401 ? 'Wrong session code.' : 'PC returned HTTP ${response.statusCode}.');
      }

      if (!mounted) return;
      Navigator.of(context).push(MaterialPageRoute(
        builder: (_) => ViewerPage(response: response, client: client),
      ));
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
      appBar: AppBar(title: const Text('Remote Screen Mobile')),
      body: SafeArea(
        child: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 520),
            child: ListView(
              padding: const EdgeInsets.all(24),
              children: [
                const Icon(Icons.desktop_windows_rounded, size: 72),
                const SizedBox(height: 20),
                const Text('View your Windows desktop', textAlign: TextAlign.center, style: TextStyle(fontSize: 24, fontWeight: FontWeight.w600)),
                const SizedBox(height: 8),
                const Text('Keep the phone and PC on the same Wi-Fi/LAN.', textAlign: TextAlign.center),
                const SizedBox(height: 28),
                TextField(
                  controller: _address,
                  keyboardType: TextInputType.url,
                  autocorrect: false,
                  decoration: const InputDecoration(labelText: 'PC address', hintText: 'http://192.168.1.20:5050', border: OutlineInputBorder()),
                ),
                const SizedBox(height: 16),
                TextField(
                  controller: _token,
                  keyboardType: TextInputType.number,
                  maxLength: 6,
                  obscureText: true,
                  inputFormatters: [FilteringTextInputFormatter.digitsOnly],
                  decoration: const InputDecoration(labelText: 'Session code', border: OutlineInputBorder()),
                  onSubmitted: (_) => _connect(),
                ),
                if (_error != null) ...[
                  const SizedBox(height: 8),
                  Text(_error!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
                ],
                const SizedBox(height: 12),
                FilledButton.icon(
                  onPressed: _busy ? null : _connect,
                  icon: _busy
                      ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2))
                      : const Icon(Icons.play_arrow_rounded),
                  label: const Padding(padding: EdgeInsets.symmetric(vertical: 14), child: Text('Connect')),
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

  const ViewerPage({super.key, required this.response, required this.client});

  @override
  State<ViewerPage> createState() => _ViewerPageState();
}

class _ViewerPageState extends State<ViewerPage> {
  Uint8List? _frame;
  StreamSubscription<List<int>>? _subscription;
  final List<int> _buffer = <int>[];
  int _frames = 0;
  int _fps = 0;
  Timer? _fpsTimer;
  bool _fit = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    SystemChrome.setPreferredOrientations([
      DeviceOrientation.landscapeLeft,
      DeviceOrientation.landscapeRight,
      DeviceOrientation.portraitUp,
    ]);
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);
    _subscription = widget.response.listen(_onChunk, onError: (e) {
      if (mounted) setState(() => _error = '$e');
    }, onDone: () {
      if (mounted) setState(() => _error ??= 'Desktop disconnected.');
    }, cancelOnError: false);

    _fpsTimer = Timer.periodic(const Duration(seconds: 1), (_) {
      if (!mounted) return;
      setState(() {
        _fps = _frames;
        _frames = 0;
      });
    });
  }

  void _onChunk(List<int> chunk) {
    _buffer.addAll(chunk);
    _extractFrames();
  }

  void _extractFrames() {
    while (true) {
      final start = _findMarker(_buffer, 0xFF, 0xD8, 0);
      if (start < 0) {
        if (_buffer.length > 2 * 1024 * 1024) _buffer.clear();
        return;
      }
      final end = _findMarker(_buffer, 0xFF, 0xD9, start + 2);
      if (end < 0) {
        if (start > 0) _buffer.removeRange(0, start);
        return;
      }

      final bytes = Uint8List.fromList(_buffer.sublist(start, end + 2));
      _buffer.removeRange(0, end + 2);
      _frames++;
      if (mounted) setState(() => _frame = bytes);
    }
  }

  int _findMarker(List<int> data, int a, int b, int from) {
    for (var i = from; i < data.length - 1; i++) {
      if (data[i] == a && data[i + 1] == b) return i;
    }
    return -1;
  }

  @override
  void dispose() {
    _subscription?.cancel();
    _fpsTimer?.cancel();
    widget.client.close(force: true);
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    SystemChrome.setPreferredOrientations(DeviceOrientation.values);
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
                    fit: _fit ? BoxFit.contain : BoxFit.cover,
                    width: double.infinity,
                    height: double.infinity,
                    filterQuality: FilterQuality.medium,
                  ),
                ),
              ),
            if (_error != null)
              Center(child: Padding(padding: const EdgeInsets.all(24), child: Text(_error!, textAlign: TextAlign.center))),
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
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                    decoration: BoxDecoration(color: Colors.black54, borderRadius: BorderRadius.circular(20)),
                    child: Text('$_fps fps'),
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
