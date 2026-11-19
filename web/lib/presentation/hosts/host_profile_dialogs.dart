import 'package:flutter/material.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
import '../../core/platform/file_picker.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/host.dart';
import '../../domain/usecases/host_usecases.dart';
import 'host_profile_import.dart';

/// What the bulk dialog asks for. A null field is kept as it is; an empty string clears it.
class HostProfileChoice {
  const HostProfileChoice({this.department, this.location});

  final String? department;
  final String? location;

  bool get isEmpty => department == null && location == null;
}

/// The dialog behind "Set department or location": choose a value for either or both, for the hosts
/// that are ticked. Each field offers the values already in use (press one to fill it in) and takes
/// free text, so a new department is typed once and then offered everywhere. Left empty, a field
/// keeps whatever each host has.
Future<HostProfileChoice?> showHostProfileDialog(
  BuildContext context, {
  required int count,
  required List<String> departments,
  required List<String> locations,
}) =>
    showDialog<HostProfileChoice>(
      context: context,
      builder: (_) => _HostProfileDialog(count: count, departments: departments, locations: locations),
    );

class _HostProfileDialog extends StatefulWidget {
  const _HostProfileDialog({required this.count, required this.departments, required this.locations});

  final int count;
  final List<String> departments;
  final List<String> locations;

  @override
  State<_HostProfileDialog> createState() => _HostProfileDialogState();
}

class _HostProfileDialogState extends State<_HostProfileDialog> {
  final _department = TextEditingController();
  final _location = TextEditingController();
  bool _clearDepartment = false;
  bool _clearLocation = false;

  @override
  void dispose() {
    _department.dispose();
    _location.dispose();
    super.dispose();
  }

  HostProfileChoice get _choice => HostProfileChoice(
        department: _clearDepartment ? '' : (_department.text.trim().isEmpty ? null : _department.text.trim()),
        location: _clearLocation ? '' : (_location.text.trim().isEmpty ? null : _location.text.trim()),
      );

  Widget _field({
    required String label,
    required TextEditingController controller,
    required List<String> suggestions,
    required bool clear,
    required ValueChanged<bool> onClear,
    required Key fieldKey,
  }) =>
      Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          LabelledField(
            label: label,
            child: KintsugiTextField(
              key: fieldKey,
              controller: controller,
              hintText: 'Keep as is',
              enabled: !clear,
              onChanged: (_) => setState(() {}),
            ),
          ),
          if (suggestions.isNotEmpty && !clear)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Wrap(
                spacing: 6,
                runSpacing: 6,
                children: [
                  for (final value in suggestions.take(12))
                    ActionChip(
                      label: Text(value, style: const TextStyle(fontSize: 12)),
                      visualDensity: VisualDensity.compact,
                      onPressed: () => setState(() => controller.text = value),
                    ),
                ],
              ),
            ),
          KintsugiCheckbox(label: 'Clear the ${label.toLowerCase()} instead', value: clear, onChanged: onClear),
        ],
      );

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return AlertDialog(
      backgroundColor: palette.backgroundAlt,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(6),
        side: BorderSide(color: palette.border),
      ),
      title: Text('Set department or location', style: Theme.of(context).textTheme.titleLarge),
      content: SizedBox(
        width: 460,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              HintText('Applies to ${widget.count} selected host${widget.count == 1 ? '' : 's'}. A field left empty '
                  'keeps what each host has.'),
              const SizedBox(height: 14),
              _field(
                label: 'Department',
                controller: _department,
                suggestions: widget.departments,
                clear: _clearDepartment,
                onClear: (v) => setState(() => _clearDepartment = v),
                fieldKey: const Key('bulk-department'),
              ),
              const SizedBox(height: 12),
              _field(
                label: 'Location',
                controller: _location,
                suggestions: widget.locations,
                clear: _clearLocation,
                onClear: (v) => setState(() => _clearLocation = v),
                fieldKey: const Key('bulk-location'),
              ),
            ],
          ),
        ),
      ),
      actions: [
        SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(context).pop()),
        PrimaryButton(
          key: const Key('bulk-apply'),
          label: 'Apply',
          onPressed: _choice.isEmpty ? null : () => Navigator.of(context).pop(_choice),
        ),
      ],
    );
  }
}

/// Opens the import dialog. Returns the sentence to show on the page when something was imported, or
/// null when the dialog was closed without importing.
Future<String?> showHostImportDialog(BuildContext context) =>
    showDialog<String>(context: context, builder: (_) => const _HostImportDialog());

/// "Import department and location": read a Snipe-IT Assets export, **in the browser**, and send the
/// server only what was asked for.
///
/// The file never leaves the computer as a file. It is parsed here ([HostImportFile]), and the request
/// is built from the columns the administrator chose: serial number, department, location and model
/// always; the assigned person's name and email only when their box is ticked (both start unticked).
/// A column that is not chosen is not in the request, so it does not reach the server at all.
class _HostImportDialog extends StatefulWidget {
  const _HostImportDialog();

  @override
  State<_HostImportDialog> createState() => _HostImportDialogState();
}

class _HostImportDialogState extends State<_HostImportDialog> {
  String? _fileName;
  HostImportFile? _file;
  bool _includeName = false;
  bool _includeEmail = false;
  bool _importing = false;
  String? _error;
  HostProfileImportResult? _result;
  int _sent = 0;

