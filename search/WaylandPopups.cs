using Avalonia;
using Avalonia.Controls;

namespace search
{
    /// <summary>
    /// Tooltips below their control on Wayland. Under the decorations Avalonia draws itself (GNOME
    /// has no server-side ones) Avalonia 12.1.6 places a popup anchored at the pointer 8 px left of
    /// it and 38 px - the shadow and the title bar - too high. A tooltip then opened right under the
    /// pointer, and the click meant for its button went to the tooltip: resting on "Drives..." for
    /// half a second made the button do nothing. Popups anchored to their control (drop-down lists)
    /// are placed right, so tooltips are anchored to their control too. Context menus, which open
    /// at the pointer, still open that much higher.
    /// </summary>
    internal static class WaylandPopups
    {
        /// <summary>Before the first window, so that every tooltip set from then on is placed so</summary>
        public static void Apply()
            => ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, _) =>
                ToolTip.SetPlacement(control, PlacementMode.Bottom));
    }
}
