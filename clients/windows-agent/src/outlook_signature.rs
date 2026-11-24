//! Managed Outlook signatures: each signed-in person's copy of the organisation's signature, kept in
//! place in Outlook desktop on every hourly check-in, and reported per person.
//!
//! **Who.** `baseline::evaluate` hands over the `outlook-signature` items of every verified document.
//! [`check_people`] lists the interactive sessions (`presence::user_sessions`), takes each one's token
//! with `WTSQueryUserToken` and its GCPW email from `HKLM\SOFTWARE\Google\GCPW\Users\<SID>` (`email`,
//! the value Chromium's `gcp_strings.cc` names `kUserEmail` and `reg_utils.cc` keys by SID), and asks
//! the server who each session is (`POST /api/baseline-people`). The server decides; the agent never
//! takes a session's word for an identity. A 404 is an older server and skips the step.
//!
//! **As whom.** Everything that touches a person's profile runs on its own thread, impersonating that
//! person (`ImpersonateLoggedOnUser`, undone by [`Impersonation`]'s `Drop` and, failing that, by the
//! thread ending). This is the opposite of `session_launcher`, which deliberately keeps SYSTEM's rights
//! in the user's session: here the point is to have *only* the person's rights, so a junction or hard
//! link planted in their profile reaches nothing they could not already write themselves. The folder is
//! `SHGetKnownFolderPath(FOLDERID_RoamingAppData)` for that token, which honours folder redirection, and
//! the registry is `RegOpenCurrentUser`, which is the person's hive while impersonating.
//!
//! **What.** Every write is built from `baseline::SIGNATURE_WRITES` ([`build_target`]) and checked
//! against it again before it is made ([`is_listed`]): the signature's `.htm` (UTF-8 with BOM) and
//! `.txt` (UTF-16LE with BOM), `New Signature` and `Reply-Forward Signature` on each matched account,
//! and `MailSettings`' `NewSignature` and `ReplySignature`. The only part of a target the server
//! supplies is the signature name, checked by [`is_valid_signature_name`] first. The one thing ever
//! deleted is a reply default that still names ours, when the copy says the person turned replies off
//! (`reply: false`): that is Outlook's "(none)", and a reply signature the person picked is left alone.
//! The logic is ported from HTW-Infra/ninja-scripts `outlook-signature/Set-OutlookSignature.ps1`
//! (worker 1.0.1), including its account matching and its "files before the registry" order.

use std::io::{self, Read};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::baseline::{
    truncate, BaselineResult, ItemResult, SignatureItem, ValueKind, COMPLIANT, DRIFTED, ERROR, MAX_DETAIL_CHARS, MAX_OBSERVED_CHARS,
    NOT_APPLICABLE, SIGNATURE_WRITES,
};
use crate::config::Config;
use crate::logging;

/// `ResolveBaselinePeopleCommandValidator.MaxSessions`.
const MAX_SESSIONS: usize = 64;
/// Below `HostPersonResolution.MaxWindowsUserLength` (256). A Windows username is 20 at most anyway.
const MAX_WINDOWS_USER_CHARS: usize = 256;
/// `PersonSignature.MaxEmailLength`. A longer GCPW value is sent as null rather than refused with the
/// whole request.
const MAX_EMAIL_CHARS: usize = 254;

/// Outlook 2016 and later; `BaselineItemRules.OutlookSignaturePath` plus `Profiles`.
const OUTLOOK_PROFILES: &str = r"Software\Microsoft\Office\16.0\Outlook\Profiles";
/// Outlook's per-profile account list.
const ACCOUNT_LIST: &str = "9375CFF0413111d3B88A00104B2A6676";
/// What GWSMO names its own mailbox account ("Google Apps" before the rename), with no address on it.
const GWSMO_ACCOUNT_NAMES: [&str; 2] = ["Google Workspace", "Google Apps"];

// Indexes into `SIGNATURE_WRITES`.
const HTM: usize = 0;
const TXT: usize = 1;
const ACCOUNT_NEW: usize = 2;
const ACCOUNT_REPLY: usize = 3;
const MAIL_NEW: usize = 4;
const MAIL_REPLY: usize = 5;

// --- the wire: hand-mirrored from ResolveBaselinePeopleCommand.cs (see .claude/rules/hand-mirrored-dtos.md)

/// Mirrors `ResolveBaselinePeopleCommand`.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct PeopleRequest<'a> {
    serial_number: &'a str,
    sessions: Vec<SessionInput>,
}

/// Mirrors `SessionInput`. `gcpw_email` is sent as null when there is none.
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SessionInput {
    windows_user: String,
    gcpw_email: Option<String>,
}

/// Mirrors `BaselinePeopleDto`.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PeopleResponse {
    #[serde(default)]
    people: Vec<Person>,
}

/// Mirrors `BaselinePersonDto`.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Person {
    windows_user: String,
    email: Option<String>,
    copy: Option<SignatureCopy>,
    #[serde(default)]
    states: Vec<PersonState>,
}

/// Mirrors `PersonSignatureCopyDto`.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SignatureCopy {
    html: String,
    text: String,
    /// Lower-case hex SHA-256 of `html`'s UTF-8 bytes, as the server computed it.
    sha256: String,
    /// False when the person turned the signature off for replies at the source. New mail keeps it.
    /// Absent from an older server, which means on.
    #[serde(default = "replies_on")]
    reply: bool,
}

fn replies_on() -> bool {
    true
}

/// Mirrors `PersonItemStateDto`.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PersonState {
    version_id: String,
    key: String,
    state: String,
    observed: Option<String>,
}

// --- the check ------------------------------------------------------------------------------------

/// Resolves the signed-in people, judges each per-person item as each person who has a copy, and adds
/// those results to `results`, keeping only what differs from the server's copy. Best-effort, like the
/// rest of the baseline check: a failure is logged, and the host results are still reported.
pub fn check_people(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, results: &mut [BaselineResult]) {
    if let Err(err) = try_check_people(client, config, serial_number, results) {
        logging::warn(&format!("could not check managed Outlook signatures: {err:#}"));
    }
}

fn try_check_people(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, results: &mut [BaselineResult]) -> Result<()> {
    let jobs: Vec<(usize, SignatureItem)> =
        results.iter().enumerate().flat_map(|(index, r)| r.signature_items.iter().map(move |item| (index, item.clone()))).collect();
    if jobs.is_empty() {
        return Ok(());
    }

    let sessions = platform::signed_in_sessions().context("could not read who is signed in")?;
    let request = PeopleRequest { serial_number, sessions: sessions.iter().map(|s| s.input.clone()).collect() };
    let response = client.post(config.baseline_people_url()).json(&request).send().context("the request could not be sent")?;
    if response.status() == reqwest::StatusCode::NOT_FOUND {
        logging::info("the server does not resolve signed-in people (HTTP 404); skipping managed Outlook signatures this check-in");
        return Ok(());
    }
    if !response.status().is_success() {
        anyhow::bail!("the server refused the signed-in people (HTTP {})", response.status());
    }
    let people = response.json::<PeopleResponse>().context("could not parse the signed-in people")?;

    let items: Vec<SignatureItem> = jobs.iter().map(|(_, item)| item.clone()).collect();
    let writable = items.iter().any(|item| item.enforce);

    for person in &people.people {
        // Nobody resolved, or nobody with a copy: the server already knows, and nothing is touched.
        let (Some(email), Some(copy)) = (person.email.as_deref(), person.copy.as_ref()) else { continue };
        let Some(session) = sessions.iter().find(|s| s.input.windows_user.eq_ignore_ascii_case(person.windows_user.trim())) else {
            logging::warn(&format!("the server answered for {}, who is not one of this check's sessions", person.windows_user));
            continue;
        };

        let outcomes = platform::as_person(&session.token, writable, |store| {
            items.iter().map(|item| judge_person(item, email, copy, store)).collect::<Vec<_>>()
        })
        .unwrap_or_else(|err| {
            let detail = format!("could not act as {}: {err:#}", person.windows_user);
            items.iter().map(|item| person_result(item, email, ERROR, None, Some(detail.clone()), false)).collect()
        });

        for ((index, item), result) in jobs.iter().zip(outcomes) {
            if result.corrected {
                logging::info(&format!("outlook signature: {} for {email}: {}", item.key, result.detail.as_deref().unwrap_or("")));
            }
            let version = &results[*index].version_id;
            let known = person.states.iter().find(|s| s.version_id.eq_ignore_ascii_case(version) && s.key == item.key);
            if result.corrected || known.is_none_or(|k| k.state != result.state || k.observed != result.observed) {
                results[*index].items.push(result);
            }
        }
    }
    Ok(())
}

fn person_result(item: &SignatureItem, email: &str, state: &'static str, observed: Option<String>, detail: Option<String>, corrected: bool) -> ItemResult {
    ItemResult {
        key: item.key.clone(),
        state,
        observed: observed.map(|o| truncate(&o, MAX_OBSERVED_CHARS)),
        detail: detail.map(|d| truncate(&d, MAX_DETAIL_CHARS)),
        subject: Some(email.to_string()),
        corrected,
    }
}

