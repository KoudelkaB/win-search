//! Colors and fonts: the light look of the HTML app.

use egui::{Color32, FontId};

pub const BG: Color32 = Color32::from_rgb(0xee, 0xf3, 0xfb);
pub const PANEL: Color32 = Color32::from_rgb(0xff, 0xff, 0xff);
pub const SURFACE: Color32 = Color32::from_rgb(0xf7, 0xf9, 0xfd);
pub const SURFACE_2: Color32 = Color32::from_rgb(0xec, 0xf2, 0xfb);
pub const BORDER: Color32 = Color32::from_rgb(0xd4, 0xde, 0xee);
pub const BORDER_STRONG: Color32 = Color32::from_rgb(0xb8, 0xc7, 0xdf);
pub const TEXT: Color32 = Color32::from_rgb(0x1b, 0x24, 0x30);
pub const MUTED: Color32 = Color32::from_rgb(0x61, 0x75, 0x8e);
pub const ACCENT: Color32 = Color32::from_rgb(0x2f, 0x80, 0xed);
pub const ACCENT_STRONG: Color32 = Color32::from_rgb(0x19, 0x5f, 0xc4);
pub const ACCENT_SOFT: Color32 = Color32::from_rgb(0xd8, 0xe8, 0xff);
pub const PREVIEW: Color32 = Color32::from_rgb(0x1f, 0x6f, 0xe5);
pub const DANGER: Color32 = Color32::from_rgb(0xcb, 0x3b, 0x46);
pub const SELECTION: Color32 = Color32::from_rgb(0xb9, 0xd4, 0xfb);
pub const HOVER_ROW: Color32 = Color32::from_rgb(0xf1, 0xf5, 0xfc);

/// Base colors of the per-file tints
const TONES: [(u8, u8, u8); 8] = [(211, 83, 70), (28, 125, 108), (45, 116, 209), (181, 123, 16), (122, 79, 179), (0, 137, 173), (156, 79, 124), (98, 119, 42)];

/// The background of a row of source `tone`: 14 % of its color over white, 22 % under the pointer
pub fn source_tint(tone: usize, hovered: bool) -> Color32 {
    let (red, green, blue) = TONES[tone % TONES.len()];
    let alpha = if hovered { 0.22 } else { 0.14 };
    let mix = |channel: u8| (255.0 + (channel as f32 - 255.0) * alpha).round() as u8;
    Color32::from_rgb(mix(red), mix(green), mix(blue))
}

pub fn mono_font() -> FontId {
    FontId::monospace(13.0)
}

pub fn apply(ctx: &egui::Context) {
    // The monospace font also draws the arrows and symbols of the buttons, which the
    // proportional one lacks: no label depends on the fonts of the system
    let mut fonts = egui::FontDefinitions::default();
    let monospace = fonts.families.get(&egui::FontFamily::Monospace).cloned().unwrap_or_default();
    let proportional = fonts.families.entry(egui::FontFamily::Proportional).or_default();
    for name in monospace {
        if !proportional.contains(&name) {
            proportional.push(name);
        }
    }
    ctx.set_fonts(fonts);
    ctx.set_theme(egui::Theme::Light);
    ctx.all_styles_mut(|style| {
        // A click on a label of a card selects the card instead of starting a text selection
        style.interaction.selectable_labels = false;
        style.spacing.item_spacing = egui::vec2(6.0, 5.0);
        style.spacing.button_padding = egui::vec2(8.0, 3.0);
        style.visuals.panel_fill = BG;
        style.visuals.window_fill = PANEL;
        style.visuals.extreme_bg_color = PANEL;
        style.visuals.override_text_color = Some(TEXT);
        style.visuals.selection.bg_fill = ACCENT_SOFT;
        style.visuals.selection.stroke = egui::Stroke::new(1.0, ACCENT_STRONG);
        style.visuals.hyperlink_color = ACCENT_STRONG;
        style.visuals.widgets.noninteractive.bg_stroke = egui::Stroke::new(1.0, BORDER);
        style.visuals.widgets.inactive.weak_bg_fill = PANEL;
        style.visuals.widgets.inactive.bg_fill = PANEL;
        style.visuals.widgets.inactive.bg_stroke = egui::Stroke::new(1.0, BORDER);
        style.visuals.widgets.hovered.weak_bg_fill = SURFACE_2;
        style.visuals.widgets.hovered.bg_stroke = egui::Stroke::new(1.0, BORDER_STRONG);
        style.visuals.widgets.active.weak_bg_fill = ACCENT_SOFT;
        for widget in [&mut style.visuals.widgets.inactive, &mut style.visuals.widgets.hovered, &mut style.visuals.widgets.active, &mut style.visuals.widgets.open] {
            widget.corner_radius = egui::CornerRadius::same(6);
        }
    });
}
