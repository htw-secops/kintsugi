import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/domain/entities/application.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/software_title.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/software_usecases.dart';
import 'package:kintsugi_web/presentation/applications/software_bloc.dart';
import 'package:kintsugi_web/presentation/applications/software_screen.dart';

/// A stand-in for `/api/admin/software`. Answers every write with the whole screen, as the contract
/// says the server does, and records each call — secret values included, because what reached the
/// "server" is exactly what the secret tests need to compare against what the screen still shows.
class _FakeRepository implements SoftwareRepository {
  _FakeRepository(this.state);

  SoftwareOverview state;
  final calls = <String>[];
  final secretValuesReceived = <String>[];
  ForcedPatchRunResult runNowResult = const ForcedPatchRunResult(requested: 0, notRequested: []);
  ApiException? failNextSecret;
  ApiException? refuseRunNow;
  ApiException? failNextRead;
  bool unreadableAssign = false;

  @override
  Future<SoftwareOverview> read() async {
    if (failNextRead case final error?) {
      failNextRead = null;
      throw error;
    }
    return state;
  }

  @override
  Future<SoftwareOverview> create(SoftwareTitleDraft draft) async {
    calls.add('create ${draft.name} ${draft.packageManager} ${draft.packageId} ${draft.mode.wireName}');
    state = SoftwareOverview(
      titles: [
        ...state.titles,
        SoftwareTitle(
          id: 'new',
          name: draft.name,
          platform: draft.platform ?? softwarePackageManagers[draft.packageManager]!,
          packageManager: draft.packageManager,
          packageId: draft.packageId,
          mode: draft.mode,
          scriptState: SoftwareScriptState.unsigned,
        ),
      ],
      secrets: state.secrets,
    );
    return state;
  }

  @override
  Future<SoftwareOverview> update(String id, {required SoftwareMode mode, String? detectAs, required List<String> secretNames}) async {
    calls.add('update $id ${mode.wireName} $detectAs ${secretNames.join(',')}');
    return state;
  }

  @override
  Future<SoftwareOverview> assign(String id, {required bool allHosts, required List<String> hostNames}) async {
    calls.add('assign $id $allHosts ${hostNames.join(',')}');
    if (unreadableAssign) throw TypeError();
    return state;
  }

  @override
  Future<SoftwareRunNowResult> runNow(String id) async {
    calls.add('runNow $id');
    if (refuseRunNow case final error?) throw error;
    // No screen state in the answer, so the bloc has to re-read it.
    return SoftwareRunNowResult(result: runNowResult);
  }

  @override
  Future<SoftwareOverview> delete(String id) async {
    calls.add('delete $id');
    state = SoftwareOverview(titles: [for (final t in state.titles) if (t.id != id) t], secrets: state.secrets);
    return state;
  }

  @override
  Future<SoftwareOverview> setSecret(String name, String value) async {
    calls.add('setSecret $name');
    secretValuesReceived.add(value);
    if (failNextSecret case final error?) {
      failNextSecret = null;
      throw error;
    }
    state = SoftwareOverview(
      titles: state.titles,
      secrets: [
        for (final s in state.secrets) if (s.name != name) s,
        ScriptSecretSummary(name: name, hasValue: true, updatedBy: 'admin', updatedUtc: DateTime.utc(2026, 11, 19)),
      ]..sort((a, b) => a.name.compareTo(b.name)),
    );
    return state;
  }

  @override
  Future<SoftwareOverview> deleteSecret(String name) async {
    calls.add('deleteSecret $name');
    return state;
  }
}

const _winget = SoftwareTitle(
  id: 't1',
  name: 'Greenshot',
  platform: 'Windows',
  packageManager: 'winget',
  packageId: 'Greenshot.Greenshot',
  mode: SoftwareMode.required,
  assignedToAll: true,
  compliantCount: 7,
  pendingCount: 2,
  failedCount: 1,
  notApplicableCount: 3,
  scriptState: SoftwareScriptState.ready,
);