/// One person's signature, judged and — in enforce mode — set, through `store`, which acts as them.
/// Never logs: it runs while impersonating.
pub(crate) fn judge_person(item: &SignatureItem, email: &str, copy: &SignatureCopy, store: &dyn PersonStore) -> ItemResult {
    let result = |state, observed, detail: Option<String>, corrected| person_result(item, email, state, observed, detail, corrected);

    let Some(name) = item.signature_name.as_deref().filter(|n| is_valid_signature_name(n)) else {
        return result(ERROR, None, Some("the item's signature name is not a bare file name".into()), false);
    };
    if !sha256_hex(copy.html.as_bytes()).eq_ignore_ascii_case(copy.sha256.trim()) {
        return result(ERROR, None, Some("the served copy does not match its own SHA-256".into()), false);
    }
    let accounts = match store.outlook_accounts() {
        Err(err) => return result(ERROR, None, Some(format!("could not read Outlook's profiles: {err}")), false),
        Ok(None) => return result(NOT_APPLICABLE, None, Some("no Outlook 16.0 profile".into()), false),
        Ok(Some(accounts)) => accounts,
    };
    let matched = match_accounts(&accounts, email);

    let before = match survey(store, name, copy, &matched) {
        Ok(survey) => survey,
        Err(err) => return result(ERROR, None, Some(err), false),
    };
    if before.is_compliant() {
        return result(COMPLIANT, Some(before.summary()), None, false);
    }
    if !item.enforce {
        return result(DRIFTED, Some(before.summary()), None, false);
    }

    let was = before.summary();
    if let Err(err) = set(store, name, &before) {
        return result(ERROR, Some(was.clone()), Some(format!("drifted ({was}) and could not be set: {err}")), false);
    }
    match survey(store, name, copy, &matched) {
        Err(err) => result(ERROR, None, Some(format!("set by Kintsugi (was: {was}), but could not be read back: {err}")), true),
        Ok(after) if !after.is_compliant() => {
            result(ERROR, Some(after.summary()), Some(format!("set by Kintsugi, but it reads as: {} (was: {was})", after.summary())), true)
        }
        Ok(after) => result(COMPLIANT, Some(after.summary()), Some(format!("set by Kintsugi (was: {was})")), true),
    }
}

/// Writes what `survey` found drifted. Files before the registry, as the PowerShell script does: a
/// default that names a signature whose files failed to write would leave the person with none at all.
/// Our signature is always made the default, replacing a default the person chose, as script 361 does —
/// except on replies when the copy says `reply: false`, where ours is removed and theirs is kept.
fn set(store: &dyn PersonStore, name: &str, survey: &Survey) -> Result<(), String> {
    if let Some((Target::File(first), _)) = survey.files.first() {
        // The folder the listed files sit in, taken from a listed target rather than built separately.
        let folder = first.rsplit_once('\\').map(|(folder, _)| folder).ok_or("a signature file has no folder")?;
        store.create_folder(folder).map_err(|e| format!("could not create the signatures folder: {e}"))?;
    }
    for (target, bytes) in &survey.files {
        put_file(store, target, bytes)?;
    }
    for target in &survey.values {
        put_value(store, target, name)?;
    }
    for target in &survey.removals {
        drop_value(store, target)?;
    }
    Ok(())
}

/// The one way a file is written: only a target on the write list.
fn put_file(store: &dyn PersonStore, target: &Target, bytes: &[u8]) -> Result<(), String> {
    match target {
        Target::File(path) if is_listed(target, store.roaming_app_data()) => {
            store.write_file(path, bytes).map_err(|e| format!("could not write {}: {e}", file_name(path)))
        }
        _ => Err(format!("refused {target:?}: it is not on the signature write list")),
    }
}

/// The one way a registry value is written: only a target on the write list, and only ever the name.
fn put_value(store: &dyn PersonStore, target: &Target, name: &str) -> Result<(), String> {
    match target {
        Target::Value { key, name: value_name, kind } if is_listed(target, store.roaming_app_data()) => {
            store.write_value(key, value_name, kind, name).map_err(|e| format!("could not set {value_name}: {e}"))
        }
        _ => Err(format!("refused {target:?}: it is not on the signature write list")),
    }
}

/// The one way a registry value is removed: only a target on the write list, and only a reply default
/// `survey` found still naming ours while the person has replies turned off.
fn drop_value(store: &dyn PersonStore, target: &Target) -> Result<(), String> {
    match target {
        Target::Value { key, name: value_name, .. } if is_listed(target, store.roaming_app_data()) => {
            store.delete_value(key, value_name).map_err(|e| format!("could not clear {value_name}: {e}"))
        }
        _ => Err(format!("refused {target:?}: it is not on the signature write list")),
    }
}

fn file_name(path: &str) -> &str {
    path.rsplit('\\').next().unwrap_or(path)
}

// --- what is there ----------------------------------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
enum FileState {
    Matches,
    Differs,
    #[default]
    Missing,
}

/// One read of everything the item covers, and what would have to be written to fix it.
#[derive(Debug, Default)]
struct Survey {
    htm: FileState,
    txt: FileState,
    accounts: usize,
    accounts_set: usize,
    mail_set: usize,
    /// False: the person turned replies off, so a reply default is right when it is anything but ours.
    replies: bool,
    files: Vec<(Target, Vec<u8>)>,
    values: Vec<Target>,
    /// Reply defaults still naming ours while replies are off.
    removals: Vec<Target>,
}

impl Survey {
    fn is_compliant(&self) -> bool {
        self.htm == FileState::Matches && self.txt == FileState::Matches && self.accounts_set == self.accounts && self.mail_set == 2
    }

    /// Records one default: true when it is as it should be, else queues the fix. Replies off turns
    /// "ours" from what is wanted into the one thing to remove.
    fn note(&mut self, target: Target, ours: bool, want_ours: bool) -> bool {
        match (want_ours, ours) {
            (true, false) => self.values.push(target),
            (false, true) => self.removals.push(target),
            _ => return true,
        }
        false
    }

    /// Stable from check to check, so an unchanged person is not reported again.
    fn summary(&self) -> String {
        let replies = if self.replies { "" } else { " (replies off)" };
        if self.is_compliant() {
            return format!("htm and txt match; default on {n} of {n} accounts and MailSettings{replies}", n = self.accounts);
        }
        let mut parts = Vec::new();
        for (file, state) in [("htm", self.htm), ("txt", self.txt)] {
            match state {
                FileState::Matches => {}
                FileState::Differs => parts.push(format!("{file} differs")),
                FileState::Missing => parts.push(format!("{file} missing")),
            }
        }
        if self.accounts_set < self.accounts {
            parts.push(format!("default on {} of {} accounts", self.accounts_set, self.accounts));
        }
        if self.mail_set < 2 {
            parts.push(format!("MailSettings default on {} of 2", self.mail_set));
        }
        parts.join("; ") + replies
    }
}

fn survey(store: &dyn PersonStore, name: &str, copy: &SignatureCopy, matched: &[&OutlookAccount]) -> Result<Survey, String> {
    let roaming = store.roaming_app_data();
    let fill = |profile, account| Fill { roaming, signature_name: name, profile, account };
    let mut survey = Survey { accounts: matched.len(), replies: copy.reply, ..Survey::default() };
    let want_ours = |index| copy.reply || !matches!(index, ACCOUNT_REPLY | MAIL_REPLY);

    for (index, expected) in [(HTM, htm_bytes(&copy.html)), (TXT, txt_bytes(&copy.text))] {
        let target = build_target(index, &fill("", ""))?;
        let Target::File(path) = &target else { return Err("a signature file is not a file target".into()) };
        let state = match store.read_file(path).map_err(|e| format!("could not read {}: {e}", file_name(path)))? {
            None => FileState::Missing,
            Some(FileContent::Bytes(bytes)) if sha256_hex(&bytes) == sha256_hex(&expected) => FileState::Matches,
            // Over the cap cannot be ours (see MAX_PERSON_FILE_BYTES): different, so enforce rewrites it.
            Some(FileContent::Bytes(_) | FileContent::TooLarge) => FileState::Differs,
        };
        if index == HTM {
            survey.htm = state;
        } else {
            survey.txt = state;
        }
        if state != FileState::Matches {
            survey.files.push((target, expected));
        }
    }

    for account in matched {
        let mut set = true;
        for index in [ACCOUNT_NEW, ACCOUNT_REPLY] {
            let target = build_target(index, &fill(&account.profile, &account.account))?;
            let ours = value_is(store, &target, name)?;
            set &= survey.note(target, ours, want_ours(index));
        }
        survey.accounts_set += usize::from(set);
    }

    for index in [MAIL_NEW, MAIL_REPLY] {
        let target = build_target(index, &fill("", ""))?;
        let ours = value_is(store, &target, name)?;
        survey.mail_set += usize::from(survey.note(target, ours, want_ours(index)));
    }
    Ok(survey)
}

