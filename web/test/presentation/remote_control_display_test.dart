import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/platform/full_screen.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/remote_control_session.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/remote_control_usecases.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_control_bloc.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_control_screen.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_shell_view.dart';

// Aliased rather than shown: `_FakeRepository` has its own `session` method, and an unaliased
// import would be shadowed by it inside the class.
import 'remote_control_bloc_test.dart' as fakes;

/// A media channel this test drives by hand, recording what the screen sends back.
class _FakeStream implements RemoteControlStream {
  final _updates = StreamController<RemoteScreenUpdate>.broadcast();
  final sent = <RemoteInput>[];

  @override
  Stream<RemoteScreenUpdate> get updates => _updates.stream;

  @override
  void send(RemoteInput input) => sent.add(input);

  @override
  Future<void> close() => _updates.close();

  void emit(RemoteScreenUpdate update) => _updates.add(update);
}

class _FakeRepository implements RemoteControlRepository {
  _FakeRepository(this.stream);

  final _FakeStream stream;

  /// Set by [end], after which the session reads as over — as the server's does. Without it a test
  /// that hangs up would see the session still granted and the bloc reattach to a closed stream.
  bool ended = false;

  @override
  Future<RemoteControlSession> request(String hostId, RemoteControlSessionKind kind) async => granted;

  @override
  Future<RemoteControlSession?> session(String id) async => ended ? endedSession : granted;

  @override
  Future<void> end(String id) async {
    ended = true;
  }

  @override
  RemoteControlStream openStream(String sessionId) => stream;
}

final granted = fakes.session(
  kind: RemoteControlSessionKind.screen,
  consent: RemoteControlConsent.granted,
);

final endedSession = fakes.session(
  kind: RemoteControlSessionKind.screen,
  consent: RemoteControlConsent.granted,
  endedAtUtc: DateTime.utc(2026, 1, 1, 1),
);

/// A [FullScreenController] that records what was asked of it and can refuse, which is what a
/// browser does to a request made outside its activation window.
class _FakeFullScreen implements FullScreenController {
  _FakeFullScreen({this.agrees = true});

  final bool agrees;
  final _changes = StreamController<bool>.broadcast();

  bool _isFullScreen = false;
  int enterCalls = 0;
  int exitCalls = 0;

  @override
  bool get isFullScreen => _isFullScreen;

  @override
  Future<bool> enter() async {
    enterCalls++;
    if (!agrees) return false;
    _isFullScreen = true;
    _changes.add(true);
    return true;
  }

  @override
  Future<void> exit() async {
    exitCalls++;
    _isFullScreen = false;
    _changes.add(false);
  }

  @override
  Stream<bool> get onChanged => _changes.stream;
}

const _twoDisplays = RemoteDisplayGeometry(
  pointWidth: 1920,
  pointHeight: 1080,
  imageWidth: 1600,
  imageHeight: 900,
  activeDisplayId: 1,
  displays: [
    RemoteDisplayOption(id: 1, label: 'Display 1 (1920 x 1080)', width: 1920, height: 1080, isPrimary: true),
    RemoteDisplayOption(id: 2, label: 'Display 2 (1920 x 1080)', width: 1920, height: 1080, isPrimary: false),
  ],
);

/// A host whose screen is a different shape from the browser window — the case that decides whether
/// the full-screen picture fits or has to be scrolled to.
const _fourByThreeHost = RemoteDisplayGeometry(
  pointWidth: 1600,
  pointHeight: 1200,
  imageWidth: 1600,
  imageHeight: 1200,
  activeDisplayId: 1,
  displays: [
    RemoteDisplayOption(id: 1, label: 'Display 1 (1600 x 1200)', width: 1600, height: 1200, isPrimary: true),
  ],
);

const _oneDisplay = RemoteDisplayGeometry(
  pointWidth: 1920,
  pointHeight: 1080,
  imageWidth: 1600,
  imageHeight: 900,
  activeDisplayId: 1,
  displays: [
    RemoteDisplayOption(id: 1, label: 'Built-in Display (1920 x 1080)', width: 1920, height: 1080, isPrimary: true),
  ],
);

/// A Windows host whose agent announces everything this viewer can offer.
const _windowsHost = RemoteDisplayGeometry(
  pointWidth: 1920,
  pointHeight: 1080,
  imageWidth: 1600,
  imageHeight: 900,
  platform: 'windows',
  features: {
    RemoteDisplayGeometry.featureChat,
    RemoteDisplayGeometry.featureTypeText,
    RemoteDisplayGeometry.featureSecureAttention,
  },
);

