//! Device baselines: what the server says should be true on this PC (Master Daily's registry
//! settings, to begin with), read and reported — and, for an item in `enforce` mode, set back when it
//! has drifted.
//!
//! **The only write path is `write_machine_value`, and `ENFORCEABLE` decides what it may touch.** An
//! item is written only when all of these hold: its signature verified, it is a machine setting in
//! `enforce` mode, it has drifted, its exact path, value name and type are in `ENFORCEABLE`, a list
//! compiled into this agent, and its data is within that entry's range. Whatever a signed document
//! says, nothing else is created, set or deleted, so a stolen signing key cannot write a Run key or a
//! service's ImagePath through here, nor set the idle lock to "off". The
//! server's `BaselineItemRules.EnforceableValues` refuses anything else before signing and a server
//! test keeps the two lists equal. Rings and exemptions come later (see "Kintsugi device
//! configuration design", IP-841).
//!
//! The shape mirrors `policy`: the service fetches `GET /api/baseline` at each hourly check-in and
//! caches it, falling back to the cache when the server cannot be reached. Before any item is read,
//! each baseline's signature is checked against the artifact-signing key pinned at enrollment
//! (`identity::verify_artifact_signature`) — the cache included, so a file edited on disk is refused
//! exactly as a tampered response would be.
//!
//! **Only changes are reported.** The server hands back what it already holds for this host with
//! each baseline, and `POST /api/baseline-results` carries only the items whose state or observed
//! value differs. The POST is sent after every check even when it is empty, because that is what
//! moves "last checked" on the server; and since the comparison is against the server's copy rather
//! than a local one, a report that fails is simply resent at the next check.
//!
//! **Per-person items.** An `outlook-signature` item is not judged here: it names each signed-in
//! person's managed Outlook signature, which the service can only see by impersonating that person.
//! `evaluate` hands it to `outlook_signature::check_people`, which asks the server who is signed in
//! (`POST /api/baseline-people`) and adds one result per person, with that person's email as its
//! `subject`, to the same report. What that path can ever write is `SIGNATURE_WRITES`, below.
//!
//! **The Google enrolment.** A `google-mdm` item is judged by `google_mdm::judge`: does this PC's
//! Google device management enrolment still sync? Its one action, in `enforce` mode, is unregistering
//! a dead enrolment so GCPW enrols the PC again. It writes no registry value, so `ENFORCEABLE` is not
//! involved; what it can do is fixed in that module.
//!
//! **Windows only.** The DTOs here are hand-mirrored from the server's
//! (`Kintsugi.Application/Baselines/BaselineDtos.cs`, `BaselineDocument.cs` and
//! `ReportBaselineResultsCommand.cs`; see `.claude/rules/hand-mirrored-dtos.md`), and in this step
//! only this agent has them: registry items mean nothing on macOS or Linux, whose agents do not
//! fetch baselines yet.

use std::fs;
use std::path::Path;

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use winreg::enums::{RegType, HKEY_LOCAL_MACHINE, KEY_READ, KEY_SET_VALUE, KEY_WOW64_64KEY};
use winreg::RegKey;

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;

/// Below the server's `HostBaselineItemState.MaxObservedLength` (2048), so one long value is never
/// the reason a whole report is refused. Raise the server's first, never this one.
pub const MAX_OBSERVED_CHARS: usize = 2000;

/// Below `HostBaselineItemState.MaxDetailLength` (500), for the same reason.
pub const MAX_DETAIL_CHARS: usize = 400;

/// HKLM roots this agent never reads, whatever a signed document says: `SAM` holds the local
/// accounts' password hashes and `SECURITY` the LSA secrets. The service runs as SYSTEM and could
/// open both, and a value read would be reported, shown in the admin UI and cached in a file every
/// user can read. The server's `BaselineItemRules.ProtectedRoots` refuses them before signing; this
/// is the second check, for a document that reached the agent some other way.
const PROTECTED_ROOTS: [&str; 2] = ["SAM", "SECURITY"];

/// The one document schema this agent understands — `BaselineDocument.CurrentSchema` on the server.
const SCHEMA: u32 = 1;

/// Every HKLM value this agent may write, as (path, value name, type, lowest data, highest data), compared
/// ignoring case for the path and name. The SYSTEM service writing the wrong value is code execution (Run,
/// a service's ImagePath, Image File Execution Options, a COM server) or a quieter weakening even under
/// `SOFTWARE\Policies`, so this is a list of exact values, not a prefix. The range is part of the bound:
/// `InactivityTimeoutSecs` 0 turns the idle lock off and a password-on-wake index 0 stops asking, so a
/// name alone would let a signed document switch either off. Data outside it is never written
/// (`enforceable`). Widening either is a reviewed change here and in the server's
/// `BaselineItemRules.EnforceableValues`, whose test reads this block, so keep each entry on the
/// `(r"…", "…", "…", n, n)` shape. The server's copy says why each range is what it is.
const ENFORCEABLE: [(&str, &str, &str, u64, u64); 4] = [
    // Master Daily section 17, the enforce pilot: Chrome keeps its file and folder pickers open.
    (r"SOFTWARE\Policies\Google\Chrome", "WindowOcclusionEnabled", "dword", 0, 1),
    // IP-926: "Interactive logon: Machine inactivity limit", in seconds. 0 is no lock at all.
    (r"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "InactivityTimeoutSecs", "dword", 60, 900),
    // IP-926: "Require a password when a computer wakes" (CONSOLELOCK), plugged in and on battery.
    (r"SOFTWARE\Policies\Microsoft\Power\PowerSettings\0e796bdb-100d-47d6-a2d5-f7d2daa51f51", "ACSettingIndex", "dword", 1, 1),
    (r"SOFTWARE\Policies\Microsoft\Power\PowerSettings\0e796bdb-100d-47d6-a2d5-f7d2daa51f51", "DCSettingIndex", "dword", 1, 1),
];

/// Everything an `outlook-signature` item can ever make this agent write, as (target, path, value name,
/// value type): the signature's two files and the four values that make it the default. Every write
/// `outlook_signature` makes is built from one of these entries and checked against them again before it
/// is made (`outlook_signature::is_listed`); a target anywhere else is refused as an error. The
/// placeholders are filled by the agent, never by the document: `<RoamingAppData>` from the person's own
/// known folder, `<profile>` and `<account>` from the Outlook profiles under that person's HKCU, and
/// `<signatureName>` from the item, after `outlook_signature::is_valid_signature_name`. These writes are
/// made under the person's own token, never as SYSTEM.
///
/// The server's `BaselineItemRules.SignatureWrites` is the same list, and `OutlookSignatureItemTests`
/// reads this block to hold the two together, so keep each entry on the `("…", r"…", "…", "…")` shape.
pub(crate) const SIGNATURE_WRITES: [(&str, &str, &str, &str); 6] = [
    ("file", r"<RoamingAppData>\Microsoft\Signatures\<signatureName>.htm", "", ""),
    ("file", r"<RoamingAppData>\Microsoft\Signatures\<signatureName>.txt", "", ""),
    ("HKCU", r"Software\Microsoft\Office\16.0\Outlook\Profiles\<profile>\9375CFF0413111d3B88A00104B2A6676\<account>", "New Signature", "string"),
    ("HKCU", r"Software\Microsoft\Office\16.0\Outlook\Profiles\<profile>\9375CFF0413111d3B88A00104B2A6676\<account>", "Reply-Forward Signature", "string"),
    ("HKCU", r"Software\Microsoft\Office\16.0\Common\MailSettings", "NewSignature", "expandString"),
    ("HKCU", r"Software\Microsoft\Office\16.0\Common\MailSettings", "ReplySignature", "expandString"),
];

pub(crate) const ENFORCE: &str = "enforce";

/// The item kind judged per person rather than here — `BaselineItemRules.KindOutlookSignature`.
const KIND_OUTLOOK_SIGNATURE: &str = "outlook-signature";

/// Reads one HKLM value: `read_machine_value` for real, a fake in tests.
pub(crate) type ReadValue<'a> = &'a dyn Fn(&str, &str) -> std::io::Result<Option<Observed>>;

/// Sets one HKLM value: `write_machine_value` for real, a fake in tests.
pub(crate) type WriteValue<'a> = &'a dyn Fn(&str, &str, &ValueKind, &str) -> std::io::Result<()>;

/// Mirrors `AgentBaselinesDto`.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BaselineResponse {
    pub baselines: Vec<ServedBaseline>,
}