/// Whether the value holds exactly `name` (case and all, as the script's `-ceq`), once decoded and trimmed.
fn value_is(store: &dyn PersonStore, target: &Target, name: &str) -> Result<bool, String> {
    let Target::Value { key, name: value_name, .. } = target else { return Err("a default is not a registry target".into()) };
    store.read_value(key, value_name).map(|v| v.as_deref() == Some(name)).map_err(|e| format!("could not read {value_name}: {e}"))
}

/// One account in one Outlook profile of the person's: its profile and account key names (what fills
/// `<profile>` and `<account>`) and the two strings matching reads.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub(crate) struct OutlookAccount {
    pub profile: String,
    pub account: String,
    pub name: String,
    pub email: String,
}

/// The accounts that are this person's, as the script matches them: any account that carries the
/// address, as its `Account Name` or its `Email`; or GWSMO's own mailbox account, which carries no
/// address — named exactly `Google Workspace` or `Google Apps`, in a profile named `<email> - …`. Exact
/// names keep an added mailbox such as "Google Workspace - events@…" out.
pub(crate) fn match_accounts<'a>(accounts: &'a [OutlookAccount], email: &str) -> Vec<&'a OutlookAccount> {
    let email = email.trim().to_lowercase();
    let profile_prefix = format!("{email} - ");
    accounts
        .iter()
        .filter(|a| {
            a.name.trim().to_lowercase() == email
                || a.email.trim().to_lowercase() == email
                || (a.profile.to_lowercase().starts_with(&profile_prefix)
                    && GWSMO_ACCOUNT_NAMES.iter().any(|gwsmo| a.name.trim().eq_ignore_ascii_case(gwsmo)))
        })
        .collect()
}

/// An account string as the script's `ConvertFrom-RegValue` reads it: REG_SZ, or REG_BINARY holding
/// UTF-16LE (older Outlook), trailing NULs and whitespace trimmed. Anything else reads as empty.
pub(crate) fn account_text(vtype: &winreg::enums::RegType, bytes: &[u8]) -> String {
    use winreg::enums::RegType::{REG_BINARY, REG_EXPAND_SZ, REG_SZ};
    match vtype {
        REG_SZ | REG_EXPAND_SZ | REG_BINARY => {
            let units: Vec<u16> = bytes.chunks_exact(2).map(|pair| u16::from_le_bytes([pair[0], pair[1]])).collect();
            String::from_utf16_lossy(&units).trim_end_matches('\0').trim().to_string()
        }
        _ => String::new(),
    }
}

// --- the write list ---------------------------------------------------------------------------------

/// The server's `BaselineItemRules.IsValidSignatureName`, exactly: 1–64 letters, digits, spaces and
/// `( ) . _ -`, starting with a letter or digit, and not ending with a dot or a space (Windows strips
/// both from a file name, and the registry would then name a file that does not exist).
pub(crate) fn is_valid_signature_name(name: &str) -> bool {
    let mut chars = name.chars();
    let Some(first) = chars.next() else { return false };
    first.is_ascii_alphanumeric()
        && name.chars().count() <= 64
        && chars.all(|c| c.is_ascii_alphanumeric() || " ()._-".contains(c))
        && !name.ends_with('.')
        && !name.ends_with(' ')
}

/// A concrete thing a signature write may touch.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum Target {
    File(String),
    Value { key: String, name: String, kind: ValueKind },
}

/// What the agent fills a write-list entry's placeholders with.
pub(crate) struct Fill<'a> {
    pub roaming: &'a str,
    pub signature_name: &'a str,
    pub profile: &'a str,
    pub account: &'a str,
}

/// `SIGNATURE_WRITES[index]` with its placeholders filled, each checked first, and the result checked
/// against the list again.
pub(crate) fn build_target(index: usize, fill: &Fill<'_>) -> Result<Target, String> {
    let (target, template, value_name, value_type) = SIGNATURE_WRITES.get(index).ok_or("no such signature write")?;
    let mut path = template.to_string();
    for (placeholder, value) in
        [("<RoamingAppData>", fill.roaming), ("<signatureName>", fill.signature_name), ("<profile>", fill.profile), ("<account>", fill.account)]
    {
        if path.contains(placeholder) {
            if !fits(placeholder, value) {
                return Err(format!("{placeholder} cannot be {value:?}"));
            }
            path = path.replace(placeholder, value);
        }
    }
    let built = match *target {
        "file" => Target::File(path),
        "HKCU" => Target::Value {
            key: path,
            name: value_name.to_string(),
            kind: ValueKind::from_document(value_type).ok_or("the write list names a type this agent does not write")?,
        },
        _ => return Err("the write list names a target this agent does not write".into()),
    };
    if !is_listed(&built, fill.roaming) {
        return Err(format!("refused {built:?}: it is not on the signature write list"));
    }
    Ok(built)
}

/// Whether `target` is exactly one of `SIGNATURE_WRITES` with valid placeholders, `<RoamingAppData>`
/// being `roaming`. Checked on every write, whatever built the target.
pub(crate) fn is_listed(target: &Target, roaming: &str) -> bool {
    SIGNATURE_WRITES.iter().any(|(kind, template, value_name, value_type)| match target {
        Target::File(path) => *kind == "file" && matches_template(template, path, roaming),
        Target::Value { key, name, kind: value_kind } => {
            *kind == "HKCU"
                && name == value_name
                && ValueKind::from_document(value_type).as_ref() == Some(value_kind)
                && matches_template(template, key, roaming)
        }
    })
}

fn matches_template(template: &str, concrete: &str, roaming: &str) -> bool {
    let (template, concrete) = match template.strip_prefix("<RoamingAppData>\\") {
        Some(rest) => {
            let prefix = format!("{roaming}\\");
            if !fits("<RoamingAppData>", roaming) || !concrete.get(..prefix.len()).is_some_and(|p| p.eq_ignore_ascii_case(&prefix)) {
                return false;
            }
            (rest, &concrete[prefix.len()..])
        }
        None => (template, concrete),
    };
    let template: Vec<&str> = template.split('\\').collect();
    let concrete: Vec<&str> = concrete.split('\\').collect();
    template.len() == concrete.len() && template.iter().zip(&concrete).all(|(t, c)| segment_matches(t, c))
}

/// One path segment: literal (ignoring ASCII case, as Windows paths and keys are) or one placeholder
/// with literal text either side.
fn segment_matches(template: &str, concrete: &str) -> bool {
    match (template.find('<'), template.find('>')) {
        (Some(open), Some(close)) if open < close => {
            let (before, placeholder, after) = (&template[..open], &template[open..=close], &template[close + 1..]);
            concrete.len() >= before.len() + after.len()
                && concrete.get(..before.len()).is_some_and(|b| b.eq_ignore_ascii_case(before))
                && concrete.get(concrete.len() - after.len()..).is_some_and(|a| a.eq_ignore_ascii_case(after))
                && concrete.get(before.len()..concrete.len() - after.len()).is_some_and(|middle| fits(placeholder, middle))
        }
        _ => template.eq_ignore_ascii_case(concrete),
    }
}

/// Whether `value` may fill `placeholder`.
fn fits(placeholder: &str, value: &str) -> bool {
    let plain = |v: &str| !v.is_empty() && !v.chars().any(char::is_control);
    match placeholder {
        "<signatureName>" => is_valid_signature_name(value),
        // An Outlook profile's key name: one key, not a path.
        "<profile>" => plain(value) && value.chars().count() <= 255 && !value.contains('\\') && value != "." && value != "..",
        // An account key is eight hex digits.
        "<account>" => value.len() == 8 && value.chars().all(|c| c.is_ascii_hexdigit()),
        // An absolute folder (a drive or, redirected, a share) with no trailing separator and no `..`.
        "<RoamingAppData>" => {
            let absolute = value.get(1..3) == Some(":\\") || value.starts_with("\\\\");
            plain(value) && absolute && !value.ends_with('\\') && !value.split('\\').any(|segment| segment == "..")
        }
        _ => false,
    }
}

// --- encodings: exactly the PowerShell script's ------------------------------------------------------

/// `.htm`: UTF-8 with BOM (`New-Object System.Text.UTF8Encoding $true`). Its meta tag says utf-8 too.
pub(crate) fn htm_bytes(html: &str) -> Vec<u8> {
    let mut bytes = vec![0xEF, 0xBB, 0xBF];
    bytes.extend_from_slice(html.as_bytes());
    bytes
}

/// `.txt`: UTF-16LE with BOM (`[Text.Encoding]::Unicode`), which is how Outlook itself writes a
/// non-ANSI plain-text signature.
pub(crate) fn txt_bytes(text: &str) -> Vec<u8> {
    let mut bytes = vec![0xFF, 0xFE];
    bytes.extend(text.encode_utf16().flat_map(u16::to_le_bytes));
    bytes
}

