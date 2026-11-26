import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import '../../core/di/locator.dart';
import '../../core/diagnostics/diagnostics_log.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/platform/full_screen.dart';
import '../../core/theme/theme_cubit.dart';
import '../../core/widgets/frosted.dart';
import '../../domain/usecases/server_info_usecases.dart';
import '../session/session_bloc.dart';
import '../settings/appearance_cubit.dart';
import 'diagnostics_panel.dart';
import 'logo_spot.dart';
import 'server_version_cubit.dart';

/// The sidebar and the page beside it — what `_Layout.cshtml` was.
///
/// A [ShellRoute] rather than a widget each screen wraps itself in, so navigating between screens
/// rebuilds only the page: the sidebar keeps its scroll position and does not flicker, which is
/// most of what "no full page refresh" means in practice.
class AppShell extends StatelessWidget {
  const AppShell({
    super.key,
    required this.location,
    required this.child,
    this.fullScreen,
    this.diagnostics,
  });

  final String location;
  final Widget child;

  /// The application-wide record of error and log output, and the panel that shows it — docked to
  /// the window's right edge, opposite the sidebar.
  ///
  /// It belongs to the shell rather than to any screen because that is the whole point of it: the
  /// output of "Check for Updates" is read *while* navigating to the screens it names, and a
  /// screen-scoped alert is destroyed by the first of those navigations. The [ShellRoute] keeps
  /// this element across them, so the panel neither closes nor reopens — see `app_router.dart`.
  ///
  /// Nullable, and passed in rather than looked up, for the same reason [fullScreen] is: null is
  /// "nobody is collecting", which is what a widget test pumping this shell — or pumping one
  /// screen with no shell at all — wants without having to register anything.
  final DiagnosticsLog? diagnostics;

  /// Watched so the sidebar gets out of the way in full screen — which one screen asks for, the
  /// remote-control viewer, where the host's desktop is the whole point of the page and 240px of
  /// navigation beside it is width taken from it.
  ///
  /// Nullable, and passed in rather than looked up: a null controller is "never full screen", which
  /// is what a widget test pumping this shell wants without having to register anything. The real
  /// one comes from the router — see `app_router.dart`.
  final FullScreenController? fullScreen;

  @override
  Widget build(BuildContext context) {
    final fullScreen = this.fullScreen;
    if (fullScreen == null) return _build(context, withSidebar: true);

    // A stream rather than the screen telling this shell, because full screen can be *left* without
    // anything in this app being asked: Escape and F11 both do it, and a shell that trusted its own
    // last request would keep the sidebar hidden on a page that was no longer full-screen.
    return StreamBuilder<bool>(
      initialData: fullScreen.isFullScreen,
      stream: fullScreen.onChanged,
      builder: (context, snapshot) => _build(context, withSidebar: snapshot.data != true),
    );
  }

  Widget _build(BuildContext context, {required bool withSidebar}) {
    final diagnostics = this.diagnostics;
    final wallpaper = context.watch<AppearanceCubit>().state.effective.wallpaper;

    final scaffold = BlocProvider(
      // Provided here rather than in main.dart beside SessionBloc: the route it reads is gated,
      // so it must not be asked for until the router has decided this browser may use the app —
      // which is exactly when this shell is first built. The ShellRoute keeps this element
      // across navigations, so the version is fetched once per page load, not once per screen.
      create: (_) => ServerVersionCubit(locator<GetServerVersion>()),
      child: Scaffold(
        // Over a company wallpaper the picture sits under everything and stays visible: a light cover
        // over it, and panels, the header and the sidebar turn to frosted glass (see [Frosted]).
        body: Frosted(enabled: wallpaper != null, child: Stack(fit: StackFit.expand, children: [
        if (wallpaper != null)
          Image.memory(wallpaper, fit: BoxFit.cover, gaplessPlayback: true, errorBuilder: (_, _, _) => const SizedBox.shrink()),
        if (wallpaper != null) ColoredBox(color: context.palette.background.withValues(alpha: frostedCover)),
        Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (withSidebar) _Sidebar(location: location, diagnostics: diagnostics),
            Expanded(child: child),
            // Last, so it docks against the window's right edge — the opposite end from the
            // navigation it has to leave usable, since the panel's whole reason to exist is
            // being read while moving between the screens its output names. A row child rather
            // than an overlay: it takes width from the page instead of covering it. Suppressed
            // in full screen along with the sidebar, because that screen asked for the window.
            //
            // Not on the Diagnostics page itself, which shows the same entries with room to read
            // them: a strip beside a page of the same thing would be the thing twice.
            if (withSidebar && diagnostics != null && location != Routes.diagnostics)
              DiagnosticsPanel(log: diagnostics),
          ],
        ),
        ])),
      ),
    );

    if (diagnostics == null) return scaffold;

    // Above the page rather than around the panel: this is how a *screen* reaches the log to
    // record into it, and screens are the `child` here. See `DiagnosticsLogScope.maybeOf`.
    return DiagnosticsLogScope(log: diagnostics, child: scaffold);
  }
}