const _homebrew = SoftwareTitle(
  id: 't2',
  name: 'Example Browser',
  platform: 'macOS',
  packageManager: 'Homebrew',
  packageId: 'example-browser',
  mode: SoftwareMode.prohibited,
  assignedHostNames: ['host-a', 'host-b'],
  pendingCount: 1,
  scriptState: SoftwareScriptState.noActionMode,
);

const _snap = SoftwareTitle(
  id: 't3',
  name: 'Example Editor',
  platform: 'Linux',
  packageManager: 'Snap',
  packageId: 'example-editor',
  mode: SoftwareMode.selfService,
  scriptState: SoftwareScriptState.unsigned,
);

const _initial = SoftwareOverview(
  titles: [_winget, _homebrew, _snap],
  secrets: [ScriptSecretSummary(name: 'EXAMPLE_KEY', hasValue: true, updatedBy: 'admin')],
);

Future<_FakeRepository> _pump(WidgetTester tester, {SoftwareOverview overview = _initial}) async {
  final repository = _FakeRepository(overview);
  locator
    ..registerSingleton(GetSoftware(repository))
    ..registerSingleton(AddSoftwareTitle(repository))
    ..registerSingleton(UpdateSoftwareTitle(repository))
    ..registerSingleton(AssignSoftwareTitle(repository))
    ..registerSingleton(RunSoftwareTitleNow(repository))
    ..registerSingleton(DeleteSoftwareTitle(repository))
    ..registerSingleton(SetScriptSecret(repository))
    ..registerSingleton(DeleteScriptSecret(repository));

  // Wide enough that the eleven-column table does not scroll sideways, and tall enough for an
  // expanded row's two forms.
  tester.view.physicalSize = const Size(1900, 2600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  await tester.pumpWidget(MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: SoftwareScreen())));
  await tester.pumpAndSettle();
  return repository;
}

Future<void> _expand(WidgetTester tester, SoftwareTitle title) async {
  await tester.tap(find.byTooltip('Edit ${title.name}'));
  await tester.pumpAndSettle();
}

String _countIn(WidgetTester tester, String key) =>
    tester.widget<Text>(find.descendant(of: find.byKey(ValueKey(key)), matching: find.byType(Text))).data!;