/// The most bytes read from any file in the person's profile, by every read in this module.
///
/// The person owns `<RoamingAppData>\Microsoft\Signatures`, so they can put a file of any size where this
/// service reads; an unbounded read would let any signed-in user make the SYSTEM service allocate as much
/// as they like. 1 MiB is twice the largest file this module ever writes — the `.txt`, UTF-16LE of a body the
/// server caps at 256 KiB of UTF-8, so at most 2 + 2 × 256 KiB bytes — so a file over it can never be the
/// managed copy: it counts as different, and enforce rewrites it.
pub(crate) const MAX_PERSON_FILE_BYTES: u64 = 1024 * 1024;

/// What a capped read of a person's file found.
#[derive(Debug, PartialEq, Eq)]
pub(crate) enum FileContent {
    Bytes(Vec<u8>),
    /// Over [`MAX_PERSON_FILE_BYTES`]; only that many bytes and one more were read to find out.
    TooLarge,
}

/// Reads at most [`MAX_PERSON_FILE_BYTES`] + 1 bytes, whatever the file claims its size is: a length taken
/// from metadata could change between the check and the read, so the bound is on the read itself.
pub(crate) fn read_capped(reader: impl io::Read) -> io::Result<FileContent> {
    let mut bytes = Vec::new();
    reader.take(MAX_PERSON_FILE_BYTES + 1).read_to_end(&mut bytes)?;
    Ok(if bytes.len() as u64 > MAX_PERSON_FILE_BYTES { FileContent::TooLarge } else { FileContent::Bytes(bytes) })
}

fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes).iter().map(|b| format!("{b:02x}")).collect()
}

// --- the person's store -----------------------------------------------------------------------------

/// Everything `judge_person` reads and writes, as the person. Raw paths here; the only writers are
/// `put_file`, `put_value` and `drop_value`, which check the write list first.
pub(crate) trait PersonStore {
    /// The person's own `FOLDERID_RoamingAppData`, redirected or not.
    fn roaming_app_data(&self) -> &str;
    /// Every account under `OUTLOOK_PROFILES`, or `None` when the person has no Outlook 16.0 profile.
    fn outlook_accounts(&self) -> io::Result<Option<Vec<OutlookAccount>>>;
    /// The file's content through [`read_capped`] — never more than [`MAX_PERSON_FILE_BYTES`] + 1 bytes —
    /// or `None` when it does not exist.
    fn read_file(&self, path: &str) -> io::Result<Option<FileContent>>;
    fn create_folder(&self, path: &str) -> io::Result<()>;
    fn write_file(&self, path: &str, bytes: &[u8]) -> io::Result<()>;
    /// A string value under the person's HKCU, decoded as `account_text` does. `None` when absent.
    fn read_value(&self, key: &str, name: &str) -> io::Result<Option<String>>;
    fn write_value(&self, key: &str, name: &str, kind: &ValueKind, data: &str) -> io::Result<()>;
    /// Removes the value; one already gone is not an error.
    fn delete_value(&self, key: &str, name: &str) -> io::Result<()>;
}

/// Undoes the calling thread's impersonation when dropped, so an early return or a panic cannot leave a
/// SYSTEM thread acting as somebody. `revert` is `RevertToSelf`, or a counter in tests.
pub(crate) struct Impersonation {
    revert: fn() -> bool,
}

impl Drop for Impersonation {
    fn drop(&mut self) {
        if !(self.revert)() {
            // Nothing else runs on this thread: it ends straight after, and its impersonation with it.
            logging::error("could not stop acting as a signed-in person; the thread that was doing so is ending");
        }
    }
}

mod platform {
    use std::io;
    use std::ptr;

    use anyhow::{anyhow, Result};
    use windows_sys::Win32::Foundation::{CloseHandle, LocalFree, HANDLE};
    use windows_sys::Win32::Security::Authorization::ConvertSidToStringSidW;
    use windows_sys::Win32::Security::{GetTokenInformation, ImpersonateLoggedOnUser, RevertToSelf, TokenUser, TOKEN_USER};
    use windows_sys::Win32::System::Com::CoTaskMemFree;
    use windows_sys::Win32::System::Registry::RegOpenCurrentUser;
    use windows_sys::Win32::System::RemoteDesktop::WTSQueryUserToken;
    use windows_sys::Win32::UI::Shell::{FOLDERID_RoamingAppData, SHGetKnownFolderPath, KF_FLAG_DEFAULT};
    use winreg::enums::{RegType, HKEY_LOCAL_MACHINE, KEY_READ, KEY_SET_VALUE, KEY_WOW64_64KEY, KEY_WRITE};
    use winreg::{RegKey, RegValue};

    use super::{
        account_text, read_capped, FileContent, Impersonation, OutlookAccount, PersonStore, SessionInput, ValueKind, ACCOUNT_LIST,
        MAX_EMAIL_CHARS, MAX_SESSIONS,
        MAX_WINDOWS_USER_CHARS, OUTLOOK_PROFILES,
    };
    use crate::{logging, presence};

    /// Where GCPW records each Google account it signed in, by the Windows account's SID.
    const GCPW_USERS: &str = r"SOFTWARE\Google\GCPW\Users";

    /// A signed-in person's primary token from `WTSQueryUserToken`, closed on drop.
    pub(crate) struct UserToken(pub(super) HANDLE);

    // SAFETY: a token handle is a kernel handle, usable from any thread; it is only read (impersonated,
    // queried) once made, and closed once, by Drop.
    unsafe impl Send for UserToken {}
    unsafe impl Sync for UserToken {}

    impl Drop for UserToken {
        fn drop(&mut self) {
            // SAFETY: ours, from WTSQueryUserToken or DuplicateToken, and closed only here.
            unsafe { CloseHandle(self.0) };
        }
    }

    impl UserToken {
        fn of_session(session_id: u32) -> io::Result<Self> {
            let mut token: HANDLE = ptr::null_mut();
            // SAFETY: a valid out-pointer. Needs SE_TCB_NAME, which the service has as SYSTEM.
            if unsafe { WTSQueryUserToken(session_id, &mut token) } == 0 {
                return Err(io::Error::last_os_error());
            }
            Ok(Self(token))
        }

        /// The token's user as a string SID (`S-1-5-21-…`).
        fn sid(&self) -> io::Result<String> {
            let mut needed: u32 = 0;
            // SAFETY: asking for the size only, with a null buffer of length 0.
            unsafe { GetTokenInformation(self.0, TokenUser, ptr::null_mut(), 0, &mut needed) };
            if needed == 0 {
                return Err(io::Error::last_os_error());
            }
            // u64s, so the TOKEN_USER at its start is aligned.
            let mut buffer = vec![0u64; (needed as usize).div_ceil(8)];
            // SAFETY: `buffer` holds at least `needed` bytes.
            if unsafe { GetTokenInformation(self.0, TokenUser, buffer.as_mut_ptr().cast(), needed, &mut needed) } == 0 {
                return Err(io::Error::last_os_error());
            }
            // SAFETY: Windows wrote a TOKEN_USER at the start of the aligned buffer, whose SID points
            // inside the same buffer, which outlives this read.
            let user = unsafe { &*(buffer.as_ptr().cast::<TOKEN_USER>()) };
            let mut text: windows_sys::core::PWSTR = ptr::null_mut();
            // SAFETY: a SID Windows just returned and a valid out-pointer; `text` is freed with LocalFree.
            if unsafe { ConvertSidToStringSidW(user.User.Sid, &mut text) } == 0 {
                return Err(io::Error::last_os_error());
            }
            let sid = from_wide(text);
            // SAFETY: allocated by ConvertSidToStringSidW for us.
            unsafe { LocalFree(text.cast()) };
            Ok(sid)
        }
    }

    /// Reads a NUL-terminated UTF-16 string Windows allocated.
    fn from_wide(text: *const u16) -> String {
        if text.is_null() {
            return String::new();
        }
        let mut len = 0;
        // SAFETY: Windows returned a NUL-terminated string; read up to and not past the NUL.
        while unsafe { *text.add(len) } != 0 {
            len += 1;
        }
        // SAFETY: `len` UTF-16 units precede the NUL found above.
        String::from_utf16_lossy(unsafe { std::slice::from_raw_parts(text, len) })
    }

    /// One signed-in person, as sent to the server, and their token.
    pub(crate) struct Session {
        pub input: SessionInput,
        pub token: UserToken,
    }

