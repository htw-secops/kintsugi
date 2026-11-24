import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/platform/file_picker.dart';
import '../../core/theme/image_accent.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/theme/theme_presets.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../domain/entities/appearance.dart';
import '../../domain/entities/settings.dart';
import 'appearance_cubit.dart';

/// Settings > Appearance: a colour, a picture behind the app, and the company's own logo (optional).
///
/// Everything chosen here is tried on the whole app at once (the cubit's preview), so the company
/// sees the result before it saves. Leaving the page without saving drops the preview. The colour,
/// picture and animation are company-wide settings on the appearance route; the name and logo are
/// the ones the patching policy already holds for the agent's pop-ups.
class AppearanceSettingsScreen extends StatefulWidget {
  const AppearanceSettingsScreen({super.key});

  @override
  State<AppearanceSettingsScreen> createState() => _AppearanceSettingsScreenState();
}

class _AppearanceSettingsScreenState extends State<AppearanceSettingsScreen> {
  late final AppearanceCubit _cubit = context.read<AppearanceCubit>();
  late AppearanceSettings _draft = _cubit.state.saved;
  late Organisation _org = _cubit.state.organisation;
  late final TextEditingController _accent = TextEditingController(text: _draft.accentHex ?? '');
  late final TextEditingController _name = TextEditingController(text: _org.name ?? '');

  /// `PatchingPolicySettings.MaxOrganisationNameLength` on the server, which still has the last word.
  static const _maxNameLength = 80;

  String? _wallpaperError;
  String? _logoError;
  String? _accentNote;

  @override
  void dispose() {
    _accent.dispose();
    _name.dispose();
    // After this frame: the preview change rebuilds the whole app, which cannot happen while the
    // tree is being torn down.
    Future.microtask(_cubit.discardPreview);
    super.dispose();
  }

  void _set(AppearanceSettings draft) {
    setState(() => _draft = draft);
    _cubit.preview(draft);
  }

  void _setOrg(Organisation org) {
    setState(() => _org = org);
    _cubit.previewOrganisation(org);
  }

  void _adopt(AppearanceState state) {
    _accent.text = state.saved.accentHex ?? '';
    _name.text = state.organisation.name ?? '';
    setState(() {
      _draft = state.saved;
      _org = state.organisation;
      _wallpaperError = _logoError = _accentNote = null;
    });
  }

  void _reset() {
    _adopt(_cubit.state);
    _cubit.discardPreview();
  }

  /// A colour typed by hand. A partial or invalid entry is explained under the field and leaves the
  /// last good colour in force.
  void _typedAccent(String text) {
    if (text.trim().isEmpty) return _set(_draft.copyWith(clearAccent: true));
    final colour = parseAccentHex(text);
    if (colour == null) return setState(() {});
    _set(_draft.copyWith(accentHex: _hex(colour)));
  }

  static String _hex(Color colour) => '#${colour.toARGB32().toRadixString(16).substring(2).toUpperCase()}';

  // --- the picture ---------------------------------------------------------------------------

  /// Opens the browser's file dialog. Nothing may be awaited before the call: the browser opens the
  /// dialog only inside the click's user activation (see [FilePicker.pick]).
  Future<void> _chooseWallpaper() async {
    Uint8List? bytes;
    try {
      bytes = await locator<FilePicker>().pick(
        accept: '.png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp',
        maxBytes: AppearanceSettings.maxWallpaperBytes,
      );
    } on FileTooLargeException {
      if (mounted) setState(() => _wallpaperError = AppearanceSettings.wallpaperTooLarge);
      return;
    } catch (_) {
      if (mounted) setState(() => _wallpaperError = 'That file could not be read. Try choosing it again.');
      return;
    }
    if (bytes == null || !mounted) return;
    final problem = AppearanceSettings.wallpaperProblem(bytes);
    if (problem != null) return setState(() => _wallpaperError = problem);
    setState(() => _wallpaperError = null);
    _set(_draft.copyWith(wallpaper: bytes));
    // A colour the company typed or picked is theirs; only an empty choice is filled in for them.
    if (_draft.accentHex == null) await _matchImage();
  }

  void _removeWallpaper() {
    setState(() => _wallpaperError = _accentNote = null);
    _set(_draft.copyWith(clearWallpaper: true));
  }

  /// Sets the colour to the most vivid colour in the picture.
  Future<void> _matchImage() async {
    final image = _draft.wallpaper;
    if (image == null) return;
    final colour = await accentFromImage(image);
    if (!mounted || _draft.wallpaper != image) return;
    if (colour == null) {
      return setState(() => _accentNote = 'This image has no clear colour to match. Pick a colour above.');
    }
    final hex = _hex(colour);
    _accent.text = hex;
    setState(() => _accentNote = 'Colour taken from the image. Change it above if you prefer.');
    _set(_draft.copyWith(accentHex: hex));
  }

