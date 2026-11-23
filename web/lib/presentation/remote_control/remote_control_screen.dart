import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../../core/di/locator.dart';
import '../../core/platform/full_screen.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/remote_control_session.dart';
import '../../domain/usecases/remote_control_usecases.dart';
import 'remote_chat_panel.dart';
import 'remote_control_bloc.dart';
import 'remote_screen_view.dart';
import 'remote_shell_view.dart';
import 'terminal_copy_setting.dart';

/// One host's remote session — its screen, or a terminal on it.
///
/// Both kinds are this screen because they are the same session underneath: the same request, the
/// same poll, the same relayed socket, the same audit row. What differs is what the panel holds and
/// whether anybody at the host was asked.
///
/// A **screen** session starts by *asking*: the host's own user gets a dialog naming the
/// administrator, and nothing is captured or shown here until they allow it, so everything before
/// that point is a waiting state. A **shell** session asks nobody and opens as soon as the agent
/// answers — see `RemoteControlSessionKind` on the server for why, and what stands in for the
/// dialog.
///
/// **It opens in a window of its own**, a popup the Hosts screen opens, and draws no sidebar: the
/// administrator sizes it by dragging its edges and the picture fits whatever they choose. A
/// bookmarked address opened in a tab gets the same chrome-less page with "Back to Hosts" in place
/// of "Close Window".
class RemoteControlScreen extends StatefulWidget {
  const RemoteControlScreen({
    required this.hostId,
    this.kind = RemoteControlSessionKind.screen,
    this.hostname,
    this.fullScreen,
    this.onCloseWindow,
    super.key,
  });

  final String hostId;

  /// What to open. Fixed by the route rather than chosen here, so the two are separate addresses a
  /// support call can be pointed at.
  final RemoteControlSessionKind kind;

  /// Passed through from the Hosts screen so the heading names the host immediately, before the
  /// first response has arrived. Absent on a bookmarked or hand-typed URL, which is why the screen
  /// falls back to the session's own copy.
  final String? hostname;

  /// The browser's full-screen mode, or null in a test that has not registered one.
  ///
  /// Nullable rather than required so this screen can be pumped without a DOM — `package:web` is
  /// unavailable under `flutter test`, where `kIsWeb` is false. Null means the toggle is not
  /// offered and nothing is requested.
  final FullScreenController? fullScreen;

  /// Closes the popup window this screen is in, or null when it is not in one (a bookmarked address
  /// opened in an ordinary tab, where a script cannot close the window and "Back to Hosts" is
  /// offered instead).
  final VoidCallback? onCloseWindow;

  @override
  State<RemoteControlScreen> createState() => _RemoteControlScreenState();
}

class _RemoteControlScreenState extends State<RemoteControlScreen> {
  StreamSubscription<bool>? _fullScreenChanges;
  bool _isFullScreen = false;

  /// Kept here rather than in the view, which rebuilds on every state change: the switch and the
  /// terminal both need the one answer, and it has to outlive a rebuild. Backed by the browser's
  /// storage when the app has it, and by memory alone when it does not.
  late final TerminalCopySetting _allowCopy = TerminalCopySetting(
    locator.isRegistered<SharedPreferences>() ? locator<SharedPreferences>() : null,
  );

  /// Set when the browser refused a full-screen request, so the screen can offer the button rather
  /// than silently doing nothing. Only ever asked from the toggle, where the press itself is the
  /// gesture the browser was waiting for, so a refusal here has some other cause: a permissions
  /// policy on the page, or a user agent configured to decline.
  bool _fullScreenRefused = false;

  @override
  void initState() {
    super.initState();

    final fullScreen = widget.fullScreen;
    if (fullScreen == null) return;

    // **Nothing is requested as the screen opens any more.** It used to take the whole browser for
    // a screen session, because a desktop inside the sidebar's 240px-inset panel was the host at
    // half the size it could be. The session now has a window of its own that the administrator
    // sizes by dragging its edges, so taking the screen unasked would undo the one choice they just
    // made. The toggle is still here for whoever wants it.
    _isFullScreen = fullScreen.isFullScreen;
    _fullScreenChanges = fullScreen.onChanged.listen((isFullScreen) {
      if (mounted) setState(() => _isFullScreen = isFullScreen);
    });
  }