    /// Every interactive session with a user, one per username (the server dedupes the same way), each
    /// with its token and GCPW email. A session whose token cannot be had — logging off as it is read —
    /// is left out rather than failing the rest.
    pub(crate) fn signed_in_sessions() -> Result<Vec<Session>> {
        let mut sessions: Vec<Session> = Vec::new();
        for (session_id, user) in presence::user_sessions()? {
            let user = user.trim().to_string();
            if user.is_empty() || user.chars().count() > MAX_WINDOWS_USER_CHARS || sessions.iter().any(|s| s.input.windows_user.eq_ignore_ascii_case(&user)) {
                continue;
            }
            if sessions.len() >= MAX_SESSIONS {
                break;
            }
            let token = match UserToken::of_session(session_id) {
                Ok(token) => token,
                Err(err) => {
                    logging::warn(&format!("could not take session {session_id}'s token for {user}: {err}"));
                    continue;
                }
            };
            let gcpw_email = token.sid().ok().and_then(|sid| gcpw_email(&sid));
            sessions.push(Session { input: SessionInput { windows_user: user, gcpw_email }, token });
        }
        Ok(sessions)
    }

    /// `HKLM\SOFTWARE\Google\GCPW\Users\<SID>`'s `email`, or `None` when GCPW never signed this account in.
    fn gcpw_email(sid: &str) -> Option<String> {
        // A SID from Windows, but it becomes a key path, so only its own shape is accepted.
        if !sid.starts_with("S-1-") || !sid.chars().all(|c| c.is_ascii_digit() || c == '-' || c == 'S') {
            return None;
        }
        let key = RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey_with_flags(format!(r"{GCPW_USERS}\{sid}"), KEY_READ | KEY_WOW64_64KEY).ok()?;
        let email = key.get_value::<String, _>("email").ok()?.trim().to_string();
        (!email.is_empty() && email.chars().count() <= MAX_EMAIL_CHARS).then_some(email)
    }

    /// Runs `work` on a new thread impersonating `token`'s user, with that person's store. The thread is
    /// the second guarantee behind `Impersonation`: whatever happens, it ends, and nothing else ever ran on it.
    pub(crate) fn as_person<T: Send>(token: &UserToken, writable: bool, work: impl FnOnce(&dyn PersonStore) -> T + Send) -> Result<T> {
        std::thread::scope(|scope| {
            scope
                .spawn(move || -> Result<T> {
                    let _impersonating = Impersonation::begin(token)?;
                    // Declared after the guard, so the person's hive handle is closed before reverting.
                    let store = WindowsStore::open(token, writable)?;
                    Ok(work(&store))
                })
                .join()
                .map_err(|_| anyhow!("the thread acting as this person panicked"))?
        })
    }

    impl Impersonation {
        /// Makes the calling thread act as `token`'s user until the returned guard drops.
        pub(crate) fn begin(token: &UserToken) -> io::Result<Self> {
            // SAFETY: a valid token handle; affects only the calling thread.
            if unsafe { ImpersonateLoggedOnUser(token.0) } == 0 {
                return Err(io::Error::last_os_error());
            }
            Ok(Self { revert: revert_to_self })
        }
    }

    fn revert_to_self() -> bool {
        // SAFETY: no arguments; ends the calling thread's impersonation.
        unsafe { RevertToSelf() != 0 }
    }

    /// The person's roaming folder and hive, opened while impersonating them.
    pub(crate) struct WindowsStore {
        roaming: String,
        hkcu: RegKey,
    }

    impl WindowsStore {
        pub(crate) fn open(token: &UserToken, writable: bool) -> io::Result<Self> {
            let mut path: windows_sys::core::PWSTR = ptr::null_mut();
            // SAFETY: a known-folder id, the person's token and a valid out-pointer; `path` is freed with
            // CoTaskMemFree whatever the result, as the documentation requires.
            let hr = unsafe { SHGetKnownFolderPath(&FOLDERID_RoamingAppData, KF_FLAG_DEFAULT as u32, token.0, &mut path) };
            let roaming = from_wide(path);
            // SAFETY: allocated by SHGetKnownFolderPath (or null, which CoTaskMemFree accepts).
            unsafe { CoTaskMemFree(path.cast()) };
            if hr < 0 {
                return Err(io::Error::from_raw_os_error(hr));
            }

            let mut hkey = ptr::null_mut();
            let access = if writable { KEY_READ | KEY_WRITE } else { KEY_READ };
            // SAFETY: a valid out-pointer. While impersonating, this is the impersonated person's hive.
            let status = unsafe { RegOpenCurrentUser(access, &mut hkey) };
            if status != 0 {
                return Err(io::Error::from_raw_os_error(status as i32));
            }
            // A real handle, not a predefined one, so winreg closes it on drop (see its `close_`).
            Ok(Self { roaming: roaming.trim_end_matches('\\').to_string(), hkcu: RegKey::predef(hkey as _) })
        }

    }

    fn absent_is_none<T>(result: io::Result<T>) -> io::Result<Option<T>> {
        match result {
            Ok(value) => Ok(Some(value)),
            Err(err) if err.kind() == io::ErrorKind::NotFound => Ok(None),
            Err(err) => Err(err),
        }
    }

    impl PersonStore for WindowsStore {
        fn roaming_app_data(&self) -> &str {
            &self.roaming
        }

        fn outlook_accounts(&self) -> io::Result<Option<Vec<OutlookAccount>>> {
            let Some(profiles) = absent_is_none(self.hkcu.open_subkey_with_flags(OUTLOOK_PROFILES, KEY_READ))? else {
                return Ok(None);
            };
            let mut any_profile = false;
            let mut accounts = Vec::new();
            for profile in profiles.enum_keys() {
                let profile = profile?;
                any_profile = true;
                let Some(list) = absent_is_none(profiles.open_subkey_with_flags(format!(r"{profile}\{ACCOUNT_LIST}"), KEY_READ))? else {
                    continue;
                };
                for account in list.enum_keys() {
                    let account = account?;
                    // Accounts are eight hex digits; anything else under the list is not one.
                    if account.len() != 8 || !account.chars().all(|c| c.is_ascii_hexdigit()) {
                        continue;
                    }
                    let key = list.open_subkey_with_flags(&account, KEY_READ)?;
                    let text = |name: &str| key.get_raw_value(name).map(|v| account_text(&v.vtype, &v.bytes)).unwrap_or_default();
                    accounts.push(OutlookAccount { profile: profile.clone(), name: text("Account Name"), email: text("Email"), account });
                }
            }
            Ok(any_profile.then_some(accounts))
        }

        fn read_file(&self, path: &str) -> io::Result<Option<FileContent>> {
            absent_is_none(std::fs::File::open(path))?.map(read_capped).transpose()
        }

        fn create_folder(&self, path: &str) -> io::Result<()> {
            std::fs::create_dir_all(path)
        }

        fn write_file(&self, path: &str, bytes: &[u8]) -> io::Result<()> {
            std::fs::write(path, bytes)
        }

        fn read_value(&self, key: &str, name: &str) -> io::Result<Option<String>> {
            let Some(key) = absent_is_none(self.hkcu.open_subkey_with_flags(key, KEY_READ))? else { return Ok(None) };
            Ok(absent_is_none(key.get_raw_value(name))?.map(|v| account_text(&v.vtype, &v.bytes)))
        }

        fn write_value(&self, key: &str, name: &str, kind: &ValueKind, data: &str) -> io::Result<()> {
            let vtype = match kind {
                ValueKind::String => RegType::REG_SZ,
                ValueKind::ExpandString => RegType::REG_EXPAND_SZ,
                _ => return Err(io::Error::new(io::ErrorKind::Unsupported, "a signature default is a string")),
            };
            let bytes = data.encode_utf16().chain(std::iter::once(0)).flat_map(u16::to_le_bytes).collect();
            let (key, _) = self.hkcu.create_subkey_with_flags(key, KEY_SET_VALUE)?;
            key.set_raw_value(name, &RegValue { bytes, vtype })
        }

        fn delete_value(&self, key: &str, name: &str) -> io::Result<()> {
            let Some(key) = absent_is_none(self.hkcu.open_subkey_with_flags(key, KEY_SET_VALUE))? else { return Ok(()) };
            absent_is_none(key.delete_value(name)).map(|_| ())
        }
    }

}

#[cfg(test)]
mod tests {
    use std::cell::RefCell;
    use std::collections::HashMap;
    use std::sync::atomic::{AtomicUsize, Ordering};

    use super::*;

    const PERSON: &str = "person@example.com";
    const NAME: &str = "Example (managed)";
    const ROAMING: &str = r"C:\Users\person\AppData\Roaming";
    const ACCOUNT_KEY: &str = r"Software\Microsoft\Office\16.0\Outlook\Profiles\person@example.com - Google Workspace\9375CFF0413111d3B88A00104B2A6676\00000002";
    const MAIL: &str = r"Software\Microsoft\Office\16.0\Common\MailSettings";

    fn account(profile: &str, key: &str, name: &str, email: &str) -> OutlookAccount {
        OutlookAccount { profile: profile.into(), account: key.into(), name: name.into(), email: email.into() }
    }

