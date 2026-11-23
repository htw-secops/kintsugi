import 'package:flutter/material.dart';

import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/panel.dart';
import 'remote_control_bloc.dart';

/// The administrator's side of a session's chat, docked beside the host's screen.
///
/// The person at the host sees the same conversation in a window their agent draws — see
/// `chat_window` in the Windows agent and the macOS agent's equivalent. Nothing is recorded: the
/// messages travel over the media relay, which the server forwards without reading.
class RemoteChatPanel extends StatefulWidget {
  const RemoteChatPanel({
    required this.lines,
    required this.hostname,
    required this.canSend,
    required this.onSend,
    required this.onClose,
    super.key,
  });

  final List<RemoteChatLine> lines;

  /// What the other side is called here. The agent knows nothing more useful about whoever is at
  /// the keyboard, and the session's own heading already names the host the same way.
  final String hostname;

  /// False once the session has ended: there is nobody to send to, and a line in the transcript
  /// would claim otherwise.
  final bool canSend;

  final ValueChanged<String> onSend;
  final VoidCallback onClose;

  @override
  State<RemoteChatPanel> createState() => _RemoteChatPanelState();
}

class _RemoteChatPanelState extends State<RemoteChatPanel> {
  final _input = TextEditingController();
  final _scroll = ScrollController();

  @override
  void didUpdateWidget(RemoteChatPanel oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Follows the conversation to its newest line, after the frame that lays that line out.
    if (widget.lines.length != oldWidget.lines.length) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (_scroll.hasClients) _scroll.jumpTo(_scroll.position.maxScrollExtent);
      });
    }
  }

  @override
  void dispose() {
    _input.dispose();
    _scroll.dispose();
    super.dispose();
  }

  void _send() {
    final text = _input.text.trim();
    if (text.isEmpty || !widget.canSend) return;
    widget.onSend(text);
    _input.clear();
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final theme = Theme.of(context);

    return KintsugiPanel(
      padding: const EdgeInsets.all(12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(child: Text('Chat with ${widget.hostname}', style: theme.textTheme.titleSmall)),
              IconButton(
                icon: const Icon(Icons.close, size: 18),
                tooltip: 'Hide chat',
                onPressed: widget.onClose,
              ),
            ],
          ),
          const SizedBox(height: 8),
          Expanded(
            child: widget.lines.isEmpty
                ? Center(
                    child: Text(
                      'Messages appear in a window on ${widget.hostname}\'s screen. Nothing here is '
                      'saved once the session ends.',
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodySmall,
                    ),
                  )
                : ListView.separated(
                    controller: _scroll,
                    itemCount: widget.lines.length,
                    separatorBuilder: (_, _) => const SizedBox(height: 10),
                    itemBuilder: (context, index) {
                      final line = widget.lines[index];
                      final fromHere = line.sender == RemoteChatSender.administrator;
                      return Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            '${fromHere ? 'You' : widget.hostname} · ${_time(line.at)}',
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: fromHere ? palette.muted : palette.action,
                            ),
                          ),
                          const SizedBox(height: 2),
                          Text(line.text),
                        ],
                      );
                    },
                  ),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _input,
            enabled: widget.canSend,
            maxLength: RemoteControlState.maxChatChars,
            minLines: 1,
            maxLines: 4,
            // Enter sends, which is what every chat box does; a longer message wraps on its own. A
            // plain-text keyboard type is what stops a multi-line field taking Enter as a newline.
            keyboardType: TextInputType.text,
            textInputAction: TextInputAction.send,
            onSubmitted: (_) => _send(),
            decoration: InputDecoration(
              hintText: widget.canSend ? 'Type a message' : 'The session has ended',
              counterText: '',
              isDense: true,
            ),
          ),
          const SizedBox(height: 8),
          Align(
            alignment: Alignment.centerRight,
            child: SecondaryButton(label: 'Send', onPressed: widget.canSend ? _send : null),
          ),
        ],
      ),
    );
  }

  static String _time(DateTime at) {
    final local = at.toLocal();
    return '${local.hour.toString().padLeft(2, '0')}:${local.minute.toString().padLeft(2, '0')}';
  }
}
