import 'package:flutter/material.dart';

import '../theme/kintsugi_palette.dart';
import 'gradient_spinner.dart';

/// The guide's button label: 14px bold, sentence case as written — capitals are for labels and
/// badges, never buttons (htw-style STYLE-GUIDE.md, "Buttons").
TextStyle _buttonLabel(BuildContext context, Color color) =>
    Theme.of(context).textTheme.bodyMedium!.copyWith(color: color, fontWeight: FontWeight.w700, height: 1.2);

/// The shared shape of both pills: 38px tall, 18px across, the full radius.
ButtonStyle _pill() => const ButtonStyle(
      minimumSize: WidgetStatePropertyAll(Size(0, 38)),
      padding: WidgetStatePropertyAll(EdgeInsets.symmetric(horizontal: 18)),
      shape: WidgetStatePropertyAll(StadiumBorder()),
      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
      // Hover is the ring below, never a change of fill.
      overlayColor: WidgetStatePropertyAll(Colors.transparent),
    );

/// The guide's hover: a 2px ring 5px outside the pill, in the button's own colour, over a fill
/// that never changes. Drawn whole rather than tracing clockwise as the guide's does: the whole
/// ring at once is the guide's own `prefers-reduced-motion` form.
class _HoverRing extends StatefulWidget {
  const _HoverRing({required this.color, required this.enabled, required this.child});

  final Color color;
  final bool enabled;
  final Widget child;

  @override
  State<_HoverRing> createState() => _HoverRingState();
}

class _HoverRingState extends State<_HoverRing> {
  bool _hovering = false;

  @override
  Widget build(BuildContext context) => MouseRegion(
        onEnter: (_) => setState(() => _hovering = true),
        onExit: (_) => setState(() => _hovering = false),
        child: Stack(
          clipBehavior: Clip.none,
          // Passthrough, so a button the caller stretches (SizedBox(width: infinity)) still stretches.
          fit: StackFit.passthrough,
          children: [
            widget.child,
            if (_hovering && widget.enabled)
              Positioned(
                left: -5,
                top: -5,
                right: -5,
                bottom: -5,
                child: IgnorePointer(
                  child: DecoratedBox(
                    decoration: ShapeDecoration(
                      shape: StadiumBorder(side: BorderSide(color: widget.color, width: 2)),
                    ),
                  ),
                ),
              ),
          ],
        ),
      );
}

/// The filled accent button — `.btn-primary`. One per screen, on the action the screen is for.
class PrimaryButton extends StatelessWidget {
  const PrimaryButton({super.key, required this.label, required this.onPressed, this.busy = false});

  final String label;
  final VoidCallback? onPressed;

  /// Shows a spinner beside the label and blocks presses. Used while a background run is going,
  /// where the label also changes ("Scan Running…"), so the two together make it obvious the
  /// button is not simply broken.
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final enabled = onPressed != null && !busy;
    // Busy keeps the action fill: "you pressed it and it is running", not "you may not".
    final labelColor = enabled || busy ? palette.onAction : palette.muted;

    return _HoverRing(
      color: palette.action,
      enabled: enabled,
      child: FilledButton(
        onPressed: enabled ? onPressed : null,
        style: _pill().copyWith(
          backgroundColor: WidgetStateProperty.resolveWith(
            (s) => busy
                ? palette.action
                : s.contains(WidgetState.disabled)
                    ? palette.subtle
                    : s.contains(WidgetState.pressed)
                        ? palette.actionHover
                        : palette.action,
          ),
          side: WidgetStateProperty.resolveWith(
            (s) => s.contains(WidgetState.focused)
                ? BorderSide(color: palette.focus, width: 2)
                : s.contains(WidgetState.disabled) && !busy
                    ? BorderSide(color: palette.border)
                    : null,
          ),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (busy) ...[
              SizedBox(
                width: 12,
                height: 12,
                child: CircularProgressIndicator(strokeWidth: 2, color: palette.onAction),
              ),
              const SizedBox(width: 8),
            ],
            Text(label, style: _buttonLabel(context, labelColor)),
          ],
        ),
      ),
    );
  }
}

