import SwiftUI

struct HelpView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var selection: HelpSection = .overview

    var body: some View {
        NavigationSplitView {
            List(HelpSection.allCases, id: \.self, selection: $selection) { section in
                Label(section.title, systemImage: section.systemImage)
                    .tag(section)
            }
            .navigationTitle("FileFluss Help")
            .frame(minWidth: 200)
        } detail: {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    Text(selection.title)
                        .font(.largeTitle).bold()
                        .padding(.bottom, 4)
                    selection.content
                }
                .padding(24)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .navigationSubtitle(selection.title)
        }
        .frame(minWidth: 760, minHeight: 520)
    }
}

enum HelpSection: String, CaseIterable, Hashable {
    case overview
    case panels
    case addingCloudAccounts
    case copyMove
    case dragDropModes
    case sync
    case compare
    case search
    case openingFiles

    var title: String {
        switch self {
        case .overview: return "Overview"
        case .panels: return "Two‑Panel Layout"
        case .addingCloudAccounts: return "Adding Cloud Accounts"
        case .copyMove: return "Copying and Moving Files"
        case .dragDropModes: return "Copy Mode and Move Mode"
        case .sync: return "Sync"
        case .compare: return "Compare Folders"
        case .search: return "Search"
        case .openingFiles: return "Opening Files"
        }
    }

    var systemImage: String {
        switch self {
        case .overview: return "info.circle"
        case .panels: return "rectangle.split.2x1"
        case .addingCloudAccounts: return "icloud"
        case .copyMove: return "doc.on.doc"
        case .dragDropModes: return "arrow.left.arrow.right.square"
        case .sync: return "arrow.triangle.2.circlepath"
        case .compare: return "rectangle.on.rectangle.angled"
        case .search: return "magnifyingglass"
        case .openingFiles: return "arrow.up.forward.app"
        }
    }

    @ViewBuilder
    var content: some View {
        switch self {
        case .overview: HelpOverviewSection()
        case .panels: HelpPanelsSection()
        case .addingCloudAccounts: HelpCloudAccountsSection()
        case .copyMove: HelpCopyMoveSection()
        case .dragDropModes: HelpDragDropModesSection()
        case .sync: HelpSyncSection()
        case .compare: HelpCompareSection()
        case .search: HelpSearchSection()
        case .openingFiles: HelpOpenFilesSection()
        }
    }
}

// MARK: - Section Building Blocks

private struct HelpParagraph: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(LocalizedStringKey(text))
            .font(.body)
            .lineSpacing(2)
            .fixedSize(horizontal: false, vertical: true)
    }
}

private struct HelpHeading: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text)
            .font(.title3.bold())
            .padding(.top, 8)
    }
}

private struct HelpBullet: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Text("•").foregroundStyle(.secondary)
            Text(LocalizedStringKey(text))
                .lineSpacing(2)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

private struct HelpTip: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "lightbulb")
                .foregroundStyle(.yellow)
            Text(LocalizedStringKey(text))
                .lineSpacing(2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(10)
        .background(.yellow.opacity(0.1), in: RoundedRectangle(cornerRadius: 6))
    }
}

// MARK: - Sections

private struct HelpOverviewSection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HelpParagraph("FileFluss is a dual‑panel file manager for macOS that lets you work with local folders and multiple cloud storage accounts side by side. It is built around a left and right panel — copy, move, sync, and compare files between them with a few clicks.")
            HelpHeading("At a glance")
            HelpBullet("**Two independent panels**, each can show a local folder or a connected cloud account.")
            HelpBullet("**Drag & drop** between panels to copy or move files.")
            HelpBullet("**Sync** to make the two sides match (mirror, newer, or additive).")
            HelpBullet("**Compare** to see which files differ, are missing, or match between the two sides.")
            HelpBullet("**Search** across local files and every connected cloud account at once.")
            HelpHeading("Toolbar")
            HelpBullet("**Search** (⌘F) — open the search window.")
            HelpBullet("**Sync** — open the Sync planner.")
            HelpBullet("**Compare** — open the Compare Folders window.")
            HelpBullet("**Refresh** — reload both panels.")
            HelpBullet("**Hidden Files** — toggle showing dotfiles in the active panel.")
            HelpBullet("**Copy Mode** / **Move Mode** — force every drag & drop to that operation.")
            HelpTip("Right‑click the toolbar and choose *Customize Toolbar…* to rearrange or hide buttons.")
        }
    }
}

