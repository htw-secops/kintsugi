import 'dart:async';
import 'dart:convert';
import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:xterm/xterm.dart';

import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../domain/entities/remote_control_session.dart';

/// The terminal itself: a real VT emulator fed by the agent's output frames, typing back into them.
///
/// # Why a terminal emulator rather than a text box
///
/// What arrives is not lines of text. It is whatever the shell wrote to its PTY — cursor moves,
/// colour, scroll regions, alternate-screen switches, everything `top`, `vim`, `less` and a
/// progress bar produce. Rendering that as text shows the escape sequences instead of obeying
/// them, which makes exactly the programs an administrator opens a terminal for unusable. `xterm`
/// is a pure-Dart emulator, so it works on web where nothing platform-specific would.
///
/// # The two things this widget owns that the BLoC deliberately does not
///
/// **The scrollback**, because a terminal is a mutable buffer fed imperatively, and putting it in
/// an `Equatable` state would mean re-emitting a growing buffer on every keystroke — see
/// `RemoteControlBloc.shellOutput`.
///
/// **The UTF-8 decoder**, and it has to be one decoder for the whole session rather than one per
/// frame. The agent sends bytes when it has them, so a frame can end in the middle of a multi-byte
/// character; decoding each frame on its own turns any such character into a replacement mark. A
/// single `Utf8Decoder` in non-terminating mode holds the partial sequence over to the next frame,
/// which is the whole reason [RemoteShellOutput] carries bytes rather than a string.
class RemoteShellView extends StatefulWidget {
  const RemoteShellView({
    required this.output,
    required this.onInput,
    this.allowCopy,
    super.key,
  });

  /// The agent's terminal output. Subscribed to once, for the life of this widget.
  final Stream<Uint8List> output;

  final ValueChanged<RemoteInput> onInput;

  /// When this holds true, letting go of the mouse over a selection copies it — see
  /// [TerminalCopySetting]. Null means the terminal behaves as xterm does: copy is Cmd-C, or
  /// Ctrl-Shift-C where Ctrl-C is the interrupt. Read on every release rather than once, so the
  /// switch takes effect on the next selection made; one already open is not copied when it is turned on.
  final ValueListenable<bool>? allowCopy;

  @override
  State<RemoteShellView> createState() => _RemoteShellViewState();
}

class _RemoteShellViewState extends State<RemoteShellView> {
  /// Scrollback deep enough that the output of a build or a long `journalctl` is still there to
  /// scroll back through, which is most of why anybody reads a terminal after the fact.
  static const int _scrollbackLines = 5000;

  late final Terminal _terminal = Terminal(maxLines: _scrollbackLines);
  final TerminalController _controller = TerminalController();

  /// Supplied rather than left to xterm to create, because [Listener] above needs something to
  /// focus. Disposed here for the same reason: a node this widget made is a node this widget owns.
  final FocusNode _focusNode = FocusNode(debugLabel: 'remote terminal');

  StreamSubscription<Uint8List>? _subscription;

  /// The note shown over the terminal for a moment after a copy. A notifier rather than state so a copy
  /// repaints only the note, never the terminal.
  final ValueNotifier<String?> _copied = ValueNotifier<String?>(null);
  Timer? _copiedTimer;

  /// What the press that is now open began with: whether it was the primary button, and the selection
  /// that was already on screen. Together they tell a release that made a selection from one that did not.
  bool _primaryPress = false;
  BufferRange? _selectionAtPress;
  Offset _pressOrigin = Offset.zero;
  double _travel = 0;

  void _onPress(PointerDownEvent event) {
    _primaryPress = (event.buttons & kPrimaryButton) != 0;
    _selectionAtPress = _controller.selection;
    _pressOrigin = event.position;
    _travel = 0;
    _takeKeyboard();
  }

  void _onMove(PointerMoveEvent event) => _travel = math.max(_travel, (event.position - _pressOrigin).distance);