  Future<void> _pick() async {
    final PickedTextFile? picked;
    try {
      picked = await locator<FilePicker>().pickText(maxBytes: hostImportMaxBytes);
    } on FileTooLargeException {
      if (mounted) setState(() => _error = 'That file is over ${hostImportMaxBytes ~/ (1024 * 1024)} MB. Choose the Assets export.');
      return;
    } catch (_) {
      if (mounted) setState(() => _error = 'Could not open that file.');
      return;
    }
    if (picked == null || !mounted) return;
    setState(() {
      _fileName = picked!.name;
      _file = HostImportFile.parse(picked.text);
      _result = null;
      _error = null;
    });
  }

  Future<void> _import() async {
    final file = _file;
    if (file == null) return;
    final rows = file.buildRows(includeName: _includeName, includeEmail: _includeEmail);
    setState(() {
      _importing = true;
      _error = null;
      _sent = rows.length;
    });
    try {
      final result = await importHostRows(locator<ImportHostProfiles>(), rows);
      if (mounted) setState(() => _result = result);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = 'The import failed: ${error.message}');
    } finally {
      if (mounted) setState(() => _importing = false);
    }
  }

  /// What the page says after a successful import.
  String _message(HostProfileImportResult r) =>
      'Imported: matched by serial ${r.matched} of $_sent, ${r.notFound} not found, ${r.updated} '
      'host${r.updated == 1 ? '' : 's'} updated.';

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final file = _file;
    final result = _result;
    final columns = file?.columns;

    String found() => [
          if (columns!.serial != null) 'serial number',
          if (columns.department != null) 'department',
          if (columns.location != null) 'location',
          if (columns.model != null) 'model',
          if (columns.assignedName != null) 'assigned name',
          if (columns.assignedEmail != null) 'email',
        ].join(', ');

    String sent() => [
          'serial number',
          if (columns!.department != null) 'department',
          if (columns.location != null) 'location',
          if (columns.model != null) 'model',
          if (_includeName && columns.assignedName != null) "the assigned person's name",
          if (_includeEmail && columns.assignedEmail != null) 'their email',
        ].join(', ');

    return AlertDialog(
      backgroundColor: palette.backgroundAlt,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(6),
        side: BorderSide(color: palette.border),
      ),
      title: Text('Import department and location', style: Theme.of(context).textTheme.titleLarge),
      content: SizedBox(
        width: 520,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const HintText('Choose the Assets export from Snipe-IT. Hosts are matched by serial number. The file is '
                  'read in your browser, and only the columns listed below are sent.'),
              const SizedBox(height: 14),
              Row(
                children: [
                  SecondaryButton(key: const Key('import-pick'), label: 'Choose a CSV file', onPressed: _importing ? null : _pick),
                  const SizedBox(width: 12),
                  if (_fileName != null) Flexible(child: Text(_fileName!, overflow: TextOverflow.ellipsis)),
                ],
              ),
              if (file != null && !file.columns.hasSerial) ...[
                const SizedBox(height: 12),
                Text(
                  'This file has no serial number column, so hosts cannot be matched. Export the Assets list from '
                  'Snipe-IT: a Users export has no serial numbers. The column is looked for as '
                  '${HostImportColumns.serialNames.map((n) => '"$n"').join(', ')}.',
                  key: const Key('import-no-serial'),
                  style: TextStyle(color: palette.red),
                ),
              ],
              if (file != null && file.columns.hasSerial) ...[
                const SizedBox(height: 12),
                Text(
                  '${file.serialRowCount} row${file.serialRowCount == 1 ? '' : 's'} with a serial number. '
                  'Columns found: ${found()}.',
                  key: const Key('import-found'),
                ),
                const SizedBox(height: 10),
                KintsugiCheckbox(
                  label: "Also import the assigned person's name"
                      '${columns!.assignedName == null ? ' (this file has no such column)' : ''}',
                  value: _includeName,
                  onChanged: columns.assignedName == null || _importing ? (_) {} : (v) => setState(() => _includeName = v),
                ),
                KintsugiCheckbox(
                  label: 'Also import their email'
                      '${columns.assignedEmail == null ? ' (this file has no such column)' : ''}',
                  value: _includeEmail,
                  onChanged: columns.assignedEmail == null || _importing ? (_) {} : (v) => setState(() => _includeEmail = v),
                ),
                const SizedBox(height: 4),
                const HintText('Off by default. Shown only to administrators; the email stays hidden until revealed, and '
                    'every reveal is logged. When ticked, an empty cell removes the name or email already stored (the '
                    'device is checked out to nobody).'),
                const SizedBox(height: 10),
                Text('Will send: ${sent()}.', key: const Key('import-will-send')),
              ],
              if (_error != null) ...[
                const SizedBox(height: 12),
                Text(_error!, style: TextStyle(color: palette.red)),
              ],
              if (result != null) ...[
                const SizedBox(height: 12),
                Text(
                  'Matched by serial ${result.matched} of $_sent, ${result.notFound} not found. '
                  '${result.updated} host${result.updated == 1 ? '' : 's'} updated.',
                  key: const Key('import-result'),
                  style: AppTheme.display(color: palette.green, size: 11, letterSpacing: 0.5),
                ),
              ],
            ],
          ),
        ),
      ),
      actions: [
        SecondaryButton(
          label: result == null ? 'Cancel' : 'Close',
          onPressed: () => Navigator.of(context).pop(result == null ? null : _message(result)),
        ),
        if (result == null)
          PrimaryButton(
            key: const Key('import-apply'),
            label: 'Import',
            busy: _importing,
            onPressed: file != null && file.columns.hasSerial && file.serialRowCount > 0 ? _import : null,
          ),
      ],
    );
  }
}