/// The viewer's window: choosing which of a host's displays to watch, a session that fits whatever
/// size the administrator drags its window to, and the support tools beside the picture — chat,
/// paste as keystrokes and Ctrl+Alt+Del.
///
/// Worth widget tests rather than only bloc ones, because what goes wrong here is in the wiring. The
/// picker must not appear for a host with one display — a control that can only be set to where it
/// already is. Nothing may take full screen unasked, since the window's size is the administrator's
/// choice. And a support control must appear only where the agent announced it, or it sends into
/// silence.
void main() {
  Future<(_FakeStream, _FakeFullScreen)> pump(
    WidgetTester tester, {
    bool fullScreenAgrees = true,
    RemoteControlSessionKind kind = RemoteControlSessionKind.screen,
    Size window = const Size(1600, 1000),
    VoidCallback? onCloseWindow,
  }) async {
    final stream = _FakeStream();
    final repository = _FakeRepository(stream);
    final fullScreen = _FakeFullScreen(agrees: fullScreenAgrees);

    locator
      ..registerSingleton(RequestRemoteControlSession(repository))
      ..registerSingleton(GetRemoteControlSession(repository))
      ..registerSingleton(EndRemoteControlSession(repository))
      ..registerSingleton(OpenRemoteControlStream(repository));

    // Set before the first pump, deliberately: resizing the view *after* the tree is built leaves
    // the screen's subtree unlaid-out for the frame the assertions run in.
    tester.view.physicalSize = window;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    addTearDown(locator.reset);

    // Bare, as the router builds it: the screen is outside the sidebar's shell and brings its own
    // Scaffold.
    await tester.pumpWidget(MaterialApp(
      theme: AppTheme.light(),
      home: RemoteControlScreen(
        hostId: 'host',
        kind: kind,
        hostname: 'mac-01',
        fullScreen: fullScreen,
        onCloseWindow: onCloseWindow,
      ),
    ));
    await tester.pump();

    return (stream, fullScreen);
  }

  /// One test against a freshly pumped screen, torn down before the body returns.
  ///
  /// The teardown is inside the body rather than in `addTearDown` deliberately: the screen's bloc
  /// polls every two seconds, and the framework's "a Timer is still pending" assertion fires at the
  /// end of the body — before any registered teardown runs. Replacing the tree disposes the
  /// BlocProvider, which closes the bloc and stops the poll, which is exactly what navigating away
  /// from this screen does in the app.
  void screenTest(
    String description,
    Future<void> Function(WidgetTester tester, _FakeStream stream, _FakeFullScreen fullScreen) run, {
    bool fullScreenAgrees = true,
    RemoteControlSessionKind kind = RemoteControlSessionKind.screen,
    Size window = const Size(1600, 1000),
    VoidCallback? onCloseWindow,
  }) {
    testWidgets(description, (tester) async {
      final (stream, fullScreen) = await pump(
        tester,
        fullScreenAgrees: fullScreenAgrees,
        kind: kind,
        window: window,
        onCloseWindow: onCloseWindow,
      );
      await run(tester, stream, fullScreen);
      await tester.pumpWidget(const SizedBox());
    });
  }

  screenTest('offers no display picker for a host with one display', (tester, stream, _) async {
    // `pumpAndSettle`, not `pump`: the bloc raises the geometry event from an async stream callback,
    // so the state it produces lands a microtask after the frame a single pump renders.
    stream.emit(_oneDisplay);
    await tester.pumpAndSettle();

    expect(find.text('Display'), findsNothing);
  });

  screenTest('offers the picker once the host reports two, naming the primary', (tester, stream, _) async {
    stream.emit(_twoDisplays);
    await tester.pumpAndSettle();

    expect(find.text('Display'), findsOneWidget);
    // The label the agent built, not one composed here — two identical monitors are told apart by
    // what the host calls them and by which is primary, and nothing else.
    expect(find.text('Display 1 (1920 x 1080) — primary'), findsWidgets);
  });

  screenTest('sends the display the administrator picked, and nothing before that', (tester, stream, _) async {
    stream.emit(_twoDisplays);
    await tester.pumpAndSettle();
    expect(stream.sent, isEmpty);

    await tester.tap(find.text('Display 1 (1920 x 1080) — primary').first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Display 2 (1920 x 1080)').last);
    await tester.pumpAndSettle();

    // The id the agent offered, echoed back unchanged: it is opaque to this client, and reading
    // anything into it is what a mapping table between the two ends would be.
    expect(stream.sent, [isA<RemoteDisplaySelection>().having((i) => i.displayId, 'displayId', 2)]);
  });

  screenTest('keeps showing the display the agent says it is on, not the one that was clicked',
      (tester, stream, _) async {
    // **A local selection would be a lie on Wayland**, where a switch is asynchronous: the agent
    // renegotiates a portal stream and only then reports the new display. Until it does, the dropdown
    // has to say it is switching rather than claim a display that may never be reached — a monitor
    // can be unplugged between the list being drawn and the choice being made, and the agent then
    // stays where it was.
    stream.emit(_twoDisplays);
    await tester.pumpAndSettle();

    await tester.tap(find.text('Display 1 (1920 x 1080) — primary').first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Display 2 (1920 x 1080)').last);
    await tester.pumpAndSettle();

    // Still on display 1 as far as the agent has said, so that is what is shown.
    expect(find.text('Display 1 (1920 x 1080) — primary'), findsWidgets);
  });

  screenTest('takes no full screen as it opens, and gives it back on the way out if asked for',
      (tester, _, fullScreen) async {
    // The session has a window of its own that the administrator sizes; taking the whole screen
    // unasked would undo that choice.
    expect(fullScreen.enterCalls, 0);

    await tester.tap(find.text('FULL SCREEN'));
    await tester.pumpAndSettle();
    expect(fullScreen.isFullScreen, isTrue);

    await tester.pumpWidget(
      MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: SizedBox())),
    );
    await tester.pump();

    expect(fullScreen.exitCalls, greaterThanOrEqualTo(1));
  });

  screenTest('draws no page heading, and keeps a way out of the session', (tester, stream, _) async {
    await tester.pumpAndSettle();

    stream.emit(_twoDisplays);
    await tester.pumpAndSettle();

    // Every pixel of the window is the session's. Disconnect is the one control a session must
    // never be without. Labels are upper-cased as painted.
    expect(find.text('REMOTE CONTROL'), findsNothing);
    expect(find.text('DISCONNECT'), findsOneWidget);
    expect(find.text('FULL SCREEN'), findsOneWidget);
  });

  screenTest('fits the picture into the window instead of scrolling it', (tester, stream, _) async {
    // **The case that would otherwise scroll, and the point of `fillsViewport`.** A `Column` gives
    // its non-flexible children an unbounded main axis, so the picture's `AspectRatio` sizes itself
    // from the width alone — and a 4:3 host in a wide window is then far taller than the window,
    // with the bottom of somebody's desktop reachable only by scrolling. Dragging the window smaller
    // has to shrink the picture, so it is `Flexible` here and fits.
    stream.emit(_fourByThreeHost);
    await tester.pumpAndSettle();

    final picture = tester.getRect(find.byType(AspectRatio));

    expect(picture.height, lessThanOrEqualTo(700), reason: 'taller than the window: $picture');
    expect(picture.width, lessThanOrEqualTo(1600));
    // And it is still a real picture rather than having been squeezed to nothing.
    expect(picture.height, greaterThan(200), reason: 'collapsed: $picture');
  }, window: const Size(1600, 700));

  screenTest('lays a full-screen terminal out, which has no aspect ratio to size itself by',
      (tester, stream, fullScreen) async {
    // The screen view survives an unbounded height because `AspectRatio` derives one from the
    // width; a terminal has no intrinsic height at all, so a frame that handed it one would throw
    // rather than render. Both kinds share this screen, so both have to be pumped through it.
    //
    // **Reached by pressing the toggle, because that is now the only way a terminal gets here.** A
    // shell session does not ask for full screen as it opens, so a test that pumped one and
    // asserted the full-screen layout would be asserting a state the app cannot produce.
    stream.emit(const RemoteShellInfo(shell: '/bin/zsh', user: 'root'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('FULL SCREEN'));
    await tester.pumpAndSettle();
    expect(fullScreen.isFullScreen, isTrue);

    expect(tester.takeException(), isNull);
    expect(find.byType(RemoteShellView), findsOneWidget);
    expect(tester.getRect(find.byType(RemoteShellView)).height, greaterThan(100));
    expect(find.text('DISCONNECT'), findsOneWidget);
  }, kind: RemoteControlSessionKind.shell);

  screenTest('offers the toggle again rather than an error when the browser refuses',
      (tester, _, fullScreen) async {
    // A permissions policy or a user agent configured to decline: the window stays as it was and the
    // toggle is still there to press.
    await tester.tap(find.text('FULL SCREEN'));
    await tester.pumpAndSettle();

    expect(fullScreen.isFullScreen, isFalse);
    expect(find.text('FULL SCREEN'), findsOneWidget);
  }, fullScreenAgrees: false);

  screenTest('offers Close Window in its own popup, and closes it after hanging up',
      (tester, stream, _) async {
    stream.emit(_oneDisplay);
    await tester.pumpAndSettle();

    expect(find.text('BACK TO HOSTS'), findsNothing);
    await tester.tap(find.text('CLOSE WINDOW'));
    await tester.pumpAndSettle();

    expect(_closed, 1);
  }, onCloseWindow: () => _closed++);

  screenTest('offers Back to Hosts in an ordinary tab, which a script cannot close',
      (tester, stream, _) async {
    expect(find.text('BACK TO HOSTS'), findsOneWidget);
    expect(find.text('CLOSE WINDOW'), findsNothing);
  });

  group('support tools', () {
    screenTest('an agent that announced nothing gets no chat, paste or Ctrl+Alt+Del', (tester, stream, _) async {
      // Every agent from before these existed, and the Linux one today.
      stream.emit(_oneDisplay);
      await tester.pumpAndSettle();

      expect(find.text('CHAT'), findsNothing);
      expect(find.text('PASTE AS KEYSTROKES'), findsNothing);
      expect(find.text('Ctrl+Alt+Del'), findsNothing);
      // An agent too old to say what it runs on keeps the Mac buttons it always had.
      expect(find.text('Force Quit ⌘⌥⎋'), findsOneWidget);
    });

    screenTest('a Windows host gets Ctrl+Alt+Del as its own message, and no ⌘ shortcuts',
        (tester, stream, _) async {
      stream.emit(_windowsHost);
      await tester.pumpAndSettle();

      // ⌘ arrives on Windows as the Windows key, so "Close Window ⌘W" would open Widgets.
      expect(find.text('Force Quit ⌘⌥⎋'), findsNothing);

      await tester.tap(find.text('Ctrl+Alt+Del'));
      await tester.pump();

      expect(stream.sent, [isA<RemoteSecureAttentionInput>()]);
    });

    screenTest('sends a chat message and shows it in the transcript', (tester, stream, _) async {
      stream.emit(_windowsHost);
      await tester.pumpAndSettle();

      await tester.tap(find.text('CHAT'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField), 'Can you see my cursor?');
      await tester.tap(find.text('SEND'));
      await tester.pumpAndSettle();

      expect(stream.sent.whereType<RemoteChatInput>().single.text, 'Can you see my cursor?');
      expect(find.text('Can you see my cursor?'), findsOneWidget);
    });

    screenTest('opens the chat by itself when the host replies', (tester, stream, _) async {
      // A reply nobody sees is worse than a panel that opened by itself.
      stream.emit(_windowsHost);
      await tester.pumpAndSettle();
      expect(find.text('HIDE CHAT'), findsNothing);

      stream.emit(const RemoteChatReceived('Yes, go ahead'));
      await tester.pumpAndSettle();

      expect(find.text('Yes, go ahead'), findsOneWidget);
      expect(find.text('HIDE CHAT'), findsOneWidget);
    });

    screenTest('types whatever is on the clipboard', (tester, stream, _) async {
      tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
        if (call.method == 'Clipboard.getData') return {'text': 'p@ss w0rd'};
        return null;
      });
      addTearDown(() => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));

      stream.emit(_windowsHost);
      await tester.pumpAndSettle();

      await tester.tap(find.text('PASTE AS KEYSTROKES'));
      await tester.pumpAndSettle();

      expect(stream.sent.whereType<RemoteTypeTextInput>().single.text, 'p@ss w0rd');
      expect(find.text('Typing 9 characters from your clipboard.'), findsOneWidget);
    });

    screenTest('says so when there is nothing to paste, and sends nothing', (tester, stream, _) async {
      tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async => null);
      addTearDown(() => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));

      stream.emit(_windowsHost);
      await tester.pumpAndSettle();

      await tester.tap(find.text('PASTE AS KEYSTROKES'));
      await tester.pumpAndSettle();

      expect(stream.sent.whereType<RemoteTypeTextInput>(), isEmpty);
      expect(find.textContaining('Nothing to paste'), findsOneWidget);
    });

    screenTest('cuts a long paste to what the agent will type, at a character boundary',
        (tester, stream, _) async {
      // Emoji are two UTF-16 units each: a cut in the middle of one would put a lone surrogate in
      // the JSON, which the agent refuses — dropping the whole paste.
      final long = '😀' * (RemoteControlState.maxTypedChars + 5);
      tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
        if (call.method == 'Clipboard.getData') return {'text': long};
        return null;
      });
      addTearDown(() => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));

      stream.emit(_windowsHost);
      await tester.pumpAndSettle();

      await tester.tap(find.text('PASTE AS KEYSTROKES'));
      await tester.pumpAndSettle();

      final typed = stream.sent.whereType<RemoteTypeTextInput>().single.text;
      expect(typed.runes.length, RemoteControlState.maxTypedChars);
      expect(typed.runes.every((rune) => rune == '😀'.runes.single), isTrue);
      expect(find.textContaining('the rest was left out'), findsOneWidget);
    });
  });
}

int _closed = 0;