/// Mirrors `AgentBaselineDto`: one signed baseline and what the server already holds for this host.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServedBaseline {
    pub version_id: String,
    /// The signed canonical JSON, byte for byte. Verified before it is parsed.
    pub document: String,
    pub signature: String,
    #[serde(default)]
    pub states: Vec<KnownState>,
    #[serde(default)]
    pub facts: Vec<KnownFact>,
}

/// Mirrors `AgentItemStateDto`. `state` is a `BaselineItemState` member *name*.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KnownState {
    pub key: String,
    pub state: String,
    pub observed: Option<String>,
}

/// Mirrors `AgentFactDto`.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KnownFact {
    pub key: String,
    pub value: Option<String>,
}

/// Mirrors `BaselineDocument` — only what this agent reads. Unknown fields are ignored, so a later
/// server can add to the document without breaking this agent.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Document {
    schema: u32,
    version_id: String,
    items: Vec<Item>,
}

/// Mirrors `BaselineItem`. `mode` is `enforce` or anything else, which is audited.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Item {
    key: String,
    kind: String,
    scope: String,
    hive: String,
    path: String,
    value_name: String,
    value_type: Option<String>,
    data: Option<String>,
    secret: Option<String>,
    #[serde(default)]
    mode: Option<String>,
    /// `outlook-signature` only; absent for every other kind.
    #[serde(default)]
    signature_name: Option<String>,
}

/// Mirrors `ReportBaselineResultsCommand`.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct BaselineResultsRequest<'a> {
    serial_number: &'a str,
    baselines: &'a [BaselineResult],
}

/// Mirrors `BaselineResultInput`. `version_id` is taken from inside the signed document.
#[derive(Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BaselineResult {
    pub version_id: String,
    pub items: Vec<ItemResult>,
    pub facts: Vec<FactResult>,
    /// The verified document's per-person items. Never sent: `outlook_signature::check_people` judges
    /// them, once per signed-in person, and adds those results to `items`.
    #[serde(skip)]
    pub signature_items: Vec<SignatureItem>,
}

/// One `outlook-signature` item from a verified document.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SignatureItem {
    pub key: String,
    pub enforce: bool,
    /// Checked by `outlook_signature::is_valid_signature_name` before any use.
    pub signature_name: Option<String>,
}

/// Mirrors `ItemResultInput`.
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ItemResult {
    pub key: String,
    pub state: &'static str,
    pub observed: Option<String>,
    pub detail: Option<String>,
    /// Whose result this is, for a per-person item: the email the server resolved. Absent for the
    /// host's own results, which is how every older server reads them.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub subject: Option<String>,
    /// This check wrote the value. Never sent; it makes the item reported even when the server
    /// already holds the corrected state, so a correction is never invisible.
    #[serde(skip)]
    pub corrected: bool,
}

/// Mirrors `FactResultInput`.
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct FactResult {
    pub key: String,
    pub value: Option<String>,
}

// The `BaselineItemState` member names, which is what crosses this wire (never the ordinals).
pub(crate) const COMPLIANT: &str = "Compliant";
pub(crate) const DRIFTED: &str = "Drifted";
pub(crate) const NOT_APPLICABLE: &str = "NotApplicable";
pub(crate) const ERROR: &str = "Error";

/// A registry value's type, in the document's own spellings.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ValueKind {
    Dword,
    Qword,
    String,
    ExpandString,
    MultiString,
    Binary,
    Other,
}

impl ValueKind {
    pub(crate) fn from_document(value_type: &str) -> Option<Self> {
        Some(match value_type {
            "dword" => Self::Dword,
            "qword" => Self::Qword,
            "string" => Self::String,
            "expandString" => Self::ExpandString,
            "multiString" => Self::MultiString,
            "binary" => Self::Binary,
            _ => return None,
        })
    }

    fn name(&self) -> &'static str {
        match self {
            Self::Dword => "dword",
            Self::Qword => "qword",
            Self::String => "string",
            Self::ExpandString => "expandString",
            Self::MultiString => "multiString",
            Self::Binary => "binary",
            Self::Other => "other",
        }
    }
}

/// A value as read: its type and its data as text, formatted the way the document writes expected
/// data (decimal for dword/qword, lower-case hex for binary, one line per string for multiString).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Observed {
    pub kind: ValueKind,
    pub text: String,
}

/// Fetches, verifies, reads and reports. Best-effort: a failure is logged and never fails the
/// check-in it rides on.
pub fn check_and_report(client: &reqwest::blocking::Client, config: &Config, identity: Option<&AgentIdentity>, serial_number: &str) {
    if let Err(err) = try_check_and_report(client, config, identity, serial_number) {
        logging::warn(&format!("could not check device baselines: {err:#}"));
    }
}