  @override
  void dispose() {
    _allowCopy.dispose();
    _fullScreenChanges?.cancel();
    // Left on the way out rather than when the session ends: a page that stayed full-screen after
    // navigating back to Hosts would show that screen with no sidebar and no way to guess why.
    widget.fullScreen?.exit();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => RemoteControlBloc(
          requestSession: locator<RequestRemoteControlSession>(),
          getSession: locator<GetRemoteControlSession>(),
          endSession: locator<EndRemoteControlSession>(),
          openStream: locator<OpenRemoteControlStream>(),
        )..add(RemoteControlRequested(widget.hostId, widget.kind)),
        child: _RemoteControlView(
          kind: widget.kind,
          hostname: widget.hostname,
          isFullScreen: _isFullScreen,
          // Hidden entirely when there is no controller — a button that cannot do anything is worse
          // than no button. Offered whenever there is one, including after a refusal, because the
          // press itself is the gesture the browser was waiting for.
          onToggleFullScreen: widget.fullScreen == null
              ? null
              : () async {
                  final fullScreen = widget.fullScreen!;
                  if (_isFullScreen) {
                    await fullScreen.exit();
                  } else if (!await fullScreen.enter() && mounted) {
                    setState(() => _fullScreenRefused = true);
                  }
                },
          fullScreenRefused: _fullScreenRefused,
          onCloseWindow: widget.onCloseWindow,
          allowCopy: _allowCopy,
        ),
      );
}

class _RemoteControlView extends StatelessWidget {
  const _RemoteControlView({
    required this.kind,
    required this.isFullScreen,
    required this.fullScreenRefused,
    this.hostname,
    this.onToggleFullScreen,
    this.onCloseWindow,
    this.allowCopy,
  });

  final RemoteControlSessionKind kind;
  final String? hostname;
  final bool isFullScreen;
  final bool fullScreenRefused;
  final Future<void> Function()? onToggleFullScreen;
  final VoidCallback? onCloseWindow;

  /// The terminal's "copy when I let go of the mouse" setting; null where there is nothing to offer it.
  final TerminalCopySetting? allowCopy;

  @override
  Widget build(BuildContext context) => BlocBuilder<RemoteControlBloc, RemoteControlState>(
        builder: (context, state) {
          final session = state.session;
          final name = session?.hostname.isNotEmpty == true ? session!.hostname : (hostname ?? 'this host');

          final shell = state.shell;

          final subtitle = session == null
              ? 'Connecting to $name'
              // The account is named the moment it is known: root on macOS and Linux, SYSTEM on
              // Windows, never the logged-in user. Shown rather than assumed because a shell
              // running as somebody's own account would be a different thing to hand out, and an
              // administrator should be able to see which they have.
              : shell != null
                  ? '$name — ${shell.shell} as ${shell.user}, requested by ${session.requestedBy}'
                  : '$name — requested by ${session.requestedBy}';

          final geometry = state.geometry;
          final offersChat = state.isStreaming &&
              geometry != null &&
              geometry.supports(RemoteDisplayGeometry.featureChat);

          // The browser window's own title, which is what the operating system's window list and
          // the taskbar show — the way to find this session among everything else that is open.
          return Title(
            title: '${kind == RemoteControlSessionKind.shell ? 'Terminal' : 'Remote Control'} — $name',
            color: Theme.of(context).colorScheme.primary,
            child: Scaffold(
              body: _WindowFrame(
                subtitle: subtitle,
                actions: _buildActions(context, state),
                notice: state.error != null ? AlertBox.error(state.error!) : _buildConsentNotice(state),
                body: _buildBody(context, state),
                // Kept while the session lasts so the conversation can be read back; hidden only by
                // the administrator, and reopened by any reply from the host.
                chat: state.chatOpen && (offersChat || state.chat.isNotEmpty)
                    ? RemoteChatPanel(
                        lines: state.chat,
                        hostname: name,
                        canSend: offersChat,
                        onSend: (text) => context.read<RemoteControlBloc>().add(RemoteControlChatSent(text)),
                        onClose: () =>
                            context.read<RemoteControlBloc>().add(const RemoteControlChatToggled(open: false)),
                      )
                    : null,
              ),
            ),
          );
        },
      );

