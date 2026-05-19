import SwiftUI
import AppKit

/// 1pt-wide visible divider with a 6pt-wide invisible hit area for
/// dragging. Sits between a sidebar and the adjacent file list to let
/// the user resize the sidebar independently for the left and right
/// panels. Direction of drag is mirrored for the right panel where the
/// sidebar is on the trailing edge.
struct SidebarResizeHandle: View {
    @Binding var width: Double
    let side: PanelSide
    let minWidth: Double
    let maxWidth: Double
    /// Width restored when the user double-clicks the handle. Matches
    /// the `@AppStorage` default so a fresh launch and a double-click
    /// land on the same value.
    let defaultWidth: Double

    @State private var dragStartWidth: Double?

    var body: some View {
        ZStack {
            // The visible separator.
            Rectangle()
                .fill(Color(NSColor.separatorColor))
                .frame(width: 1)
            // Wider invisible hit area so the user doesn't have to
            // pixel-hunt the divider. Centred over the separator.
            Color.clear
                .frame(width: 6)
                .contentShape(Rectangle())
                .onHover { hovering in
                    // Pushing/popping NSCursor on every onHover change
                    // is overkill and tends to leak frames; `.set()` on
                    // the way in and reset to arrow on the way out is
                    // the simplest reliable pattern.
                    if hovering {
                        NSCursor.resizeLeftRight.set()
                    } else {
                        NSCursor.arrow.set()
                    }
                }
                // Double-click resets to the saved default. Listed
                // before the drag gesture so SwiftUI's gesture
                // arbitration recognises the tap when the user just
                // clicks; the drag still wins as soon as movement
                // crosses the minimumDistance threshold below.
                .onTapGesture(count: 2) {
                    withAnimation(.easeInOut(duration: 0.15)) {
                        width = defaultWidth
                    }
                }
                .gesture(
                    DragGesture(minimumDistance: 1)
                        .onChanged { value in
                            if dragStartWidth == nil { dragStartWidth = width }
                            guard let start = dragStartWidth else { return }
                            let delta = Double(value.translation.width)
                            // Left-side sidebar: dragging right widens.
                            // Right-side sidebar: dragging left widens.
                            let signed = side == .left ? delta : -delta
                            width = max(minWidth, min(maxWidth, start + signed))
                        }
                        .onEnded { _ in
                            dragStartWidth = nil
                        }
                )
        }
        .frame(width: 6)
    }
}