    /// The layouts Set-OutlookSignature.ps1's comments describe, with placeholder addresses.
    fn real_world_accounts() -> Vec<OutlookAccount> {
        vec![
            // GWSMO: the profile carries the address, its own mailbox account does not.
            account("person@example.com - Google Workspace", "00000002", "Google Workspace", ""),
            // An added mailbox in the same profile: never the person's own.
            account("person@example.com - Google Workspace", "00000003", "Google Workspace - events@example.com", ""),
            // An IMAP account named for the address, and one carrying it as its Email.
            account("Outlook", "00000001", PERSON, ""),
            account("Outlook", "00000004", "Work", "Person@Example.com"),
            // Somebody else's GWSMO profile on the same PC, and a longer address that starts the same.
            account("other@example.com - Google Workspace", "00000002", "Google Workspace", ""),
            account("person@example.com.au - Google Workspace", "00000002", "Google Apps", ""),
            // The name before the rename.
            account("person@example.com - Google Apps", "00000005", "Google Apps", ""),
        ]
    }

    #[test]
    fn accounts_match_by_address_or_as_the_persons_gwsmo_mailbox() {
        let accounts = real_world_accounts();
        let matched: Vec<(&str, &str)> = match_accounts(&accounts, "Person@Example.com").iter().map(|a| (a.profile.as_str(), a.account.as_str())).collect();
        assert_eq!(
            matched,
            vec![
                ("person@example.com - Google Workspace", "00000002"),
                ("Outlook", "00000001"),
                ("Outlook", "00000004"),
                ("person@example.com - Google Apps", "00000005"),
            ]
        );
        assert!(match_accounts(&accounts, "nobody@example.com").is_empty());
    }

    #[test]
    fn account_strings_read_as_the_script_reads_them() {
        let utf16 = |s: &str| s.encode_utf16().flat_map(u16::to_le_bytes).collect::<Vec<u8>>();
        use winreg::enums::RegType::{REG_BINARY, REG_DWORD, REG_SZ};
        assert_eq!(account_text(&REG_SZ, &utf16(" Google Workspace\0")), "Google Workspace");
        // Older Outlook: REG_BINARY holding UTF-16LE, NUL-terminated.
        assert_eq!(account_text(&REG_BINARY, &utf16("person@example.com\0\0")), PERSON);
        assert_eq!(account_text(&REG_DWORD, &[1, 0, 0, 0]), "");
    }

    #[test]
    fn a_signature_name_is_the_servers_bare_file_name() {
        for good in ["Example (managed)", "A", "Example_Signature-2.v1", &"0".repeat(64)] {
            assert!(is_valid_signature_name(good), "{good}");
        }
        for bad in ["", " Example", "(managed)", "Example.", "Example ", r"..\..\Startup\run", "Example/managed", "Example:managed", "Exämple", &"0".repeat(65)] {
            assert!(!is_valid_signature_name(bad), "{bad}");
        }
    }

    fn fill<'a>(profile: &'a str, account: &'a str) -> Fill<'a> {
        Fill { roaming: ROAMING, signature_name: NAME, profile, account }
    }

    #[test]
    fn every_target_is_built_from_the_write_list() {
        let profile = "person@example.com - Google Workspace";
        assert_eq!(build_target(HTM, &fill("", "")), Ok(Target::File(format!(r"{ROAMING}\Microsoft\Signatures\{NAME}.htm"))));
        assert_eq!(build_target(TXT, &fill("", "")), Ok(Target::File(format!(r"{ROAMING}\Microsoft\Signatures\{NAME}.txt"))));
        assert_eq!(
            build_target(ACCOUNT_REPLY, &fill(profile, "00000002")),
            Ok(Target::Value { key: ACCOUNT_KEY.into(), name: "Reply-Forward Signature".into(), kind: ValueKind::String })
        );
        assert_eq!(build_target(MAIL_NEW, &fill("", "")), Ok(Target::Value { key: MAIL.into(), name: "NewSignature".into(), kind: ValueKind::ExpandString }));
        for index in 0..SIGNATURE_WRITES.len() {
            assert!(is_listed(&build_target(index, &fill(profile, "0000000a")).unwrap(), ROAMING), "{index}");
        }
    }

    #[test]
    fn a_placeholder_that_would_leave_its_place_is_refused() {
        let bad = |index, fill: Fill<'_>| assert!(build_target(index, &fill).is_err());
        bad(HTM, Fill { signature_name: r"..\..\Start Menu\Programs\Startup\run", ..fill("", "") });
        bad(ACCOUNT_NEW, fill(r"Outlook\..\..\..\Run", "00000001"));
        bad(ACCOUNT_NEW, fill("Outlook", "0000001"));
        bad(ACCOUNT_NEW, fill("Outlook", "zzzzzzzz"));
        bad(HTM, Fill { roaming: r"Users\person", ..fill("", "") });
        bad(HTM, Fill { roaming: r"C:\Users\person\..\..\Windows", ..fill("", "") });
        bad(SIGNATURE_WRITES.len(), fill("", ""));
    }

    #[test]
    fn a_target_outside_the_list_is_refused_and_never_written() {
        let forged = [
            Target::Value { key: r"Software\Microsoft\Windows\CurrentVersion\Run".into(), name: "New Signature".into(), kind: ValueKind::String },
            Target::Value { key: MAIL.into(), name: "Run".into(), kind: ValueKind::String },
            Target::Value { key: MAIL.into(), name: "NewSignature".into(), kind: ValueKind::Dword },
            Target::File(r"C:\Windows\System32\example.htm".into()),
            Target::File(format!(r"{ROAMING}\Microsoft\Windows\Start Menu\Programs\Startup\{NAME}.htm")),
            Target::File(format!(r"{ROAMING}\Microsoft\Signatures\{NAME}.bat")),
        ];
        let store = FakeStore::with(None);
        for target in &forged {
            assert!(!is_listed(target, ROAMING), "{target:?}");
            assert!(put_value(&store, target, NAME).is_err() && put_file(&store, target, b"x").is_err());
        }
        assert!(store.writes.borrow().is_empty());
    }

    #[test]
    fn the_files_are_encoded_byte_for_byte_as_the_script_writes_them() {
        assert_eq!(htm_bytes("<p>é</p>"), [0xEF, 0xBB, 0xBF, b'<', b'p', b'>', 0xC3, 0xA9, b'<', b'/', b'p', b'>']);
        assert_eq!(txt_bytes("é\r\n"), [0xFF, 0xFE, 0xE9, 0x00, 0x0D, 0x00, 0x0A, 0x00]);
        assert_eq!(txt_bytes(""), [0xFF, 0xFE]);
    }

    static REVERTS: AtomicUsize = AtomicUsize::new(0);

    fn counting_revert() -> bool {
        REVERTS.fetch_add(1, Ordering::SeqCst);
        true
    }

    #[test]
    fn the_guard_reverts_on_an_early_return_and_on_a_panic() {
        fn early() -> Result<(), String> {
            let _guard = Impersonation { revert: counting_revert };
            Err("an early return".into())
        }
        let before = REVERTS.load(Ordering::SeqCst);

        assert!(early().is_err());
        assert_eq!(REVERTS.load(Ordering::SeqCst), before + 1);

        let panicked = std::panic::catch_unwind(|| {
            let _guard = Impersonation { revert: counting_revert };
            panic!("while impersonating");
        });
        assert!(panicked.is_err());
        assert_eq!(REVERTS.load(Ordering::SeqCst), before + 2);
    }

    // --- judging, against a fake profile ---------------------------------------------------------------

    #[derive(Default)]
    struct FakeStore {
        accounts: Option<Vec<OutlookAccount>>,
        files: RefCell<HashMap<String, Vec<u8>>>,
        values: RefCell<HashMap<(String, String), String>>,
        writes: RefCell<Vec<String>>,
        refuse_file_writes: bool,
    }

    impl FakeStore {
        fn with(accounts: Option<Vec<OutlookAccount>>) -> Self {
            Self { accounts, ..Self::default() }
        }

        fn value(&self, key: &str, name: &str) -> Option<String> {
            self.values.borrow().get(&(key.to_string(), name.to_string())).cloned()
        }
    }

    impl PersonStore for FakeStore {
        fn roaming_app_data(&self) -> &str {
            ROAMING
        }
        fn outlook_accounts(&self) -> io::Result<Option<Vec<OutlookAccount>>> {
            Ok(self.accounts.clone())
        }
        fn read_file(&self, path: &str) -> io::Result<Option<FileContent>> {
            // Through the same cap as the real store, so the tests see what the service would.
            self.files.borrow().get(path).map(|bytes| read_capped(bytes.as_slice())).transpose()
        }
        fn create_folder(&self, path: &str) -> io::Result<()> {
            self.writes.borrow_mut().push(format!("mkdir {path}"));
            Ok(())
        }
        fn write_file(&self, path: &str, bytes: &[u8]) -> io::Result<()> {
            if self.refuse_file_writes {
                return Err(io::Error::from(io::ErrorKind::PermissionDenied));
            }
            self.writes.borrow_mut().push(path.to_string());
            self.files.borrow_mut().insert(path.to_string(), bytes.to_vec());
            Ok(())
        }
        fn read_value(&self, key: &str, name: &str) -> io::Result<Option<String>> {
            Ok(self.value(key, name))
        }
        fn write_value(&self, key: &str, name: &str, _: &ValueKind, data: &str) -> io::Result<()> {
            self.writes.borrow_mut().push(format!(r"{key}\{name}"));
            self.values.borrow_mut().insert((key.to_string(), name.to_string()), data.to_string());
            Ok(())
        }
        fn delete_value(&self, key: &str, name: &str) -> io::Result<()> {
            self.writes.borrow_mut().push(format!(r"del {key}\{name}"));
            self.values.borrow_mut().remove(&(key.to_string(), name.to_string()));
            Ok(())
        }
    }