fn try_check_and_report(
    client: &reqwest::blocking::Client,
    config: &Config,
    identity: Option<&AgentIdentity>,
    serial_number: &str,
) -> Result<()> {
    let identity = identity.context("this agent has not enrolled an identity yet")?;
    let Some(response) = refresh(client, config, serial_number, &config::baseline_cache_path()) else {
        return Ok(());
    };

    let mut results = Vec::new();
    for served in &response.baselines {
        match evaluate(identity, served, &read_machine_value, &write_machine_value) {
            Ok(result) => results.push(result),
            // One refused baseline must not cost the others their report.
            Err(err) => logging::warn(&format!("refusing baseline version {}: {err:#}", served.version_id)),
        }
    }

    // Only for a verified document that holds a per-person item: an older server never sees the route.
    if results.iter().any(|r| !r.signature_items.is_empty()) {
        crate::outlook_signature::check_people(client, config, serial_number, &mut results);
    }

    if results.is_empty() {
        return Ok(());
    }

    let request = BaselineResultsRequest { serial_number, baselines: &results };
    let response = client.post(config.baseline_results_url()).json(&request).send().context("the report could not be sent")?;
    if !response.status().is_success() {
        anyhow::bail!("the server rejected the report (HTTP {})", response.status());
    }

    let changed: usize = results.iter().map(|r| r.items.len() + r.facts.len()).sum();
    logging::info(&format!("checked {} device baseline(s); reported {changed} change(s)", results.len()));
    Ok(())
}

/// Fetches the baselines and caches them, falling back to the cache when the fetch fails — the same
/// arrangement as `policy::refresh`. None when there is neither.
fn refresh(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, cache_path: &Path) -> Option<BaselineResponse> {
    match fetch(client, config, serial_number, cache_path) {
        Ok(response) => Some(response),
        Err(err) => {
            logging::warn(&format!("could not fetch device baselines, using cached copy if any: {err:#}"));
            load_cached(cache_path)
        }
    }
}

fn fetch(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, cache_path: &Path) -> Result<BaselineResponse> {
    let response = crate::service::get_with_retry("the device baselines", || {
        client.get(config.baseline_url()).query(&[("serialNumber", serial_number)])
    })?;

    if !response.status().is_success() {
        anyhow::bail!("request rejected (HTTP {})", response.status());
    }

    let baselines = response.json::<BaselineResponse>().context("could not parse response")?;

    if let Some(parent) = cache_path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    if let Ok(json) = serde_json::to_string(&baselines) {
        let _ = fs::write(cache_path, json);
    }

    Ok(baselines)
}

/// The last baselines fetched. Every one is verified again before use, so this file is a convenience
/// and never a source of trust. It holds no secrets: no secret value reaches an agent in this step.
pub fn load_cached(cache_path: &Path) -> Option<BaselineResponse> {
    serde_json::from_str(&fs::read_to_string(cache_path).ok()?).ok()
}

/// Verifies one served baseline, reads every item, sets back each drifted enforceable one, and
/// returns only what differs from what the server already holds, plus every correction.
pub(crate) fn evaluate(identity: &AgentIdentity, served: &ServedBaseline, read: ReadValue<'_>, write: WriteValue<'_>) -> Result<BaselineResult> {
    evaluate_with(identity, served, read, write, &crate::google_mdm::system::System)
}

/// `evaluate`, with the PC a `google-mdm` item is judged against passed in: the real one, or a fake in tests.
pub(crate) fn evaluate_with(
    identity: &AgentIdentity,
    served: &ServedBaseline,
    read: ReadValue<'_>,
    write: WriteValue<'_>,
    mdm: &dyn crate::google_mdm::MdmSystem,
) -> Result<BaselineResult> {
    // Before anything is parsed, read or written.
    identity::verify_artifact_signature(identity, &served.document, &served.signature)?;

    let document: Document = serde_json::from_str(&served.document).context("the signed document could not be parsed")?;
    if document.schema != SCHEMA {
        anyhow::bail!("document schema {} is not one this agent understands (it reads schema {SCHEMA})", document.schema);
    }
    // The ids reported are the signed ones; an envelope that disagrees with them is not trusted.
    if !document.version_id.eq_ignore_ascii_case(&served.version_id) {
        anyhow::bail!("the signed document is version {}, not the {} it was served as", document.version_id, served.version_id);
    }

    let mut items = Vec::new();
    let mut facts = Vec::new();
    let mut signature_items = Vec::new();

    for item in &document.items {
        match item.kind.as_str() {
            "setting" => {
                let result = judge(item, read, write);
                let known = served.states.iter().find(|s| s.key == item.key);
                if result.corrected || known.is_none_or(|k| k.state != result.state || k.observed != result.observed) {
                    items.push(result);
                }
            }
            "fact" => {
                let Some(value) = read_fact(item, read) else { continue };
                let known = served.facts.iter().find(|f| f.key == item.key);
                if known.is_none_or(|k| k.value != value) {
                    facts.push(FactResult { key: item.key.clone(), value });
                }
            }
            // Judged per person, as that person, by `outlook_signature`. The host's own row for it is the
            // server's to keep (`ResolveBaselinePeopleCommandHandler`), so nothing is reported here.
            KIND_OUTLOOK_SIGNATURE => signature_items.push(SignatureItem {
                key: item.key.clone(),
                enforce: item.mode.as_deref() == Some(ENFORCE),
                signature_name: item.signature_name.clone(),
            }),
            crate::google_mdm::KIND => {
                let enforce = item.scope == "machine" && item.mode.as_deref() == Some(ENFORCE);
                let result = crate::google_mdm::judge(&item.key, item.data.as_deref(), enforce, mdm);
                let known = served.states.iter().find(|s| s.key == item.key);
                if result.corrected || known.is_none_or(|k| k.state != result.state || k.observed != result.observed) {
                    items.push(result);
                }
            }
            // A kind from a later server: not this agent's to judge, and not a setting the server
            // would record a state for.
            other => logging::warn(&format!("skipping baseline item {} of unknown kind {other}", item.key)),
        }
    }

    Ok(BaselineResult { version_id: document.version_id, items, facts, signature_items })
}

