import SwiftUI
import FileFlussCore

struct SyncPlannerView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    @State private var direction: PlanDirection = .leftToRight
    @State private var mode: SyncMode = .newer
    @State private var confirmDestructive: Bool = false
    @State private var confirmLargeDeletion: Bool = false
    @State private var plan: SyncPlan?
    /// The two sides `plan` was worked out for. A sync runs against these,
    /// never against whatever the controls happen to say by then.
    @State private var plannedEndpoints: (source: SyncEndpoint, destination: SyncEndpoint)?
    @State private var isCalculating: Bool = false
    @State private var errorMessage: String?
    /// Free space at the destination at Calculate time (cloud quota or local
    /// volume). nil = unlimited / unknown — no projection shown.
    @State private var destAvailable: Int64?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            LText("Sync")
                .font(.title2).bold()

            endpointsSection
            directionSection
            modeSection
            planSection

            if let errorMessage {
                Text(errorMessage)
                    .font(.callout)
                    .foregroundStyle(.red)
            }

            Divider()
            footerButtons
        }
        .padding(20)
        .frame(width: 520)
        .onChange(of: direction) { _, _ in resetPlan() }
        .onChange(of: mode) { _, _ in resetPlan() }
    }

    // MARK: - Sections

    private var endpointsSection: some View {
        HStack(alignment: .top, spacing: 12) {
            endpointCard(title: L10n.text("Left"), endpoint: leftEndpoint)
            Image(systemName: direction == .leftToRight ? "arrow.right" : "arrow.left")
                .font(.title2)
                .foregroundStyle(.secondary)
                .frame(width: 28)
                .padding(.top, 24)
            endpointCard(title: L10n.text("Right"), endpoint: rightEndpoint)
        }
    }

    private func endpointCard(title: String, endpoint: SyncEndpoint?) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.caption).foregroundStyle(.secondary)
            if let endpoint {
                if case .cloud(let accountId, _) = endpoint,
                   let account = appState.syncManager.accounts.first(where: { $0.id == accountId }) {
                    HStack(spacing: 6) {
                        CloudProviderIcon(providerType: account.providerType, size: 16)
                        Text(account.displayName)
                            .font(.callout).bold()
                            .lineLimit(1)
                            .truncationMode(.tail)
                    }
                }
                HStack(spacing: 6) {
                    Image(systemName: endpoint.isCloud ? "folder.fill" : "folder.fill")
                        .foregroundStyle(.secondary)
                    Text(endpoint.displayPath)
                        .lineLimit(2)
                        .truncationMode(.middle)
                        .font(.callout)
                }
            } else {
                LText("No folder open")
                    .foregroundStyle(.secondary)
                    .italic()
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.05)))
    }

    private var directionSection: some View {
        HStack {
            LText("Direction").font(.headline)
            Spacer()
            Picker("", selection: $direction) {
                LText("Left → Right").tag(PlanDirection.leftToRight)
                LText("Left ← Right").tag(PlanDirection.rightToLeft)
            }
            .pickerStyle(.segmented)
            .frame(width: 240)
            .labelsHidden()
            .disabled(isCalculating)
        }
    }

    private var modeSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            LText("Mode").font(.headline)
            ForEach(SyncMode.allCases, id: \.self) { option in
                Button {
                    mode = option
                } label: {
                    HStack(alignment: .top, spacing: 10) {
                        Image(systemName: mode == option ? "largecircle.fill.circle" : "circle")
                            .foregroundStyle(mode == option ? Color.accentColor : .secondary)
                            .padding(.top, 2)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(L10n.text(option.title)).font(.callout).bold()
                            Text(L10n.text(option.subtitle)).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .disabled(isCalculating)
            }
            if mode.isDestructive {
                Toggle(isOn: $confirmDestructive) {
                    LText("I understand files on the destination will be deleted.")
                        .font(.caption)
                        .foregroundStyle(.red)
                }
                .toggleStyle(.checkbox)
                .padding(.top, 4)
            }
        }
    }

    @ViewBuilder
    private var planSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                LText("Plan").font(.headline)
                Spacer()
                Button {
                    Task { await calculate() }
                } label: {
                    if isCalculating {
                        ProgressView().controlSize(.small)
                    } else {
                        LText("Calculate")
                    }
                }
                .disabled(isCalculating || leftEndpoint == nil || rightEndpoint == nil || readOnlyDestination != nil)
            }

            if let plan {
                Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 4) {
                    GridRow { LText("Files to add:").foregroundStyle(.secondary); Text("\(plan.filesToAdd)") }
                    GridRow { LText("Files to replace:").foregroundStyle(.secondary); Text("\(plan.filesToReplace)") }
                    GridRow {
                        LText("Files to delete:").foregroundStyle(.secondary)
                        Text("\(plan.filesToDelete)")
                            .foregroundStyle(plan.filesToDelete > 0 ? .red : .primary)
                    }
                    GridRow { LText("Folders to add:").foregroundStyle(.secondary); Text("\(plan.foldersToAdd)") }
                    GridRow {
                        LText("Folders to delete:").foregroundStyle(.secondary)
                        Text("\(plan.foldersToDelete)")
                            .foregroundStyle(plan.foldersToDelete > 0 ? .red : .primary)
                    }
                    Divider().gridCellColumns(2)
                    if plan.downloadBytes > 0 {
                        GridRow {
                            LText("Download:").foregroundStyle(.secondary)
                            Text(ByteCountFormatter.string(fromByteCount: plan.downloadBytes, countStyle: .file))
                        }
                    }
                    if plan.uploadBytes > 0 {
                        GridRow {
                            LText("Upload:").foregroundStyle(.secondary)
                            Text(ByteCountFormatter.string(fromByteCount: plan.uploadBytes, countStyle: .file))
                        }
                    }
                    if plan.downloadBytes == 0 && plan.uploadBytes == 0 && plan.totalBytes > 0 {
                        GridRow {
                            LText("Transfer:").foregroundStyle(.secondary)
                            Text(ByteCountFormatter.string(fromByteCount: plan.totalBytes, countStyle: .file))
                        }
                    }
                    if let available = destAvailable {
                        Divider().gridCellColumns(2)
                        GridRow {
                            LText("Available:").foregroundStyle(.secondary)
                            Text(ByteCountFormatter.string(fromByteCount: available, countStyle: .file))
                        }
                        let after = available - plan.netDestinationDelta
                        GridRow {
                            LText("After sync:").foregroundStyle(.secondary)
                            Text(ByteCountFormatter.string(fromByteCount: max(0, after), countStyle: .file))
                                .foregroundStyle(after < 0 ? .red : .primary)
                        }
                    }
                    // A sync that both downloads and uploads is passing the
                    // files through this Mac, and the disk they cross is
                    // not the destination's quota. Shown next to it, since
                    // that is where someone is already looking.
                    if let staging = stagingNeed(for: plan) {
                        Divider().gridCellColumns(2)
                        GridRow {
                            LText("Needs on this Mac:").foregroundStyle(.secondary)
                            Text(ByteCountFormatter.string(fromByteCount: staging.needed, countStyle: .file))
                                .foregroundStyle(staging.wouldExceed ? .red : .primary)
                        }
                        GridRow {
                            Text(verbatim: staging.volumeName).foregroundStyle(.secondary)
                            Text(staging.free.map {
                                L10n.format("%@ free", ByteCountFormatter.string(fromByteCount: max(0, $0), countStyle: .file))
                            } ?? L10n.text("free space unknown"))
                        }
                    }
                }
                .font(.callout)

                if plan.typeClashes > 0 {
                    Label(L10n.format("Leaving %d file(s) alone: a folder with the same name is at the destination.", plan.typeClashes),
                          systemImage: "exclamationmark.triangle.fill")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .padding(.top, 2)
                }
                if plan.wouldEmptyDestination {
                    Label(L10n.text("The source is empty, so Mirror would delete everything at the destination. Check that the right folder is open."),
                          systemImage: "exclamationmark.triangle.fill")
                        .font(.caption)
                        .foregroundStyle(.red)
                        .padding(.top, 2)
                } else if plan.deletesMostOfDestination {
                    Toggle(isOn: $confirmLargeDeletion) {
                        Text(L10n.format("Yes, delete %d of the %d items at the destination.",
                                         plan.filesToDelete + plan.foldersToDelete, plan.destinationEntryCount))
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                    .toggleStyle(.checkbox)
                    .padding(.top, 2)
                }
                if let available = destAvailable, available - plan.netDestinationDelta < 0 {
                    Label(L10n.text("This sync would exceed the available space at the destination."),
                          systemImage: "exclamationmark.triangle.fill")
                        .font(.caption)
                        .foregroundStyle(.red)
                        .padding(.top, 2)
                }
                if let staging = stagingNeed(for: plan), staging.wouldExceed {
                    Label(
                        L10n.format("The files pass through this Mac: %@ doesn't have room for that.", staging.volumeName),
                        systemImage: "exclamationmark.triangle.fill"
                    )
                    .font(.caption)
                    .foregroundStyle(.red)
                    .padding(.top, 2)
                }
            } else if let readOnlyDestination {
                Label(SyncPlannerError.destinationReadOnly(readOnlyDestination).localizedDescription,
                      systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.red)
            } else {
                LText("Press Calculate to compute how many files will be added, replaced, or deleted.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var footerButtons: some View {
        HStack {
            Spacer()
            Button(L10n.text("Cancel"), role: .cancel) { dismiss() }
            Button(L10n.text("Start Sync")) {
                Task { await startSync() }
            }
            .keyboardShortcut(.defaultAction)
            .disabled(!canStart)
        }
    }

    // MARK: - Derived state

    private var leftEndpoint: SyncEndpoint? { endpoint(for: .left) }
    private var rightEndpoint: SyncEndpoint? { endpoint(for: .right) }
    private var sourceEndpoint: SyncEndpoint? { direction == .leftToRight ? leftEndpoint : rightEndpoint }
    private var destEndpoint: SyncEndpoint? { direction == .leftToRight ? rightEndpoint : leftEndpoint }

    private func endpoint(for side: PanelSide) -> SyncEndpoint? {
        if let accountId = appState.cloudAccountId(for: side) {
            let vm = appState.cloudFileManager(for: accountId, side: side)
            return .cloud(accountId: accountId, rootPath: vm.currentPath)
        }
        // Inside an archive `currentDirectory` is still the folder around
        // it, which isn't what the panel shows and not what the user means.
        let fileManager = appState.fileManager(for: side)
        return fileManager.isBrowsingArchive ? nil : .local(fileManager.currentDirectory)
    }

    /// The account type's name, when the destination is one that can't be
    /// written to: a GoPro or a GitHub repository.
    private var readOnlyDestination: String? {
        guard case .cloud(let accountId, _) = destEndpoint,
              let type = appState.syncManager.accounts.first(where: { $0.id == accountId })?.providerType,
              type.isReadOnly else { return nil }
        return type.displayName
    }

    private var canStart: Bool {
        guard let plan, !isCalculating, plannedEndpoints != nil, readOnlyDestination == nil,
              plan.matches(mode: mode, direction: direction) else { return false }
        if plan.mode.isDestructive && !confirmDestructive { return false }
        if plan.wouldEmptyDestination { return false }
        if plan.deletesMostOfDestination && !confirmLargeDeletion { return false }
        return !plan.operations.isEmpty
    }

    // MARK: - Actions

    /// Everything a calculation produced or a confirmation agreed to belongs
    /// to one direction and mode, so a change throws it all away.
    private func resetPlan() {
        plan = nil
        plannedEndpoints = nil
        destAvailable = nil
        confirmDestructive = false
        confirmLargeDeletion = false
    }

    private func calculate() async {
        guard let src = sourceEndpoint, let dst = destEndpoint else { return }
        isCalculating = true
        errorMessage = nil
        confirmLargeDeletion = false
        defer { isCalculating = false }
        let planner = SyncPlanner()
        do {
            async let srcEntriesTask = planner.enumerate(src)
            async let dstEntriesTask = planner.enumerate(dst)
            let (srcEntries, dstEntries) = try await (srcEntriesTask, dstEntriesTask)
            plan = await planner.plan(
                sourceEntries: srcEntries,
                destEntries: dstEntries,
                mode: mode,
                direction: direction,
                sourceIsCloud: src.isCloud,
                destIsCloud: dst.isCloud,
                dateTolerance: dateTolerance(between: src, and: dst)
            )
            plannedEndpoints = (src, dst)
            destAvailable = await availableSpace(for: dst)
        } catch {
            errorMessage = L10n.format("Could not compute plan: %@", error.localizedDescription)
            plan = nil
            plannedEndpoints = nil
            destAvailable = nil
        }
    }

    /// How far apart two dates can be and still mean the same moment.
    private func dateTolerance(between source: SyncEndpoint, and destination: SyncEndpoint) -> TimeInterval {
        let usesMinuteDates = [source, destination].contains { endpoint in
            guard case .cloud(let accountId, _) = endpoint,
                  let type = appState.syncManager.accounts.first(where: { $0.id == accountId })?.providerType
            else { return false }
            return type == .sftp || type == .ftp
        }
        return usesMinuteDates ? SyncPlanner.minuteDateTolerance : SyncPlanner.defaultDateTolerance
    }

    /// What this sync needs on the Mac while it runs, or nil when nothing
    /// passes through: a plan that only uploads reads from files already
    /// here, and one that only downloads writes straight to where they are
    /// going.
    private func stagingNeed(for plan: SyncPlan) -> SpaceImpact.Staging? {
        guard plan.downloadBytes > 0, plan.uploadBytes > 0 else { return nil }
        let staging = StagingLocation.base()
        return SpaceImpact.Staging(
            volumeName: SpaceCheck.volumeName(at: staging),
            needed: plan.downloadBytes,
            free: SpaceCheck.localVolumeFreeBytes(at: staging)
        )
    }

    /// Free space at the destination: a cloud account's `total − used` quota,
    /// or a local volume's free bytes. nil = unlimited / unknown.
    private func availableSpace(for endpoint: SyncEndpoint) async -> Int64? {
        switch endpoint {
        case .cloud(let accountId, _):
            guard let quota = await appState.syncManager.storageQuota(for: accountId),
                  let total = quota.totalBytes, total > 0 else { return nil }
            return max(0, total - quota.usedBytes)
        case .local(let url):
            return SpaceCheck.localVolumeFreeBytes(at: url)
        case .offlineIndexed:
            return nil
        }
    }

    private func startSync() async {
        guard let plan, let (src, dst) = plannedEndpoints, plan.matches(mode: mode, direction: direction) else { return }
        let destSide: PanelSide = plan.direction == .leftToRight ? .right : .left
        let label: String
        switch plan.mode {
        case .mirror:   label = "Mirroring"
        case .newer:    label = "Syncing newer"
        case .additive: label = "Adding"
        }
        let transfer = TransferProgress(operation: label, totalItems: plan.operations.count)
        appState.addTransfer(transfer, panel: destSide)
        transfer.task = Task {
            await SyncExecutor.execute(plan: plan, source: src, destination: dst, progress: transfer)
            if let destAccountId = appState.cloudAccountId(for: destSide) {
                await appState.cloudFileManager(for: destAccountId, side: destSide).refresh()
            } else {
                await appState.fileManager(for: destSide).refresh()
            }
        }
        dismiss()
    }
}
