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
    /// The two magnetic detents the drag locks to: an icon-only width and
    /// the narrowest comfortable expanded width. Anything dragged into the
    /// gap between them snaps to the nearer of the two, so the bar never
    /// rests inside the "useless" middle range — the visible jump at the
    /// midpoint is what gives the resize its satisfying magnetic feel.
    let collapsedSnapWidth: Double
    let expandedSnapMinWidth: Double

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
                    // `.global` coordinate space matters: the handle moves
                    // as the sidebar resizes, so a default `.local` drag
                    // would compute its translation against a moving
                    // frame and feed the width back to itself each tick —
                    // visible as a left/right shake. Pinning to the
                    // window's global frame eliminates that feedback loop
                    // and gives the same feel as a native window resize.
                    DragGesture(minimumDistance: 1, coordinateSpace: .global)
                        .onChanged { value in
                            if dragStartWidth == nil { dragStartWidth = width }
                            guard let start = dragStartWidth else { return }
                            let delta = Double(value.translation.width)
                            // Left-side sidebar: dragging right widens.
                            // Right-side sidebar: dragging left widens.
                            let signed = side == .left ? delta : -delta
                            let raw = max(minWidth, min(maxWidth, start + signed))
                            // Disable implicit animations on the binding
                            // write so the panel tracks the cursor 1:1 —
                            // any animation here would lag the drag by a
                            // frame or two and show up as stutter.
                            var transaction = Transaction()
                            transaction.disablesAnimations = true
                            withTransaction(transaction) {
                                width = magneticSnap(raw)
                            }
                        }
                        .onEnded { _ in
                            dragStartWidth = nil
                        }
                )
        }
        .frame(width: 6)
    }

    /// Locks the dragged width to one of two detents below
    /// `expandedSnapMinWidth`, leaving widths above that point continuous.
    /// The "snap" happens at the midpoint between the two detents — that's
    /// where the bar visibly jumps, which is what the user feels as magnetic.
    private func magneticSnap(_ raw: Double) -> Double {
        guard raw < expandedSnapMinWidth else { return raw }
        let midpoint = (collapsedSnapWidth + expandedSnapMinWidth) / 2
        return raw < midpoint ? collapsedSnapWidth : expandedSnapMinWidth
    }
}