    fn copy() -> SignatureCopy {
        let html = "<p>Example Person</p>".to_string();
        SignatureCopy { sha256: sha256_hex(html.as_bytes()), html, text: "Example Person\r\n".into(), reply: true }
    }

    fn item(enforce: bool) -> SignatureItem {
        SignatureItem { key: "outlook.signature".into(), enforce, signature_name: Some(NAME.into()) }
    }

    /// Two of the person's accounts (the GWSMO mailbox and an IMAP one) plus somebody else's.
    fn two_accounts() -> FakeStore {
        FakeStore::with(Some(vec![
            account("person@example.com - Google Workspace", "00000002", "Google Workspace", ""),
            account("Outlook", "00000001", PERSON, ""),
            account("other@example.com - Google Workspace", "00000002", "Google Workspace", ""),
        ]))
    }

    fn judge(store: &FakeStore, enforce: bool) -> ItemResult {
        judge_person(&item(enforce), PERSON, &copy(), store)
    }

    #[test]
    fn audit_reports_what_differs_and_writes_nothing() {
        let store = two_accounts();
        store.values.borrow_mut().insert((ACCOUNT_KEY.into(), "New Signature".into()), NAME.into());
        store.values.borrow_mut().insert((ACCOUNT_KEY.into(), "Reply-Forward Signature".into()), NAME.into());
        store.files.borrow_mut().insert(format!(r"{ROAMING}\Microsoft\Signatures\{NAME}.htm"), b"<p>old</p>".to_vec());

        let result = judge(&store, false);

        assert_eq!(result.state, DRIFTED);
        assert_eq!(result.subject.as_deref(), Some(PERSON));
        assert_eq!(result.observed.as_deref(), Some("htm differs; txt missing; default on 1 of 2 accounts; MailSettings default on 0 of 2"));
        assert!(store.writes.borrow().is_empty());
    }

    #[test]
    fn a_read_stops_one_byte_past_the_cap_whatever_the_file_holds() {
        let exact = read_capped(io::repeat(b'x').take(MAX_PERSON_FILE_BYTES)).unwrap();
        assert!(matches!(exact, FileContent::Bytes(ref b) if b.len() as u64 == MAX_PERSON_FILE_BYTES));

        assert_eq!(read_capped(io::repeat(b'x').take(MAX_PERSON_FILE_BYTES + 1)).unwrap(), FileContent::TooLarge);

        // A file that never ends (a planted device, or one still growing) is still a bounded read.
        let mut endless = io::repeat(b'x');
        let mut counted = CountingReader { inner: &mut endless, read: 0 };
        assert_eq!(read_capped(&mut counted).unwrap(), FileContent::TooLarge);
        assert_eq!(counted.read, MAX_PERSON_FILE_BYTES + 1);
    }