  // --- the company logo ----------------------------------------------------------------------

  Future<void> _chooseLogo() async {
    Uint8List? bytes;
    try {
      bytes = await locator<FilePicker>()
          .pick(accept: '.png,image/png', maxBytes: PatchingPolicySettings.maxOrganisationLogoBytes);
    } on FileTooLargeException {
      if (mounted) setState(() => _logoError = PatchingPolicySettings.logoTooLarge);
      return;
    } catch (_) {
      if (mounted) setState(() => _logoError = 'That file could not be read. Try choosing it again.');
      return;
    }
    if (bytes == null || !mounted) return;
    final problem = PatchingPolicySettings.logoProblem(bytes);
    if (problem != null) return setState(() => _logoError = problem);
    setState(() => _logoError = null);
    _setOrg(Organisation(name: _org.name, logoPng: bytes));
  }

  void _removeLogo() {
    setState(() => _logoError = null);
    _setOrg(Organisation(name: _org.name));
  }

  void _typedName(String text) {
    _setOrg(Organisation(name: text.trim().isEmpty ? null : text, logoPng: _org.logoPng));
  }

  // --- saving --------------------------------------------------------------------------------

  Future<void> _save() async {
    final trimmed = _org.name?.trim();
    // This browser's light/dark pick is left alone: the screen has no colour-mode control, so there is no
    // company mode that a reset could restore, and clearing the pick switched an admin in light mode to dark.
    await _cubit.save(
      _draft,
      Organisation(name: trimmed == null || trimmed.isEmpty ? null : trimmed, logoPng: _org.logoPng),
    );
  }

