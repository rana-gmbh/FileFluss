import SwiftUI

struct SyncPlannerView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    @State private var direction: PlanDirection = .leftToRight
    @State private var mode: SyncMode = .newer
    @State private var confirmDestructive: Bool = false
    @State private var plan: SyncPlan?
    @State private var isCalculating: Bool = false
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Sync")
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
        .onChange(of: direction) { _, _ in plan = nil }
        .onChange(of: mode) { _, _ in plan = nil; confirmDestructive = false }
    }

    // MARK: - Sections

    private var endpointsSection: some View {
        HStack(alignment: .top, spacing: 12) {
            endpointCard(title: "Left", endpoint: leftEndpoint)
            Image(systemName: direction == .leftToRight ? "arrow.right" : "arrow.left")
                .font(.title2)
                .foregroundStyle(.secondary)
                .frame(width: 28)
                .padding(.top, 24)
            endpointCard(title: "Right", endpoint: rightEndpoint)
        }
    }

    private func endpointCard(title: String, endpoint: SyncEndpoint?) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.caption).foregroundStyle(.secondary)
            if let endpoint {
                HStack(spacing: 6) {
                    Image(systemName: endpoint.isCloud ? "cloud.fill" : "folder.fill")
                        .foregroundStyle(.secondary)
                    Text(endpoint.displayPath)
                        .lineLimit(2)
                        .truncationMode(.middle)
                        .font(.callout)
                }
            } else {
                Text("No folder open")
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
            Text("Direction").font(.headline)
            Spacer()
            Picker("", selection: $direction) {
                Text("Left → Right").tag(PlanDirection.leftToRight)
                Text("Left ← Right").tag(PlanDirection.rightToLeft)
            }
            .pickerStyle(.segmented)
            .frame(width: 240)
            .labelsHidden()
        }
    }

    private var modeSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Mode").font(.headline)
            ForEach(SyncMode.allCases, id: \.self) { option in
                Button {
                    mode = option
                } label: {
                    HStack(alignment: .top, spacing: 10) {
                        Image(systemName: mode == option ? "largecircle.fill.circle" : "circle")
                            .foregroundStyle(mode == option ? Color.accentColor : .secondary)
                            .padding(.top, 2)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(option.title).font(.callout).bold()
                            Text(option.subtitle).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            if mode.isDestructive {
                Toggle(isOn: $confirmDestructive) {
                    Text("I understand files on the destination will be deleted.")
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
                Text("Plan").font(.headline)
                Spacer()
                Button {
                    Task { await calculate() }
                } label: {
                    if isCalculating {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Calculate")
                    }
                }
                .disabled(isCalculating || leftEndpoint == nil || rightEndpoint == nil)
            }

            if let plan {
                Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 4) {
                    GridRow { Text("Files to add:").foregroundStyle(.secondary); Text("\(plan.filesToAdd)") }
                    GridRow { Text("Files to replace:").foregroundStyle(.secondary); Text("\(plan.filesToReplace)") }
                    GridRow {
                        Text("Files to delete:").foregroundStyle(.secondary)
                        Text("\(plan.filesToDelete)")
                            .foregroundStyle(plan.filesToDelete > 0 ? .red : .primary)
                    }
                    GridRow { Text("Folders to add:").foregroundStyle(.secondary); Text("\(plan.foldersToAdd)") }
                    GridRow {
                        Text("Folders to delete:").foregroundStyle(.secondary)
                        Text("\(plan.foldersToDelete)")
                            .foregroundStyle(plan.foldersToDelete > 0 ? .red : .primary)
                    }
                    Divider().gridCellColumns(2)
                    if plan.downloadBytes > 0 {
                        GridRow {
                            Text("Download:").foregroundStyle(.secondary)
                            Text(ByteCountFormatter.string(fromByteCount: plan.downloadBytes, countStyle: .file))
                        }
                    }
                    if plan.uploadBytes > 0 {
                        GridRow {
                            Text("Upload:").foregroundStyle(.secondary)
                            Text(ByteCountFormatter.string(fromByteCount: plan.uploadBytes, countStyle: .file))
                        }
                    }
                    if plan.downloadBytes == 0 && plan.uploadBytes == 0 && plan.totalBytes > 0 {
                        GridRow {
                            Text("Transfer:").foregroundStyle(.secondary)
                            Text(ByteCountFormatter.string(fromByteCount: plan.totalBytes, countStyle: .file))
                        }
                    }
                }
                .font(.callout)
            } else {
                Text("Press Calculate to compute how many files will be added, replaced, or deleted.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var footerButtons: some View {
        HStack {
            Spacer()
            Button("Cancel", role: .cancel) { dismiss() }
            Button("Start Sync") {
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
            let vm = appState.cloudFileManager(for: accountId)
            return .cloud(accountId: accountId, rootPath: vm.currentPath)
        }
        return .local(appState.fileManager(for: side).currentDirectory)
    }

    private var canStart: Bool {
        guard let plan, !isCalculating else { return false }
        if mode.isDestructive && !confirmDestructive { return false }
        return !plan.operations.isEmpty
    }

    // MARK: - Actions

    private func calculate() async {
        guard let src = sourceEndpoint, let dst = destEndpoint else { return }
        isCalculating = true
        errorMessage = nil
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
                destIsCloud: dst.isCloud
            )
        } catch {
            errorMessage = "Could not compute plan: \(error.localizedDescription)"
            plan = nil
        }
    }

    private func startSync() async {
        guard let plan, let src = sourceEndpoint, let dst = destEndpoint else { return }
        let destSide: PanelSide = direction == .leftToRight ? .right : .left
        let label: String
        switch mode {
        case .mirror:   label = "Mirroring"
        case .newer:    label = "Syncing newer"
        case .additive: label = "Adding"
        }
        let transfer = TransferProgress(operation: label, totalItems: plan.operations.count)
        appState.addTransfer(transfer, panel: destSide)
        transfer.task = Task {
            await SyncExecutor.execute(plan: plan, source: src, destination: dst, progress: transfer)
            if let destAccountId = appState.cloudAccountId(for: destSide) {
                await appState.cloudFileManager(for: destAccountId).refresh()
            } else {
                await appState.fileManager(for: destSide).refresh()
            }
        }
        dismiss()
    }
}