  /// The consent state, said explicitly rather than left implicit in an empty screen.
  ///
  /// Each of these is something the administrator has to act on — wait, give up, or go and speak to
  /// somebody — so none of them should look like a loading spinner.
  Widget _buildConsentNotice(RemoteControlState state) {
    final session = state.session;
    if (session == null) return const SizedBox.shrink();

    // Said plainly rather than left to be assumed from a terminal that simply opened. Nobody at this
    // host was asked, and an administrator should know that is what they are doing — the record of
    // who opened it is the only thing standing in for the dialog a screen session shows.
    //
    // Deliberately says nothing about whether the host shows the session, because the three agents
    // differ: the macOS one puts it in the menu bar (`tray_menu::report_remote_session`), and the
    // Linux and Windows shells run outside any desktop and announce nothing at all. A sentence
    // claiming either would be false on some of the fleet, and the honesty of this notice is the
    // whole reason it exists.
    if (session.kind == RemoteControlSessionKind.shell && session.isConnectable) {
      return AlertBox.info(
        'Nobody at ${session.hostname} was asked before this terminal opened. The session has been '
        'recorded against your name.',
      );
    }

    // A shell the agent has not answered yet gets no notice at all. It is Pending only in the
    // window between the request and the agent reporting NotRequired — waiting for the *agent*,
    // never for a person — and both sentences available below claim somebody is looking at a dialog
    // that was never raised. The body says it is connecting instead, and the notice above takes over
    // the moment the answer arrives. Deliberately narrow: an unreachable or unavailable shell still
    // falls through to the switch, which is where those are said out loud.
    if (session.kind == RemoteControlSessionKind.shell &&
        session.consent == RemoteControlConsent.pending &&
        session.endedAtUtc == null) {
      return const SizedBox.shrink();
    }

    if (session.isAwaitingConsent) {
      return AlertBox.info(
        'Waiting for the person at ${session.hostname} to allow this. They have been shown a dialog '
        'naming you; nothing is captured until they agree.',
      );
    }

    return switch (session.consent) {
      _ when session.endedAtUtc != null =>
        AlertBox.info('The session ended: ${session.endReason ?? 'the connection closed'}.'),
      final consent when !session.isConnectable => AlertBox.info(consent.label),
      _ => const SizedBox.shrink(),
    };
  }