  /// Whether the pointer moved the way a selection is made rather than the way a click wobbles. xterm
  /// reads anything past 2px as a drag, but a click tolerates 18px, and a drag of 2 to 18px selects one
  /// to three cells: the signature of an ordinary click-to-focus, which must not replace the clipboard.
  /// So a drag has to be clearly past the tap's tolerance; a double-click moves nothing at all. The price
  /// is a short deliberate drag, which copies nothing: two or three letters, or straight down to the next
  /// row (about 15.6px at 13px text and xterm's 1.2 line height), which highlights a whole line.
  bool get _movedLikeASelection => _travel > kTouchSlop || _travel <= kPrecisePointerPanSlop;

  /// A selection of one cell or less. xterm turns a click that drifts a couple of pixels into a drag,
  /// and a drag selects the cell under the pointer, so an ordinary click-to-focus leaves exactly this.
  static bool _isOneCell(BufferRange range) {
    final r = range.normalized;
    return r.begin.y == r.end.y && r.end.x - r.begin.x <= 1;
  }

  /// Puts the selection this press made on the clipboard when [RemoteShellView.allowCopy] is on.
  ///
  /// Run from the pointer release, after the arena is swept (the same microtask that hands the keyboard
  /// back): the selection is final by then, whether it came from a drag or a double-click. xterm 4.0.0
  /// has no triple-click selection. It copies only when all of these hold, so nothing but a deliberate
  /// selection ever replaces the clipboard:
  ///  * the press was the primary button: a right or middle click leaves an old selection standing, and
  ///    right-click is the habitual paste gesture;
  ///  * the press made or changed the selection, rather than leaving one from before;
  ///  * it spans more than one cell, and the pointer either moved well past a click's tolerance or hardly
  ///    at all (a double-click): a click-to-focus that drifted a few pixels leaves one to three cells.
  Future<void> _copySelectionIfAllowed() async {
    if (!(widget.allowCopy?.value ?? false)) return;
    if (!_primaryPress || !_movedLikeASelection) return;
    final range = _controller.selection;
    if (range == null || range == _selectionAtPress || _isOneCell(range)) return;
    final text = _terminal.buffer.getText(range);
    if (text.trim().isEmpty) return;

    String note;
    try {
      await Clipboard.setData(ClipboardData(text: text));
      final lines = text.trimRight().split('\n').length;
      note = 'Copied ($lines ${lines == 1 ? 'line' : 'lines'})';
    } on Object {
      // The browser can refuse (permission, an unfocused document): say so rather than claim a copy.
      note = 'Copy failed';
    }
    if (!mounted) return;
    _copied.value = note;
    _copiedTimer?.cancel();
    _copiedTimer = Timer(const Duration(milliseconds: 2200), () => _copied.value = null);
  }

  /// Takes the keyboard on a press, whatever that press later turns out to be.
  ///
  /// Two things conspire to lose it otherwise, and together they are why clicking back into a
  /// terminal took a random number of attempts.
  ///
  /// xterm focuses from `onTapDown`, so it needs the press to be recognised as a *tap*. A press
  /// that drifts a pixel or two — which is most real clicks — is a drag instead, and drives its
  /// selection; nothing focuses. Worse, that drag leaves a selection behind, and xterm's next
  /// `onTapDown` is spent clearing it rather than focusing, so the click after a drifting one is
  /// wasted too. Listening for the raw pointer event side-steps all of it: a [Listener] does not
  /// compete in the gesture arena, so it fires on every press however the press is later
  /// interpreted, and xterm's own tap, drag-to-select and scroll handling are untouched.
  ///
  /// The microtask is the other half, and without it this does nothing at all when it is most
  /// needed. A focused text field anywhere on the page unfocuses itself on any pointer down
  /// outside it (`TapRegion`, via `onTapOutside`), and that runs *after* this handler — so a
  /// request made inline is immediately undone and focus lands on the route's modal scope, with
  /// the keyboard going nowhere. Deferring to the end of the current event dispatch puts this
  /// last, which is the only position that survives.
  /// **It has to run on pointer *up* as well, or Cmd-C after a drag-selection copies nothing.**
  ///
  /// The app-wide `SelectionArea` in `main.dart` puts a `SelectableRegion` above this widget, and
  /// its `TapAndPanGestureRecognizer` arms a `kPressTimeout` (100ms) deadline on every press
  /// whatever the gesture arena later decides. Hold the button down longer than that — which
  /// dragging across several lines always does — and `_startNewMouseSelectionGesture` fires and
  /// requests focus for the *region's* node, taking the keyboard off the terminal mid-drag. xterm
  /// never takes it back: it focuses only from `onTapDown` (`TerminalView._onTapDown`), and its
  /// drag path paints the selection without touching focus — `TerminalGestureHandler` wires no
  /// `onDragEnd` at all.
  ///
  /// That matters because copy is resolved through focus: the Cmd-C map lives in
  /// `TerminalView._handleKeyEvent`, on the terminal's own node, so an unfocused terminal never
  /// sees the keypress. It reaches `SelectableRegion`'s copy action instead, which asks the region
  /// for its selected content — and the terminal is a `CustomPaint` that registers no `Selectable`,
  /// so it finds none and returns having done nothing. No clipboard write, no error anywhere.
  /// Double-clicking a word escapes all of this by resolving inside the 100ms, which is why
  /// copying one word worked and copying three lines did not.
  ///
  /// See test/presentation/remote_shell_copy_test.dart, which pins the copy and the focus halves
  /// separately.
  void _takeKeyboard() => scheduleMicrotask(() {
        if (mounted) _focusNode.requestFocus();
      });