private struct HelpPanelsSection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HelpParagraph("FileFluss shows a left panel and a right panel. The active panel is marked with a thin accent line at the top — most actions (toolbar buttons, keyboard shortcuts, the search target) apply to whichever panel is active.")
            HelpHeading("Switching panels")
            HelpBullet("Click anywhere inside a panel to make it active.")
            HelpBullet("The sidebar of each panel is independent — choose a different favourite, location, or cloud account on each side.")
            HelpHeading("The path bar")
            HelpBullet("The breadcrumb at the top of each panel shows the current folder path. Click any segment to navigate up.")
            HelpBullet("You can drop files onto a path‑bar segment to move/copy them into that folder.")
            HelpHeading("Cloud accounts on both sides")
            HelpParagraph("If you open the same cloud account in both panels (e.g. kDrive on the left and on the right), the two panels behave **independently**: you can browse to different folders on each side and drag between them.")
            HelpTip("Dragging a file from the same cloud account on the left to a folder on the right does an **in‑cloud** move/copy — the data stays on the server, no re‑upload.")
        }
    }
}

private struct HelpCloudAccountsSection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HelpParagraph("Cloud accounts are connected once and then appear in the sidebar of both panels.")
            HelpHeading("Add an account")
            HelpBullet("Open **Settings → Cloud Accounts** (⌘,).")
            HelpBullet("Click **Add Cloud Account** and pick the provider (kDrive, pCloud, OneDrive, Google Drive, Dropbox, MEGA, Koofr, NextCloud, WebDAV, SFTP, GMX Cloud, WordPress, …).")
            HelpBullet("Sign in via the provider's authentication flow. FileFluss stores the tokens securely in your macOS Keychain.")
            HelpBullet("The new account immediately appears in both panels' sidebars.")
            HelpHeading("Use an account")
            HelpBullet("Click the account in either sidebar to load it in that panel.")
            HelpBullet("Navigate folders the same way as local — double‑click to enter, or click breadcrumbs to go back up.")
            HelpHeading("Remove an account")
            HelpBullet("In **Settings → Cloud Accounts**, click the trash icon next to the account.")
            HelpBullet("FileFluss disconnects the account, removes the cached login, and clears its panels.")
            HelpTip("Some providers expose a sandboxed root (e.g. WordPress' media library). Behaviour like *create folder* may be unavailable for those providers — FileFluss greys out the actions that don't apply.")
        }
    }
}

private struct HelpCopyMoveSection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HelpParagraph("There are several ways to move or copy files between the two panels:")
            HelpHeading("Drag & drop")
            HelpBullet("Drag selected files from one panel and drop them on the other.")
            HelpBullet("By default a prompt appears asking *Move or Copy?*.")
            HelpBullet("Drop directly onto a folder row to land inside that folder, or onto the table background to drop into the panel's current directory.")
            HelpBullet("You can also drop onto a breadcrumb in the path bar to land in that ancestor folder.")
            HelpHeading("Right‑click")
            HelpBullet("Select files, right‑click, and choose **Copy to Other Panel** or **Move to Other Panel**.")
            HelpBullet("Same effect as drag & drop, but no prompt — the action is taken immediately.")
            HelpHeading("Conflicts")
            HelpParagraph("If a file with the same name already exists at the destination, FileFluss asks how to resolve the conflict (replace, keep both, skip).")
            HelpTip("If you find the *Move or Copy?* prompt repetitive, enable **Copy Mode** or **Move Mode** in the toolbar — see the next section.")
        }
    }
}

private struct HelpDragDropModesSection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HelpParagraph("**Copy Mode** and **Move Mode** are two toolbar toggles that change how every drag & drop is interpreted.")
            HelpHeading("Copy Mode")
            HelpBullet("When active, every drop is automatically a **copy** — the *Move or Copy?* prompt is skipped.")
            HelpBullet("Useful when you're duplicating a lot of files between folders or panels.")
            HelpHeading("Move Mode")
            HelpBullet("When active, every drop is automatically a **move** — files are removed from the source.")
            HelpBullet("Useful when re‑organising a folder.")
            HelpHeading("Important")
            HelpBullet("The two toggles are **mutually exclusive** — turning one on turns the other off.")
            HelpBullet("Both reset to off when FileFluss relaunches, so a stuck *Move Mode* can't silently destroy files across sessions.")
            HelpTip("When neither mode is on, FileFluss falls back to the standard *Move or Copy?* prompt.")
        }
    }
}

