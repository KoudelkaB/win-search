//! UI translations and help text bundled from `assets/translations.json`.

use std::collections::HashMap;

const TRANSLATIONS: &str = include_str!("../assets/translations.json");

/// `{0}`, `{1}`... replaced by the arguments
pub fn fill(text: &str, args: &[String]) -> String {
    if args.is_empty() || !text.contains('{') {
        return text.to_string();
    }
    let mut out = String::with_capacity(text.len() + 16);
    let mut rest = text;
    while let Some(open) = rest.find('{') {
        let (before, tail) = rest.split_at(open);
        out.push_str(before);
        let digits = tail[1..].bytes().take_while(u8::is_ascii_digit).count();
        match (digits > 0 && tail.as_bytes().get(1 + digits) == Some(&b'}')).then(|| tail[1..1 + digits].parse::<usize>().ok()).flatten() {
            Some(index) if index < args.len() => {
                out.push_str(&args[index]);
                rest = &tail[digits + 2..];
            }
            _ => {
                out.push('{');
                rest = &tail[1..];
            }
        }
    }
    out.push_str(rest);
    out
}

/// A number with spaces separating thousands
pub fn grouped(value: u64) -> String {
    let digits = value.to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3);
    for (index, digit) in digits.chars().enumerate() {
        if index > 0 && (digits.len() - index).is_multiple_of(3) {
            out.push(' ');
        }
        out.push(digit);
    }
    out
}

pub struct I18n {
    language: String,
    dictionary: HashMap<String, String>,
    english: HashMap<String, String>,
}

fn load(language: &str) -> HashMap<String, String> {
    serde_json::from_str::<HashMap<String, serde_json::Value>>(TRANSLATIONS)
        .ok()
        .and_then(|mut all| all.remove(language))
        .and_then(|entry| serde_json::from_value(entry).ok())
        .unwrap_or_default()
}

impl I18n {
    pub fn languages() -> Vec<String> {
        let mut languages: Vec<String> = serde_json::from_str::<HashMap<String, serde_json::Value>>(TRANSLATIONS).map(|all| all.into_keys().collect()).unwrap_or_default();
        languages.sort();
        languages
    }

    /// The first of the requested language tags that has a translation, else English
    pub fn resolve(requested: &[String]) -> String {
        let available = Self::languages();
        for tag in requested.iter().filter(|tag| !tag.is_empty()) {
            // "cs_CZ.UTF-8" of the environment is "cs-CZ"
            let lower = tag.split('.').next().unwrap_or(tag).replace('_', "-").to_lowercase();
            let base = lower.split('-').next().unwrap_or(&lower).to_string();
            if let Some(exact) = available.iter().find(|language| language.to_lowercase() == lower) {
                return exact.clone();
            }
            if base == "en" {
                return "en".to_string();
            }
            if let Some(same_base) = available.iter().find(|language| language.to_lowercase().split('-').next() == Some(&base)) {
                return same_base.clone();
            }
        }
        "en".to_string()
    }

    pub fn new(language: &str) -> Self {
        let language = Self::resolve(&[language.to_string()]);
        I18n { dictionary: if language == "en" { HashMap::new() } else { load(&language) }, english: load("en"), language }
    }

    pub fn language(&self) -> &str {
        &self.language
    }

    /// The translation of an English UI text, or of a help key
    pub fn t<'a>(&'a self, text: &'a str) -> &'a str {
        self.dictionary.get(text).or_else(|| self.english.get(text)).map_or(text, String::as_str)
    }

    /// A translated text with `{0}`... filled in
    pub fn tf(&self, text: &str, args: &[&dyn std::fmt::Display]) -> String {
        let args: Vec<String> = args.iter().map(|arg| arg.to_string()).collect();
        fill(self.t(text), &args)
    }

    /// The message of an operation error in this language
    pub fn error(&self, error: &crate::cancel::OpError) -> String {
        match error {
            crate::cancel::OpError::Canceled => "Canceled".to_string(),
            crate::cancel::OpError::Msg(text) => self.t(text).to_string(),
            crate::cancel::OpError::Fmt(text, args) => fill(self.t(text), args),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn placeholders_are_filled_and_unknown_ones_kept() {
        assert_eq!(fill("Row {0}/{1}, col {2}", &["3".into(), "9".into()]), "Row 3/9, col {2}");
        assert_eq!(fill("a {x} {0}", &["b".into()]), "a {x} b");
        assert_eq!(grouped(0), "0");
        assert_eq!(grouped(1234567), "1 234 567");
        assert_eq!(grouped(999), "999");
    }

    #[test]
    fn the_language_follows_the_request_and_falls_back_to_english() {
        assert_eq!(I18n::resolve(&["cs-CZ".into()]), "cs");
        assert_eq!(I18n::resolve(&["cs_CZ.UTF-8".into()]), "cs");
        assert_eq!(I18n::resolve(&["zh-hans".into()]), "zh-Hans");
        assert_eq!(I18n::resolve(&["pt".into()]), "pt-BR");
        assert_eq!(I18n::resolve(&["xx".into(), "de-AT".into()]), "de");
        assert_eq!(I18n::resolve(&["en-US".into(), "cs".into()]), "en");
        assert_eq!(I18n::resolve(&[]), "en");
        let czech = I18n::new("cs");
        assert_eq!(czech.t("Operations"), "Operace");
        assert_eq!(czech.tf("Lines: {0}", &[&12]), "Řádky: 12");
        assert_eq!(czech.t("not a known text"), "not a known text");
        let english = I18n::new("en");
        assert_eq!(english.t("Operations"), "Operations");
        assert!(english.t("help.keys.f1").starts_with("Open this help"));
    }
}