  Widget _buildActions(BuildContext context, RemoteControlState state) {
    final bloc = context.read<RemoteControlBloc>();
    final session = state.session;
    final live = session != null && session.endedAtUtc == null;

    final geometry = state.geometry;

    return Wrap(
      spacing: 8,
      runSpacing: 8,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        // Only where there is a genuine choice: one display is not one, and a picker naming the
        // single screen a laptop has is a control that can only be set to where it already is.
        if (state.isStreaming && geometry != null && geometry.hasDisplayChoice)
          _DisplayPicker(
            displays: geometry.displays,
            activeDisplayId: geometry.activeDisplayId,
            onChanged: (id) => bloc.add(RemoteControlInputSent(RemoteDisplaySelection(id))),
          ),
        if (state.isStreaming && state.geometry != null)
          _QualityPicker(
            // Keyed on the session, so a second session on this screen does not show the last
            // one's setting beside an agent that has gone back to its own default.
            key: ValueKey(session?.id),
            onChanged: (quality) => bloc.add(
              RemoteControlInputSent(RemoteQualityInput(jpegQuality: quality)),
            ),
          ),
        // Each offered only where the agent announced it (`DisplayInfo.features`), so an older
        // agent — or a Linux one, which has no chat window to draw yet — shows no button that
        // would send into silence.
        if (state.isStreaming && geometry != null && geometry.supports(RemoteDisplayGeometry.featureChat))
          SecondaryButton(
            label: state.chatOpen ? 'Hide Chat' : 'Chat',
            onPressed: () => bloc.add(RemoteControlChatToggled(open: !state.chatOpen)),
          ),
        if (state.isStreaming &&
            geometry != null &&
            geometry.canControlInput &&
            geometry.supports(RemoteDisplayGeometry.featureTypeText))
          SecondaryButton(
            label: 'Paste as Keystrokes',
            tooltip: 'Types your clipboard on the host, character by character — for a password '
                'prompt, a sign-in screen or anything else that will not take a paste.',
            onPressed: () async {
              // Read on the press, so it is whatever was copied last. The browser may ask the
              // administrator to allow this once; a refusal arrives as null and is said as such.
              String? text;
              try {
                text = (await Clipboard.getData(Clipboard.kTextPlain))?.text;
              } on PlatformException {
                text = null;
              }
              // The window may have closed while the browser was asking.
              if (bloc.isClosed) return;
              bloc.add(RemoteControlPasteRequested(text));
            },
          ),
        if (onToggleFullScreen != null)
          SecondaryButton(
            label: isFullScreen ? 'Exit Full Screen' : 'Full Screen',
            onPressed: () => onToggleFullScreen!(),
          ),
        if (live)
          SecondaryButton(
            label: 'Disconnect',
            onPressed: () => bloc.add(const RemoteControlDisconnectRequested()),
          ),
        SecondaryButton(
          label: onCloseWindow != null ? 'Close Window' : 'Back to Hosts',
          onPressed: () {
            // Ends the session on the way out rather than leaving it running because the page went
            // away. The relay would end it when this page's socket closed anyway, but saying so is
            // what records the administrator as the one who hung up.
            if (live) bloc.add(const RemoteControlDisconnectRequested());
            if (onCloseWindow != null) {
              onCloseWindow!();
            } else {
              context.go(Routes.hosts);
            }
          },
        ),
        // Only on a terminal: a remote desktop has no text to select.
        if (allowCopy != null && state.shell != null) _AllowCopySwitch(setting: allowCopy!),
        if (state.notice != null) Text(state.notice!, style: Theme.of(context).textTheme.bodySmall),
      ],
    );
  }

  Widget _buildBody(BuildContext context, RemoteControlState state) {
    final bloc = context.read<RemoteControlBloc>();

    if (state.shell != null && state.isStreaming) {
      return RemoteShellView(
        // Keyed on the session, so a second session on this screen gets a fresh terminal rather
        // than the last one's scrollback and its stale cursor position.
        key: ValueKey(state.session?.id),
        output: bloc.shellOutput,
        onInput: (input) => bloc.add(RemoteControlInputSent(input)),
        allowCopy: allowCopy,
      );
    }

    final geometry = state.geometry;

    if (geometry == null || !state.isStreaming) {
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: 48),
        child: Center(
          child: Text(
            state.status ?? 'Nothing to show yet.',
            textAlign: TextAlign.center,
            style: Theme.of(context).textTheme.bodyMedium,
          ),
        ),
      );
    }

    return RemoteScreenView(
      geometry: geometry,
      tiles: state.tiles,
      // Always inside `_WindowFrame`'s bounded height, so the picture fits the window — which is
      // what lets dragging the window's edges resize the host's screen.
      fillsViewport: true,
      onInput: (input) => context.read<RemoteControlBloc>().add(RemoteControlInputSent(input)),
    );
  }
}

/// The session's window: the session, its controls, and the chat beside it — nothing else.
///
/// A bare column rather than `PageScaffold` because every pixel of the window is the host's screen
/// or the terminal: the window's size is the administrator's choice, made by dragging its edges, and
/// a heading would spend some of it on nothing. The subtitle survives as one small line, since "who
/// asked, and what account this shell runs as" should not need looking for.
class _WindowFrame extends StatelessWidget {
  const _WindowFrame({
    required this.subtitle,
    required this.actions,
    required this.notice,
    required this.body,
    this.chat,
  });

  final String subtitle;
  final Widget actions;
  final Widget notice;
  final Widget body;

  /// The chat panel, or null when it is hidden or not on offer.
  final Widget? chat;

  /// Wide enough to read a sentence, narrow enough that a laptop-sized window keeps most of its
  /// width for the host's screen.
  static const double chatWidth = 320;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    subtitle,
                    style: Theme.of(context).textTheme.bodySmall,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            actions,
            notice,
            const SizedBox(height: 8),
            // The session takes everything left, in a box of known height and **no scroll view**.
            // A scroll view here would hand the body an unbounded height, which for the screen view
            // means an `AspectRatio` sized from the width alone: a host screen a different shape
            // from this window would then be taller than the window, with the bottom of somebody's
            // desktop reachable only by scrolling. See `RemoteScreenView.fillsViewport`, which is
            // the other half of this.
            Expanded(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Expanded(child: body),
                  if (chat != null) ...[
                    const SizedBox(width: 12),
                    SizedBox(width: chatWidth, child: chat),
                  ],
                ],
              ),
            ),
          ],
        ),
      );
}