  /// A release does both: hands the keyboard back, and copies the selection when copying is allowed.
  void _onRelease() {
    _takeKeyboard();
    scheduleMicrotask(() {
      if (mounted) unawaited(_copySelectionIfAllowed());
    });
  }

  /// One decoder for the whole session. See the class note — per-frame decoding corrupts any
  /// character that straddles a frame boundary.
  final Converter<List<int>, String> _decoder = const Utf8Decoder(allowMalformed: true);

  /// The last size reported to the host, so an identical resize is not sent again. `onResize` fires
  /// on every layout pass, not only on a change.
  int _columns = 0;
  int _rows = 0;

  @override
  void initState() {
    super.initState();

    // Typing. `onOutput` is the emulator's own name for "the user produced these characters",
    // already encoded the way a terminal expects — arrow keys as escape sequences, Ctrl-C as 0x03.
    // Sent as UTF-8 bytes, which is what the PTY at the other end is written with.
    _terminal.onOutput = (text) =>
        widget.onInput(RemoteShellInput(Uint8List.fromList(const Utf8Encoder().convert(text))));

    // The host re-wraps to whatever the browser is showing, so a window dragged wider is not a
    // terminal still wrapping at 80 columns.
    _terminal.onResize = (width, height, _, _) {
      if (width == _columns && height == _rows) return;
      _columns = width;
      _rows = height;
      widget.onInput(RemoteShellResize(columns: width, rows: height));
    };

    _subscription = widget.output.listen((bytes) => _terminal.write(_decoder.convert(bytes)));
  }