  @override
  Widget build(BuildContext context) => BlocListener<AppearanceCubit, AppearanceState>(
        // The settings can arrive after this screen opened (a bookmarked address): adopt them,
        // unless the company has already started changing something.
        listenWhen: (previous, current) =>
            (previous.saved != current.saved || previous.organisation != current.organisation) &&
            current.preview == null &&
            current.organisationPreview == null,
        listener: (_, state) => _adopt(state),
        child: BlocBuilder<AppearanceCubit, AppearanceState>(
          builder: (context, state) {
            final accentText = _accent.text.trim();
            final accentInvalid = accentText.isNotEmpty && parseAccentHex(accentText) == null;
            final trimmedName = _org.name?.trim();
            final org = Organisation(name: trimmedName == null || trimmedName.isEmpty ? null : trimmedName, logoPng: _org.logoPng);
            final appearanceDirty = _draft != state.saved;
            final orgDirty = org != state.organisation;
            // On a server without the appearance route only the name and logo can be kept.
            final canSave = ((appearanceDirty && !state.unsupported) || orgDirty) && !accentInvalid && !state.saving;
            return PageScaffold(
              title: 'Appearance',
              subtitle: 'Make Kintsugi look like your company. Changes show here straight away and are kept when you save.',
              children: [
                if (state.unsupported)
                  const AlertBox.info(
                    'This server does not support saving a colour or picture yet. You can try them here, but they '
                    'are only shown on this page until the server is updated. The company name and logo can be saved.',
                  ),
                if (state.justSaved) const AlertBox.success('Settings saved.'),
                if (state.error != null) AlertBox.error(state.error!),
                _Section(
                  title: 'Colour',
                  hint: 'Used for buttons, links and highlights; everything else keeps the HTW style. Kintsugi adjusts '
                      'the shade where needed so text stays easy to read on both the light and the dark page.',
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Wrap(spacing: 14, runSpacing: 12, crossAxisAlignment: WrapCrossAlignment.center, children: [
                      for (final preset in themePresets)
                        _Dot(
                          key: Key('appearance-preset-${preset.key}'),
                          label: preset.name,
                          colour: preset.accent ?? KintsugiPalette.light.action,
                          selected: _draft.accentHex == null && _draft.presetKey == preset.key,
                          onTap: () {
                            _accent.clear();
                            setState(() => _accentNote = null);
                            _set(_draft.copyWith(presetKey: preset.key, clearAccent: true));
                          },
                        ),
                    ]),
                    const SizedBox(height: 18),
                    ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 320),
                      child: LabelledField(
                        label: 'Your own colour (#RRGGBB)',
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
                    if (_draft.wallpaper != null) ...[
                      const SizedBox(height: 14),
                      SecondaryButton(
                        key: const Key('appearance-wallpaper-match'),
                        label: 'Match colours to image',
                        onPressed: _matchImage,
                      ),
                    ],
                    if (_accentNote != null) ...[
                      const SizedBox(height: 10),
                      Text(_accentNote!, style: Theme.of(context).textTheme.bodySmall),
                    ],
                  ]),
                ),
                _Section(
                  title: 'Image',
                  hint: 'A picture behind every screen, for example your brand image. It stays visible; panels are '
                      'frosted glass so text stays easy to read. PNG, JPEG or WebP, up to 1 MB.',
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    if (_draft.wallpaper != null) ...[
                      ClipRRect(
                        borderRadius: BorderRadius.circular(4),
                        child: Image.memory(
                          _draft.wallpaper!,
                          key: const Key('appearance-wallpaper-preview'),
                          height: 120,
                          errorBuilder: (_, _, _) => const Text('This image cannot be shown.'),
                        ),
                      ),
                      const SizedBox(height: 12),
                    ],
                    Wrap(spacing: 12, runSpacing: 12, children: [
                      SecondaryButton(
                        key: const Key('appearance-wallpaper-choose'),
                        label: _draft.wallpaper == null ? 'Choose image' : 'Replace image',
                        onPressed: _chooseWallpaper,
                      ),
                      if (_draft.wallpaper != null)
                        SecondaryButton(
                          key: const Key('appearance-wallpaper-remove'),
                          label: 'Remove',
                          onPressed: _removeWallpaper,
                        ),
                    ]),
                    if (_wallpaperError != null) ...[
                      const SizedBox(height: 10),
                      Text(_wallpaperError!, style: TextStyle(color: context.palette.red)),
                    ],
                  ]),
                ),
                _Section(
                  title: 'Company logo (optional)',
                  hint: 'Replaces the HTW logo at the top of the sidebar and stays still. It is also saved as the '
                      'patching policy logo, so it shows on the patching pop-ups staff see on their computers. '
                      'Leave it empty to keep the HTW logo.',
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 320),
                      child: LabelledField(
                        label: 'Company name',
                        child: KintsugiTextField(
                          key: const Key('appearance-org-name'),
                          controller: _name,
                          hintText: 'Example Corp',
                          onChanged: _typedName,
                          errorText: (_name.text.trim().length > _maxNameLength)
                              ? 'Use $_maxNameLength characters or fewer.'
                              : null,
                        ),
                      ),
                    ),
                    const SizedBox(height: 16),
                    Wrap(spacing: 14, runSpacing: 12, crossAxisAlignment: WrapCrossAlignment.center, children: [
                      if (_org.logoPng != null)
                        Container(
                          padding: const EdgeInsets.all(6),
                          decoration: BoxDecoration(
                            color: context.palette.background,
                            border: Border.all(color: context.palette.border),
                            borderRadius: BorderRadius.circular(4),
                          ),
                          child: Image.memory(
                            _org.logoPng!,
                            key: const Key('appearance-logo-preview'),
                            height: 48,
                            errorBuilder: (_, _, _) => const Text('This logo cannot be shown.'),
                          ),
                        ),
                      SecondaryButton(
                        key: const Key('appearance-logo-choose'),
                        label: _org.logoPng == null ? 'Choose logo' : 'Replace logo',
                        onPressed: _chooseLogo,
                      ),
                      if (_org.logoPng != null)
                        SecondaryButton(key: const Key('appearance-logo-remove'), label: 'Remove', onPressed: _removeLogo),
                    ]),
                    Padding(
                      padding: const EdgeInsets.only(top: 6),
                      child: Text(
                        'A PNG of up to ${PatchingPolicySettings.maxOrganisationLogoBytes ~/ 1024} KB.',
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ),
                    if (_logoError != null) ...[
                      const SizedBox(height: 10),
                      Text(_logoError!, style: TextStyle(color: context.palette.red)),
                    ],
                  ]),
                ),
                Wrap(spacing: 12, runSpacing: 12, children: [
                  PrimaryButton(
                    key: const Key('appearance-save'),
                    label: 'Save',
                    busy: state.saving,
                    onPressed: canSave ? _save : null,
                  ),
                  SecondaryButton(
                    key: const Key('appearance-reset'),
                    label: 'Reset',
                    onPressed: appearanceDirty || orgDirty ? _reset : null,
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
                child: Text(title, style: Theme.of(context).textTheme.titleLarge),
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

/// A colour to pick: a round swatch, ringed when it is the choice.
class _Dot extends StatelessWidget {
  const _Dot({super.key, required this.label, required this.colour, required this.selected, required this.onTap});

  final String label;
  final Color colour;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Semantics(
        button: true,
        selected: selected,
        label: label,
        child: Tooltip(
          message: label,
          child: InkWell(
            onTap: onTap,
            customBorder: const CircleBorder(),
            child: Container(
              width: 40,
              height: 40,
              padding: const EdgeInsets.all(3),
              decoration: BoxDecoration(
                shape: BoxShape.circle,
                border: Border.all(color: selected ? context.palette.text : Colors.transparent, width: 2),
              ),
              child: DecoratedBox(decoration: BoxDecoration(color: colour, shape: BoxShape.circle)),
            ),
          ),
        ),
      );
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
