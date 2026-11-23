import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import '../../core/router/app_router.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/theme/theme_cubit.dart';
import '../../core/theme/theme_presets.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../domain/entities/appearance.dart';
import 'appearance_cubit.dart';

/// Settings > Appearance: how Kintsugi looks for everyone at the company.
///
/// A choice here is tried on the whole app at once (the cubit's preview), so the company sees the
/// result before it saves. Leaving the screen without saving drops the preview. What is saved is
/// company-wide; the sun/moon button in the sidebar still lets one browser override the colour mode.
class AppearanceSettingsScreen extends StatefulWidget {
  const AppearanceSettingsScreen({super.key});

  @override
  State<AppearanceSettingsScreen> createState() => _AppearanceSettingsScreenState();
}

class _AppearanceSettingsScreenState extends State<AppearanceSettingsScreen> {
  late final AppearanceCubit _cubit = context.read<AppearanceCubit>();
  late AppearanceSettings _draft = _cubit.state.saved;
  late final TextEditingController _accent = TextEditingController(text: _draft.accentHex ?? '');

  @override
  void dispose() {
    _accent.dispose();
    // After this frame: the preview change rebuilds the whole app, which cannot happen while the
    // tree is being torn down.
    Future.microtask(_cubit.discardPreview);
    super.dispose();
  }

  void _set(AppearanceSettings draft) {
    setState(() => _draft = draft);
    _cubit.preview(draft);
  }

  void _reset() {
    final saved = _cubit.state.saved;
    _accent.text = saved.accentHex ?? '';
    _set(saved);
  }

  void _typedAccent(String text) {
    if (text.trim().isEmpty) return _set(_draft.copyWith(clearAccent: true));
    final colour = parseAccentHex(text);
    if (colour == null) return setState(() {});
    final hex = '#${colour.toARGB32().toRadixString(16).substring(2).toUpperCase()}';
    _set(_draft.copyWith(accentHex: hex));
  }

  Future<void> _save() async {
    final theme = context.read<ThemeCubit>();
    // Saving makes the company's choice the one on screen. A sun/moon pick made earlier in this
    // browser would otherwise hide the colour mode just saved.
    await _cubit.save(_draft, onAccepted: theme.useCompanyDefault);
  }

  @override
  Widget build(BuildContext context) => BlocListener<AppearanceCubit, AppearanceState>(
        // The settings can arrive after this screen opened (a bookmarked address): adopt them,
        // unless the company has already started changing something.
        listenWhen: (previous, current) => previous.saved != current.saved && current.preview == null,
        listener: (_, state) {
          _accent.text = state.saved.accentHex ?? '';
          setState(() => _draft = state.saved);
        },
        child: BlocBuilder<AppearanceCubit, AppearanceState>(
          builder: (context, state) {
            final accentText = _accent.text.trim();
            final accentInvalid = accentText.isNotEmpty && parseAccentHex(accentText) == null;
            final dirty = _draft != state.saved;
            return PageScaffold(
              title: 'Appearance',
              subtitle: 'How Kintsugi looks for everyone at your company. Changes show here straight away '
                  'and are kept when you save.',
              children: [
                if (state.justSaved) const AlertBox.success('Settings saved.'),
                if (state.error != null) AlertBox.error(state.error!),
                _Section(
                  title: 'Colour mode',
                  hint: 'The default for every administrator. Anyone can still switch light or dark for '
                      'their own browser with the button at the bottom of the sidebar.',
                  child: Wrap(spacing: 12, runSpacing: 12, children: [
                    for (final (mode, label, icon) in const [
                      ('light', 'Light', Icons.light_mode_outlined),
                      ('dark', 'Dark', Icons.dark_mode_outlined),
                      ('system', 'System', Icons.desktop_windows_outlined),
                    ])
                      _Choice(
                        key: Key('appearance-mode-$mode'),
                        label: label,
                        leading: Icon(icon, size: 20, color: context.palette.neon),
                        selected: _draft.colourMode == mode,
                        onTap: () => _set(_draft.copyWith(colourMode: mode)),
                      ),
                  ]),
                ),
                _Section(
                  title: 'Theme',
                  hint: 'Changes the accent colour: headings, buttons, links and highlights. Status colours '
                      '(green, amber, red) keep their meaning in every theme.',
                  child: Wrap(spacing: 12, runSpacing: 12, children: [
                    for (final preset in themePresets)
                      _Choice(
                        key: Key('appearance-preset-${preset.key}'),
                        label: preset.name,
                        leading: _Swatch(preset.accent ?? KintsugiPalette.dark.neon),
                        selected: _draft.accentHex == null && _draft.presetKey == preset.key,
                        onTap: () {
                          _accent.clear();
                          _set(_draft.copyWith(presetKey: preset.key, clearAccent: true));
                        },
                      ),
                  ]),
                ),
                _Section(
                  title: 'Your own accent colour',
                  hint: 'Optional, and used instead of the theme above. Kintsugi adjusts the shade where '
                      'needed so text stays easy to read on both the light and the dark page.',
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 320),
                    child: LabelledField(
                      label: 'Colour (#RRGGBB)',
                      child: Row(children: [
                        Expanded(
                          child: KintsugiTextField(
                            key: const Key('appearance-accent'),
                            controller: _accent,
                            hintText: '#7C3AED',
                            onChanged: _typedAccent,
                            errorText: accentInvalid ? 'Use six hex digits, for example #7C3AED.' : null,
                          ),
                        ),
                        if (parseAccentHex(accentText) != null) ...[
                          const SizedBox(width: 12),
                          _Swatch(parseAccentHex(accentText)!),
                        ],
                      ]),
                    ),
                  ),
                ),
                _Section(
                  title: 'Organisation',
                  hint: 'The name and logo shown at the top of the sidebar. They are the same ones '
                      'end users see on patching pop-ups, so they are changed in Patching Policy.',
                  child: Wrap(spacing: 20, runSpacing: 12, crossAxisAlignment: WrapCrossAlignment.center, children: [
                    _OrganisationPreview(state),
                    SecondaryButton(
                      label: 'Change in Patching Policy',
                      onPressed: () => context.go(Routes.settingsPatchingPolicy),
                    ),
                  ]),
                ),
                Wrap(spacing: 12, runSpacing: 12, children: [
                  PrimaryButton(
                    key: const Key('appearance-save'),
                    label: 'Save',
                    busy: state.saving,
                    onPressed: dirty && !accentInvalid ? _save : null,
                  ),
                  SecondaryButton(
                    key: const Key('appearance-reset'),
                    label: 'Reset',
                    onPressed: dirty ? _reset : null,
                  ),
                ]),
              ],
            );
          },
        ),
      );
}