/// What one setting is on this PC, after setting it back if it is an enforceable item that drifted.
fn judge(item: &Item, read: ReadValue<'_>, write: WriteValue<'_>) -> ItemResult {
    let found = audit(item, read);
    if item.mode.as_deref() != Some(ENFORCE) || found.state != DRIFTED {
        return found;
    }
    let detail = |text: String| Some(truncate(&text, MAX_DETAIL_CHARS));
    let was = found.observed.clone().unwrap_or_else(|| "absent".into());
    let Some((lowest, highest)) = enforceable(item) else {
        return ItemResult { detail: detail(format!("not set back (was {was}): this agent writes only its ENFORCEABLE values")), ..found };
    };
    // audit() reports Drifted only once it has understood both, so neither is ever missing here.
    let (Some(kind), Some(data)) = (item.value_type.as_deref().and_then(ValueKind::from_document), item.data.as_deref()) else {
        return found;
    };
    if !data.parse::<u64>().is_ok_and(|value| (lowest..=highest).contains(&value)) {
        return ItemResult {
            detail: detail(format!("not set back (was {was}): {data} is outside the {lowest} to {highest} this agent may write here")),
            ..found
        };
    }

    if let Err(err) = write(&item.path, &item.value_name, &kind, data) {
        return ItemResult { state: ERROR, detail: detail(format!("drifted (was {was}) and could not be set back: {err}")), ..found };
    }

    let after = audit(item, read);
    if after.state != COMPLIANT {
        return ItemResult { state: ERROR, detail: detail(format!("set back, but it reads as {} (was {was})", after.state)), corrected: true, ..after };
    }
    logging::info(&format!("baseline: set {} back to {data} (was {was})", item.key));
    ItemResult { detail: detail(format!("set back by Kintsugi (was {was})")), corrected: true, ..after }
}

/// The range `ENFORCEABLE` allows for `item`, when it is a machine setting whose exact path, value name and
/// type are on that list. Whether its data is in that range is `judge`'s check, so it can say so.
fn enforceable(item: &Item) -> Option<(u64, u64)> {
    if item.kind != "setting" || item.scope != "machine" || item.hive != "HKLM" || item.secret.is_some() {
        return None;
    }
    ENFORCEABLE
        .iter()
        .find(|(path, name, value_type, _, _)| {
            item.path.eq_ignore_ascii_case(path) && item.value_name.eq_ignore_ascii_case(name) && item.value_type.as_deref() == Some(*value_type)
        })
        .map(|(_, _, _, lowest, highest)| (*lowest, *highest))
}

/// What one setting is on this PC, read only.
fn audit(item: &Item, read: ReadValue<'_>) -> ItemResult {
    let result = |state, observed: Option<String>, detail: Option<&str>| ItemResult {
        key: item.key.clone(),
        state,
        observed: observed.map(|o| truncate(&o, MAX_OBSERVED_CHARS)),
        detail: detail.map(|d| truncate(d, MAX_DETAIL_CHARS)),
        subject: None,
        corrected: false,
    };

    // A user-scope *setting* is not read yet. The service
    // runs as SYSTEM, whose HKCU is its own profile — which is exactly why Ninja's HKCU writes never
    // reached anybody — so reading it here would report on nobody.
    if item.scope == "user" {
        return result(NOT_APPLICABLE, None, Some("user-scope items are checked by the tray process, in a later step"));
    }
    if item.scope != "machine" || item.hive != "HKLM" {
        return result(ERROR, None, Some("a machine item must be under HKLM"));
    }
    if is_protected_path(&item.path) {
        return result(ERROR, None, Some("SAM and SECURITY are never read"));
    }
    // Never read: the observed value would be the secret itself, and it would be reported.
    if item.secret.is_some() {
        return result(NOT_APPLICABLE, None, Some("the value is a secret, which agents do not receive yet"));
    }

    let (Some(expected_kind), Some(expected_data)) = (item.value_type.as_deref().and_then(ValueKind::from_document), item.data.as_deref())
    else {
        return result(ERROR, None, Some("the item has no value type or data this agent understands"));
    };

    match read(&item.path, &item.value_name) {
        Ok(None) => result(DRIFTED, None, Some("absent")),
        Err(err) => result(ERROR, None, Some(&format!("could not read the value: {err}"))),
        Ok(Some(observed)) if observed.kind != expected_kind => {
            let shown = format!("{} ({})", observed.text, observed.kind.name());
            result(DRIFTED, Some(shown), Some(&format!("expected a {} value", expected_kind.name())))
        }
        Ok(Some(observed)) => {
            let state = if data_matches(&expected_kind, expected_data, &observed.text) { COMPLIANT } else { DRIFTED };
            result(state, Some(observed.text), None)
        }
    }
}

/// Numbers compare as numbers ("01" is 1); everything else as the exact text.
fn data_matches(kind: &ValueKind, expected: &str, observed: &str) -> bool {
    match kind {
        ValueKind::Dword | ValueKind::Qword => match (expected.parse::<u64>(), observed.parse::<u64>()) {
            (Ok(e), Ok(o)) => e == o,
            _ => false,
        },
        ValueKind::Binary => expected.eq_ignore_ascii_case(observed),
        _ => expected == observed,
    }
}

/// A fact's value, `Some(None)` when it is absent, or `None` when it could not be read (so a
/// transient failure never overwrites a known value with nothing).
fn read_fact(item: &Item, read: &dyn Fn(&str, &str) -> std::io::Result<Option<Observed>>) -> Option<Option<String>> {
    if item.scope != "machine" || item.hive != "HKLM" || is_protected_path(&item.path) {
        return None;
    }

    match read(&item.path, &item.value_name) {
        Ok(value) => Some(value.map(|v| truncate(&v.text, MAX_OBSERVED_CHARS))),
        Err(err) => {
            logging::warn(&format!("could not read baseline fact {}: {err}", item.key));
            None
        }
    }
}

/// Whether `path`'s first segment is one of `PROTECTED_ROOTS`, ignoring case and a leading backslash.
fn is_protected_path(path: &str) -> bool {
    let root = path.trim_start_matches('\\').split('\\').next().unwrap_or("");
    PROTECTED_ROOTS.iter().any(|protected| root.eq_ignore_ascii_case(protected))
}

/// Reads one value under HKLM, from the 64-bit view explicitly (see `system_info::machine_guid` for
/// why), with `KEY_READ` and nothing more. `Ok(None)` when the key or the value does not exist.
fn read_machine_value(path: &str, value_name: &str) -> std::io::Result<Option<Observed>> {
    let key = match RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey_with_flags(path, KEY_READ | KEY_WOW64_64KEY) {
        Ok(key) => key,
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(err) => return Err(err),
    };

    match key.get_raw_value(value_name) {
        Ok(value) => Ok(Some(observe(kind_of(&value.vtype), &value.bytes))),
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(err) => Err(err),
    }
}