    struct CountingReader<'a, R: io::Read> {
        inner: &'a mut R,
        read: u64,
    }

    impl<R: io::Read> io::Read for CountingReader<'_, R> {
        fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
            let n = self.inner.read(buf)?;
            self.read += n as u64;
            Ok(n)
        }
    }

    /// The PR #80 review: a huge file where the service reads counts as different, and enforce
    /// replaces it with the managed copy.
    #[test]
    fn a_signature_file_over_the_cap_differs_and_enforce_rewrites_it() {
        let store = two_accounts();
        let htm = format!(r"{ROAMING}\Microsoft\Signatures\{NAME}.htm");
        let txt = format!(r"{ROAMING}\Microsoft\Signatures\{NAME}.txt");
        store.files.borrow_mut().insert(htm.clone(), vec![b'x'; MAX_PERSON_FILE_BYTES as usize + 1]);
        store.files.borrow_mut().insert(txt.clone(), txt_bytes("Example Person\r\n"));

        let audit = judge(&store, false);
        assert_eq!(audit.state, DRIFTED);
        assert!(audit.observed.as_deref().unwrap().starts_with("htm differs; default on 0 of 2"), "{:?}", audit.observed);

        let enforced = judge(&store, true);
        assert_eq!((enforced.state, enforced.corrected), (COMPLIANT, true));
        assert_eq!(store.files.borrow()[&htm], htm_bytes(&copy().html));
    }

    #[test]
    fn enforce_writes_files_first_then_every_default_and_says_what_it_was() {
        let store = two_accounts();
        // The person's own default, which ours replaces.
        store.values.borrow_mut().insert((MAIL.into(), "NewSignature".into()), "Personal".into());

        let result = judge(&store, true);

        assert_eq!(result.state, COMPLIANT);
        assert!(result.corrected);
        assert_eq!(result.observed.as_deref(), Some("htm and txt match; default on 2 of 2 accounts and MailSettings"));
        assert_eq!(
            result.detail.as_deref(),
            Some("set by Kintsugi (was: htm missing; txt missing; default on 0 of 2 accounts; MailSettings default on 0 of 2)")
        );
        let writes = store.writes.borrow();
        assert_eq!(writes[0], format!(r"mkdir {ROAMING}\Microsoft\Signatures"));
        assert!(writes[1].ends_with(".htm") && writes[2].ends_with(".txt"), "files before the registry: {writes:?}");
        assert_eq!(writes.len(), 3 + 4 + 2);
        assert!(writes.iter().all(|w| !w.contains("other@example.com")));
        assert_eq!(store.value(MAIL, "NewSignature").as_deref(), Some(NAME));
        assert_eq!(store.files.borrow()[&format!(r"{ROAMING}\Microsoft\Signatures\{NAME}.txt")], txt_bytes("Example Person\r\n"));

        // Set now, so the next check writes nothing.
        drop(writes);
        let again = judge(&store, true);
        assert_eq!((again.state, again.corrected, again.detail), (COMPLIANT, false, None));
        assert_eq!(store.writes.borrow().len(), 9);
    }

    // --- replies turned off at the source --------------------------------------------------------------

    const IMAP_KEY: &str = r"Software\Microsoft\Office\16.0\Outlook\Profiles\Outlook\9375CFF0413111d3B88A00104B2A6676\00000001";

    fn replies_off() -> SignatureCopy {
        SignatureCopy { reply: false, ..copy() }
    }

    /// Everything as a reply-on check leaves it: both files, ours on every default.
    fn all_ours() -> FakeStore {
        let store = two_accounts();
        for (key, value) in [
            (ACCOUNT_KEY, "New Signature"),
            (ACCOUNT_KEY, "Reply-Forward Signature"),
            (IMAP_KEY, "New Signature"),
            (IMAP_KEY, "Reply-Forward Signature"),
            (MAIL, "NewSignature"),
            (MAIL, "ReplySignature"),
        ] {
            store.values.borrow_mut().insert((key.into(), value.into()), NAME.into());
        }
        store.files.borrow_mut().insert(format!(r"{ROAMING}\Microsoft\Signatures\{NAME}.htm"), htm_bytes(&copy().html));
        store.files.borrow_mut().insert(format!(r"{ROAMING}\Microsoft\Signatures\{NAME}.txt"), txt_bytes(&copy().text));
        store
    }

    #[test]
    fn replies_off_takes_ours_off_replies_and_leaves_new_mail() {
        let store = all_ours();
        assert_eq!(judge(&store, true).state, COMPLIANT, "a reply-on check of the same store is compliant");

        let result = judge_person(&item(true), PERSON, &replies_off(), &store);

        assert_eq!((result.state, result.corrected), (COMPLIANT, true));
        assert_eq!(result.observed.as_deref(), Some("htm and txt match; default on 2 of 2 accounts and MailSettings (replies off)"));
        for key in [ACCOUNT_KEY, IMAP_KEY] {
            assert_eq!(store.value(key, "New Signature").as_deref(), Some(NAME));
            assert_eq!(store.value(key, "Reply-Forward Signature"), None);
        }
        assert_eq!(store.value(MAIL, "NewSignature").as_deref(), Some(NAME));
        assert_eq!(store.value(MAIL, "ReplySignature"), None);
        assert!(store.writes.borrow().iter().all(|w| w.starts_with("del ")), "only removals: {:?}", store.writes.borrow());

        // Removed now, so the next check touches nothing, and an audit of it is compliant too.
        let writes = store.writes.borrow().len();
        assert!(!judge_person(&item(true), PERSON, &replies_off(), &store).corrected);
        assert_eq!(judge_person(&item(false), PERSON, &replies_off(), &store).state, COMPLIANT);
        assert_eq!(store.writes.borrow().len(), writes);
    }

    #[test]
    fn replies_off_leaves_a_reply_signature_the_person_picked() {
        let store = all_ours();
        store.values.borrow_mut().insert((ACCOUNT_KEY.into(), "Reply-Forward Signature".into()), "Personal".into());
        store.values.borrow_mut().insert((MAIL.into(), "ReplySignature".into()), "Personal".into());

        judge_person(&item(true), PERSON, &replies_off(), &store);

        assert_eq!(store.value(ACCOUNT_KEY, "Reply-Forward Signature").as_deref(), Some("Personal"));
        assert_eq!(store.value(MAIL, "ReplySignature").as_deref(), Some("Personal"));
        assert_eq!(*store.writes.borrow(), vec![format!(r"del {IMAP_KEY}\Reply-Forward Signature")]);
    }

    #[test]
    fn an_audit_with_replies_off_reports_ours_on_replies_and_removes_nothing() {
        let store = all_ours();

        let result = judge_person(&item(false), PERSON, &replies_off(), &store);

        assert_eq!(result.state, DRIFTED);
        assert_eq!(result.observed.as_deref(), Some("default on 0 of 2 accounts; MailSettings default on 1 of 2 (replies off)"));
        assert!(store.writes.borrow().is_empty());
        assert_eq!(store.value(MAIL, "ReplySignature").as_deref(), Some(NAME));
    }

    #[test]
    fn turning_replies_back_on_puts_ours_back() {
        let store = all_ours();
        judge_person(&item(true), PERSON, &replies_off(), &store);

        let result = judge(&store, true);

        assert_eq!((result.state, result.corrected), (COMPLIANT, true));
        assert_eq!(store.value(IMAP_KEY, "Reply-Forward Signature").as_deref(), Some(NAME));
        assert_eq!(store.value(MAIL, "ReplySignature").as_deref(), Some(NAME));
    }

    #[test]
    fn a_file_that_cannot_be_written_leaves_the_registry_alone() {
        let store = FakeStore { refuse_file_writes: true, ..two_accounts() };

        let result = judge(&store, true);

        assert_eq!(result.state, ERROR);
        assert!(result.detail.as_deref().unwrap().starts_with("drifted (htm missing; txt missing;"), "{:?}", result.detail);
        assert!(store.values.borrow().is_empty());
    }

    #[test]
    fn no_outlook_profile_is_not_applicable() {
        let result = judge(&FakeStore::with(None), true);
        assert_eq!((result.state, result.detail.as_deref()), (NOT_APPLICABLE, Some("no Outlook 16.0 profile")));
    }

    #[test]
    fn a_copy_that_disagrees_with_its_hash_or_a_bad_name_is_an_error_and_nothing_is_touched() {
        let store = two_accounts();
        let tampered = SignatureCopy { html: "<p>other</p>".into(), ..copy() };
        assert_eq!(judge_person(&item(true), PERSON, &tampered, &store).state, ERROR);

        let unnamed = SignatureItem { signature_name: Some(r"..\run".into()), ..item(true) };
        assert_eq!(judge_person(&unnamed, PERSON, &copy(), &store).state, ERROR);
        assert!(store.writes.borrow().is_empty());
    }

    // --- the wire ---------------------------------------------------------------------------------------

    #[test]
    fn the_people_request_and_response_match_the_servers_shapes() {
        let request = PeopleRequest {
            serial_number: "SERIAL",
            sessions: vec![SessionInput { windows_user: "person".into(), gcpw_email: None }],
        };
        assert_eq!(serde_json::to_string(&request).unwrap(), r#"{"serialNumber":"SERIAL","sessions":[{"windowsUser":"person","gcpwEmail":null}]}"#);

        let response: PeopleResponse = serde_json::from_str(
            r#"{"people":[{"windowsUser":"person","email":"person@example.com","resolvedBy":"gcpw","reason":null,
                "copy":{"html":"<p>x</p>","text":"x","sha256":"ab","renderedAtUtc":"2026-11-18T00:00:00+00:00"},
                "states":[{"versionId":"7f0c8f9e-2b1a-4c3d-9e8f-0a1b2c3d4e5f","key":"outlook.signature","state":"Compliant","observed":"o"}]},
              {"windowsUser":"guest","email":null,"resolvedBy":null,"reason":"no person with a signature matches","copy":null,"states":[]}]}"#,
        )
        .unwrap();
        assert_eq!(response.people[0].copy.as_ref().unwrap().html, "<p>x</p>");
        assert!(response.people[0].copy.as_ref().unwrap().reply, "an older server sends no reply: on");
        let off: SignatureCopy = serde_json::from_str(r#"{"html":"h","text":"t","sha256":"ab","reply":false}"#).unwrap();
        assert!(!off.reply);
        assert_eq!(response.people[0].states[0].state, COMPLIANT);
        assert!(response.people[1].email.is_none());
    }

    #[test]
    fn a_host_result_carries_no_subject_and_a_persons_does() {
        let host = ItemResult { key: "k".into(), state: COMPLIANT, observed: None, detail: None, subject: None, corrected: false };
        assert!(!serde_json::to_string(&host).unwrap().contains("subject"));
        let person = person_result(&item(false), PERSON, COMPLIANT, None, None, false);
        assert!(serde_json::to_string(&person).unwrap().contains(r#""subject":"person@example.com""#));
    }

    /// The real thing on a real Windows: impersonating, the person's own folder and hive, and the guard
    /// putting the thread back. CI's Windows runner runs these; Wine is not expected to (`--skip real_windows`).
    #[cfg(windows)]
    mod real_windows {
        use std::ptr;

        use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, ERROR_NO_TOKEN, HANDLE};
        use windows_sys::Win32::Security::{DuplicateToken, SecurityImpersonation, TOKEN_DUPLICATE, TOKEN_IMPERSONATE, TOKEN_QUERY};
        use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetCurrentThread, OpenProcessToken, OpenThreadToken};
        use winreg::enums::RegType;

        use super::super::platform::{as_person, UserToken};
        use super::super::*;
        use super::NAME;

        /// This process's own user, as an impersonation token: the nearest a test gets to a signed-in person.
        fn own_token() -> UserToken {
            let mut process: HANDLE = ptr::null_mut();
            let mut duplicate: HANDLE = ptr::null_mut();
            unsafe {
                assert_ne!(OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_IMPERSONATE, &mut process), 0);
                assert_ne!(DuplicateToken(process, SecurityImpersonation, &mut duplicate), 0);
                CloseHandle(process);
            }
            UserToken(duplicate)
        }

        /// Whether the calling thread is impersonating: it has a thread token only while it is.
        fn impersonating() -> bool {
            let mut token: HANDLE = ptr::null_mut();
            if unsafe { OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, 1, &mut token) } != 0 {
                unsafe { CloseHandle(token) };
                return true;
            }
            assert_eq!(unsafe { GetLastError() }, ERROR_NO_TOKEN);
            false
        }

        #[test]
        fn impersonation_ends_when_the_guard_drops() {
            let token = own_token();
            std::thread::spawn(move || {
                assert!(!impersonating());
                {
                    let _guard = Impersonation::begin(&token).expect("could not impersonate this process's own user");
                    assert!(impersonating());
                }
                assert!(!impersonating());
            })
            .join()
            .unwrap();
        }

        #[test]
        fn the_persons_folder_files_and_hive_round_trip_while_impersonating() {
            let token = own_token();
            let key = format!(r"Software\Kintsugi-agent-test-{}", std::process::id());
            let file = std::env::temp_dir().join(format!("kintsugi-signature-test-{}.txt", std::process::id()));
            let file = file.to_str().unwrap().to_string();

            let (roaming, value) = as_person(&token, true, |store| {
                store.write_file(&file, &txt_bytes("x")).unwrap();
                assert_eq!(store.read_file(&file).unwrap(), Some(FileContent::Bytes(txt_bytes("x"))));
                store.write_value(&key, "NewSignature", &ValueKind::ExpandString, NAME).unwrap();
                (store.roaming_app_data().to_string(), store.read_value(&key, "NewSignature").unwrap())
            })
            .unwrap();

            assert!(fits("<RoamingAppData>", &roaming), "{roaming}");
            assert_eq!(value.as_deref(), Some(NAME));
            let hkcu = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
            assert_eq!(hkcu.open_subkey(&key).unwrap().get_raw_value("NewSignature").unwrap().vtype, RegType::REG_EXPAND_SZ);

            let _ = hkcu.delete_subkey_all(&key);
            let _ = std::fs::remove_file(&file);
        }
    }
}