private struct HelpSyncSection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HelpParagraph("**Sync** makes the destination folder match the source folder according to the rule you choose. Use it for one‑shot replications between local and cloud, or between two cloud accounts.")
            HelpHeading("Run a sync")
            HelpBullet("Open one folder on the left and one on the right.")
            HelpBullet("Click **Sync** in the toolbar.")
            HelpBullet("Choose the **direction** (Left → Right or Right → Left).")
            HelpBullet("Choose the **mode** (see below).")
            HelpBullet("FileFluss enumerates both sides and shows a plan: how many files will be added, replaced, deleted, and how many bytes will be transferred. Click **Start Sync**.")
            HelpHeading("Modes")
            HelpBullet("**Newer** — copy files from the source whose modification date is newer than the destination's. Existing files only get replaced if they're truly newer.")
            HelpBullet("**Mirror** — make the destination an exact copy of the source. **Files at the destination that are not on the source are deleted.** This is destructive — FileFluss requires explicit confirmation.")
            HelpBullet("**Additive** — only copy files that are missing on the destination. If a file already exists, the source is added next to it with a unique name. Nothing is overwritten or deleted.")
            HelpHeading("Conflicts")
            HelpParagraph("During sync, if a file with the same name exists on both sides but neither mode handles it cleanly, FileFluss asks per‑file how to resolve.")
            HelpTip("Sync runs in the background — you can keep working in either panel while it transfers. Progress shows in the destination panel's sidebar; click it to see details.")
        }
    }
}

private struct HelpCompareSection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HelpParagraph("**Compare** shows you the differences between the two panels' folders without changing anything.")
            HelpHeading("Run a comparison")
            HelpBullet("Open one folder on the left and one on the right.")
            HelpBullet("Click **Compare** in the toolbar.")
            HelpBullet("FileFluss enumerates both sides recursively and opens a window with the result.")
            HelpHeading("Reading the result")
            HelpBullet("**Different** (red ⚠) — file exists on both sides but its size (or modification date, if enabled) differs.")
            HelpBullet("**Only on Left** (← blue) — file exists only in the left folder.")
            HelpBullet("**Only on Right** (→ orange) — file exists only in the right folder.")
            HelpBullet("**Identical** (✓ green) — file matches on both sides.")
            HelpBullet("Folders are **collapsible** — click the disclosure arrow to expand and see what's inside. The folder header shows aggregate counts for the active filter.")
            HelpBullet("Use the **filter chips** at the top to narrow to one status, e.g. show only differences.")
            HelpHeading("Compare Date")
            HelpBullet("Off by default — only file size is compared.")
            HelpBullet("When on, files with the same size but a different modification date count as **Different**, and each row shows both modify and create timestamps along with a *Left newer* / *Right newer* badge.")
            HelpHeading("Compare Again")
            HelpBullet("The comparison only re‑runs when you click **Compare** in the toolbar or **Compare Again** in the window — browsing folders while the window is open does not trigger a fresh scan.")
            HelpTip("The compare window stays open and is **movable** — drag it aside to see both panels behind it.")
        }
    }
}

private struct HelpSearchSection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HelpParagraph("**Search** queries the active local panel and every connected cloud account simultaneously.")
            HelpHeading("Run a search")
            HelpBullet("Click **Search** in the toolbar, or press **⌘F**.")
            HelpBullet("Type a query — results stream in as each source responds.")
            HelpBullet("Filter by source using the chips below the search field.")
            HelpHeading("Acting on results")
            HelpBullet("**Double‑click** a result to load its parent folder in the active panel.")
            HelpBullet("**Right‑click** to choose **Open in Left Panel** or **Open in Right Panel** explicitly.")
            HelpBullet("The search window stays open after picking a result, so you can pick more.")
            HelpHeading("Closing")
            HelpBullet("Click the round X close button, press **Esc**, or click the red traffic‑light button.")
            HelpTip("The search window is movable and resizable — its size is remembered across launches.")
        }
    }
}

private struct HelpOpenFilesSection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HelpParagraph("Files on local panels and on cloud accounts open the same way.")
            HelpHeading("Open in the default app")
            HelpBullet("**Double‑click** any file. Word documents open in Word, PDFs in Preview, images in your image viewer, and so on.")
            HelpBullet("Cloud files are downloaded to a temporary cache the first time, so reopening the same file is instant.")
            HelpBullet("Edits made in the local app are **not** synced back to the cloud automatically — use Sync or drag the modified file back if you want it uploaded.")
            HelpHeading("Quick Look")
            HelpBullet("Select a file and press **Space** to preview it without opening it.")
            HelpHeading("File icons")
            HelpBullet("Local images and PDFs show a real preview thumbnail of the file content.")
            HelpBullet("Cloud files show a colourful generic icon based on the file extension.")
        }
    }
}