/// Sets one value under HKLM, in the same 64-bit view `read_machine_value` reads, creating the key if
/// it is missing. Called only from `judge`, for an item `is_enforceable` accepted, after its baseline's
/// signature verified. Writes the types `ENFORCEABLE` names today; anything else is refused, not guessed.
fn write_machine_value(path: &str, value_name: &str, kind: &ValueKind, data: &str) -> std::io::Result<()> {
    let invalid = |what: &str| std::io::Error::new(std::io::ErrorKind::InvalidData, format!("'{data}' is not {what}"));
    // Opened only once the data has parsed, so a refused write leaves no empty key behind.
    let key = || RegKey::predef(HKEY_LOCAL_MACHINE).create_subkey_with_flags(path, KEY_SET_VALUE | KEY_WOW64_64KEY).map(|(key, _)| key);
    match kind {
        ValueKind::Dword => {
            let value = data.parse::<u32>().map_err(|_| invalid("a dword"))?;
            key()?.set_value(value_name, &value)
        }
        ValueKind::Qword => {
            let value = data.parse::<u64>().map_err(|_| invalid("a qword"))?;
            key()?.set_value(value_name, &value)
        }
        ValueKind::String => key()?.set_value(value_name, &data.to_string()),
        other => Err(std::io::Error::new(std::io::ErrorKind::Unsupported, format!("this agent does not write {} values", other.name()))),
    }
}

fn kind_of(vtype: &RegType) -> ValueKind {
    match vtype {
        RegType::REG_DWORD => ValueKind::Dword,
        RegType::REG_QWORD => ValueKind::Qword,
        RegType::REG_SZ => ValueKind::String,
        RegType::REG_EXPAND_SZ => ValueKind::ExpandString,
        RegType::REG_MULTI_SZ => ValueKind::MultiString,
        RegType::REG_BINARY => ValueKind::Binary,
        _ => ValueKind::Other,
    }
}

/// Raw registry bytes as text, in the document's formats. A dword or qword of the wrong length is
/// shown as hex rather than guessed at.
pub(crate) fn observe(kind: ValueKind, bytes: &[u8]) -> Observed {
    let text = match kind {
        ValueKind::Dword if bytes.len() == 4 => u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]).to_string(),
        ValueKind::Qword if bytes.len() == 8 => {
            u64::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7]]).to_string()
        }
        ValueKind::String | ValueKind::ExpandString => utf16(bytes).trim_end_matches('\0').to_string(),
        ValueKind::MultiString => utf16(bytes).split('\0').filter(|s| !s.is_empty()).collect::<Vec<_>>().join("\n"),
        _ => bytes.iter().map(|b| format!("{b:02x}")).collect(),
    };
    Observed { kind, text }
}

fn utf16(bytes: &[u8]) -> String {
    let units: Vec<u16> = bytes.chunks_exact(2).map(|pair| u16::from_le_bytes([pair[0], pair[1]])).collect();
    String::from_utf16_lossy(&units)
}