  @override
  void dispose() {
    _focusNode.dispose();
    unawaited(_subscription?.cancel());
    _copiedTimer?.cancel();
    _copied.dispose();
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Container(
      // A fixed height rather than an unbounded one: this sits inside the page's vertical scroll
      // view, which gives its children infinite height — a terminal laid out in that resolves to
      // one row and reports it to the host, so the shell wraps at a single line.
      height: 560,
      decoration: BoxDecoration(
        color: palette.background,
        borderRadius: BorderRadius.circular(4),
        border: Border.all(color: palette.border),
      ),
      padding: const EdgeInsets.all(8),
      child: Stack(children: [
        Positioned.fill(child: Listener(
        onPointerDown: _onPress,
        onPointerMove: _onMove,
        // And again on release, which is the half that makes Cmd-C work after a drag — see
        // `_takeKeyboard`. The microtask lands after the arena is swept for this pointer, so it is
        // the last word on focus for the whole press-drag-release.
        onPointerUp: (_) => _onRelease(),
        // **Space, and space alone, has to be taken back from the framework — on web this is the
        // difference between a terminal that types spaces and one that does not.**
        //
        // A printable character does not reach xterm as a key event at all: the emulator attaches a
        // `TextInput` connection and reads what the browser's own hidden input element receives,
        // which is why every other character works with no help from here. But Flutter web asks the
        // framework whether it consumed each key press and calls `preventDefault()` on the browser
        // event when the answer is yes — and `WidgetsApp`'s web shortcuts map a bare space to
        // `PrioritizedIntents([ActivateIntent, ScrollIntent(page down)])`. The scroll half is always
        // enabled, because there is a `Scrollable` above this widget (the page's, and xterm's own),
        // so every space was claimed, prevented, and never typed: the input element saw nothing and
        // the page nudged down instead. Nothing else was affected, which is what makes it read as a
        // terminal fault rather than as a shortcut.
        //
        // `DoNothingAndStopPropagationIntent` is the answer, and both halves of its name are
        // load-bearing: its action reports the key *unhandled*, so the embedding lets the browser
        // insert the character, and it stops the event climbing any higher, so `WidgetsApp`'s
        // mapping never runs. It is what a `TextField` gets for the same collision by being an
        // `EditableText`; xterm's text input is not one, so it inherits none of that.
        //
        // No other key needs this. Every key xterm cares about is answered by `Terminal.keyInput`,
        // which writes the escape sequence itself and reports the press handled — the
        // `preventDefault` that follows is then right, because the character has already been sent.
        child: Shortcuts(
          shortcuts: const <ShortcutActivator, Intent>{
            SingleActivator(LogicalKeyboardKey.space): DoNothingAndStopPropagationIntent(),
          },
          child: TerminalView(
            _terminal,
            controller: _controller,
            focusNode: _focusNode,
            // The terminal takes the keyboard as soon as the session opens, so an administrator can
            // type straight away rather than having to click into it first.
            autofocus: true,
            backgroundOpacity: 0,
            theme: _themeFor(palette),
            // AppTheme.monoFamily, the bundled Liberation Mono: on web there is no system monospace
            // to fall back to, so a family that is not registered lands in the proportional default
            // (it once did, under a google_fonts name). xterm's own fallback list is left alone below
            // it, which costs nothing on web and is what a desktop build would land on.
            textStyle: const TerminalStyle(fontFamily: AppTheme.monoFamily, fontSize: 13),
          ),
        ),
      )),
        // Over the top right corner, and ignoring the pointer so it never gets in the way of the next
        // selection. Shown for about two seconds after a copy.
        Positioned(
          top: 4,
          right: 12,
          child: IgnorePointer(
            child: ValueListenableBuilder<String?>(
              valueListenable: _copied,
              builder: (context, note, _) => note == null
                  ? const SizedBox.shrink()
                  : Container(
                      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                      decoration: BoxDecoration(
                        color: palette.surface,
                        border: Border.all(color: palette.action),
                        borderRadius: BorderRadius.circular(4),
                      ),
                      child: Text(note, style: TextStyle(color: palette.action, fontSize: 12.5)),
                    ),
            ),
          ),
        ),
      ]),
    );
  }

  /// The emulator's own 16-colour palette.
  ///
  /// Left as the standard xterm colours rather than themed to match the app: a shell's own prompt,
  /// `ls --color` and every TUI choose from these by *index*, on the understanding that index 1 is
  /// red and index 2 is green. Re-mapping them to a house palette would make a failing build print
  /// its errors in whatever colour happened to sit at index 1 — so only the ground and the
  /// foreground follow the theme, which is what a terminal application expects to vary.
  TerminalTheme _themeFor(KintsugiPalette palette) => TerminalTheme(
        cursor: palette.action,
        selection: palette.action.withValues(alpha: 0.35),
        foreground: palette.text,
        background: palette.background,
        black: const Color(0xFF000000),
        red: const Color(0xFFCD3131),
        green: const Color(0xFF0DBC79),
        yellow: const Color(0xFFE5E510),
        blue: const Color(0xFF2472C8),
        magenta: const Color(0xFFBC3FBC),
        cyan: const Color(0xFF11A8CD),
        white: const Color(0xFFE5E5E5),
        brightBlack: const Color(0xFF666666),
        brightRed: const Color(0xFFF14C4C),
        brightGreen: const Color(0xFF23D18B),
        brightYellow: const Color(0xFFF5F543),
        brightBlue: const Color(0xFF3B8EEA),
        brightMagenta: const Color(0xFFD670D6),
        brightCyan: const Color(0xFF29B8DB),
        brightWhite: const Color(0xFFFFFFFF),
        searchHitBackground: palette.action,
        searchHitBackgroundCurrent: palette.action,
        searchHitForeground: palette.background,
      );
}