class _Section extends StatelessWidget {
  const _Section({required this.title, required this.hint, required this.child});

  final String title;
  final String hint;
  final Widget child;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 20),
        child: KintsugiPanel(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              SizedBox(
                width: double.infinity,
                child: Text(title.toUpperCase(), style: Theme.of(context).textTheme.titleLarge),
              ),
              const SizedBox(height: 6),
              Text(hint, style: Theme.of(context).textTheme.bodySmall),
              const SizedBox(height: 16),
              child,
            ],
          ),
        ),
      );
}

/// One option in a row of them: a button-sized card, outlined in the accent when it is the choice.
class _Choice extends StatelessWidget {
  const _Choice({super.key, required this.label, required this.leading, required this.selected, required this.onTap});

  final String label;
  final Widget leading;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return Semantics(
      button: true,
      selected: selected,
      label: label,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(6),
        child: Container(
          width: 168,
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
          decoration: BoxDecoration(
            color: selected ? palette.accentWash(0.10) : Colors.transparent,
            border: Border.all(color: selected ? palette.neon : palette.border, width: selected ? 2 : 1),
            borderRadius: BorderRadius.circular(6),
          ),
          child: Row(children: [
            leading,
            const SizedBox(width: 12),
            Flexible(
              child: Text(
                label.toUpperCase(),
                overflow: TextOverflow.ellipsis,
                style: AppTheme.display(color: selected ? palette.neonSoft : palette.text, size: 11.5),
              ),
            ),
          ]),
        ),
      ),
    );
  }
}

class _Swatch extends StatelessWidget {
  const _Swatch(this.colour);

  final Color colour;

  @override
  Widget build(BuildContext context) => Container(
        width: 22,
        height: 22,
        decoration: BoxDecoration(
          color: colour,
          shape: BoxShape.circle,
          border: Border.all(color: context.palette.border),
        ),
      );
}

class _OrganisationPreview extends StatelessWidget {
  const _OrganisationPreview(this.state);

  final AppearanceState state;

  @override
  Widget build(BuildContext context) {
    final logo = state.organisationLogoPng;
    final name = state.organisationName;
    return Row(mainAxisSize: MainAxisSize.min, children: [
      if (logo != null)
        Image.memory(logo, height: 40, errorBuilder: (_, _, _) => const SizedBox.shrink())
      else
        Icon(Icons.apartment_outlined, size: 32, color: context.palette.muted),
      const SizedBox(width: 12),
      Text(
        name ?? 'No organisation name set',
        style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: name == null ? context.palette.muted : null),
      ),
    ]);
  }
}