pub(crate) fn truncate(text: &str, max_chars: usize) -> String {
    text.chars().take(max_chars).collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use base64::Engine;
    use p256::ecdsa::signature::Signer;
    use p256::ecdsa::{Signature, SigningKey};
    use p256::pkcs8::EncodePublicKey;

    // The same fixed keypair arrangement as `identity`'s own tests.
    fn signing_key() -> SigningKey {
        SigningKey::from_bytes(&[0x11; 32].into()).expect("32 fixed bytes is a valid P-256 scalar")
    }

    fn identity() -> AgentIdentity {
        AgentIdentity {
            certificate_pem: String::new(),
            private_key_pem: String::new(),
            artifact_signing_public_key_pem: signing_key().verifying_key().to_public_key_pem(Default::default()).unwrap(),
        }
    }

    const VERSION: &str = "7f0c8f9e-2b1a-4c3d-9e8f-0a1b2c3d4e5f";

    fn document(items: &str) -> String {
        format!(r#"{{"schema":1,"baselineId":"11111111-1111-1111-1111-111111111111","baseline":"b","versionId":"{VERSION}","version":1,"items":[{items}]}}"#)
    }

    fn setting(key: &str, path: &str, value_type: &str, data: &str) -> String {
        format!(
            r#"{{"key":"{key}","kind":"setting","scope":"machine","hive":"HKLM","path":"{path}","valueName":"V","valueType":"{value_type}","data":"{data}","mode":"audit"}}"#
        )
    }

    fn served(document: String, states: Vec<KnownState>) -> ServedBaseline {
        let signature: Signature = signing_key().sign(document.as_bytes());
        ServedBaseline {
            version_id: VERSION.to_string(),
            signature: base64::engine::general_purpose::STANDARD.encode(signature.to_der().as_bytes()),
            document,
            states,
            facts: Vec::new(),
        }
    }

    fn dword(value: u32) -> std::io::Result<Option<Observed>> {
        Ok(Some(observe(ValueKind::Dword, &value.to_le_bytes())))
    }

    /// One fake registry: path "on" holds dword 1, "off" holds dword 0, "text" a string, anything
    /// else is absent, and "denied" fails to read.
    fn registry(path: &str, _value: &str) -> std::io::Result<Option<Observed>> {
        match path {
            "on" => dword(1),
            "off" => dword(0),
            "text" => Ok(Some(observe(ValueKind::String, &"abc\0".encode_utf16().flat_map(u16::to_le_bytes).collect::<Vec<_>>()))),
            "denied" => Err(std::io::Error::from(std::io::ErrorKind::PermissionDenied)),
            _ => Ok(None),
        }
    }

    fn state_of(result: &BaselineResult, key: &str) -> &'static str {
        result.items.iter().find(|i| i.key == key).unwrap().state
    }

    fn item_of<'r>(result: &'r BaselineResult, key: &str) -> &'r ItemResult {
        result.items.iter().find(|i| i.key == key).unwrap()
    }

    /// Passed wherever nothing may be written.
    fn never_write(_: &str, _: &str, _: &ValueKind, _: &str) -> std::io::Result<()> {
        panic!("a value was written")
    }

    const CHROME: &str = r"SOFTWARE\Policies\Google\Chrome";
    const OCCLUSION: &str = "WindowOcclusionEnabled";

    fn item(key: &str, path: &str, name: &str, value_type: &str, data: &str, mode: &str) -> String {
        let path = path.replace('\\', r"\\");
        format!(
            r#"{{"key":"{key}","kind":"setting","scope":"machine","hive":"HKLM","path":"{path}","valueName":"{name}","valueType":"{value_type}","data":"{data}","mode":"{mode}"}}"#
        )
    }

    /// The pilot's item: Chrome's occlusion policy, enforced at 0.
    fn occlusion() -> String {
        item("occlusion", CHROME, OCCLUSION, "dword", "0", ENFORCE)
    }

    /// A registry that remembers what is written, so a value set back reads back as set.
    #[derive(Default)]
    struct FakeRegistry {
        values: std::cell::RefCell<std::collections::HashMap<String, Observed>>,
        writes: std::cell::RefCell<Vec<String>>,
        refuse_writes: bool,
    }

    impl FakeRegistry {
        fn holding(path: &str, name: &str, value: Observed) -> Self {
            let registry = Self::default();
            registry.values.borrow_mut().insert(format!(r"{path}\{name}"), value);
            registry
        }

        fn read(&self, path: &str, name: &str) -> std::io::Result<Option<Observed>> {
            Ok(self.values.borrow().get(&format!(r"{path}\{name}")).cloned())
        }

        fn write(&self, path: &str, name: &str, kind: &ValueKind, data: &str) -> std::io::Result<()> {
            if self.refuse_writes {
                return Err(std::io::Error::from(std::io::ErrorKind::PermissionDenied));
            }
            self.writes.borrow_mut().push(format!(r"{path}\{name}={data}"));
            self.values.borrow_mut().insert(format!(r"{path}\{name}"), Observed { kind: kind.clone(), text: data.to_string() });
            Ok(())
        }

        fn check(&self, items: &[String], known: Vec<KnownState>) -> BaselineResult {
            let read = |path: &str, name: &str| self.read(path, name);
            let write = |path: &str, name: &str, kind: &ValueKind, data: &str| self.write(path, name, kind, data);
            evaluate(&identity(), &served(document(&items.join(",")), known), &read, &write).unwrap()
        }
    }

    #[test]
    fn each_setting_is_judged_against_what_the_registry_holds() {
        let items = [
            setting("compliant", "on", "dword", "1"),
            setting("drifted", "off", "dword", "1"),
            setting("absent", "missing", "dword", "1"),
            setting("unreadable", "denied", "dword", "1"),
            setting("wrong-type", "text", "dword", "1"),
            setting("string", "text", "string", "abc"),
        ]
        .join(",");

        let result = evaluate(&identity(), &served(document(&items), Vec::new()), &registry, &never_write).unwrap();

        assert_eq!(result.version_id, VERSION);
        assert_eq!(state_of(&result, "compliant"), COMPLIANT);
        assert_eq!(state_of(&result, "drifted"), DRIFTED);
        assert_eq!(state_of(&result, "absent"), DRIFTED);
        assert_eq!(state_of(&result, "unreadable"), ERROR);
        assert_eq!(state_of(&result, "wrong-type"), DRIFTED);
        assert_eq!(state_of(&result, "string"), COMPLIANT);
    }

    #[test]
    fn a_signature_over_different_bytes_is_refused_before_anything_is_read() {
        let mut baseline = served(document(&setting("a", "on", "dword", "1")), Vec::new());
        baseline.document = baseline.document.replace(r#""data":"1""#, r#""data":"0""#);

        let read_nothing = |_: &str, _: &str| -> std::io::Result<Option<Observed>> { panic!("read before the signature was checked") };
        assert!(evaluate(&identity(), &baseline, &read_nothing, &never_write).is_err());
    }

    #[test]
    fn an_envelope_that_disagrees_with_the_signed_version_is_refused() {
        let mut baseline = served(document(&setting("a", "on", "dword", "1")), Vec::new());
        baseline.version_id = "00000000-0000-0000-0000-000000000000".to_string();

        assert!(evaluate(&identity(), &baseline, &registry, &never_write).is_err());
    }

    #[test]
    fn only_what_differs_from_the_servers_copy_is_reported() {
        let items = [setting("same", "on", "dword", "1"), setting("changed", "off", "dword", "1")].join(",");
        let known = vec![
            KnownState { key: "same".into(), state: COMPLIANT.into(), observed: Some("1".into()) },
            KnownState { key: "changed".into(), state: COMPLIANT.into(), observed: Some("1".into()) },
        ];

        let result = evaluate(&identity(), &served(document(&items), known), &registry, &never_write).unwrap();

        assert_eq!(result.items.iter().map(|i| i.key.as_str()).collect::<Vec<_>>(), vec!["changed"]);
    }

    #[test]
    fn user_scope_and_secret_items_are_not_applicable_and_never_read() {
        let items = [
            r#"{"key":"user","kind":"setting","scope":"user","hive":"HKCU","path":"denied","valueName":"V","valueType":"dword","data":"1","mode":"audit"}"#,
            r#"{"key":"secret","kind":"setting","scope":"machine","hive":"HKLM","path":"denied","valueName":"V","valueType":"string","secret":"TOKEN","mode":"audit"}"#,
        ]
        .join(",");
        let read_nothing = |_: &str, _: &str| -> std::io::Result<Option<Observed>> { panic!("a not-applicable item was read") };

        let result = evaluate(&identity(), &served(document(&items), Vec::new()), &read_nothing, &never_write).unwrap();

        assert_eq!(state_of(&result, "user"), NOT_APPLICABLE);
        assert_eq!(state_of(&result, "secret"), NOT_APPLICABLE);
        assert!(result.items.iter().all(|i| i.observed.is_none()));
    }

    #[test]
    fn sam_and_security_are_never_read() {
        let items = [
            setting("hashes", "SAM", "binary", "00"),
            r#"{"key":"lsa","kind":"fact","scope":"machine","hive":"HKLM","path":"security","valueName":"CurrVal"}"#.to_string(),
        ]
        .join(",");
        let read_nothing = |_: &str, _: &str| -> std::io::Result<Option<Observed>> { panic!("a protected hive was read") };

        let result = evaluate(&identity(), &served(document(&items), Vec::new()), &read_nothing, &never_write).unwrap();

        assert_eq!(state_of(&result, "hashes"), ERROR);
        assert!(result.facts.is_empty());
        assert!(is_protected_path(r"\Security\Policy\Secrets"));
        assert!(!is_protected_path(r"SOFTWARE\SAMSUNG"));
    }

    #[test]
    fn facts_are_reported_when_new_or_changed() {
        let items = r#"{"key":"edition","kind":"fact","scope":"machine","hive":"HKLM","path":"text","valueName":"EditionID"}"#;
        let mut baseline = served(document(items), Vec::new());

        let first = evaluate(&identity(), &baseline, &registry, &never_write).unwrap();
        assert_eq!(first.facts, vec![FactResult { key: "edition".into(), value: Some("abc".into()) }]);

        baseline.facts = vec![KnownFact { key: "edition".into(), value: Some("abc".into()) }];
        assert!(evaluate(&identity(), &baseline, &registry, &never_write).unwrap().facts.is_empty());
    }

    #[test]
    fn registry_bytes_are_shown_in_the_documents_formats() {
        assert_eq!(observe(ValueKind::Dword, &600u32.to_le_bytes()).text, "600");
        assert_eq!(observe(ValueKind::Qword, &5u64.to_le_bytes()).text, "5");
        assert_eq!(observe(ValueKind::Binary, &[0x90, 0x12, 0x03]).text, "901203");
        let multi: Vec<u8> = "a\0b\0\0".encode_utf16().flat_map(u16::to_le_bytes).collect();
        assert_eq!(observe(ValueKind::MultiString, &multi).text, "a\nb");
        // A dword of the wrong length is shown, not guessed at.
        assert_eq!(observe(ValueKind::Dword, &[1, 0]).text, "0100");
    }

    #[test]
    fn long_values_are_cut_below_the_servers_limit() {
        assert_eq!(truncate(&"é".repeat(MAX_OBSERVED_CHARS + 10), MAX_OBSERVED_CHARS).chars().count(), MAX_OBSERVED_CHARS);
        assert!(MAX_OBSERVED_CHARS < 2048 && MAX_DETAIL_CHARS < 500);
    }

    #[test]
    fn the_cache_round_trips() {
        let path = std::env::temp_dir().join(format!("kintsugi-baseline-cache-{}.json", std::process::id()));
        let response = BaselineResponse { baselines: vec![served(document(&setting("a", "on", "dword", "1")), Vec::new())] };
        fs::write(&path, serde_json::to_string(&response).unwrap()).unwrap();

        let loaded = load_cached(&path).expect("a cache the service wrote must load");
        assert_eq!(loaded.baselines[0].document, response.baselines[0].document);

        let _ = fs::remove_file(&path);
    }

    #[test]
    fn a_drifted_enforced_value_is_set_back_and_says_so() {
        let registry = FakeRegistry::holding(CHROME, OCCLUSION, observe(ValueKind::Dword, &1u32.to_le_bytes()));

        let result = registry.check(&[occlusion()], Vec::new());

        let item = item_of(&result, "occlusion");
        assert_eq!(item.state, COMPLIANT);
        assert_eq!(item.observed.as_deref(), Some("0"));
        assert_eq!(item.detail.as_deref(), Some("set back by Kintsugi (was 1)"));
        assert_eq!(*registry.writes.borrow(), vec![format!(r"{CHROME}\{OCCLUSION}=0")]);
    }

    #[test]
    fn an_absent_enforced_value_is_created_whatever_the_case_of_its_path() {
        let registry = FakeRegistry::default();

        let result = registry.check(&[item("occlusion", &CHROME.to_lowercase(), &OCCLUSION.to_uppercase(), "dword", "0", ENFORCE)], Vec::new());

        assert_eq!(state_of(&result, "occlusion"), COMPLIANT);
        assert_eq!(item_of(&result, "occlusion").detail.as_deref(), Some("set back by Kintsugi (was absent)"));
        assert_eq!(registry.writes.borrow().len(), 1);
    }

    /// Each of these is something a signed document could ask for, and none is in ENFORCEABLE.
    #[test]
    fn nothing_outside_the_allowlist_is_ever_written() {
        let items = [
            item("run", r"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Updater", "string", "evil.exe", ENFORCE),
            item("forcelist", &format!(r"{CHROME}\ExtensionInstallForcelist"), "1", "string", "abc", ENFORCE),
            item("wrong-type", CHROME, OCCLUSION, "string", "0", ENFORCE),
            item("audited", CHROME, OCCLUSION, "dword", "0", "audit"),
        ];
        let empty = FakeRegistry::default();
        let read = |path: &str, name: &str| empty.read(path, name);

        let result = evaluate(&identity(), &served(document(&items.join(",")), Vec::new()), &read, &never_write).unwrap();

        assert!(result.items.iter().all(|i| i.state == DRIFTED && !i.corrected));
        for key in ["run", "forcelist", "wrong-type"] {
            assert!(item_of(&result, key).detail.as_deref().unwrap().starts_with("not set back (was absent)"), "{key}");
        }
        assert_eq!(item_of(&result, "audited").detail.as_deref(), Some("absent"));
    }

    const SYSTEM_POLICIES: &str = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    const CONSOLELOCK: &str = r"SOFTWARE\Policies\Microsoft\Power\PowerSettings\0e796bdb-100d-47d6-a2d5-f7d2daa51f51";

    /// IP-926: the idle lock and password on wake are written when the document asks for a value in range.
    #[test]
    fn the_idle_lock_and_password_on_wake_are_set_within_their_ranges() {
        let registry = FakeRegistry::holding(SYSTEM_POLICIES, "InactivityTimeoutSecs", observe(ValueKind::Dword, &0u32.to_le_bytes()));
        let items = [
            item("lock", SYSTEM_POLICIES, "InactivityTimeoutSecs", "dword", "600", ENFORCE),
            item("wake-ac", CONSOLELOCK, "ACSettingIndex", "dword", "1", ENFORCE),
            item("wake-dc", CONSOLELOCK, "DCSettingIndex", "dword", "1", ENFORCE),
        ];

        let result = registry.check(&items, Vec::new());

        assert!(result.items.iter().all(|i| i.state == COMPLIANT && i.corrected));
        assert_eq!(item_of(&result, "lock").detail.as_deref(), Some("set back by Kintsugi (was 0)"));
        assert_eq!(registry.writes.borrow().len(), 3);
    }

    /// A value's "off" is outside its range, so a signed document cannot use enforce to switch the lock off,
    /// stop the password on wake, or lock every screen a few seconds after the last keystroke.
    #[test]
    fn data_outside_an_enforceable_values_range_is_never_written() {
        let items = [
            item("lock-off", SYSTEM_POLICIES, "InactivityTimeoutSecs", "dword", "0", ENFORCE),
            item("lock-hours", SYSTEM_POLICIES, "InactivityTimeoutSecs", "dword", "36000", ENFORCE),
            item("lock-seconds", SYSTEM_POLICIES, "InactivityTimeoutSecs", "dword", "5", ENFORCE),
            item("wake-off", CONSOLELOCK, "ACSettingIndex", "dword", "0", ENFORCE),
        ];
        let empty = FakeRegistry::default();
        let read = |path: &str, name: &str| empty.read(path, name);

        let result = evaluate(&identity(), &served(document(&items.join(",")), Vec::new()), &read, &never_write).unwrap();

        assert!(result.items.iter().all(|i| i.state == DRIFTED && !i.corrected));
        assert_eq!(
            item_of(&result, "lock-off").detail.as_deref(),
            Some("not set back (was absent): 0 is outside the 60 to 900 this agent may write here")
        );
        assert!(item_of(&result, "wake-off").detail.as_deref().unwrap().contains("outside the 1 to 1"));
    }

    #[test]
    fn a_compliant_enforced_value_is_left_alone() {
        let registry = FakeRegistry::holding(CHROME, OCCLUSION, observe(ValueKind::Dword, &0u32.to_le_bytes()));
        let read = |path: &str, name: &str| registry.read(path, name);

        let result = evaluate(&identity(), &served(document(&occlusion()), Vec::new()), &read, &never_write).unwrap();

        assert_eq!(state_of(&result, "occlusion"), COMPLIANT);
        assert_eq!(item_of(&result, "occlusion").detail, None);
    }

    #[test]
    fn a_write_that_fails_is_an_error_naming_what_was_there() {
        let registry = FakeRegistry { refuse_writes: true, ..FakeRegistry::default() };

        let result = registry.check(&[occlusion()], Vec::new());

        let item = item_of(&result, "occlusion");
        assert_eq!(item.state, ERROR);
        assert!(item.detail.as_deref().unwrap().starts_with("drifted (was absent) and could not be set back"));
    }

    #[test]
    fn a_correction_is_reported_even_when_the_server_already_holds_the_corrected_state() {
        let registry = FakeRegistry::default();
        let known = vec![KnownState { key: "occlusion".into(), state: COMPLIANT.into(), observed: Some("0".into()) }];

        assert_eq!(registry.check(&[occlusion()], known.clone()).items.len(), 1);
        // Set back now, so the next check has nothing to write and nothing to report.
        assert!(registry.check(&[occlusion()], known).items.is_empty());
        assert_eq!(registry.writes.borrow().len(), 1);
    }

    #[test]
    fn an_outlook_signature_item_is_handed_on_and_not_reported_as_the_host() {
        let items = [
            occlusion(),
            r#"{"key":"outlook.signature","kind":"outlook-signature","scope":"user","hive":"HKCU","path":"Software\\Microsoft\\Office\\16.0\\Outlook","valueName":"","mode":"enforce","signatureName":"Example (managed)"}"#.to_string(),
        ];
        let registry = FakeRegistry::holding(CHROME, OCCLUSION, observe(ValueKind::Dword, &0u32.to_le_bytes()));

        let result = registry.check(&items, Vec::new());

        assert_eq!(result.items.iter().map(|i| i.key.as_str()).collect::<Vec<_>>(), vec!["occlusion"]);
        assert_eq!(
            result.signature_items,
            vec![SignatureItem { key: "outlook.signature".into(), enforce: true, signature_name: Some("Example (managed)".into()) }]
        );
    }

    /// A PC whose one Google enrolment synced yesterday.
    struct HealthyMdm;

    impl crate::google_mdm::MdmSystem for HealthyMdm {
        fn snapshot(&self) -> std::io::Result<crate::google_mdm::Snapshot> {
            let today = time::macros::date!(2026 - 11 - 24);
            Ok(crate::google_mdm::Snapshot {
                gcpw_installed: true,
                google: vec![crate::google_mdm::Enrollment {
                    id: "BBBBBBBB-2222-4222-8222-222222222222".into(),
                    upn: "current@example.com".into(),
                    state: 1,
                    last_success: Some(today.previous_day().unwrap()),
                    last_failure: None,
                }],
                gcpw_users: Vec::new(),
                today,
            })
        }
        fn registration(&self) -> Result<crate::google_mdm::Registration, String> {
            unreachable!("a healthy enrolment is never acted on")
        }
        fn unregister(&self, _: &str) -> i32 {
            unreachable!("a healthy enrolment is never unregistered")
        }
    }

    #[test]
    fn a_google_mdm_item_is_judged_as_the_host_and_reported_only_when_it_changes() {
        let item = r#"{"key":"google.mdm","kind":"google-mdm","scope":"machine","hive":"HKLM","path":"SOFTWARE\\Microsoft\\Enrollments","valueName":"","data":"14","mode":"enforce"}"#;
        let read_nothing = |_: &str, _: &str| -> std::io::Result<Option<Observed>> { panic!("a google-mdm item reads no registry value through here") };
        let check = |known| evaluate_with(&identity(), &served(document(item), known), &read_nothing, &never_write, &HealthyMdm).unwrap();

        let first = check(Vec::new());
        let result = first.items.first().unwrap();
        assert_eq!((result.key.as_str(), result.state, result.subject.as_deref()), ("google.mdm", COMPLIANT, None));

        let known = vec![KnownState { key: "google.mdm".into(), state: COMPLIANT.into(), observed: result.observed.clone() }];
        assert!(check(known).items.is_empty());
    }

    /// The real write path against the real registry: what it writes is what `read_machine_value` reads.
    /// A developer's unelevated shell skips rather than fails. CI (which sets `CI`) never skips: its
    /// runner is elevated, and a passing test's output is hidden, so a skip there would look like a pass.
    #[test]
    fn write_machine_value_round_trips_through_the_registry() {
        let path = format!(r"SOFTWARE\Kintsugi-agent-test-{}", std::process::id());
        match write_machine_value(&path, "D", &ValueKind::Dword, "0") {
            Err(err) if err.kind() == std::io::ErrorKind::PermissionDenied && std::env::var_os("CI").is_none() => {
                eprintln!("skipped: writing under HKLM needs an elevated shell");
                return;
            }
            other => other.expect("the dword could not be written"),
        }
        write_machine_value(&path, "S", &ValueKind::String, "abc").unwrap();

        assert_eq!(read_machine_value(&path, "D").unwrap(), Some(observe(ValueKind::Dword, &0u32.to_le_bytes())));
        assert_eq!(read_machine_value(&path, "S").unwrap().map(|o| o.text), Some("abc".to_string()));
        assert!(write_machine_value(&path, "B", &ValueKind::Binary, "00").is_err());
        assert!(write_machine_value(&path, "D", &ValueKind::Dword, "x").is_err());

        let _ = RegKey::predef(HKEY_LOCAL_MACHINE).delete_subkey_all(&path);
    }
}
