//! Device baselines, **audit only**: what the server says should be true on this PC (Master Daily's
//! registry settings, to begin with), read and reported, and never written.
//!
//! **There is no write path in this module, and that is the point of this step.** Every registry call
//! here opens a key with `KEY_READ` and reads a value; nothing creates, sets or deletes anything. A
//! later step adds enforcement (see "Kintsugi device configuration design", IP-841); until then the
//! worst a bad baseline can do is make the compliance numbers wrong.
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
//! **Windows only.** The DTOs here are hand-mirrored from the server's
//! (`Kintsugi.Application/Baselines/BaselineDtos.cs`, `BaselineDocument.cs` and
//! `ReportBaselineResultsCommand.cs`; see `.claude/rules/hand-mirrored-dtos.md`), and in this step
//! only this agent has them: registry items mean nothing on macOS or Linux, whose agents do not
//! fetch baselines yet.

use std::fs;
use std::path::Path;

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use winreg::enums::{RegType, HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_64KEY};
use winreg::RegKey;

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;

/// Below the server's `HostBaselineItemState.MaxObservedLength` (2048), so one long value is never
/// the reason a whole report is refused. Raise the server's first, never this one.
pub const MAX_OBSERVED_CHARS: usize = 2000;

/// Below `HostBaselineItemState.MaxDetailLength` (500), for the same reason.
pub const MAX_DETAIL_CHARS: usize = 400;

/// The one document schema this agent understands — `BaselineDocument.CurrentSchema` on the server.
const SCHEMA: u32 = 1;

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

/// Mirrors `BaselineItem`. `mode` is deliberately not read: whatever a later document says, this
/// agent has no write path, so every item is audited.
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
}

/// Mirrors `ItemResultInput`.
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ItemResult {
    pub key: String,
    pub state: &'static str,
    pub observed: Option<String>,
    pub detail: Option<String>,
}

/// Mirrors `FactResultInput`.
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct FactResult {
    pub key: String,
    pub value: Option<String>,
}

// The `BaselineItemState` member names, which is what crosses this wire (never the ordinals).
const COMPLIANT: &str = "Compliant";
const DRIFTED: &str = "Drifted";
const NOT_APPLICABLE: &str = "NotApplicable";
const ERROR: &str = "Error";

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
    fn from_document(value_type: &str) -> Option<Self> {
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
        match evaluate(identity, served, &read_machine_value) {
            Ok(result) => results.push(result),
            // One refused baseline must not cost the others their report.
            Err(err) => logging::warn(&format!("refusing baseline version {}: {err:#}", served.version_id)),
        }
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

/// Verifies one served baseline, reads every item, and returns only what differs from what the
/// server already holds. `read` reads one HKLM value — `read_machine_value` for real, a fake in tests.
pub(crate) fn evaluate(
    identity: &AgentIdentity,
    served: &ServedBaseline,
    read: &dyn Fn(&str, &str) -> std::io::Result<Option<Observed>>,
) -> Result<BaselineResult> {
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

    for item in &document.items {
        match item.kind.as_str() {
            "setting" => {
                let result = judge(item, read);
                let known = served.states.iter().find(|s| s.key == item.key);
                if known.is_none_or(|k| k.state != result.state || k.observed != result.observed) {
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
            // A kind from a later server: not this agent's to judge, and not a setting the server
            // would record a state for.
            other => logging::warn(&format!("skipping baseline item {} of unknown kind {other}", item.key)),
        }
    }

    Ok(BaselineResult { version_id: document.version_id, items, facts })
}

/// What one setting is on this PC.
fn judge(item: &Item, read: &dyn Fn(&str, &str) -> std::io::Result<Option<Observed>>) -> ItemResult {
    let result = |state, observed: Option<String>, detail: Option<&str>| ItemResult {
        key: item.key.clone(),
        state,
        observed: observed.map(|o| truncate(&o, MAX_OBSERVED_CHARS)),
        detail: detail.map(|d| truncate(d, MAX_DETAIL_CHARS)),
    };

    // The tray process reads user-scope items, as the signed-in user, in a later step. The service
    // runs as SYSTEM, whose HKCU is its own profile — which is exactly why Ninja's HKCU writes never
    // reached anybody — so reading it here would report on nobody.
    if item.scope == "user" {
        return result(NOT_APPLICABLE, None, Some("user-scope items are checked by the tray process, in a later step"));
    }
    if item.scope != "machine" || item.hive != "HKLM" {
        return result(ERROR, None, Some("a machine item must be under HKLM"));
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
    if item.scope != "machine" || item.hive != "HKLM" {
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

fn truncate(text: &str, max_chars: usize) -> String {
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

        let result = evaluate(&identity(), &served(document(&items), Vec::new()), &registry).unwrap();

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
        assert!(evaluate(&identity(), &baseline, &read_nothing).is_err());
    }

    #[test]
    fn an_envelope_that_disagrees_with_the_signed_version_is_refused() {
        let mut baseline = served(document(&setting("a", "on", "dword", "1")), Vec::new());
        baseline.version_id = "00000000-0000-0000-0000-000000000000".to_string();

        assert!(evaluate(&identity(), &baseline, &registry).is_err());
    }

    #[test]
    fn only_what_differs_from_the_servers_copy_is_reported() {
        let items = [setting("same", "on", "dword", "1"), setting("changed", "off", "dword", "1")].join(",");
        let known = vec![
            KnownState { key: "same".into(), state: COMPLIANT.into(), observed: Some("1".into()) },
            KnownState { key: "changed".into(), state: COMPLIANT.into(), observed: Some("1".into()) },
        ];

        let result = evaluate(&identity(), &served(document(&items), known), &registry).unwrap();

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

        let result = evaluate(&identity(), &served(document(&items), Vec::new()), &read_nothing).unwrap();

        assert_eq!(state_of(&result, "user"), NOT_APPLICABLE);
        assert_eq!(state_of(&result, "secret"), NOT_APPLICABLE);
        assert!(result.items.iter().all(|i| i.observed.is_none()));
    }

    #[test]
    fn facts_are_reported_when_new_or_changed() {
        let items = r#"{"key":"edition","kind":"fact","scope":"machine","hive":"HKLM","path":"text","valueName":"EditionID"}"#;
        let mut baseline = served(document(items), Vec::new());

        let first = evaluate(&identity(), &baseline, &registry).unwrap();
        assert_eq!(first.facts, vec![FactResult { key: "edition".into(), value: Some("abc".into()) }]);

        baseline.facts = vec![KnownFact { key: "edition".into(), value: Some("abc".into()) }];
        assert!(evaluate(&identity(), &baseline, &registry).unwrap().facts.is_empty());
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
}