class _Sidebar extends StatelessWidget {
  const _Sidebar({required this.location, this.diagnostics});

  final String location;

  /// Null when nothing is collecting output, which is what a widget test pumping the shell gets —
  /// the footer simply has no Diagnostics button in that case.
  final DiagnosticsLog? diagnostics;

  /// 240px, as it was. The diagnostics panel's and the Applications table's widths are worked out
  /// against it (web/CLAUDE.md), so it stays put though the labels are narrower in Arial.
  static const _width = 240.0;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final inApplications = location.startsWith(Routes.applications);
    final inSync = location == Routes.clients || location == Routes.upgradeScripts;
    // startsWith, so CVE Mapping keeps the parent lit. `/settings/vulnerabilities` is a different
    // prefix and does not collide with it.
    final inVulnerabilities = location.startsWith(Routes.vulnerabilities);
    final inSettings = location.startsWith('/settings');
    // A menu shows its sub tabs only while the current page is one of its own, so the sidebar stays
    // short and the parent entry is how a group is opened. No state: the route decides.

    // The UI guide's navy sidebar, in both themes, with htw-style's Cyan 500 accent line along the
    // edge that meets the page — the line the guide's top bar carries along its bottom.
    final frosted = Frosted.of(context);
    final bar = Container(
      width: _width,
      decoration: BoxDecoration(
        color: frosted ? palette.nav.withValues(alpha: 0.78) : palette.nav,
        border: Border(right: BorderSide(color: palette.navLine, width: 3)),
      ),
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(14, 0, 14, 32),
        child: ConstrainedBox(
          // Fills the viewport when the nav is shorter than it, so the footer sits at the bottom;
          // scrolls when it is not, which is what stops a short window clipping the log-out
          // button off with no way back to it.
          constraints: BoxConstraints(minHeight: MediaQuery.sizeOf(context).height - 32),
          // A scroll view hands its child an unbounded height, and a Column holding an Expanded
          // cannot lay out under one: an assertion on every frame in debug, and whatever RenderFlex
          // makes of infinity in the release bundle. IntrinsicHeight turns "at least the viewport"
          // into an exact height the spacer below can fill.
          child: IntrinsicHeight(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                const _Brand(),
                // First, because it is the map of the others: what each menu below is for.
                _NavLink(label: 'Overview', path: Routes.overview, selected: location == Routes.overview),
                const SizedBox(height: 8),
                // Who started each patch, and what is wrong with which devices: the view across
                // hosts that Patch Health (failures only) and the host page (one host) do not give.
                _NavLink(
                  label: 'Autonomy',
                  path: Routes.autonomy,
                  selected: location == Routes.autonomy || location.startsWith('${Routes.autonomy}/'),
                ),
                const SizedBox(height: 8),
                // startsWith, not equality, because /hosts/{id}/remote is a Hosts route: a remote
                // control session leaving nothing highlighted in the sidebar reads as having
                // navigated out of the app.
                _NavLink(
                  label: 'Hosts',
                  path: Routes.hosts,
                  selected: location == Routes.hosts || location.startsWith('${Routes.hosts}/'),
                ),
                const SizedBox(height: 8),
                // Beside Hosts rather than under Applications: it answers "which machines are in
                // trouble", which spans operating system and application failures alike.
                _NavLink(
                  label: 'Patch Health',
                  path: Routes.patchHealth,
                  selected: location == Routes.patchHealth,
                ),
                const SizedBox(height: 8),
                // The parent goes to Currently Installed, the same way Sync's goes to Clients:
                // a menu heading that led nowhere would be the only dead entry in the sidebar.
                _NavLink(
                  label: 'Applications',
                  path: Routes.applications,
                  selected: inApplications,
                ),
                // Alphabetical by label, like every other subnav here. Keep it that way when
                // adding one — the list is a lookup, not a workflow.
                if (inApplications) _SubNav(
                  children: [
                    _SubNavLink(
                      label: 'Currently Installed',
                      path: Routes.applications,
                      selected: location == Routes.applications,
                    ),
                    _SubNavLink(
                      label: 'Failed Updates',
                      path: Routes.applicationsFailed,
                      selected: location == Routes.applicationsFailed,
                    ),
                    _SubNavLink(
                      label: 'Managed Elsewhere',
                      path: Routes.applicationsManaged,
                      selected: location == Routes.applicationsManaged,
                    ),
                    _SubNavLink(
                      label: 'Software',
                      path: Routes.applicationsSoftware,
                      selected: location == Routes.applicationsSoftware,
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                // A top-level entry, because it answers a question about the fleet rather than
                // about one application's update state. The parent goes to CVE Reporting and not
                // to the first entry below it: what an administrator comes here for is "what is
                // being exploited right now", and the mapping queue is the work that makes that
                // answer complete rather than the answer.
                _NavLink(
                  label: 'Vulnerabilities',
                  path: Routes.vulnerabilities,
                  selected: inVulnerabilities,
                ),
                // Alphabetical by label, like every other subnav here. Keep it that way when
                // adding one — the list is a lookup, not a workflow.
                if (inVulnerabilities) _SubNav(
                  children: [
                    _SubNavLink(
                      label: 'CVE Mapping',
                      path: Routes.vulnerabilitiesMapping,
                      selected: location == Routes.vulnerabilitiesMapping,
                    ),
                    _SubNavLink(
                      label: 'CVE Reporting',
                      path: Routes.vulnerabilities,
                      selected: location == Routes.vulnerabilities,
                    ),
                    _SubNavLink(
                      label: 'Exposed Applications',
                      path: Routes.vulnerabilitiesExposed,
                      selected: location == Routes.vulnerabilitiesExposed,
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                // What should be true on a device, and how many comply. Its own entry because it is
                // about device configuration, which none of the menus above it are.
                _NavLink(
                  label: 'Baselines',
                  path: Routes.baselines,
                  selected: location.startsWith(Routes.baselines),
                ),
                const SizedBox(height: 8),
                _NavLink(label: 'Sync', path: Routes.clients, selected: inSync),
                // Alphabetical by label. Keep it that way when adding one — the list is a lookup,
                // not a workflow, so there is no order to it a reader could otherwise predict.
                if (inSync) _SubNav(
                  children: [
                    _SubNavLink(label: 'Clients', path: Routes.clients, selected: location == Routes.clients),
                    _SubNavLink(
                      label: 'Upgrade Scripts',
                      path: Routes.upgradeScripts,
                      selected: location == Routes.upgradeScripts,
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                _NavLink(label: 'Settings', path: Routes.settingsAiAgent, selected: inSettings),
                if (inSettings) _SubNav(
                  children: [
                    _SubNavLink(
                      label: 'Administrators',
                      path: Routes.settingsAdministrators,
                      selected: location == Routes.settingsAdministrators,
                    ),
                    _SubNavLink(
                      label: 'AI Agent',
                      path: Routes.settingsAiAgent,
                      selected: location == Routes.settingsAiAgent,
                    ),
                    _SubNavLink(
                      label: 'AI Usage',
                      path: Routes.settingsAiUsage,
                      selected: location == Routes.settingsAiUsage,
                    ),
                    _SubNavLink(
                      label: 'Appearance',
                      path: Routes.settingsAppearance,
                      selected: location == Routes.settingsAppearance,
                    ),
                    _SubNavLink(
                      label: 'Auditing',
                      path: Routes.settingsAuditing,
                      selected: location == Routes.settingsAuditing,
                    ),
                    _SubNavLink(
                      label: 'Authentication',
                      path: Routes.settingsAuthentication,
                      selected: location == Routes.settingsAuthentication,
                    ),
                    _SubNavLink(
                      label: 'GitHub',
                      path: Routes.settingsGitHub,
                      selected: location == Routes.settingsGitHub,
                    ),
                    _SubNavLink(
                      label: 'Outlook Signatures',
                      path: Routes.settingsOutlookSignatures,
                      selected: location == Routes.settingsOutlookSignatures,
                    ),
                    _SubNavLink(
                      label: 'Patching Policy',
                      path: Routes.settingsPatchingPolicy,
                      selected: location == Routes.settingsPatchingPolicy,
                    ),
                    // Alphabetical by label, and Vulnerabilities lands last. This subnav is a
                    // lookup rather than a workflow, so there is no other order a reader could
                    // predict.
                    _SubNavLink(
                      label: 'Vanta',
                      path: Routes.settingsVanta,
                      selected: location == Routes.settingsVanta,
                    ),
                    _SubNavLink(
                      label: 'Vulnerabilities',
                      path: Routes.settingsVulnerabilities,
                      selected: location == Routes.settingsVulnerabilities,
                    ),
                  ],
                ),
                const Expanded(child: SizedBox(height: 24)),
                _SidebarFooter(
                  diagnostics: diagnostics,
                  onDiagnosticsPage: location == Routes.diagnostics,
                ),
              ],
            ),
          ),
        ),
      ),
    );
    // Over a wallpaper the navy sidebar is glass too, so the picture shows behind it.
    return frosted
        ? ClipRect(child: BackdropFilter(filter: ImageFilter.blur(sigmaX: 14, sigmaY: 14), child: bar))
        : bar;
  }
}

/// The logo spot, the product's name and, once it has arrived, the server's version beneath it.
///
/// The spot shows the HTW logo (the reverse artwork, because the sidebar is navy in both themes) or
/// a company's own, which replaces it and stays still; see [LogoSpot]. The HTW logo is the official
/// SVG rasterised to PNG, rather than drawn from the SVG, which would need a package for one image.
class _Brand extends StatelessWidget {
  const _Brand();

  @override
  Widget build(BuildContext context) {
    final version = context.watch<ServerVersionCubit>().state;
    final palette = context.palette;
    final body = Theme.of(context).textTheme.bodyMedium!;
    final appearance = context.watch<AppearanceCubit>().state;
    final company = appearance.effectiveOrganisation.name;

    return Padding(
      padding: const EdgeInsets.fromLTRB(4, 24, 4, 24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          LogoSpot(organisation: appearance.effectiveOrganisation),
          const SizedBox(height: 16),
          Text('Kintsugi', style: body.copyWith(color: palette.onNav, fontSize: 18, fontWeight: FontWeight.w700)),
          if (company != null && company.isNotEmpty)
            Text(company, style: body.copyWith(color: palette.navMuted, fontSize: 13)),
          if (version != null)
            Text('v$version', style: body.copyWith(color: palette.navMuted, fontSize: 13)),
        ],
      ),
    );
  }
}

/// One sidebar entry, the guide's nav link: white and regular weight, a 10% white wash on hover,
/// and the current page in a pill with its label bold in the action text (5.15:1).
Widget _navEntry(BuildContext context, {required String label, required bool selected, required bool hovering, double size = 14}) {
  final palette = context.palette;
  return Container(
    padding: EdgeInsets.symmetric(horizontal: 14, vertical: size < 14 ? 6 : 9),
    decoration: BoxDecoration(
      color: selected ? palette.navPill : (hovering ? palette.onNav.withValues(alpha: 0.10) : null),
      borderRadius: BorderRadius.circular(999),
    ),
    child: Text(
      label,
      style: Theme.of(context).textTheme.bodyMedium!.copyWith(
            color: selected ? palette.navPillText : palette.onNav,
            fontSize: size,
            fontWeight: selected ? FontWeight.w700 : FontWeight.w400,
          ),
    ),
  );
}

class _NavLink extends StatelessWidget {
  const _NavLink({required this.label, required this.path, required this.selected});

  final String label;
  final String path;
  final bool selected;

  @override
  Widget build(BuildContext context) => _HoverTarget(
        builder: (hovering) => _navEntry(context, label: label, selected: selected, hovering: hovering),
        onTap: () => context.go(path),
        semanticLabel: label,
        selected: selected,
      );
}

class _SubNav extends StatelessWidget {
  const _SubNav({required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.only(left: 14, top: 6),
        padding: const EdgeInsets.only(left: 8),
        decoration: BoxDecoration(
          border: Border(left: BorderSide(color: context.palette.onNav.withValues(alpha: 0.18))),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            for (var i = 0; i < children.length; i++) ...[
              if (i > 0) const SizedBox(height: 2),
              children[i],
            ],
          ],
        ),
      );
}

class _SubNavLink extends StatelessWidget {
  const _SubNavLink({required this.label, required this.path, required this.selected});

  final String label;
  final String path;
  final bool selected;

  @override
  Widget build(BuildContext context) => _HoverTarget(
        builder: (hovering) => _navEntry(context, label: label, selected: selected, hovering: hovering, size: 13),
        onTap: () => context.go(path),
        semanticLabel: label,
        selected: selected,
      );
}

/// A tappable nav entry that restyles on hover, and carries the selected state to assistive
/// technology the way `aria-current="page"` did.
class _HoverTarget extends StatefulWidget {
  const _HoverTarget({
    required this.builder,
    required this.onTap,
    required this.semanticLabel,
    required this.selected,
  });

  final Widget Function(bool hovering) builder;
  final VoidCallback onTap;
  final String semanticLabel;
  final bool selected;

  @override
  State<_HoverTarget> createState() => _HoverTargetState();
}

class _HoverTargetState extends State<_HoverTarget> {
  bool _hovering = false;

  @override
  Widget build(BuildContext context) => Semantics(
        label: widget.semanticLabel,
        button: true,
        selected: widget.selected,
        child: MouseRegion(
          cursor: SystemMouseCursors.click,
          onEnter: (_) => setState(() => _hovering = true),
          onExit: (_) => setState(() => _hovering = false),
          child: GestureDetector(onTap: widget.onTap, child: widget.builder(_hovering)),
        ),
      );
}

/// The diagnostics toggle, the theme toggle and, when there is one, the signed-in account.
class _SidebarFooter extends StatelessWidget {
  const _SidebarFooter({this.diagnostics, this.onDiagnosticsPage = false});

  final DiagnosticsLog? diagnostics;

  /// On the Diagnostics page the button has nothing to toggle — the panel is not drawn there — so it
  /// is not drawn either, rather than opening a panel that would appear on the next screen.
  final bool onDiagnosticsPage;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final session = context.watch<SessionBloc>().state;
    final user = session is SessionReady && session.session.signedIn ? session.session.userName : null;
    final diagnostics = this.diagnostics;

    return Container(
      padding: const EdgeInsets.only(top: 16),
      decoration: BoxDecoration(border: Border(top: BorderSide(color: palette.onNav.withValues(alpha: 0.18)))),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // The way back to output that has been hidden. Without it, closing the panel would be
          // the one irreversible action in the app: the entries survive, and nothing else on any
          // screen would offer to show them again.
          if (diagnostics != null && !onDiagnosticsPage)
            AnimatedBuilder(
              animation: diagnostics,
              builder: (context, _) => diagnostics.isEmpty
                  ? const SizedBox.shrink()
                  : Padding(
                      padding: const EdgeInsets.only(bottom: 10),
                      child: _FooterButton(
                        icon:
                            diagnostics.isOpen ? Icons.receipt_long : Icons.receipt_long_outlined,
                        label: 'Diagnostics (${diagnostics.count})',
                        onTap: diagnostics.toggle,
                      ),
                    ),
            ),
          _FooterButton(
            icon: context.watch<ThemeCubit>().state == ThemeMode.light
                ? Icons.dark_mode_outlined
                : Icons.light_mode_outlined,
            label: context.watch<ThemeCubit>().state == ThemeMode.light ? 'Dark mode' : 'Light mode',
            onTap: () => context.read<ThemeCubit>().toggle(),
          ),
          if (user != null) ...[
            const SizedBox(height: 10),
            Text(
              user,
              overflow: TextOverflow.ellipsis,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(color: palette.navMuted),
            ),
            const SizedBox(height: 8),
            _FooterButton(
              icon: Icons.logout,
              label: 'Log out',
              onTap: () => context.read<SessionBloc>().add(const SignOutRequested()),
            ),
          ],
        ],
      ),
    );
  }
}

class _FooterButton extends StatelessWidget {
  const _FooterButton({required this.icon, required this.label, required this.onTap});

  final IconData icon;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return _HoverTarget(
      selected: false,
      semanticLabel: label,
      onTap: onTap,
      // The guide's button on a dark bar: a white icon and border, the white wash on hover.
      builder: (hovering) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 7),
        decoration: BoxDecoration(
          color: hovering ? palette.onNav.withValues(alpha: 0.10) : null,
          border: Border.all(color: palette.onNav.withValues(alpha: 0.45)),
          borderRadius: BorderRadius.circular(999),
        ),
        child: Row(
          children: [
            Icon(icon, size: 15, color: palette.onNav),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                label,
                style: Theme.of(context).textTheme.bodyMedium!.copyWith(color: palette.onNav, fontSize: 13),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