/// The outline button — `.btn-outline-primary`. A positive action that is not the screen's main
/// one: white, the action border and label, the action tint while pressed.
class SecondaryButton extends StatelessWidget {
  const SecondaryButton({
    super.key,
    required this.label,
    required this.onPressed,
    this.tooltip,
    this.busy = false,
  });

  final String label;
  final VoidCallback? onPressed;
  final String? tooltip;

  /// Replaces the label with a spinner and blocks presses — [PrimaryButton.busy] for a secondary
  /// action. The label is kept in the layout, invisibly, so a button in a [Wrap] with a message
  /// beside it holds its width and nothing to its right moves when the spinner comes and goes.
  /// The border keeps its enabled colour for the same reason the spinner exists at all: a disabled
  /// look says "you may not press this", where this state says "you did, and it is running".
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final enabled = onPressed != null && !busy;
    final labelText = Text(label, style: _buttonLabel(context, enabled ? palette.action : palette.muted));
    final button = _HoverRing(
      color: palette.action,
      enabled: enabled,
      child: OutlinedButton(
        onPressed: enabled ? onPressed : null,
        style: _pill().copyWith(
          backgroundColor: WidgetStateProperty.resolveWith(
            (s) => s.contains(WidgetState.pressed)
                ? palette.actionTint
                : s.contains(WidgetState.disabled) && !busy
                    ? palette.subtle
                    : palette.surface,
          ),
          side: WidgetStateProperty.resolveWith(
            (s) => s.contains(WidgetState.focused)
                ? BorderSide(color: palette.focus, width: 2)
                : BorderSide(color: enabled || busy ? palette.action : palette.border),
          ),
        ),
        child: busy
            ? Stack(
                alignment: Alignment.center,
                children: [
                  Opacity(opacity: 0, child: labelText),
                  GradientSpinner(color: palette.action),
                ],
              )
            : labelText,
      ),
    );

    return tooltip == null ? button : Tooltip(message: tooltip!, child: button);
  }
}

/// A bare icon button in a table's action column — `.icon-btn`.
class IconActionButton extends StatelessWidget {
  const IconActionButton({
    super.key,
    required this.icon,
    required this.onPressed,
    required this.tooltip,
    this.danger = false,
    this.busy = false,
  });

  final IconData icon;
  final VoidCallback? onPressed;
  final String tooltip;

  /// Red rather than accent-coloured, for the one action that cannot be undone from here.
  final bool danger;

  /// Replaces the icon with a spinner and blocks presses — [PrimaryButton.busy] for a table row.
  /// The spinner is drawn *as* the button's icon rather than beside it, so a row that swaps
  /// between the two moves nothing: Material 3 lays an `IconButton` out at 40px whatever
  /// [constraints] below say, and a stand-in sized to that figure would drift with the theme.
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final color = danger ? palette.red : palette.muted;
    return IconButton(
      onPressed: busy ? null : onPressed,
      tooltip: tooltip,
      icon: busy
          ? SizedBox(
              width: 14,
              height: 14,
              child: CircularProgressIndicator(strokeWidth: 2, color: color),
            )
          : Icon(icon, size: 18),
      color: color,
      disabledColor: busy ? color : null,
      hoverColor: (danger ? palette.red : palette.action).withValues(alpha: 0.1),
      constraints: const BoxConstraints(minWidth: 34, minHeight: 34),
      padding: EdgeInsets.zero,
      visualDensity: VisualDensity.compact,
    );
  }
}

/// Text that behaves like a link — `.link-btn` and `.upgrade-link`.
class LinkText extends StatelessWidget {
  const LinkText({super.key, required this.label, required this.onTap, this.muted = false, this.dense = false});

  final String label;
  final VoidCallback onTap;
  final bool muted;

  /// Table-sized type, on one line with an ellipsis: for a name that must never break across lines.
  final bool dense;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: onTap,
        child: Text(
          label,
          maxLines: dense ? 1 : null,
          softWrap: dense ? false : null,
          overflow: dense ? TextOverflow.ellipsis : null,
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                color: muted ? palette.muted : palette.action,
                fontSize: muted || dense ? 12.8 : null,
                decoration: TextDecoration.underline,
                decorationColor: (muted ? palette.muted : palette.action).withValues(alpha: 0.5),
              ),
        ),
      ),
    );
  }
}