void main() {
  tearDown(() => locator.reset());

  testWidgets('renders every title with its source, mode, assignment, counts and script state', (tester) async {
    await _pump(tester);

    expect(find.text('Greenshot'), findsOneWidget);
    expect(find.text('Greenshot.Greenshot'), findsOneWidget);
    expect(find.text('Example Browser'), findsOneWidget);
    expect(find.text('Example Editor'), findsOneWidget);

    expect(find.text('All Windows hosts'), findsOneWidget);
    expect(find.text('2 hosts'), findsOneWidget);
    expect(find.text('Nobody'), findsOneWidget);

    expect(find.text('PROHIBITED'), findsOneWidget);
    expect(find.text('SELF-SERVICE'), findsOneWidget);
    expect(find.text('NO ACTION MODE'), findsOneWidget);
    expect(find.text('READY'), findsOneWidget);

    expect(_countIn(tester, 'compliant-t1'), '7');
    expect(_countIn(tester, 'pending-t1'), '2');
    expect(_countIn(tester, 'failed-t1'), '1');
    expect(_countIn(tester, 'na-t1'), '3');
  });

  /// `KintsugiTable`'s rules (web/CLAUDE.md): at a width that forces the table below its floor it
  /// scrolls inside its panel rather than painting one column over the next. An overflow is an
  /// exception the test framework fails on, with the panel expanded too.
  testWidgets('lays out without overflow in a narrow window, row expanded', (tester) async {
    await _pump(tester);
    tester.view.physicalSize = const Size(1000, 2600);
    await tester.pumpAndSettle();
    await _expand(tester, _homebrew);

    expect(tester.takeException(), isNull);
    expect(find.text('Example Browser'), findsOneWidget);
  });

  testWidgets('every column header is on screen in order', (tester) async {
    await _pump(tester);

    final labels = ['NAME', 'SOURCE', 'PLATFORM', 'MODE', 'ASSIGNED', 'COMPLIANT', 'PENDING', 'FAILED', 'N/A', 'SCRIPT'];
    final xs = [for (final label in labels) tester.getTopLeft(find.text(label).last).dx];
    expect(xs, orderedEquals([...xs]..sort()));
  });

  testWidgets('Add Software sends a package-manager title and shows the new row', (tester) async {
    final repository = await _pump(tester, overview: const SoftwareOverview());

    expect(find.textContaining('No software titles yet'), findsOneWidget);

    await tester.tap(find.text('ADD SOFTWARE'));
    await tester.pumpAndSettle();

    await tester.enterText(find.byKey(const ValueKey('add-name')), 'Greenshot');
    await tester.enterText(find.byKey(const ValueKey('add-package-id')), 'Greenshot.Greenshot');
    await tester.pumpAndSettle();
    await tester.tap(find.text('ADD'));
    await tester.pumpAndSettle();

    expect(repository.calls, ['create Greenshot winget Greenshot.Greenshot Required']);
    expect(find.text('Greenshot'), findsOneWidget);
    expect(find.text('Added Greenshot.'), findsOneWidget);
  });

  testWidgets('Add is disabled until a package-manager title has its id', (tester) async {
    await _pump(tester, overview: const SoftwareOverview());

    await tester.tap(find.text('ADD SOFTWARE'));
    await tester.pumpAndSettle();
    await tester.enterText(find.byKey(const ValueKey('add-name')), 'Greenshot');
    await tester.pumpAndSettle();

    final add = tester.widget<FilledButton>(find.ancestor(of: find.text('ADD'), matching: find.byType(FilledButton)));
    expect(add.onPressed, isNull);
  });

  testWidgets('saving an assignment sends the hosts typed, one per line', (tester) async {
    final repository = await _pump(tester);
    await _expand(tester, _winget);

    // Untick "every Windows host" and name two instead.
    await tester.tap(find.text('Every Windows host'));
    await tester.enterText(find.byKey(const ValueKey('assignment-hosts-t1')), 'host-a\n\n host-b \n');
    await tester.tap(find.text('SAVE ASSIGNMENT'));
    await tester.pumpAndSettle();

    expect(repository.calls, ['assign t1 false host-a,host-b']);
  });

  testWidgets('an answer the mapper cannot read does not leave the screen locked', (tester) async {
    final repository = await _pump(tester);
    await _expand(tester, _winget);
    repository.unreadableAssign = true;

    await tester.tap(find.text('SAVE ASSIGNMENT'));
    await tester.pumpAndSettle();
    expect(find.text(unreadableSoftwareAnswer), findsOneWidget);

    // Still pressable: `busy` was cleared.
    repository.unreadableAssign = false;
    await tester.tap(find.text('SAVE ASSIGNMENT'));
    await tester.pumpAndSettle();
    expect(repository.calls, ['assign t1 true ', 'assign t1 true ']);
  });

  testWidgets('saving a title sends its mode by name, detect-as and secrets', (tester) async {
    final repository = await _pump(tester);
    await _expand(tester, _winget);

    await tester.tap(find.widgetWithText(FilterChip, 'EXAMPLE_KEY'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('SAVE'));
    await tester.pumpAndSettle();

    expect(repository.calls, ['update t1 Required null EXAMPLE_KEY']);
  });

  testWidgets('Run now asks the server and says which hosts it could not instruct', (tester) async {
    final repository = await _pump(tester);
    repository.runNowResult = const ForcedPatchRunResult(requested: 2, notRequested: ['host-c']);
    await _expand(tester, _winget);

    await tester.tap(find.text('RUN NOW'));
    await tester.pumpAndSettle();

    expect(repository.calls, ['runNow t1']);
    expect(find.textContaining('Asked 2 hosts to act on Greenshot now.'), findsOneWidget);
    expect(find.textContaining('Not asked: host-c.'), findsOneWidget);
  });

  testWidgets('Run now retries failed hosts when nothing is pending', (tester) async {
    const allFailed = SoftwareTitle(
      id: 't4',
      name: 'Example Agent',
      platform: 'Windows',
      packageManager: 'winget',
      packageId: 'Example.Agent',
      mode: SoftwareMode.required,
      assignedToAll: true,
      failedCount: 3,
      scriptState: SoftwareScriptState.ready,
    );
    final repository = await _pump(tester, overview: const SoftwareOverview(titles: [allFailed]));
    repository.runNowResult = const ForcedPatchRunResult(requested: 3, notRequested: []);
    await _expand(tester, allFailed);

    await tester.tap(find.text('RUN NOW'));
    await tester.pumpAndSettle();

    expect(repository.calls, ['runNow t4']);
    expect(find.textContaining('Asked 3 hosts to act on Example Agent now.'), findsOneWidget);
  });

  testWidgets('Run now is unavailable, and says so on screen, with nothing pending or failed', (tester) async {
    const settled = SoftwareTitle(
      id: 't5',
      name: 'Example Tool',
      platform: 'Windows',
      packageManager: 'winget',
      packageId: 'Example.Tool',
      mode: SoftwareMode.required,
      assignedToAll: true,
      compliantCount: 4,
      scriptState: SoftwareScriptState.ready,
    );
    await _pump(tester, overview: const SoftwareOverview(titles: [settled]));
    await _expand(tester, settled);

    expect(find.text('Run now is unavailable: no host is pending or failed.'), findsOneWidget);
  });

  testWidgets('a self-service title says on screen why Run now is unavailable', (tester) async {
    const offered = SoftwareTitle(
      id: 't6',
      name: 'Example Viewer',
      platform: 'macOS',
      packageManager: 'Homebrew',
      packageId: 'example-viewer',
      mode: SoftwareMode.selfService,
      assignedToAll: true,
      pendingCount: 2,
      scriptState: SoftwareScriptState.ready,
    );
    final repository = await _pump(tester, overview: const SoftwareOverview(titles: [offered]));
    await _expand(tester, offered);

    expect(find.textContaining('Run now is unavailable: self-service titles'), findsOneWidget);
    final button = tester.widget<OutlinedButton>(
      find.ancestor(of: find.text('RUN NOW'), matching: find.byType(OutlinedButton)),
    );
    expect(button.onPressed, isNull);
    expect(repository.calls, isEmpty);
  });

  testWidgets('a refusal from the server is shown as a Run now refusal', (tester) async {
    final repository = await _pump(tester);
    repository.refuseRunNow = const ApiException('The script for this title is not Ready.');
    await _expand(tester, _winget);

    await tester.tap(find.text('RUN NOW'));
    await tester.pumpAndSettle();

    expect(
      find.text('Run now was refused for Greenshot: The script for this title is not Ready.'),
      findsOneWidget,
    );
  });

  testWidgets('a reload failing after Run now was accepted is not shown as a refusal', (tester) async {
    final repository = await _pump(tester);
    repository.runNowResult = const ForcedPatchRunResult(requested: 2, notRequested: []);
    await _expand(tester, _winget);
    repository.failNextRead = const ApiException('Service unavailable.');

    await tester.tap(find.text('RUN NOW'));
    await tester.pumpAndSettle();

    expect(find.textContaining('Run now was refused'), findsNothing);
    expect(find.textContaining('Asked 2 hosts to act on Greenshot now.'), findsOneWidget);
    expect(
      find.text('Hosts were asked to act, but the screen could not reload: Service unavailable. Press Reload.'),
      findsOneWidget,
    );
  });

  testWidgets('Run now is disabled, with a reason, when the script is not ready', (tester) async {
    final repository = await _pump(tester);
    await _expand(tester, _homebrew);

    final button = tester.widget<OutlinedButton>(
      find.ancestor(of: find.text('RUN NOW'), matching: find.byType(OutlinedButton)),
    );
    expect(button.onPressed, isNull);
    expect(find.byTooltip('its script is not Ready, so the server refuses — see the note below.'), findsOneWidget);
    expect(find.textContaining('Run now is unavailable: its script is not Ready'), findsOneWidget);
    expect(repository.calls, isEmpty);
  });

  testWidgets('a package-manager row whose script has no action mode says to Take + Sign it', (tester) async {
    await _pump(tester);
    await _expand(tester, _homebrew);

    expect(find.textContaining('Acts through the Homebrew script (pm:Homebrew)'), findsOneWidget);
    expect(find.textContaining('Take + Sign the Homebrew script on Upgrade Scripts'), findsOneWidget);
  });

  testWidgets('an unsigned package-manager script says to Take + Sign it too', (tester) async {
    await _pump(tester);
    await _expand(tester, _snap);

    expect(find.textContaining('It is not signed'), findsOneWidget);
    expect(find.textContaining('Take + Sign the Snap script on Upgrade Scripts'), findsOneWidget);
  });

  testWidgets('a ready package-manager script does not ask for anything', (tester) async {
    await _pump(tester);
    await _expand(tester, _winget);

    expect(find.textContaining('It is signed and supports install and uninstall.'), findsOneWidget);
    expect(find.textContaining('Take + Sign'), findsNothing);
  });

  testWidgets('setting a new secret sends the value once and never shows it again', (tester) async {
    const value = 'example-value-not-a-real-secret';
    final repository = await _pump(tester);

    await tester.enterText(find.byKey(const ValueKey('new-secret-name')), 'SECOND_KEY');
    await tester.enterText(find.byKey(const ValueKey('new-secret-value')), value);
    await tester.pumpAndSettle();

    // Obscured while typed.
    final field = tester.widget<TextField>(
      find.descendant(of: find.byKey(const ValueKey('new-secret-value')), matching: find.byType(TextField)),
    );
    expect(field.obscureText, isTrue);

    await tester.tap(find.text('ADD SECRET'));
    await tester.pumpAndSettle();

    expect(repository.calls, ['setSecret SECOND_KEY']);
    expect(repository.secretValuesReceived, [value]);

    // The new row is there, as a name with "set" and nothing else.
    expect(find.text('SECOND_KEY'), findsOneWidget);
    expect(find.text('SET'), findsNWidgets(2));

    // The value is gone from every field on the screen and from the bloc's state.
    for (final textField in tester.widgetList<TextField>(find.byType(TextField))) {
      expect(textField.controller?.text, isNot(contains(value)));
    }
    expect(find.textContaining(value), findsNothing);
  });

  testWidgets('a refused secret value stays typed for correction and is not kept anywhere else', (tester) async {
    const value = 'example-refused-value';
    final repository = await _pump(tester);
    repository.failNextSecret = const ApiException('Refused.');

    await tester.enterText(find.byKey(const ValueKey('secret-value-EXAMPLE_KEY')), value);
    await tester.pumpAndSettle();
    await tester.tap(find.text('REPLACE'));
    await tester.pumpAndSettle();

    expect(find.text('Refused.'), findsOneWidget);
    expect(repository.secretValuesReceived, [value]);
    final field = tester.widget<TextField>(
      find.descendant(of: find.byKey(const ValueKey('secret-value-EXAMPLE_KEY')), matching: find.byType(TextField)),
    );
    expect(field.controller!.text, value);
    expect(field.obscureText, isTrue);
  });

  testWidgets('replacing a secret clears the box once the server accepts it', (tester) async {
    const value = 'example-replacement-value';
    final repository = await _pump(tester);

    await tester.enterText(find.byKey(const ValueKey('secret-value-EXAMPLE_KEY')), value);
    await tester.pumpAndSettle();
    await tester.tap(find.text('REPLACE'));
    await tester.pumpAndSettle();

    expect(repository.calls, ['setSecret EXAMPLE_KEY']);
    final field = tester.widget<TextField>(
      find.descendant(of: find.byKey(const ValueKey('secret-value-EXAMPLE_KEY')), matching: find.byType(TextField)),
    );
    expect(field.controller!.text, isEmpty);
  });

  test('the bloc never holds a secret value in its state, and the event redacts it', () async {
    const value = 'example-bloc-value';
    final repository = _FakeRepository(_initial);
    final bloc = SoftwareBloc(
      getSoftware: GetSoftware(repository),
      addTitle: AddSoftwareTitle(repository),
      updateTitle: UpdateSoftwareTitle(repository),
      assignTitle: AssignSoftwareTitle(repository),
      runNow: RunSoftwareTitleNow(repository),
      deleteTitle: DeleteSoftwareTitle(repository),
      setSecret: SetScriptSecret(repository),
      deleteSecret: DeleteScriptSecret(repository),
    );
    addTearDown(bloc.close);

    final states = <SoftwareState>[];
    final subscription = bloc.stream.listen(states.add);
    addTearDown(subscription.cancel);

    const event = SoftwareSecretSet('EXAMPLE_KEY', value);
    expect(event.toString(), isNot(contains(value)));
    expect(event.props, isNot(contains(value)));

    bloc.add(event);
    await pumpEventQueue();

    expect(repository.secretValuesReceived, [value]);
    expect(states, isNotEmpty);
    for (final state in states) {
      expect(state.toString(), isNot(contains(value)));
      expect(state.props.toString(), isNot(contains(value)));
    }
    expect(bloc.state.savedSecretName, 'EXAMPLE_KEY');
  });

  testWidgets('adding a secret whose name already exists refuses and points to Replace', (tester) async {
    final repository = await _pump(tester);

    await tester.enterText(find.byKey(const ValueKey('new-secret-name')), 'EXAMPLE_KEY');
    await tester.enterText(find.byKey(const ValueKey('new-secret-value')), 'example-duplicate-value');
    await tester.pumpAndSettle();

    expect(find.textContaining('EXAMPLE_KEY already exists. Use Replace on its row above'), findsOneWidget);
    final add = tester.widget<OutlinedButton>(
      find.ancestor(of: find.text('ADD SECRET'), matching: find.byType(OutlinedButton)),
    );
    expect(add.onPressed, isNull);
    expect(repository.calls, isEmpty);
  });

  testWidgets('a secret name the server would refuse is said so before sending', (tester) async {
    await _pump(tester);

    await tester.enterText(find.byKey(const ValueKey('new-secret-name')), 'lower_case');
    await tester.enterText(find.byKey(const ValueKey('new-secret-value')), 'x');
    await tester.pumpAndSettle();

    expect(find.textContaining('Upper-case letters'), findsOneWidget);
    final add = tester.widget<OutlinedButton>(
      find.ancestor(of: find.text('ADD SECRET'), matching: find.byType(OutlinedButton)),
    );
    expect(add.onPressed, isNull);
  });

  testWidgets('deleting a title asks first, then removes the row', (tester) async {
    final repository = await _pump(tester);
    await _expand(tester, _snap);

    await tester.tap(find.text('DELETE'));
    await tester.pumpAndSettle();
    expect(find.text('Delete Example Editor?'), findsOneWidget);

    await tester.tap(find.text('DELETE').last);
    await tester.pumpAndSettle();

    expect(repository.calls, ['delete t3']);
    expect(find.text('Example Editor'), findsNothing);
  });
}