/// Which of the host's displays to watch.
///
/// **Shown only when the host offered more than one**, which is why this widget never has to render
/// an empty or single-entry state — see `RemoteDisplayGeometry.hasDisplayChoice`.
///
/// Stateless, unlike the quality picker beside it: the agent is the authority on which display is
/// being shown, and it says so in every geometry message. Holding a local selection would let the
/// dropdown claim a display the switch had failed to reach — which is a real case, since a monitor
/// can be unplugged between the list being drawn and the choice being made, and the agent then
/// stays where it was and says so.
class _DisplayPicker extends StatelessWidget {
  const _DisplayPicker({
    required this.displays,
    required this.activeDisplayId,
    required this.onChanged,
  });

  final List<RemoteDisplayOption> displays;
  final int activeDisplayId;
  final ValueChanged<int> onChanged;

  @override
  Widget build(BuildContext context) {
    // A value the list does not contain would assert inside DropdownButton. It happens legitimately
    // in the moment before the first geometry of a switched-to display arrives on Wayland, where the
    // active id follows the *frames* rather than the request.
    final value = displays.any((display) => display.id == activeDisplayId) ? activeDisplayId : null;

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        const Text('Display'),
        const SizedBox(width: 8),
        DropdownButton<int>(
          value: value,
          hint: const Text('Switching…'),
          onChanged: (id) {
            if (id == null || id == activeDisplayId) return;
            onChanged(id);
          },
          items: [
            for (final display in displays)
              DropdownMenuItem(
                value: display.id,
                child: Text(display.isPrimary ? '${display.label} — primary' : display.label),
              ),
          ],
        ),
      ],
    );
  }
}

/// How much picture to spend bandwidth on.
///
/// Worth having rather than a fixed quality: the same session is run over an office LAN and over a
/// phone tether, and the useful setting is different by an order of magnitude. Changing it makes the
/// agent resend the whole screen, so the change is visible immediately.
class _QualityPicker extends StatefulWidget {
  const _QualityPicker({required this.onChanged, super.key});

  final ValueChanged<int> onChanged;

  @override
  State<_QualityPicker> createState() => _QualityPickerState();
}

class _QualityPickerState extends State<_QualityPicker> {
  int _quality = 60;

  @override
  Widget build(BuildContext context) => Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Text('Quality'),
          const SizedBox(width: 8),
          DropdownButton<int>(
            value: _quality,
            onChanged: (quality) {
              if (quality == null) return;
              setState(() => _quality = quality);
              widget.onChanged(quality);
            },
            items: const [
              DropdownMenuItem(value: 30, child: Text('Low')),
              DropdownMenuItem(value: 60, child: Text('Normal')),
              DropdownMenuItem(value: 85, child: Text('High')),
            ],
          ),
        ],
      );
}

/// The "Allow copy" switch beside the terminal's other buttons: on, selecting text with the mouse copies it
/// as soon as the button is released. See [TerminalCopySetting].
class _AllowCopySwitch extends StatelessWidget {
  const _AllowCopySwitch({required this.setting});

  final TerminalCopySetting setting;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return ValueListenableBuilder<bool>(
      valueListenable: setting,
      builder: (context, on, _) => Tooltip(
        message: 'Select text with the mouse and it is copied as soon as you let go. '
            'Off, copying is ${defaultTargetPlatform == TargetPlatform.macOS ? 'Cmd+C' : 'Ctrl+Shift+C'}.',
        child: Semantics(
          button: true,
          toggled: on,
          label: 'Allow copy',
          child: InkWell(
            borderRadius: BorderRadius.circular(4),
            onTap: () => unawaited(setting.toggle()),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
              child: Row(mainAxisSize: MainAxisSize.min, children: [
                Container(
                  width: 40,
                  height: 20,
                  alignment: on ? Alignment.centerRight : Alignment.centerLeft,
                  padding: const EdgeInsets.all(2),
                  decoration: BoxDecoration(
                    border: Border.all(color: on ? palette.green : palette.border),
                    borderRadius: BorderRadius.circular(12),
                    color: palette.background,
                  ),
                  child: Container(
                    width: 14,
                    height: 14,
                    decoration: BoxDecoration(shape: BoxShape.circle, color: on ? palette.green : palette.muted),
                  ),
                ),
                const SizedBox(width: 10),
                Text(
                  'ALLOW COPY',
                  style: AppTheme.label(color: on ? palette.action : palette.muted, size: 10.9,),
                ),
              ]),
            ),
          ),
        ),
      ),
    );
  }
}
